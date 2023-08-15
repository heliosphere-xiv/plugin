using System.Net.Http.Headers;
using System.Text;
using Blake3;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Logging;
using gfoidl.Base64;
using Heliosphere.Exceptions;
using Heliosphere.Model;
using Heliosphere.Model.Generated;
using Heliosphere.Model.Penumbra;
using Heliosphere.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using StrawberryShake;
using ZstdSharp;

namespace Heliosphere;

internal class DownloadTask : IDisposable {
    #if DEBUG
    internal const string ApiBase = "http://192.168.174.222:42011";
    #else
    internal const string ApiBase = "https://heliosphere.app/api";
    #endif

    private Plugin Plugin { get; }
    private string ModDirectory { get; }
    private Guid Version { get; }
    private Dictionary<string, List<string>> Options { get; }
    private bool Full { get; }
    private string? DownloadKey { get; }
    private bool IncludeTags { get; }
    private string? PenumbraModPath { get; set; }
    private string? PenumbraCollection { get; set; }
    internal string? PackageName { get; private set; }
    internal string? VariantName { get; private set; }

    internal CancellationTokenSource CancellationToken { get; } = new();
    internal State State { get; private set; } = State.NotStarted;
    internal uint StateData { get; private set; }
    internal uint StateDataMax { get; private set; }
    internal Exception? Error { get; private set; }

    private bool _disposed;
    private string? _oldModName;

    internal DownloadTask(Plugin plugin, string modDirectory, Guid version, bool includeTags, string? collection, string? downloadKey) {
        this.Plugin = plugin;
        this.ModDirectory = modDirectory;
        this.Version = version;
        this.Options = new Dictionary<string, List<string>>();
        this.Full = true;
        this.DownloadKey = downloadKey;
        this.IncludeTags = includeTags;
        this.PenumbraCollection = collection;
    }

    internal DownloadTask(Plugin plugin, string modDirectory, Guid version, Dictionary<string, List<string>> options, bool includeTags, string? collection, string? downloadKey) {
        this.Plugin = plugin;
        this.ModDirectory = modDirectory;
        this.Version = version;
        this.Options = options;
        this.DownloadKey = downloadKey;
        this.IncludeTags = includeTags;
        this.PenumbraCollection = collection;
    }

    ~DownloadTask() {
        this.Dispose();
    }

    public void Dispose() {
        if (this._disposed) {
            return;
        }

        this._disposed = true;
        GC.SuppressFinalize(this);
        this.CancellationToken.Dispose();
    }

    internal Task Start() {
        return Task.Run(this.Run, this.CancellationToken.Token);
    }

    private async Task Run() {
        try {
            var info = await this.GetPackageInfo();
            if (this.Full) {
                foreach (var group in info.Groups) {
                    this.Options[group.Name] = new List<string>();

                    foreach (var option in group.Options) {
                        this.Options[group.Name].Add(option.Name);
                    }
                }
            }

            this.PackageName = info.Variant.Package.Name;
            this.VariantName = info.Variant.Name;
            await this.DownloadFiles(info);
            await this.ConstructModPack(info);
            this.AddMod(info);
            this.RemoveOldFiles(info);
            this.State = State.Finished;
            this.Plugin.Interface.UiBuilder.AddNotification(
                $"{this.PackageName} installed in Penumbra.",
                this.Plugin.Name,
                NotificationType.Success
            );

            // refresh the manager package list after install finishes
            await this.Plugin.State.UpdatePackages();
        } catch (Exception ex) {
            this.State = State.Errored;
            this.Error = ex;
            this.Plugin.Interface.UiBuilder.AddNotification(
                $"Failed to install {this.PackageName ?? "mod"}.",
                this.Plugin.Name,
                NotificationType.Error,
                5_000
            );

            // probably antivirus (ioexception is being used by other process or
            // access denied)
            if (ex.IsAntiVirus()) {
                this.Plugin.PluginUi.ShowAvWarning = true;
            } else {
                ErrorHelper.Handle(ex, $"Error downloading version {this.Version}");
            }
        }
    }

    private void SetStateData(uint current, uint max) {
        this.StateData = current;
        this.StateDataMax = max;
    }

    internal static async Task<HttpResponseMessage> GetImage(Guid id, int imageId, CancellationToken token = default) {
        var resp = await Plugin.Client.GetAsync($"{ApiBase}/web/package/{id:N}/image/{imageId}", HttpCompletionOption.ResponseHeadersRead, token);
        resp.EnsureSuccessStatusCode();
        return resp;
    }

