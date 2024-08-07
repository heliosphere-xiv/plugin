using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Blake3;
using Dalamud.Interface.ImGuiNotification;
using DequeNet;
using gfoidl.Base64;
using Heliosphere.Exceptions;
using Heliosphere.Model;
using Heliosphere.Model.Api;
using Heliosphere.Model.Generated;
using Heliosphere.Model.Penumbra;
using Heliosphere.Ui;
using Heliosphere.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Penumbra.Api.Enums;
using StrawberryShake;
using ZstdSharp;

namespace Heliosphere;

internal class DownloadTask : IDisposable {
    #if LOCAL
    internal const string ApiBase = "http://192.168.174.246:42011";
    #else
    internal const string ApiBase = "https://heliosphere.app/api";
    #endif

    internal Guid TaskId { get; } = Guid.NewGuid();
    internal required Plugin Plugin { get; init; }
    internal required string ModDirectory { get; init; }
    internal required Guid PackageId { get; init; }
    internal required Guid VariantId { get; init; }
    internal required Guid VersionId { get; init; }
    internal required Dictionary<string, List<string>> Options { get; init; }
    internal required bool Full { get; init; }
    internal required string? DownloadKey { get; init; }
    internal required bool IncludeTags { get; init; }
    internal required bool OpenInPenumbra { get; init; }
    internal required Guid? PenumbraCollection { get; init; }
    internal required IActiveNotification? Notification { get; set; }

    private string? PenumbraModPath { get; set; }
    internal string? PackageName { get; private set; }
    internal string? VariantName { get; private set; }

    internal CancellationTokenSource CancellationToken { get; } = new();
    internal State State { get; private set; } = State.NotStarted;
    internal uint StateData { get; private set; }
    internal uint StateDataMax { get; private set; }
    internal Exception? Error { get; private set; }
    private ConcurrentDeque<Measurement> Entries { get; } = new();
    private Util.SentryTransaction? Transaction { get; set; }

    private const double Window = 5;

    internal double BytesPerSecond {
        get {
            if (this.Entries.Count == 0) {
                return 0;
            }

            var total = 0u;
            var removeTo = 0;
            foreach (var entry in this.Entries) {
                if (Stopwatch.GetElapsedTime(entry.Ticks) > TimeSpan.FromSeconds(Window)) {
                    removeTo += 1;
                    continue;
                }

                total += entry.Data;
            }

            for (var i = 0; i < removeTo; i++) {
                this.Entries.TryPopLeft(out _);
            }

            return total / Window;
        }
    }

    private bool _disposed;

    /// This is non-null when a directory exists in the Penumbra directory that
    /// starts with hs- and ends with the variant/package IDs, and it does not
    /// equal the expected mod installation path. Essentially only true for
    /// version updates (not reinstalls).
    private string? _oldModName;

    private bool _reinstall;

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
        using var setNull = new OnDispose(() => this.Transaction = null);
        using var transaction = SentryHelper.StartTransaction(
            this.GetType().Name,
            nameof(this.Run)
        );
        this.Transaction = transaction;

        this.Transaction?.Inner.SetExtras(new Dictionary<string, object?> {
            [nameof(this.VersionId)] = this.VersionId.ToCrockford(),
            [nameof(this.Options)] = this.Options,
            [nameof(this.Full)] = this.Full,
            ["HasDownloadKey"] = this.DownloadKey != null,
            [nameof(this.IncludeTags)] = this.IncludeTags,
        });

        SentrySdk.AddBreadcrumb("Started download", "user", data: new Dictionary<string, string> {
            [nameof(this.VersionId)] = this.VersionId.ToCrockford(),
            [nameof(this.PenumbraModPath)] = this.PenumbraModPath ?? "<null>",
            [nameof(this.PenumbraCollection)] = this.PenumbraCollection?.ToString("N") ?? "<null>",
        });