    private async Task<IDownloadTask_GetVersion> GetPackageInfo() {
        this.State = State.DownloadingPackageInfo;
        this.SetStateData(0, 1);

        var resp = await Plugin.GraphQl.DownloadTask.ExecuteAsync(this.Version, this.Options, this.DownloadKey, this.Full);
        resp.EnsureNoErrors();

        var version = resp.Data?.GetVersion ?? throw new MissingVersionException(this.Version);

        if (this.DownloadKey != null) {
            this.Plugin.DownloadCodes.TryInsert(version.Variant.Package.Id, this.DownloadKey);
            this.Plugin.DownloadCodes.Save();
        }

        this.StateData += 1;
        return version;
    }

    private async Task DownloadFiles(IDownloadTask_GetVersion info) {
        this.State = State.DownloadingFiles;
        this.SetStateData(0, (uint) info.NeededFiles.Files.Files.Count);

        var directories = Directory.EnumerateDirectories(this.ModDirectory)
            .Select(Path.GetFileName)
            .Where(path => !string.IsNullOrEmpty(path))
            .Where(path => path!.StartsWith("hs-") && path.EndsWith($"-{info.Variant.Id:N}-{info.Variant.Package.Id:N}"))
            .ToArray();

        var dirName = HeliosphereMeta.ModDirectoryName(info.Variant.Package.Id, info.Variant.Package.Name, info.Version, info.Variant.Id);
        this.PenumbraModPath = Path.Join(this.ModDirectory, dirName);
        if (directories.Length == 1) {
            var oldName = Path.Join(this.ModDirectory, directories[0]!);
            if (oldName != this.PenumbraModPath) {
                this._oldModName = directories[0];
                Directory.Move(oldName, this.PenumbraModPath);
            }
        } else if (directories.Length > 1) {
            PluginLog.Warning($"multiple heliosphere mod directories found for {info.Variant.Package.Name} - not attempting a rename");
        }

        var filesPath = Path.Join(this.PenumbraModPath, "files");
        if (!await PathHelper.CreateDirectory(filesPath)) {
            throw new DirectoryNotFoundException($"Directory '{filesPath}' could not be found after waiting");
        }

        var tasks = info.Batched
            ? this.DownloadBatchedFiles(info.NeededFiles, info.Batches, filesPath)
            : this.DownloadNormalFiles(info.NeededFiles, filesPath);
        await Task.WhenAll(tasks);
    }

    private IEnumerable<Task> DownloadNormalFiles(IDownloadTask_GetVersion_NeededFiles neededFiles, string filesPath) {
        return neededFiles.Files.Files
            .Select(pair => Task.Run(async () => {
                var (hash, files) = pair;
                GetExtensionsAndDiscriminators(files, hash, out var extensions, out var discriminators, out var allUi);

                using (await SemaphoreGuard.WaitAsync(Plugin.DownloadSemaphore)) {
                    await this.DownloadFile(new Uri(neededFiles.BaseUri), filesPath, extensions, allUi, discriminators, hash);
                }
            }));
    }

    private IEnumerable<Task> DownloadBatchedFiles(IDownloadTask_GetVersion_NeededFiles neededFiles, BatchList batches, string filesPath) {
        var neededHashes = neededFiles.Files.Files.Keys.ToList();
        var clonedBatches = batches.Files.ToDictionary(pair => pair.Key, pair => pair.Value.ToDictionary(pair => pair.Key, pair => pair.Value));
        foreach (var (batch, files) in batches.Files) {
            // remove any hashes that aren't needed
            foreach (var hash in files.Keys) {
                if (!neededHashes.Contains(hash)) {
                    clonedBatches[batch].Remove(hash);
                }
            }

            // remove any empty batches
            if (clonedBatches[batch].Count == 0) {
                clonedBatches.Remove(batch);
            }
        }

        return clonedBatches.Select(pair => Task.Run(async () => {
            var (batch, batchedFiles) = pair;

            // list all files in the download directory
            var preInstalledHashes = Directory.EnumerateFiles(filesPath)
                .Select(path => (Hash: PathHelper.GetBaseName(Path.GetFileName(path)), path))
                .Where(pair => batchedFiles.ContainsKey(pair.Hash));
            var installedHashes = new HashSet<string>();
            var toDuplicate = new List<string>();
            using var blake3 = new Blake3HashAlgorithm();
            foreach (var (hash, path) in preInstalledHashes) {
                if (installedHashes.Contains(hash)) {
                    // this will just get duplicated anyway
                    continue;
                }

                blake3.Initialize();
                await using var file = File.OpenRead(path);
                var computed = await blake3.ComputeHashAsync(file, this.CancellationToken.Token);
                // if the hash matches, don't redownload, just duplicate the
                // file as necessary
                if (Base64.Url.Encode(computed) != hash) {
                    continue;
                }

                installedHashes.Add(hash);
                toDuplicate.Add(path);
            }

            // sort files in batch by offset, removing already-downloaded files
            var listOfFiles = batchedFiles
                .Select(pair => (Hash: pair.Key, Info: pair.Value))
                .Where(pair => !installedHashes.Contains(pair.Hash))
                .OrderBy(pair => pair.Info.Offset).ToList();

            if (listOfFiles.Count > 0) {
                // calculate ranges
                var ranges = new List<(ulong, ulong)>();
                var begin = 0ul;
                var end = 0ul;
                var chunk = new List<string>();
                var chunks = new List<List<string>>();
                foreach (var (hash, info) in listOfFiles) {
                    if (begin == 0 && end == 0) {
                        // first item, so set begin and end
                        begin = info.Offset;
                        end = info.Offset + info.SizeCompressed;
                        // add the hash to this chunk
                        chunk.Add(hash);

                        continue;
                    }

                    if (info.Offset == end) {
                        // there's no gap, so extend the end of this range
                        end += info.SizeCompressed;
                        // add the hash to this chunk
                        chunk.Add(hash);
                        continue;
                    }

                    // there is a gap
                    // add this chunk to the list of chunks
                    chunks.Add(chunk);
                    // make a new chunk
                    chunk = new List<string>();

                    // add the range to the list of ranges
                    ranges.Add((begin, end));

                    // start a new range after the gap
                    begin = info.Offset;
                    end = info.Offset + info.SizeCompressed;

                    // add the hash to the new chunk
                    chunk.Add(hash);
                }

                if (end != 0) {
                    // add the last range if necessary
                    ranges.Add((begin, end));

                    if (chunk.Count > 0) {
                        chunks.Add(chunk);
                    }
                }

                // check if we're just downloading the whole file - cf cache
                // won't kick in for range requests
                var totalBatchSize = batchedFiles.Values
                    .Select(file => file.SizeCompressed)
                    // no Sum function for ulong
                    .Aggregate((total, size) => total + size);

                RangeHeaderValue? rangeHeader;
                if (ranges is [{ Item1: 0, Item2: var rangeEnd }] && rangeEnd == totalBatchSize) {
                    rangeHeader = null;
                } else {
                    // construct the header
                    rangeHeader = new RangeHeaderValue();
                    foreach (var (from, to) in ranges) {
                        rangeHeader.Ranges.Add(new RangeItemHeaderValue((long) from, (long) to));
                    }
                }

                // construct the request
                var baseUri = new Uri(new Uri(neededFiles.BaseUri), "../batches/");
                var uri = new Uri(baseUri, batch);
                var req = new HttpRequestMessage(HttpMethod.Get, uri) {
                    Headers = {
                        Range = rangeHeader,
                    },
                };

                using (await SemaphoreGuard.WaitAsync(Plugin.DownloadSemaphore)) {
                    var counter = new StateCounter();
                    await Retry<object?>(3, $"could not download batched file {batch}", async () => {
                        // if we're retrying, remove the files that this task added
                        this.StateData -= counter.Added;
                        counter.Added = 0;

                        await this.DownloadBatchedFile(neededFiles, filesPath, req, chunks, batchedFiles, counter);
                        return null;
                    });
                }
            }

            foreach (var path in toDuplicate) {
                var hash = PathHelper.GetBaseName(Path.GetFileName(path));
                var gamePaths = neededFiles.Files.Files[hash];
                GetExtensionsAndDiscriminators(gamePaths, hash, out var extensions, out var discriminators, out var allUi);

                // first extension and discriminator should be the one of this path
                var ext = Path.GetExtension(path);
                var discrimMaybe = Path.GetExtension(Path.ChangeExtension(path, null));

                // make the first extension this one
                extensions.Remove(ext);
                extensions.Insert(0, ext);

                // if this path has a discriminator, put it first
                if (!string.IsNullOrEmpty(discrimMaybe)) {
                    discriminators.Remove(discrimMaybe);
                    discriminators.Insert(0, discrimMaybe);
                }

                await DuplicateFile(extensions, discriminators, allUi, path);

                this.StateData += 1;
            }
        }));
    }

    private class StateCounter {
        internal uint Added { get; set; }
    }

    private async Task DownloadBatchedFile(
        IDownloadTask_GetVersion_NeededFiles neededFiles,
        string filesPath,
        HttpRequestMessage req,
        IReadOnlyList<List<string>> chunks,
        IReadOnlyDictionary<string, BatchedFile> batchedFiles,
        StateCounter counter
    ) {
        // send the request
        using var resp = await Plugin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, this.CancellationToken.Token);
        resp.EnsureSuccessStatusCode();