        try {
            var info = await this.GetPackageInfo();
            if (this.Full) {
                foreach (var group in GroupsUtil.Convert(info.Groups)) {
                    this.Options[group.Name] = [];

                    foreach (var option in group.Options) {
                        this.Options[group.Name].Add(option.Name);
                    }
                }
            }

            this.Transaction?.Inner.SetExtra("Package", info.Variant.Package.Id.ToCrockford());
            this.Transaction?.Inner.SetExtra("Variant", info.Variant.Id.ToCrockford());

            this.PackageName = info.Variant.Package.Name;
            this.VariantName = info.Variant.Name;
            await this.DownloadFiles(info);
            await this.ConstructModPack(info);
            await this.AddMod(info);
            this.RemoveOldFiles(info);

            // before setting state to finished, set the directory name

            this.State = State.Finished;
            this.StateData = this.StateDataMax = 1;

            if (!this.Plugin.Config.UseNotificationProgress) {
                this.Notification = this.Notification.AddOrUpdate(
                    this.Plugin.NotificationManager,
                    type: NotificationType.Success,
                    title: "Install successful",
                    content: $"{this.PackageName} was installed in Penumbra.",
                    autoDuration: true
                );
                this.Notification.Click += async _ => await this.OpenModInPenumbra();
            }

            SentrySdk.AddBreadcrumb("Finished download", data: new Dictionary<string, string> {
                [nameof(this.VersionId)] = this.VersionId.ToCrockford(),
            });

            if (this.OpenInPenumbra) {
                await this.OpenModInPenumbra();
            }

            // refresh the manager package list after install finishes
            using (this.Transaction?.StartChild(nameof(this.Plugin.State.UpdatePackages))) {
                await this.Plugin.State.UpdatePackages();
            }
        } catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException) {
            this.State = State.Cancelled;
            this.StateData = 0;
            this.StateDataMax = 0;

            if (this.Transaction?.Inner is { } inner) {
                inner.Status = SpanStatus.Cancelled;
            }
        } catch (Exception ex) {
            this.State = State.Errored;
            this.StateData = 0;
            this.StateDataMax = 0;
            this.Error = ex;
            this.Notification = this.Notification.AddOrUpdate(
                this.Plugin.NotificationManager,
                type: NotificationType.Error,
                title: "Install failed",
                content: $"Failed to install {this.PackageName ?? "mod"}.",
                autoDuration: true
            );

            if (this.Transaction?.Inner is { } inner) {
                inner.Status = SpanStatus.InternalError;
            }

            // probably antivirus (ioexception is being used by other process or
            // access denied)
            if (ex.IsAntiVirus()) {
                this.Plugin.PluginUi.OpenAntiVirusWarning();
                Plugin.Log.Warning(ex, $"[AV] Error downloading version {this.VersionId}");

                this.Transaction?.Inner?.SetExtra("WasAntivirus", true);
            } else {
                this.Transaction?.Inner?.SetExtra("WasAntivirus", false);
                ErrorHelper.Handle(ex, $"Error downloading version {this.VersionId}", this.Transaction?.LatestChild()?.Inner ?? this.Transaction?.Inner);
            }
        }
    }

    private void SetStateData(uint current, uint max) {
        this.StateData = current;
        this.StateDataMax = max;
    }

    internal static async Task<HttpResponseMessage> GetImage(Guid id, int imageId, CancellationToken token = default) {
        var resp = await Plugin.Client.GetAsync2($"{ApiBase}/web/package/{id:N}/image/{imageId}", HttpCompletionOption.ResponseHeadersRead, token);
        resp.EnsureSuccessStatusCode();
        return resp;
    }

    private async Task<IDownloadTask_GetVersion> GetPackageInfo() {
        using var span = this.Transaction?.StartChild(nameof(this.GetPackageInfo));

        this.State = State.DownloadingPackageInfo;
        this.SetStateData(0, 1);

        var downloadKind = DownloadKind.Install;
        var installed = await Plugin.State.GetInstalled(this.CancellationToken.Token);
        if (installed.TryGetValue(this.PackageId, out var pkg)) {
            if (pkg.Variants.Any(variant => variant.Id == this.VariantId)) {
                downloadKind = DownloadKind.Update;
            }
        }

        var resp = await Plugin.GraphQl.DownloadTask.ExecuteAsync(this.VersionId, this.Options, this.DownloadKey, this.Full, downloadKind, this.CancellationToken.Token);
        resp.EnsureNoErrors();

        var version = resp.Data?.GetVersion ?? throw new MissingVersionException(this.VersionId);

        if (this.DownloadKey != null) {
            this.Plugin.DownloadCodes.TryInsert(version.Variant.Package.Id, this.DownloadKey);
            this.Plugin.DownloadCodes.Save();
        }

        this.StateData += 1;
        return version;
    }

    private async Task DownloadFiles(IDownloadTask_GetVersion info) {
        using var span = this.Transaction?.StartChild(nameof(this.DownloadFiles));

        var directories = Directory.EnumerateDirectories(this.ModDirectory)
            .Select(Path.GetFileName)
            .Where(path => !string.IsNullOrEmpty(path))
            .Cast<string>()
            .Where(path =>
                HeliosphereMeta.ParseDirectory(path) is { PackageId: var packageId, VariantId: var variantId }
                    && packageId == info.Variant.Package.Id
                    && variantId == info.Variant.Id
            )
            .ToArray();

        var dirName = HeliosphereMeta.ModDirectoryName(info.Variant.Package.Id, info.Variant.Package.Name, info.Version, info.Variant.Id);
        this.PenumbraModPath = Path.Join(this.ModDirectory, dirName);
        if (directories.Length == 1) {
            var oldName = Path.Join(this.ModDirectory, directories[0]!);
            if (oldName == this.PenumbraModPath) {
                // the path found is what we expect it to be, so this is not a
                // version change but a reinstall
                this._reinstall = true;
            } else {
                // the path found is not what we expect it to be, so the version
                // has changed. rename the directory to the new version
                this._oldModName = directories[0];
                Directory.Move(oldName, this.PenumbraModPath);
            }
        } else if (directories.Length > 1) {
            Plugin.Log.Warning($"multiple heliosphere mod directories found for {info.Variant.Package.Name} - not attempting a rename");
        }

        var filesPath = Path.Join(this.PenumbraModPath, "files");
        if (!await PathHelper.CreateDirectory(filesPath)) {
            throw new DirectoryNotFoundException($"Directory '{filesPath}' could not be found after waiting");
        }

        var tasks = info.Batched
            ? await this.DownloadBatchedFiles(info.NeededFiles, info.Batches, filesPath)
            : this.DownloadNormalFiles(info.NeededFiles, filesPath);
        await Task.WhenAll(tasks);
    }

    private IEnumerable<Task> DownloadNormalFiles(IDownloadTask_GetVersion_NeededFiles neededFiles, string filesPath) {
        using var span = this.Transaction?.StartChild(nameof(this.DownloadNormalFiles));

        this.State = State.DownloadingFiles;
        this.SetStateData(0, (uint) neededFiles.Files.Files.Count);

        return neededFiles.Files.Files
            .Select(pair => Task.Run(async () => {
                var (hash, files) = pair;
                GetExtensionsAndDiscriminators(files, hash, out var extensions, out var discriminators, out var allUi);

                using (await SemaphoreGuard.WaitAsync(Plugin.DownloadSemaphore, this.CancellationToken.Token)) {
                    await this.DownloadFile(new Uri(neededFiles.BaseUri), filesPath, extensions, allUi, discriminators, hash);
                }
            }));
    }

    private async Task<IEnumerable<Task>> DownloadBatchedFiles(IDownloadTask_GetVersion_NeededFiles neededFiles, BatchList batches, string filesPath) {
        using var span = this.Transaction?.StartChild(nameof(this.DownloadBatchedFiles));

        var neededHashes = neededFiles.Files.Files.Keys.ToList();
        var clonedBatches = batches.Files.ToDictionary(pair => pair.Key, pair => pair.Value.ToDictionary(pair => pair.Key, pair => pair.Value));
        var seenHashes = new List<string>();
        foreach (var (batch, files) in batches.Files) {
            // remove any hashes that aren't needed
            foreach (var hash in files.Keys) {
                if (neededHashes.Contains(hash) && !seenHashes.Contains(hash)) {
                    seenHashes.Add(hash);
                } else {
                    clonedBatches[batch].Remove(hash);
                }
            }

            // remove any empty batches
            if (clonedBatches[batch].Count == 0) {
                clonedBatches.Remove(batch);
            }
        }

        this.State = State.CheckingExistingFiles;
        this.StateData = this.StateDataMax = 0;

        // get all pre-existing files and validate them, storing which file path
        // is associated with each hash
        var existingFiles = Directory.EnumerateFiles(filesPath)
            .Select(path => (Hash: PathHelper.GetBaseName(Path.GetFileName(path)), path))
            .ToList();
        // map of hash => path
        var installedHashes = new ConcurrentDictionary<string, string>();

        this.StateDataMax = (uint) existingFiles.Count;

        var tasks = existingFiles.Select(pair => Task.Run(async () => {
            var (hash, path) = pair;
            using var blake3 = new Blake3HashAlgorithm();

            this.StateData += 1;

            if (installedHashes.ContainsKey(hash)) {
                return;
            }

            blake3.Initialize();
            await using var file = FileHelper.OpenSharedReadIfExists(path);
            if (file == null) {
                return;
            }

            var computed = await blake3.ComputeHashAsync(file, this.CancellationToken.Token);
            if (Base64.Url.Encode(computed) != hash) {
                return;
            }

            installedHashes.TryAdd(hash, path);
        }));

        await Task.WhenAll(tasks);

        this.State = State.DownloadingFiles;
        this.SetStateData(0, (uint) neededFiles.Files.Files.Count);

        return clonedBatches.Select(pair => Task.Run(async () => {
            var (batch, batchedFiles) = pair;

            // determine which pre-existing files to duplicate in this batch
            var toDuplicate = new List<string>();
            foreach (var (hash, path) in installedHashes) {
                if (!batchedFiles.ContainsKey(hash)) {
                    continue;
                }

                toDuplicate.Add(path);
            }

            // sort files in batch by offset, removing already-downloaded files
            var listOfFiles = batchedFiles
                .Select(pair => (Hash: pair.Key, Info: pair.Value))
                .Where(pair => !installedHashes.ContainsKey(pair.Hash))
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
                    chunk = [];

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

                // construct the uri
                var baseUri = new Uri(new Uri(neededFiles.BaseUri), "../batches/");
                var uri = new Uri(baseUri, batch);

                using (await SemaphoreGuard.WaitAsync(Plugin.DownloadSemaphore, this.CancellationToken.Token)) {
                    var counter = new StateCounter();
                    await Plugin.Resilience.ExecuteAsync(
                        async _ => {
                            // if we're retrying, remove the files that this task added
                            this.StateData -= counter.Added;
                            counter.Added = 0;

                            await this.DownloadBatchedFile(neededFiles, filesPath, uri, rangeHeader, chunks, batchedFiles, counter);
                        },
                        this.CancellationToken.Token
                    );
                }
            }

            foreach (var path in toDuplicate) {
                if (!File.Exists(path)) {
                    Plugin.Log.Warning($"{path} was supposed to be duplicated but no longer exists");
                    continue;
                }

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
                    // remove leading period
                    discrimMaybe = discrimMaybe[1..];
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
        Uri uri,
        RangeHeaderValue? rangeHeader,
        IReadOnlyList<List<string>> chunks,
        IReadOnlyDictionary<string, BatchedFile> batchedFiles,
        StateCounter counter
    ) {
        using var span = this.Transaction?.StartChild(nameof(this.DownloadBatchedFile), true);
        span?.Inner.SetExtras(new Dictionary<string, object?> {
            [nameof(uri)] = uri,
            [nameof(rangeHeader)] = rangeHeader,
            [nameof(chunks)] = chunks,
            [nameof(batchedFiles)] = batchedFiles,
        });

        // construct the request
        using var req = new HttpRequestMessage(HttpMethod.Get, uri) {
            Headers = {
                Range = rangeHeader,
            },
        };

        // send the request
        using var resp = await Plugin.Client.SendAsync2(req, HttpCompletionOption.ResponseHeadersRead, this.CancellationToken.Token);
        resp.EnsureSuccessStatusCode();

        // if only one chunk is requested, it's not multipart, so check
        // for that
        IMultipartProvider multipart;
        if (resp.Content.IsMimeMultipartContent()) {
            var boundary = resp.Content.Headers.ContentType
                ?.Parameters
                .Find(p => p.Name == "boundary")
                ?.Value;
            if (boundary == null) {
                throw new Exception("missing boundary in multipart response");
            }

            multipart = new StandardMultipartProvider(boundary, resp.Content);
        } else {
            multipart = new SingleMultipartProvider(resp.Content);
        }

        using var disposeMultipart = new OnDispose(multipart.Dispose);

        foreach (var chunk in chunks) {
            await using var rawStream = await multipart.GetNextStreamAsync(this.CancellationToken.Token);
            if (rawStream == null) {
                throw new Exception("did not download correct number of chunks");
            }

            await using var stream = new GloballyThrottledStream(
                rawStream,
                this.Entries
            );

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
                await using var file = FileHelper.Create(path);
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
                // ReSharper disable once DisposeOnUsingVariable
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
            Plugin.Log.Warning($"{hash} has no extension");
            extensions.Add(".unk");
        }
    }

    private static async Task DuplicateFile(IEnumerable<string> extensions, IList<string> discriminators, bool allUi, string path) {
        foreach (var ext in extensions) {
            // duplicate the file for each ui path discriminator
            foreach (var discriminator in discriminators) {
                await DuplicateInner(PathHelper.ChangeExtension(path, $"{discriminator}{ext}"));
            }

            // only create non-discriminated files if necessary
            if (allUi) {
                continue;
            }

            // duplicate the file for each other extension it has
            await DuplicateInner(PathHelper.ChangeExtension(path, ext));

            continue;

            async Task DuplicateInner(string dest) {
                if (path == dest) {
                    return;
                }

                if (!await PathHelper.WaitForDelete(dest)) {
                    throw new DeleteFileException(dest);
                }

                File.Copy(path, dest);
            }
        }
    }

    private void RemoveOldFiles(IDownloadTask_GetVersion info) {
        using var span = this.Transaction?.StartChild(nameof(this.RemoveOldFiles));

        this.State = State.RemovingOldFiles;
        this.SetStateData(0, 1);

        // find old, normal files no longer being used to remove
        var filesPath = Path.Join(this.PenumbraModPath, "files");

        var neededHashes = info.NeededFiles.Files.Files.Keys.ToHashSet();
        var presentFiles = Directory.EnumerateFiles(filesPath)
            .Select(Path.GetFileName)
            .Where(path => !string.IsNullOrEmpty(path))
            .Cast<string>()
            .ToHashSet();
        var presentHashes = presentFiles
            .GroupBy(PathHelper.GetBaseName)
            .ToDictionary(group => group.Key, group => group.ToHashSet());
        var present = presentHashes.Keys.ToHashSet();
        present.ExceptWith(neededHashes);

        // find old, discriminated files no longer being used to remove
        var neededDiscriminated = new HashSet<string>();
        foreach (var (hash, files) in info.NeededFiles.Files.Files) {
            foreach (var file in files) {
                if (!file[2]!.StartsWith("ui/")) {
                    continue;
                }

                var discriminator = HashHelper.GetDiscriminator(file);
                neededDiscriminated.Add($"{hash}.{discriminator}");
            }
        }

        var presentDiscriminated = presentFiles
            .Where(path => path.Count(c => c == '.') == 2)
            .GroupBy(path => Path.ChangeExtension(path, null))
            .ToDictionary(group => group.Key, group => group.ToHashSet());
        var presentD = presentDiscriminated.Keys.ToHashSet();
        presentD.ExceptWith(neededDiscriminated);

        var total = presentHashes.Values
            .Concat(presentDiscriminated.Values)
            .Select(set => (uint) set.Count)
            .Aggregate(0u, (agg, val) => agg + val);
        this.SetStateData(0, total);

        var done = 0u;
        RemoveExtra(present, presentHashes);
        RemoveExtra(presentD, presentDiscriminated);

        return;

        void RemoveExtra(HashSet<string> present, IReadOnlyDictionary<string, HashSet<string>> hashes) {
            foreach (var extra in present) {
                foreach (var file in hashes[extra]) {
                    var extraPath = Path.Join(filesPath, file);
                    Plugin.Log.Info($"removing extra file {extraPath}");
                    Plugin.Resilience.Execute(() => FileHelper.Delete(extraPath));

                    done += 1;
                    this.SetStateData(done, total);
                }
            }
        }
    }

    private async Task DownloadFile(Uri baseUri, string filesPath, IList<string> extensions, bool allUi, IList<string> discriminators, string hash) {
        using var span = this.Transaction?.StartChild(nameof(this.DownloadFile), true);
        span?.Inner.SetExtras(new Dictionary<string, object?> {
            [nameof(hash)] = hash,
            [nameof(extensions)] = extensions,
            [nameof(allUi)] = allUi,
            [nameof(discriminators)] = discriminators,
        });

        // check if at least one expected file is valid
        string? validPath = null;
        foreach (var ext in extensions) {
            foreach (var discriminator in discriminators) {
                var check = Path.ChangeExtension(Path.Join(filesPath, hash), $"{discriminator}{ext}");
                if (!await CheckHash(check, hash)) {
                    continue;
                }

                validPath = check;
                break;
            }

            // not all ui, so check for undisciminated file
            // ReSharper disable once InvertIf
            if (!allUi) {
                var check = Path.ChangeExtension(Path.Join(filesPath, hash), ext);
                if (!await CheckHash(check, hash)) {
                    continue;
                }

                validPath = check;
            }

            // can't break outer lopp from inner discrim loop, so do it here
            // instead
            if (validPath != null) {
                goto Duplicate;
            }
        }

        // no valid, existing file, so download instead
        var path = allUi
            ? Path.ChangeExtension(Path.Join(filesPath, hash), $"{discriminators[0]}{extensions[0]}")
            : Path.ChangeExtension(Path.Join(filesPath, hash), extensions[0]);
        validPath = path;

        await Plugin.Resilience.ExecuteAsync(
            async _ => {
                var uri = new Uri(baseUri, hash).ToString();
                using var resp = await Plugin.Client.GetAsync2(uri, HttpCompletionOption.ResponseHeadersRead, this.CancellationToken.Token);
                resp.EnsureSuccessStatusCode();

                await using var file = FileHelper.Create(path);
                await using var stream = new GloballyThrottledStream(
                    await resp.Content.ReadAsStreamAsync(this.CancellationToken.Token),
                    this.Entries
                );
                await using var decompress = new DecompressionStream(stream);
                await decompress.CopyToAsync(file, this.CancellationToken.Token);

                span?.Inner.SetMeasurement("Decompressed", file.Position, MeasurementUnit.Information.Byte);
            },
            this.CancellationToken.Token
        );

        Duplicate:
        await DuplicateFile(extensions, discriminators, allUi, validPath);

        this.StateData += 1;
        return;

        async Task<bool> CheckHash(string path, string expected) {
            if (!File.Exists(path)) {
                return false;
            }

            var time = 0;
            return await Plugin.Resilience.ExecuteAsync(
                async _ => {
                    time += 1;
                    // make sure checksum matches
                    using var blake3 = new Blake3HashAlgorithm();
                    blake3.Initialize();
                    await using var file = FileHelper.OpenSharedReadIfExists(path);
                    if (file == null) {
                        // if the file couldn't be found, retry by throwing
                        // exception for the first two tries
                        if (time < 3) {
                            throw new Exception("couldn't open file, retry");
                        }

                        // otherwise just give up and redownload it
                        return false;
                    }

                    var computed = await blake3.ComputeHashAsync(file, this.CancellationToken.Token);
                    // if the hash matches, don't redownload, just duplicate the
                    // file as necessary
                    return Base64.Url.Encode(computed) == expected;
                },
                this.CancellationToken.Token
            );
        }
    }

    private async Task ConstructModPack(IDownloadTask_GetVersion info) {
        using var span = this.Transaction?.StartChild(nameof(this.ConstructModPack));

        this.State = State.ConstructingModPack;
        this.SetStateData(0, 4);
        var hsMeta = await this.ConstructHeliosphereMeta(info);
        await this.ConstructMeta(info, hsMeta);
        await this.ConstructDefaultMod(info);
        await this.ConstructGroups(info);
    }

    private string GenerateModName(IDownloadTask_GetVersion info) {
        var pkgName = info.Variant.Package.Name.Replace('/', '-');
        var name = $"{this.Plugin.Config.TitlePrefix}{pkgName}";

        if (!this.Plugin.Config.HideDefaultVariant || info.Variant.Name != Consts.DefaultVariant) {
            var varName = info.Variant.Name.Replace('/', '-');
            name += $" ({varName})";
        }

        return name;
    }

    private async Task ConstructMeta(IDownloadTask_GetVersion info, HeliosphereMeta hsMeta) {
        using var span = this.Transaction?.StartChild(nameof(this.ConstructMeta));

        var tags = this.IncludeTags
            ? info.Variant.Package.Tags.Select(tag => tag.Slug).ToList()
            : [];

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
        await using var file = FileHelper.Create(path);
        await file.WriteAsync(Encoding.UTF8.GetBytes(json), this.CancellationToken.Token);
        this.State += 1;
    }

    private async Task<HeliosphereMeta> ConstructHeliosphereMeta(IDownloadTask_GetVersion info) {
        using var span = this.Transaction?.StartChild(nameof(this.ConstructHeliosphereMeta));

        var selectedAll = true;
        foreach (var group in GroupsUtil.Convert(info.Groups)) {
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
            VersionId = this.VersionId,
            FullInstall = selectedAll,
            IncludeTags = this.IncludeTags,
            SelectedOptions = this.Options,
            ModHash = info.NeededFiles.ModHash,
        };

        var metaJson = JsonConvert.SerializeObject(meta, Formatting.Indented);
        var path = Path.Join(this.PenumbraModPath, "heliosphere.json");
        await using var file = FileHelper.Create(path);
        await file.WriteAsync(Encoding.UTF8.GetBytes(metaJson), this.CancellationToken.Token);

        // save cover image
        if (info.Variant.Package.Images.Count > 0) {
            var coverImage = info.Variant.Package.Images[0];
            var coverPath = Path.Join(this.PenumbraModPath, "cover.jpg");

            try {
                using var image = await GetImage(info.Variant.Package.Id, coverImage.Id, this.CancellationToken.Token);
                await using var cover = FileHelper.Create(coverPath);
                await image.Content.CopyToAsync(cover, this.CancellationToken.Token);
            } catch (Exception ex) {
                ErrorHelper.Handle(ex, "Could not download cover image");
            }
        }

        this.State += 1;

        return meta;
    }

    private async Task ConstructDefaultMod(IDownloadTask_GetVersion info) {
        using var span = this.Transaction?.StartChild(nameof(this.ConstructDefaultMod));

        var defaultMod = new DefaultMod {
            Manipulations = ManipTokensForOption(info.NeededFiles.Manipulations.FirstOrDefault(group => group.Name == null)?.Options, null),
            FileSwaps = info.DefaultOption?.FileSwaps.Swaps ?? [],
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
        await using var output = FileHelper.Create(path);
        await output.WriteAsync(Encoding.UTF8.GetBytes(json), this.CancellationToken.Token);
        this.StateData += 1;
    }

    private async Task ConstructGroups(IDownloadTask_GetVersion info) {
        using var span = this.Transaction?.StartChild(nameof(this.ConstructGroups));

        // remove any groups that already exist
        var existingGroups = Directory.EnumerateFiles(this.PenumbraModPath!)
            .Where(file => {
                var name = Path.GetFileName(file);
                return name.StartsWith("group_") && name.EndsWith(".json");
            });

        var oldGroups = new List<ModGroup>();
        foreach (var existing in existingGroups) {
            try {
                var text = await FileHelper.ReadAllTextAsync(existing);
                ModGroup? group;
                try {
                    group = JsonConvert.DeserializeObject<StandardModGroup>(text);
                } catch {
                    group = JsonConvert.DeserializeObject<ImcModGroup>(text);
                }

                if (group == null) {
                    Plugin.Log.Warning("Could not deserialise old group (was null)");
                    continue;
                }

                oldGroups.Add(group);
            } catch (Exception ex) {
                Plugin.Log.Warning(ex, "Could not deserialise old group");
            }

            FileHelper.Delete(existing);
        }

        var rawGroups = GroupsUtil.Convert(info.Groups).ToList();
        var modGroups = new Dictionary<string, ModGroup>(rawGroups.Count);
        foreach (var group in rawGroups) {
            ModGroup modGroup;
            switch (group) {
                case StandardGroup { Inner: var inner }: {
                    var standard = new StandardModGroup(group.Name, group.Description, group.GroupType.ToString()) {
                        Priority = group.Priority,
                        DefaultSettings = unchecked((uint) group.DefaultSettings),
                        OriginalIndex = (group.OriginalIndex, 0),
                    };
                    var groupManips = info.NeededFiles.Manipulations.FirstOrDefault(manips => manips.Name == group.Name);

                    foreach (var option in inner.Options) {
                        var manipulations = ManipTokensForOption(groupManips?.Options, option.Name);
                        standard.Options.Add(new OptionItem {
                            Name = option.Name,
                            Description = option.Description,
                            Priority = option.Priority,
                            Manipulations = manipulations,
                            FileSwaps = option.FileSwaps.Swaps,
                            IsDefault = option.IsDefault,
                        });
                    }

                    modGroup = standard;

                    break;
                }
                case ImcGroup { Inner: var inner }: {
                    var identifier = JToken.Parse(inner.Identifier.GetRawText());
                    var defaultEntry = JToken.Parse(inner.DefaultEntry.GetRawText());
                    var imc = new ImcModGroup(group.Name, group.Description, identifier, inner.AllVariants, defaultEntry) {
                        Priority = group.Priority,
                        DefaultSettings = unchecked((uint) group.DefaultSettings),
                        OriginalIndex = (group.OriginalIndex, 0),
                    };

                    foreach (var option in inner.Options) {
                        imc.Options.Add(new ImcOption {
                            Name = option.Name,
                            Description = option.Description,
                            IsDisableSubMod = option.IsDisableSubMod,
                            AttributeMask = option.AttributeMask,
                        });
                    }

                    modGroup = imc;
                    break;
                }
                default:
                    throw new Exception("unknown mod group type");
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
                if (modGroup is not StandardModGroup standard) {
                    // only standard groups handle files
                    continue;
                }

                var option = standard.Options.FirstOrDefault(opt => opt.Name == optionName);
                // this shouldn't be possible?
                if (option == null) {
                    var opt = new OptionItem {
                        Name = optionName,
                    };

                    standard.Options.Add(opt);
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
                        if (group is StandardModGroup standard) {
                            var enabled = group.DefaultSettings < standard.Options.Count
                                ? standard.Options[(int) group.DefaultSettings].Name
                                : null;

                            standard.Options.RemoveAll(opt => !selected.Contains(opt.Name));

                            var idx = standard.Options.FindIndex(mod => mod.Name == enabled);
                            group.DefaultSettings = idx == -1 ? 0 : (uint) idx;
                        }

                        break;
                    }
                    case "Multi": {
                        if (group is StandardModGroup standard) {
                            var enabled = new Dictionary<string, bool>();
                            for (var i = 0; i < standard.Options.Count; i++) {
                                var option = standard.Options[i];
                                enabled[option.Name] = (standard.DefaultSettings & (1 << i)) > 0;
                            }

                            standard.Options.RemoveAll(opt => !selected.Contains(opt.Name));
                            group.DefaultSettings = 0;

                            for (var i = 0; i < standard.Options.Count; i++) {
                                var option = standard.Options[i];
                                if (enabled.TryGetValue(option.Name, out var wasEnabled) && wasEnabled) {
                                    group.DefaultSettings |= unchecked((uint) (1 << i));
                                }
                            }
                        }

                        break;
                    }
                    case "Imc": {
                        if (group is ImcModGroup imc) {
                            var enabled = group.DefaultSettings < imc.Options.Count
                                ? imc.Options[(int) group.DefaultSettings].Name
                                : null;

                            imc.Options.RemoveAll(opt => !selected.Contains(opt.Name));

                            var idx = imc.Options.FindIndex(mod => mod.Name == enabled);
                            group.DefaultSettings = idx == -1 ? 0 : (uint) idx;
                        }

                        break;
                    }
                }
            } else {
                group.DefaultSettings = 0;
                switch (group) {
                    case StandardModGroup { Options: var options }: {
                        options.Clear();
                        break;
                    }
                    case ImcModGroup { Options: var options }: {
                        options.Clear();
                        break;
                    }
                    default:
                        throw new Exception("unexpected group type");
                }
            }
        }

        // split groups that have more than 32 options
        var splitGroups = SplitGroups(modGroups.Values);

        var invalidChars = Path.GetInvalidFileNameChars();
        var list = splitGroups
            .OrderBy(group => group.OriginalIndex)
            .ToList();

        if (this.Plugin.Config.WarnAboutBreakingChanges && this._oldModName != null) {
            var settings = await this.Plugin.Framework.RunOnFrameworkThread(() => {
                var collections = this.Plugin.Penumbra.GetCollections();
                if (collections == null) {
                    return [];
                }

                var allSettings = new Dictionary<string, HashSet<string>>();
                foreach (var (collectionId, _) in collections) {
                    var gcms = this.Plugin.Penumbra.GetCurrentModSettings(collectionId, this._oldModName, false);
                    if (gcms == null) {
                        continue;
                    }

                    var (result, settings) = gcms.Value;
                    if (result != PenumbraApiEc.Success || settings == null) {
                        continue;
                    }

                    foreach (var (group, options) in settings.Value.EnabledOptions) {
                        if (!allSettings.ContainsKey(group)) {
                            allSettings.Add(group, []);
                        }

                        foreach (var option in options) {
                            allSettings[group].Add(option);
                        }
                    }
                }

                return allSettings;
            });

            var oldVersion = "???";
            var installedPkgs = await this.Plugin.State.GetInstalled();
            if (installedPkgs.TryGetValue(info.Variant.Package.Id, out var meta)) {
                var variant = meta.Variants.Find(v => v.VariantId == info.Variant.Id);
                if (variant != null) {
                    oldVersion = variant.Version;
                }
            }

            var change = new BreakingChange {
                ModName = info.Variant.Package.Name,
                VariantName = info.Variant.Name,
                OldVersion = oldVersion,
                NewVersion = info.Version,
                ModPath = HeliosphereMeta.ModDirectoryName(info.Variant.Package.Id, info.Variant.Package.Name, info.Version, info.Variant.Id),
            };

            foreach (var oldGroup in oldGroups) {
                if (!settings.TryGetValue(oldGroup.Name, out var currentOptions)) {
                    // no settings for this group, so breaking changes don't matter
                    continue;
                }

                if (currentOptions.Count == 0) {
                    // not using this group, so breaking changes don't matter
                    continue;
                }

                var newGroup = list.Find(g => g.Name == oldGroup.Name);
                if (newGroup == null) {
                    // an old group is missing, so penumbra won't have any settings
                    // saved anymore
                    change.RemovedGroups.Add(oldGroup.Name);
                    continue;
                }

                if (newGroup.Type != oldGroup.Type) {
                    change.ChangedType.Add(oldGroup.Name);
                    // the rest of these changes don't matter, since this is a
                    // large breaking change
                    continue;
                }

                var newOptions = (newGroup switch {
                    StandardModGroup { Options: var options } => options.Select(o => o.Name),
                    ImcModGroup { Options: var options } => options.Select(o => o.Name),
                    _ => throw new Exception("unexpected mod group type"),
                }).ToArray();
                var oldOptions = (oldGroup switch {
                    StandardModGroup { Options: var options } => options.Select(o => o.Name),
                    ImcModGroup { Options: var options } => options.Select(o => o.Name),
                    _ => throw new Exception("unexpected mod group type"),
                }).ToArray();
                if (newOptions.Length < oldOptions.Length) {
                    var missingOptions = oldOptions.Skip(newOptions.Length).ToArray();
                    if (missingOptions.Any(opt => currentOptions.Contains(opt))) {
                        change.TruncatedOptions.Add((oldGroup.Name, missingOptions));
                    }
                }

                var smallest = Math.Min(newOptions.Length, oldOptions.Length);

                var sameNames = oldOptions.Take(smallest).Order()
                    .SequenceEqual(newOptions.Take(smallest).Order());
                var sameOrder = oldOptions.Take(smallest)
                    .SequenceEqual(newOptions.Take(smallest));

                var addList = sameNames switch {
                    true when !sameOrder => change.ChangedOptionOrder,
                    false => change.DifferentOptionNames,
                    _ => null,
                };

                if (addList != null) {
                    for (var i = 0; i < smallest; i++) {
                        // skip any same options or non-enabled options
                        if (oldOptions[i] == newOptions[i] || !currentOptions.Contains(oldOptions[i])) {
                            continue;
                        }

                        addList.Add((oldGroup.Name, oldOptions, newOptions));
                        break;
                    }
                }
            }

            if (change.HasChanges) {
                using var handle = await this.Plugin.PluginUi.BreakingChangeWindow.BreakingChanges.WaitAsync(this.CancellationToken.Token);
                handle.Data.Add(change);
            }
        }

        for (var i = 0; i < list.Count; i++) {
            using var innerSpan = this.Transaction?.StartChild("SaveGroup");
            innerSpan?.Inner.SetExtras(new Dictionary<string, object?> {
                ["name"] = list[i].Name,
            });

            var slug = list[i].Name.ToLowerInvariant()
                .Select(c => invalidChars.Contains(c) ? '-' : c)
                .Aggregate(new StringBuilder(), (sb, c) => sb.Append(c))
                .ToString();
            var json = JsonConvert.SerializeObject(list[i], Formatting.Indented);
            var path = Path.Join(this.PenumbraModPath, $"group_{i + 1:000}_{slug}.json");
            await using var file = FileHelper.Create(path);
            await file.WriteAsync(Encoding.UTF8.GetBytes(json), this.CancellationToken.Token);
            this.StateData += 1;
        }
    }

    private static IEnumerable<ModGroup> SplitGroups(IEnumerable<ModGroup> groups) {
        return groups.SelectMany(SplitGroup);
    }

    private static IEnumerable<ModGroup> SplitGroup(ModGroup rawGroup) {
        const int perGroup = 32;

        if (rawGroup is not StandardModGroup group) {
            return [rawGroup];
        }

        if (group.Type != "Multi" || group.Options.Count <= perGroup) {
            return [group];
        }

        var newGroups = new List<StandardModGroup>();
        for (var i = 0; i < group.Options.Count; i++) {
            var option = group.Options[i];
            var groupIdx = i / perGroup;
            var optionIdx = i % perGroup;

            if (optionIdx == 0) {
                newGroups.Add(new StandardModGroup($"{group.Name}, Part {groupIdx + 1}", group.Description, group.Type) {
                    Priority = group.Priority,
                    OriginalIndex = (group.OriginalIndex.Item1, (uint) groupIdx + 1),
                });
            }

            var newGroup = newGroups[groupIdx];
            newGroup.Options.Add(option);
            if (option.IsDefault) {
                newGroup.DefaultSettings |= unchecked((uint) (1 << optionIdx));
            }
        }

        return newGroups;
    }

    private static List<JToken> ManipTokensForOption(IEnumerable<IDownloadTask_GetVersion_NeededFiles_Manipulations_Options>? options, string? optionName) {
        if (options == null) {
            return [];
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

        return manipulations ?? [];
    }

    private async Task AddMod(IDownloadTask_GetVersion info) {
        using var span = this.Transaction?.StartChild(nameof(this.AddMod));

        this.State = State.AddingMod;
        this.SetStateData(0, 1);

        await this.Plugin.Framework.RunOnFrameworkThread(() => {
            using var span = this.Transaction?.StartChild($"{nameof(this.AddMod)} - {nameof(this.Plugin.Framework.RunOnFrameworkThread)}");

            SentrySdk.AddBreadcrumb("Adding mod", data: new Dictionary<string, string> {
                ["_oldModName"] = this._oldModName ?? "<null>",
            });

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
            if (this.Plugin.Penumbra.AddMod(modPath) is { } result && result != PenumbraApiEc.Success) {
                throw new Exception($"could not add mod to Penumbra ({Enum.GetName(result)}): \"{modPath}\"");
            }

            if (this._reinstall) {
                this.Plugin.Penumbra.ReloadMod(modPath);
            }

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
                this.Plugin.Penumbra.TrySetMod(this.PenumbraCollection.Value, modPath, true);
            }

            this.StateData += 1;
        });
    }

    /// <summary>
    /// Open the mod in Penumbra if it was successfully installed. Runs on
    /// framework thread, so no need to call this from within
    /// <see cref="Dalamud.Plugin.Services.IFramework.RunOnFrameworkThread(Action)"/>.
    /// </summary>
    internal Task OpenModInPenumbra() {
        if (this.State != State.Finished || this.PenumbraModPath is not { } path) {
            return Task.CompletedTask;
        }

        return this.Plugin.Framework.RunOnFrameworkThread(() => {
            this.Plugin.Penumbra.OpenMod(Path.GetFileName(path));
        });
    }

    internal string? GetErrorInformation() {
        if (this.Error is not { } error) {
            return null;
        }

        var sb = new StringBuilder();
        sb.Append("```\n");
        var i = 0;
        foreach (var ex in error.AsEnumerable()) {
            if (i != 0) {
                sb.Append('\n');
            }

            i += 1;

            sb.Append($"Error type: {ex.GetType().FullName}\n");
            sb.Append($"   Message: {ex.Message}\n");
            sb.Append($"   HResult: 0x{unchecked((uint) ex.HResult):X8}\n");
            if (ex.StackTrace is { } trace) {
                sb.Append(trace);
                sb.Append('\n');
            }
        }

        sb.Append("```");

        return sb.ToString();
    }

    internal struct Measurement {
        internal long Ticks;
        internal uint Data;
    }
}

internal enum State {
    NotStarted,
    DownloadingPackageInfo,
    CheckingExistingFiles,
    DownloadingFiles,
    ConstructingModPack,
    AddingMod,
    RemovingOldFiles,
    Finished,
    Errored,
    Cancelled,
}

internal static class StateExt {
    internal static string Name(this State state) {
        return state switch {
            State.NotStarted => "Not started",
            State.DownloadingPackageInfo => "Downloading package info",
            State.CheckingExistingFiles => "Checking existing files",
            State.DownloadingFiles => "Downloading files",
            State.ConstructingModPack => "Constructing mod pack",
            State.AddingMod => "Adding mod",
            State.RemovingOldFiles => "Removing old files",
            State.Finished => "Finished",
            State.Errored => "Errored",
            State.Cancelled => "Cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        };
    }

    internal static bool IsDone(this State state) {
        return state switch {
            State.Finished => true,
            State.Errored => true,
            State.Cancelled => true,
            _ => false,
        };
    }

    internal static Stream GetIconStream(this State state) {
        return state switch {
            State.NotStarted => Resourcer.Resource.AsStream("Heliosphere.Resources.clock.png"),
            State.DownloadingPackageInfo => Resourcer.Resource.AsStream("Heliosphere.Resources.magnifying-glass.png"),
            State.CheckingExistingFiles => Resourcer.Resource.AsStream("Heliosphere.Resources.hard-drives.png"),
            State.DownloadingFiles => Resourcer.Resource.AsStream("Heliosphere.Resources.cloud-arrow-down.png"),
            State.ConstructingModPack => Resourcer.Resource.AsStream("Heliosphere.Resources.package.png"),
            State.AddingMod => Resourcer.Resource.AsStream("Heliosphere.Resources.file-plus.png"),
            State.RemovingOldFiles => Resourcer.Resource.AsStream("Heliosphere.Resources.trash-simple.png"),
            State.Finished => Resourcer.Resource.AsStream("Heliosphere.Resources.check.png"),
            State.Errored => Resourcer.Resource.AsStream("Heliosphere.Resources.warning.png"),
            State.Cancelled => Resourcer.Resource.AsStream("Heliosphere.Resources.prohibit-inset.png"),
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

    /// <summary>
    /// Reads until hitting the read limit. Note that this does not allow valid
    /// reads from the buffer, as it is overwritten with multiple read calls.
    /// </summary>
    /// <param name="buffer">a buffer of bytes</param>
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