        // if only one chunk is requested, it's not multipart, so check
        // for that
        MultipartMemoryStreamProvider multipart;
        if (resp.Content.IsMimeMultipartContent()) {
            // FIXME: pretty sure this loads the whole response into memory
            multipart = await resp.Content.ReadAsMultipartAsync(this.CancellationToken.Token);
        } else {
            multipart = new MultipartMemoryStreamProvider {
                Contents = { resp.Content },
            };
        }

        // make sure that the number of chunks is the same
        if (multipart.Contents.Count != chunks.Count) {
            throw new Exception("did not download correct number of chunks");
        }

        for (var i = 0; i < chunks.Count; i++) {
            // each multipart chunk corresponds to a chunk of files we
            // generated earlier. get both of those
            var content = multipart.Contents[i];
            var chunk = chunks[i];

            // get the content of this multipart chunk as a stream
            await using var stream = await content.ReadAsStreamAsync(this.CancellationToken.Token);

            // now we're going to read each file in the chunk out,
            // decompress it, and write it to the disk
            var buffer = new byte[81_920];
            foreach (var hash in chunk) {
                // firstly, we now need to figure out which extensions and
                // discriminators to use for this specific file
                var gamePaths = neededFiles.Files.Files[hash];
                GetExtensionsAndDiscriminators(gamePaths, hash, out var extensions, out var discriminators, out var allUi);

                var batchedFileInfo = batchedFiles[hash];
                var path = allUi
                    ? Path.ChangeExtension(Path.Join(filesPath, hash), $"{discriminators[0]}{extensions[0]}")
                    : Path.ChangeExtension(Path.Join(filesPath, hash), extensions[0]);
                await using var file = File.Create(path);
                // make a stream that's only capable of reading the
                // amount of compressed bytes
                await using var limited = new LimitedStream(stream, (int) batchedFileInfo.SizeCompressed);
                await using var decompressor = new DecompressionStream(limited);

                // make sure we only read *this* file - one file is only
                // part of the multipart chunk
                var total = 0ul;
                while (total < batchedFileInfo.SizeUncompressed) {
                    var leftToRead = Math.Min(
                        (ulong) buffer.Length,
                        batchedFileInfo.SizeUncompressed - total
                    );
                    var read = await decompressor.ReadAsync(buffer.AsMemory(0, (int) leftToRead), this.CancellationToken.Token);
                    total += (ulong) read;

                    if (read == 0) {
                        break;
                    }

                    await file.WriteAsync(buffer.AsMemory()[..read], this.CancellationToken.Token);
                }

                // make sure we read all the bytes before moving on to
                // the next file
                limited.ReadToEnd(buffer);

                // flush the file and close it
                await file.FlushAsync(this.CancellationToken.Token);
                await file.DisposeAsync();

                // the file is now fully written to, so duplicate it if
                // necessary
                await DuplicateFile(extensions, discriminators, allUi, path);

                this.StateData += 1;
                counter.Added += 1;
            }
        }
    }

    private static void GetExtensionsAndDiscriminators(IReadOnlyCollection<List<string?>> gamePaths, string hash, out List<string> extensions, out List<string> discriminators, out bool allUi) {
        extensions = gamePaths
            .Select(file => Path.GetExtension(file[2]!))
            .ToHashSet()
            .ToList();
        discriminators = gamePaths
            .Where(file => file[2]!.StartsWith("ui/"))
            .Select(HashHelper.GetDiscriminator)
            .ToHashSet()
            .ToList();
        allUi = gamePaths.Count > 0 && gamePaths.All(file => file[2]!.StartsWith("ui/"));

        if (extensions.Count == 0) {
            // how does this happen?
            PluginLog.LogWarning($"{hash} has no extension");
            extensions.Add(".unk");
        }
    }

    private static async Task DuplicateFile(IList<string> extensions, IList<string> discriminators, bool allUi, string path) {
        foreach (var ext in extensions) {
            // duplicate the file for each ui path discriminator
            foreach (var discriminator in discriminators) {
                if (allUi && discriminator == discriminators[0]) {
                    continue;
                }

                var uiDest = PathHelper.ChangeExtension(path, $"{discriminator}{ext}");
                if (!await PathHelper.WaitForDelete(uiDest)) {
                    throw new DeleteFileException(uiDest);
                }

                File.Copy(path, uiDest);
            }

            // skip initial extension
            if (ext == extensions[0]) {
                continue;
            }

            // duplicate the file for each other extension it has
            var dest = PathHelper.ChangeExtension(path, ext);
            if (!await PathHelper.WaitForDelete(dest)) {
                throw new DeleteFileException(dest);
            }

            File.Copy(path, dest);
        }
    }

    private void RemoveOldFiles(IDownloadTask_GetVersion info) {
        this.State = State.RemovingOldFiles;
        this.SetStateData(0, 1);

        var filesPath = Path.Join(this.PenumbraModPath, "files");
        // FIXME: this doesn't remove unused discriminators
        // remove any old files no longer being used
        var neededHashes = info.NeededFiles.Files.Files.Keys.ToHashSet();
        var presentHashes = Directory.EnumerateFiles(filesPath)
            .Select(Path.GetFileName)
            .Where(path => !string.IsNullOrEmpty(path))
            .Cast<string>()
            .GroupBy(PathHelper.GetBaseName)
            .ToDictionary(group => group.Key, group => group.ToHashSet());
        var present = presentHashes.Keys.ToHashSet();
        present.ExceptWith(neededHashes);

        var total = (uint) presentHashes.Values
            .Select(set => set.Count)
            .Sum();
        this.SetStateData(0, total);

        var done = 0u;
        foreach (var extra in present) {
            foreach (var file in presentHashes[extra]) {
                var extraPath = Path.Join(filesPath, file);
                PluginLog.Log($"removing extra file {extraPath}");
                File.Delete(extraPath);

                done += 1;
                this.SetStateData(done, 1);
            }
        }
    }

    private static async Task<T?> Retry<T>(int times, string message, Func<Task<T>> ac) {
        for (var i = 0; i < times; i++) {
            try {
                return await ac();
            } catch (Exception ex) {
                if (i == times - 1) {
                    // failed three times, so rethrow
                    throw;
                }

                PluginLog.LogError(ex, message);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }

        return default;
    }

    private async Task DownloadFile(Uri baseUri, string filesPath, IList<string> extensions, bool allUi, IList<string> discriminators, string hash) {
        var path = allUi
            ? Path.ChangeExtension(Path.Join(filesPath, hash), $"{discriminators[0]}{extensions[0]}")
            : Path.ChangeExtension(Path.Join(filesPath, hash), extensions[0]);

        // FIXME: this needs to check if any new extensions or discriminators
        //        were added
        if (File.Exists(path)) {
            var shouldGoto = await Retry(3, $"Error calculating hash for {baseUri}/{hash}", async () => {
                // make sure checksum matches
                using var blake3 = new Blake3HashAlgorithm();
                blake3.Initialize();
                await using var file = File.OpenRead(path);
                var computed = await blake3.ComputeHashAsync(file, this.CancellationToken.Token);
                // if the hash matches, don't redownload, just duplicate the
                // file as necessary
                return Base64.Url.Encode(computed) == hash;
            });

            if (shouldGoto) {
                goto Duplicate;
            }
        }

        await Retry(3, $"Error downloading {baseUri}/{hash}", async () => {
            var uri = new Uri(baseUri, hash).ToString();
            using var resp = await Plugin.Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, this.CancellationToken.Token);
            resp.EnsureSuccessStatusCode();

            await using var file = File.Create(path);
            await using var stream = await resp.Content.ReadAsStreamAsync(this.CancellationToken.Token);
            await new DecompressionStream(stream).CopyToAsync(file, this.CancellationToken.Token);

            return false;
        });

        Duplicate:
        await DuplicateFile(extensions, discriminators, allUi, path);

        this.StateData += 1;
    }

    private async Task ConstructModPack(IDownloadTask_GetVersion info) {
        this.State = State.ConstructingModPack;
        this.SetStateData(0, 4);
        var hsMeta = await this.ConstructHeliosphereMeta(info);
        await this.ConstructMeta(info, hsMeta);
        await this.ConstructDefaultMod(info);
        await this.ConstructGroups(info);
    }

    private string GenerateModName(IDownloadTask_GetVersion info) {
        var pkgName = info.Variant.Package.Name.Replace('/', '-');
        var varName = info.Variant.Name.Replace('/', '-');
        return $"{this.Plugin.Config.TitlePrefix}{pkgName} ({varName})";
    }

    private async Task ConstructMeta(IDownloadTask_GetVersion info, HeliosphereMeta hsMeta) {
        var tags = this.IncludeTags
            ? info.Variant.Package.Tags.Select(tag => tag.Slug).ToList()
            : new List<string>();

        if (!hsMeta.FullInstall) {
            tags.Add("hs-partial-install");
        }

        var meta = new ModMeta {
            Name = this.GenerateModName(info),
            Author = info.Variant.Package.User.Username,
            Description = info.Variant.Package.Description,
            Version = info.Version,
            Website = $"https://heliosphere.app/mod/{info.Variant.Package.Id.ToCrockford()}",
            ModTags = tags.ToArray(),
        };
        var json = JsonConvert.SerializeObject(meta, Formatting.Indented);

        var path = Path.Join(this.PenumbraModPath, "meta.json");
        await using var file = File.Create(path);
        await file.WriteAsync(Encoding.UTF8.GetBytes(json), this.CancellationToken.Token);
        this.State += 1;
    }

    private async Task<HeliosphereMeta> ConstructHeliosphereMeta(IDownloadTask_GetVersion info) {
        var selectedAll = true;
        foreach (var group in info.Groups) {
            if (!this.Options.TryGetValue(group.Name, out var selected)) {
                selectedAll = false;
                break;
            }

            if (group.Options.All(option => selected.Contains(option.Name))) {
                continue;
            }

            selectedAll = false;
            break;
        }

        var meta = new HeliosphereMeta {
            Id = info.Variant.Package.Id,
            Name = info.Variant.Package.Name,
            Tagline = info.Variant.Package.Tagline,
            Description = info.Variant.Package.Description,
            Author = info.Variant.Package.User.Username,
            AuthorId = info.Variant.Package.User.Id,
            Variant = info.Variant.Name,
            VariantId = info.Variant.Id,
            Version = info.Version,
            VersionId = this.Version,
            FullInstall = selectedAll,
            IncludeTags = this.IncludeTags,
            SelectedOptions = this.Options,
            ModHash = info.NeededFiles.ModHash,
        };

        var metaJson = JsonConvert.SerializeObject(meta, Formatting.Indented);
        var path = Path.Join(this.PenumbraModPath, "heliosphere.json");
        await using var file = File.Create(path);
        await file.WriteAsync(Encoding.UTF8.GetBytes(metaJson), this.CancellationToken.Token);

        // save cover image
        if (info.Variant.Package.Images.Count > 0) {
            var coverImage = info.Variant.Package.Images[0];
            var coverPath = Path.Join(this.PenumbraModPath, "cover.jpg");

            try {
                using var image = await GetImage(info.Variant.Package.Id, coverImage.Id, this.CancellationToken.Token);
                await using var cover = File.Create(coverPath);
                await image.Content.CopyToAsync(cover, this.CancellationToken.Token);
            } catch (Exception ex) {
                ErrorHelper.Handle(ex, "Could not download cover image");
            }
        }

        this.State += 1;

        return meta;
    }

    private async Task ConstructDefaultMod(IDownloadTask_GetVersion info) {
        var defaultMod = new DefaultMod {
            Name = info.DefaultOption?.Name ?? string.Empty,
            Description = info.DefaultOption?.Description,
            Manipulations = ManipTokensForOption(info.NeededFiles.Manipulations.FirstOrDefault(group => group.Name == null)?.Options, null),
            FileSwaps = info.DefaultOption?.FileSwaps.Swaps ?? new Dictionary<string, string>(),
        };
        foreach (var (hash, files) in info.NeededFiles.Files.Files) {
            foreach (var file in files) {
                if (file[0] != null || file[1] != null) {
                    continue;
                }

                var isUi = file[2]!.StartsWith("ui/");
                var gameExt = Path.GetExtension(file[2]);
                var replacedPath = Path.Join("files", hash);

                if (isUi) {
                    var discriminator = HashHelper.GetDiscriminator(file);
                    replacedPath = Path.ChangeExtension(replacedPath, $"{discriminator}{gameExt}");
                } else {
                    replacedPath = Path.ChangeExtension(replacedPath, gameExt);
                }

                defaultMod.Files[file[2]!] = replacedPath;
            }
        }

        var json = JsonConvert.SerializeObject(defaultMod, Formatting.Indented);

        var path = Path.Join(this.PenumbraModPath, "default_mod.json");
        await using var output = File.Create(path);
        await output.WriteAsync(Encoding.UTF8.GetBytes(json), this.CancellationToken.Token);
        this.StateData += 1;
    }

    private async Task ConstructGroups(IDownloadTask_GetVersion info) {
        // remove any groups that already exist
        var existingGroups = Directory.EnumerateFiles(this.PenumbraModPath!)
            .Where(file => {
                var name = Path.GetFileName(file);
                return name.StartsWith("group_") && name.EndsWith(".json");
            });
        foreach (var existing in existingGroups) {
            File.Delete(existing);
        }

        var modGroups = new Dictionary<string, ModGroup>(info.Groups.Count);
        foreach (var group in info.Groups) {
            var modGroup = new ModGroup(group.Name, group.Description, group.SelectionType.ToString()) {
                Priority = group.Priority,
                DefaultSettings = (uint) group.DefaultSettings,
            };
            var groupManips = info.NeededFiles.Manipulations.FirstOrDefault(manips => manips.Name == group.Name);

            foreach (var option in group.Options) {
                var manipulations = ManipTokensForOption(groupManips?.Options, option.Name);
                modGroup.Options.Add(new DefaultMod {
                    Name = option.Name,
                    Description = option.Description,
                    Priority = option.Priority,
                    Manipulations = manipulations,
                    FileSwaps = option.FileSwaps.Swaps,
                    IsDefault = option.IsDefault,
                });
            }

            modGroups[group.Name] = modGroup;
        }

        foreach (var (hash, files) in info.NeededFiles.Files.Files) {
            foreach (var file in files) {
                if (file[0] == null || file[1] == null) {
                    continue;
                }

                var groupName = file[0]!;
                var optionName = file[1]!;
                var gamePath = file[2]!;

                var modGroup = modGroups[groupName];

                var option = modGroup.Options.FirstOrDefault(opt => opt.Name == optionName);
                // this shouldn't be possible?
                if (option == null) {
                    var opt = new DefaultMod {
                        Name = optionName,
                    };

                    modGroup.Options.Add(opt);
                    option = opt;
                }

                var isUi = gamePath.StartsWith("ui/");
                var gameExt = Path.GetExtension(gamePath);
                var replacedPath = Path.Join("files", hash);

                if (isUi) {
                    var discriminator = HashHelper.GetDiscriminator(file);
                    replacedPath = Path.ChangeExtension(replacedPath, $"{discriminator}{gameExt}");
                } else {
                    replacedPath = Path.ChangeExtension(replacedPath, gameExt);
                }

                option.Files[gamePath] = replacedPath;
            }
        }

        // remove options that weren't downloaded
        foreach (var group in modGroups.Values) {
            if (this.Options.TryGetValue(group.Name, out var selected)) {
                switch (group.Type) {
                    case "Single": {
                        var enabled = group.DefaultSettings < group.Options.Count
                            ? group.Options[(int) group.DefaultSettings].Name
                            : null;

                        group.Options.RemoveAll(opt => !selected.Contains(opt.Name));

                        var idx = group.Options.FindIndex(mod => mod.Name == enabled);
                        group.DefaultSettings = idx == -1 ? 0 : (uint) idx;

                        break;
                    }
                    case "Multi": {
                        var enabled = new Dictionary<string, bool>();
                        for (var i = 0; i < group.Options.Count; i++) {
                            var option = group.Options[i];
                            enabled[option.Name] = (group.DefaultSettings & (1 << i)) > 0;
                        }

                        group.Options.RemoveAll(opt => !selected.Contains(opt.Name));
                        group.DefaultSettings = 0;

                        for (var i = 0; i < group.Options.Count; i++) {
                            var option = group.Options[i];
                            if (enabled.TryGetValue(option.Name, out var wasEnabled) && wasEnabled) {
                                group.DefaultSettings |= (uint) (1 << i);
                            }
                        }

                        break;
                    }
                }
            } else {
                group.DefaultSettings = 0;
                group.Options.Clear();
            }
        }

        // split groups that have more than 32 options
        var splitGroups = SplitGroups(modGroups.Values);

        var invalidChars = Path.GetInvalidFileNameChars();
        var list = splitGroups
            .OrderBy(group => info.Groups.FindIndex(g => g.Name == group.Name))
            .ToList();
        for (var i = 0; i < list.Count; i++) {
            var slug = list[i].Name.ToLowerInvariant()
                .Select(c => invalidChars.Contains(c) ? '-' : c)
                .Aggregate(new StringBuilder(), (sb, c) => sb.Append(c))
                .ToString();
            var json = JsonConvert.SerializeObject(list[i], Formatting.Indented);
            var path = Path.Join(this.PenumbraModPath, $"group_{i + 1:000}_{slug}.json");
            await using var file = File.Create(path);
            await file.WriteAsync(Encoding.UTF8.GetBytes(json), this.CancellationToken.Token);
            this.StateData += 1;
        }
    }

    private static IEnumerable<ModGroup> SplitGroups(IEnumerable<ModGroup> groups) {
        return groups.SelectMany(SplitGroup);
    }

    private static IEnumerable<ModGroup> SplitGroup(ModGroup group) {
        const int perGroup = 32;

        if (group.Type != "Multi" || group.Options.Count <= perGroup) {
            return new[] { group };
        }

        var newGroups = new List<ModGroup>();
        for (var i = 0; i < group.Options.Count; i++) {
            var option = group.Options[i];
            var groupIdx = i / perGroup;
            var optionIdx = i % perGroup;

            if (optionIdx == 0) {
                newGroups.Add(new ModGroup($"{group.Name}, Part {groupIdx + 1}", group.Description, group.Type) {
                    Priority = group.Priority,
                });
            }

            var newGroup = newGroups[groupIdx];
            newGroup.Options.Add(option);
            if (option.IsDefault) {
                newGroup.DefaultSettings |= (uint) (1 << optionIdx);
            }
        }

        return newGroups;
    }

    private static List<JToken> ManipTokensForOption(IEnumerable<IDownloadTask_GetVersion_NeededFiles_Manipulations_Options>? options, string? optionName) {
        if (options == null) {
            return new List<JToken>();
        }

        var manipulations = options
            .FirstOrDefault(opt => opt.Name == optionName)
            ?.Manipulations
            .Select(manip => {
                var token = JToken.Parse(manip.GetRawText());
                if (token is JObject jObject) {
                    var type = jObject["Type"];
                    jObject.Remove("Type");
                    jObject.AddFirst(new JProperty("Type", type));
                }

                return token;
            })
            .ToList();

        return manipulations ?? new List<JToken>();
    }

    private void AddMod(IDownloadTask_GetVersion info) {
        this.State = State.AddingMod;
        this.SetStateData(0, 1);

        this.Plugin.Framework.RunOnFrameworkThread(() => {
            string? oldPath = null;
            if (this._oldModName != null) {
                oldPath = this.Plugin.Penumbra.GetModPath(this._oldModName);
                if (oldPath != null && this.Plugin.Config.ReplaceSortName) {
                    var parts = oldPath.Split('/');
                    parts[^1] = this.GenerateModName(info);
                    oldPath = string.Join('/', parts);
                }

                this.Plugin.Penumbra.DeleteMod(this._oldModName);
            }

            var modPath = Path.GetFileName(this.PenumbraModPath!);
            if (this.Plugin.Penumbra.AddMod(modPath)) {
                // put mod in folder
                if (oldPath == null && !string.IsNullOrWhiteSpace(this.Plugin.Config.PenumbraFolder)) {
                    var modName = this.GenerateModName(info);
                    this.Plugin.Penumbra.SetModPath(modPath, $"{this.Plugin.Config.PenumbraFolder}/{modName}");
                } else if (oldPath != null) {
                    this.Plugin.Penumbra.SetModPath(modPath, oldPath);
                }

                if (this._oldModName != null) {
                    this.Plugin.Penumbra.CopyModSettings(this._oldModName, modPath);
                }

                if (this.PenumbraCollection != null) {
                    this.Plugin.Penumbra.TrySetMod(this.PenumbraCollection, modPath, true);
                }

                this.StateData += 1;
            } else {
                throw new Exception("could not add mod to Penumbra");
            }
        });
    }

    [Serializable]
    [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
    private struct DownloadOptions {
        public Dictionary<string, List<string>> Options;

        internal DownloadOptions(Dictionary<string, List<string>> options) {
            this.Options = options;
        }
    }

    [Serializable]
    [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
    private struct FullDownloadOptions {
        public bool Full;
    }
}

internal enum State {
    NotStarted,
    DownloadingPackageInfo,
    DownloadingFiles,
    ConstructingModPack,
    AddingMod,
    RemovingOldFiles,
    Finished,
    Errored,
}

internal static class StateExt {
    internal static string Name(this State state) {
        return state switch {
            State.NotStarted => "Not started",
            State.DownloadingPackageInfo => "Downloading package info",
            State.DownloadingFiles => "Downloading files",
            State.ConstructingModPack => "Constructing mod pack",
            State.AddingMod => "Adding mod",
            State.RemovingOldFiles => "Removing old files",
            State.Finished => "Finished",
            State.Errored => "Errored",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        };
    }
}

internal class LimitedStream : Stream {
    private readonly Stream _inner;
    private readonly int _maxRead;

    private int _read;

    internal int ReadAmount => this._read;

    internal LimitedStream(Stream inner, int maxRead) {
        this._inner = inner;
        this._maxRead = maxRead;
    }

    public override void Flush() {
        this._inner.Flush();
    }

    public void ReadToEnd(byte[] buffer) {
        while (this._read < this._maxRead) {
            var leftToRead = Math.Min(
                buffer.Length,
                this._maxRead - this._read
            );

            _ = this.Read(buffer, 0, leftToRead);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) {
        if (this._read >= this._maxRead) {
            return 0;
        }

        if (count + this._read > this._maxRead) {
            count = this._maxRead - this._read;
        }

        var read = this._inner.Read(buffer, offset, count);
        this._read += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) {
        return this._inner.Seek(offset, origin);
    }

    public override void SetLength(long value) {
        this._inner.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count) {
        this._inner.Write(buffer, offset, count);
    }

    public override bool CanRead => this._inner.CanRead;
    public override bool CanSeek => this._inner.CanSeek;
    public override bool CanWrite => this._inner.CanWrite;
    public override long Length => this._inner.Length;

    public override long Position {
        get => this._inner.Position;
        set => this._inner.Position = value;
    }
}
