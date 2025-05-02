using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security;
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
using Heliosphere.Ui.Dialogs;
using Heliosphere.Util;
using Microsoft.VisualBasic.FileIO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Penumbra.Api.Enums;
using StrawberryShake;
using ZstdSharp;

namespace Heliosphere;

internal class DownloadTask : IDisposable {
    #if LOCAL
    internal const string ApiBase = "http://127.0.0.1:42011";
    #else
    internal const string ApiBase = "https://heliosphere.app/api";
    #endif

    internal Guid TaskId { get; } = Guid.NewGuid();
    internal required Plugin Plugin { get; init; }
    internal required string ModDirectory { get; init; }
    internal required Guid PackageId { get; init; }
    internal required Guid VariantId { get; init; }
    internal required Guid VersionId { get; init; }
    internal required bool IncludeTags { get; init; }
    internal required bool OpenInPenumbra { get; init; }
    internal required Guid? PenumbraCollection { get; init; }
    internal required IActiveNotification? Notification { get; set; }

    private string? PenumbraModPath { get; set; }
    private string? FilesPath { get; set; }
    private string? HashesPath { get; set; }
    internal string? PackageName { get; private set; }
    internal string? VariantName { get; private set; }

    internal CancellationTokenSource CancellationToken { get; } = new();
    internal State State { get; private set; } = State.NotStarted;
    private uint _stateData;

    internal uint StateData => Interlocked.CompareExchange(ref this._stateData, 0, 0);

    internal uint StateDataMax { get; private set; }
    internal Exception? Error { get; private set; }
    private ConcurrentDeque<Measurement> Entries { get; } = new();
    private Util.SentryTransaction? Transaction { get; set; }
    private bool SupportsHardLinks { get; set; }
    private SemaphoreSlim DuplicateMutex { get; } = new(1, 1);
    private bool RequiresDuplicateMutex { get; set; }

    private HashSet<string> ExistingHashes { get; } = [];

    /// <summary>
    /// A list of files expected by the group jsons made by this task. These
    /// paths should be relative to the files directory.
    /// </summary>
    private HashSet<string> ExpectedFiles { get; } = [];

    private Dictionary<Guid, (string, string)> ContainerNames { get; } = [];

    private const double Window = 5;
    private const string DefaultFolder = "_default";

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
        this.DuplicateMutex.Dispose();
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
            [nameof(this.IncludeTags)] = this.IncludeTags,
        });

        SentrySdk.AddBreadcrumb("Started download", "user", data: new Dictionary<string, string> {
            [nameof(this.VersionId)] = this.VersionId.ToCrockford(),
            [nameof(this.PenumbraModPath)] = this.PenumbraModPath ?? "<null>",
            [nameof(this.PenumbraCollection)] = this.PenumbraCollection?.ToString("N") ?? "<null>",
        });

        try {
            var info = await this.GetPackageInfo();

            this.Transaction?.Inner.SetExtra("Package", info.Variant.Package.Id.ToCrockford());
            this.Transaction?.Inner.SetExtra("Variant", info.Variant.Id.ToCrockford());

            this.PackageName = info.Variant.Package.Name;
            this.VariantName = info.Variant.Name;
            this.GenerateModDirectoryPath(info);
            this.DetermineIfUpdate(info);
            this.CreateDirectories();
            await this.TestHardLinks();
            this.CheckOutputPaths(info);
            await this.HashExistingFiles();
            await this.DownloadFiles(info);
            await this.ConstructModPack(info);
            this.RemoveWorkingDirectories();
            this.RemoveOldFiles();
            await this.AddMod(info);

            // before setting state to finished, set the directory name

            this.State = State.Finished;
            Interlocked.Exchange(ref this._stateData, 1);
            this.StateDataMax = 1;

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
                await this.Plugin.State.UpdatePackages(this.CancellationToken.Token);
            }
        } catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException) {
            this.State = State.Cancelled;
            Interlocked.Exchange(ref this._stateData, 0);
            this.StateDataMax = 0;

            if (this.Transaction?.Inner is { } inner) {
                inner.Status = SpanStatus.Cancelled;
            }
        } catch (Exception ex) {
            this.State = State.Errored;
            Interlocked.Exchange(ref this._stateData, 0);
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

            this.Transaction?.Inner?.SetExtra("WasAntivirus", false);

            // probably antivirus (ioexception is being used by other process or
            // access denied)
            if (ex.IsAntiVirus()) {
                this.Plugin.PluginUi.OpenAntiVirusWarning();
                Plugin.Log.Warning(ex, $"[AV] Error downloading version {this.VersionId}");

                this.Transaction?.Inner?.SetExtra("WasAntivirus", true);
            } else if (ex is MultipleModDirectoriesException multiple) {
                await this.Plugin.PluginUi.AddToDrawAsync(new MultipleModDirectoriesDialog(
                    this.Plugin,
                    multiple
                ));
            } else {
                ErrorHelper.Handle(ex, $"Error downloading version {this.VersionId}", this.Transaction?.LatestChild()?.Inner ?? this.Transaction?.Inner);
            }
        }
    }

    private void SetStateData(uint current, uint max) {
        Interlocked.Exchange(ref this._stateData, current);
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
        var installed = await this.Plugin.State.GetInstalled(this.CancellationToken.Token);
        if (installed.TryGetValue(this.PackageId, out var pkg)) {
            if (pkg.Variants.Any(meta => meta.VariantId == this.VariantId)) {
                downloadKind = DownloadKind.Update;
            }
        }

        var resp = await Plugin.GraphQl.DownloadTask.ExecuteAsync(this.VersionId, downloadKind, this.CancellationToken.Token);
        resp.EnsureNoErrors();

        var version = resp.Data?.GetVersion ?? throw new MissingVersionException(this.VersionId);

        // sort needed files for dedupe consistency
        foreach (var files in version.NeededFiles.Files.Files.Values) {
            foreach (var list in files.Values) {
                list.Sort((a, b) => string.Compare($"{a.GamePath}:{a.ArchivePath}", $"{b.GamePath}:{b.ArchivePath}", StringComparison.Ordinal));
            }
        }

        // get the group/option names for containers
        var groups = GroupsUtil.Convert(version.Groups);
        foreach (var group in groups) {
            if (group is StandardGroup standard) {
                foreach (var option in standard.Inner.Options) {
                    this.ContainerNames[option.HsId] = (group.Name, option.Name);
                }
            } else if (group is CombiningGroup combining) {
                for (int i = 0; i < combining.Inner.Containers.Count; i++) {
                    var container = combining.Inner.Containers[i];
                    var name = string.IsNullOrWhiteSpace(container.Name)
                        ? $"container-{i + 1}"
                        : container.Name;
                    this.ContainerNames[container.HsId] = (group.Name, name);
                }
            }
        }

        Interlocked.Increment(ref this._stateData);
        return version;
    }

    private void GenerateModDirectoryPath(IDownloadTask_GetVersion info) {
        var dirName = HeliosphereMeta.ModDirectoryName(info.Variant.Package.Id, info.Variant.Package.Name, info.Version, info.Variant.Id, this.Plugin.Config.UseExtremePathSanitisation);
        this.PenumbraModPath = Path.Join(this.ModDirectory, dirName);
    }

    private void CreateDirectories() {
        this.FilesPath = Path.GetFullPath(Path.Join(this.PenumbraModPath, "files"));
        this.HashesPath = Path.GetFullPath(Path.Join(this.PenumbraModPath, ".hs-hashes"));

        Plugin.Resilience.Execute(() => Directory.CreateDirectory(this.FilesPath));

        Plugin.Resilience.Execute(() => {
            try {
                Directory.Delete(this.HashesPath, true);
            } catch (DirectoryNotFoundException) {
                // ignore
            }
        });

        var di = Plugin.Resilience.Execute(() => Directory.CreateDirectory(this.HashesPath));
        di.Attributes |= FileAttributes.Hidden;
    }

    private async Task TestHardLinks() {
        string? a = null;
        string? b = null;

        try {
            a = Path.Join(this.PenumbraModPath, Path.GetRandomFileName());
            await FileHelper.Create(a, true).DisposeAsync();

            b = Path.Join(this.PenumbraModPath, Path.GetRandomFileName());
            FileHelper.CreateHardLink(a, b);

            this.SupportsHardLinks = true;
        } catch (InvalidOperationException) {
            this.SupportsHardLinks = false;
        } finally {
            if (a != null) {
                try {
                    File.Delete(a);
                } catch (Exception ex) {
                    Plugin.Log.Warning(ex, "Could not delete temp files");
                }
            }

            if (b != null) {
                try {
                    File.Delete(b);
                } catch (Exception ex) {
                    Plugin.Log.Warning(ex, "Could not delete temp files");
                }
            }
        }
    }

    private void DetermineIfUpdate(IDownloadTask_GetVersion info) {
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
                Directory.Move(oldName, this.PenumbraModPath!);
            }
        } else if (directories.Length > 1) {
            var rejoined = directories
                .Select(name => Path.Join(this.ModDirectory, name))
                .ToArray();

            throw new MultipleModDirectoriesException(
                info.Variant.Package.Name,
                info.Variant.Name,
                info.Version,
                rejoined
            );
        }
    }

    private void CheckOutputPaths(IDownloadTask_GetVersion info) {
        var neededFiles = info.NeededFiles.Files.Files;

        var outputToHash = new Dictionary<string, string>();
        foreach (var (hash, file) in neededFiles) {
            foreach (var outputPath in this.GetOutputPaths(file)) {
                if (outputToHash.TryGetValue(outputPath, out var stored) && stored != hash) {
                    Plugin.Log.Warning($"V:{this.VersionId.ToCrockford()} has the same output path linked to multiple hashes, will use slow duplication");
                    this.RequiresDuplicateMutex = true;
                    return;
                }

                outputToHash[outputPath] = hash;
            }
        }
    }

    private async Task HashExistingFiles() {
        this.State = State.CheckingExistingFiles;
        this.SetStateData(0, 0);

        if (this.FilesPath == null) {
            throw new Exception("files path was null");
        }

        // hash => path
        var hashes = new ConcurrentDictionary<string, string>();
        var allFiles = DirectoryHelper.GetFilesRecursive(this.FilesPath).ToList();

        this.StateDataMax = (uint) allFiles.Count;

        await Parallel.ForEachAsync(
            allFiles,
            new ParallelOptions {
                CancellationToken = this.CancellationToken.Token,
            },
            async (path, token) => {
                using var blake3 = new Blake3HashAlgorithm();
                blake3.Initialize();
                await using var file = FileHelper.OpenSharedReadIfExists(path);
                if (file == null) {
                    return;
                }

                await blake3.ComputeHashAsync(file, token);
                var hash = Base64.Url.Encode(blake3.Hash);

                hashes.TryAdd(hash, path);
                Interlocked.Increment(ref this._stateData);
            }
        );

        this.State = State.SettingUpExistingFiles;
        this.SetStateData(0, (uint) hashes.Count);

        Action<string, string> action = this.SupportsHardLinks
            ? FileHelper.CreateHardLink
            : File.Move;
        using var mutex = new SemaphoreSlim(1, 1);
        await Parallel.ForEachAsync(
            hashes,
            new ParallelOptions {
                CancellationToken = this.CancellationToken.Token,
            },
            async (entry, token) => {
                var (hash, path) = entry;
                // move/link each path to the hashes path
                Plugin.Resilience.Execute(() => action(
                    path,
                    Path.Join(this.HashesPath, hash)
                ));

                // ReSharper disable once AccessToDisposedClosure
                using (await SemaphoreGuard.WaitAsync(mutex, token)) {
                    this.ExistingHashes.Add(hash);
                }

                Interlocked.Increment(ref this._stateData);
            }
        );
    }

    private async Task DownloadFiles(IDownloadTask_GetVersion info) {
        using var span = this.Transaction?.StartChild(nameof(this.DownloadFiles));

        var task = info.Batched
            ? this.DownloadBatchedFiles(info.NeededFiles, info.Batches, this.FilesPath!)
            : this.DownloadNormalFiles(info.NeededFiles, this.FilesPath!);
        await task;
    }

    private Task DownloadNormalFiles(IDownloadTask_GetVersion_NeededFiles neededFiles, string filesPath) {
        using var span = this.Transaction?.StartChild(nameof(this.DownloadNormalFiles));

        this.State = State.DownloadingFiles;
        this.SetStateData(0, (uint) neededFiles.Files.Files.Count);

        return Parallel.ForEachAsync(
            neededFiles.Files.Files,
            new ParallelOptions {
                CancellationToken = this.CancellationToken.Token,
            },
            async (pair, token) => {
                var (hash, files) = pair;
                var outputPaths = this.GetOutputPaths(files);

                using (await SemaphoreGuard.WaitAsync(Plugin.DownloadSemaphore, token)) {
                    await this.DownloadFile(new Uri(neededFiles.BaseUri), filesPath, outputPaths, hash);
                }
            }
        );
    }

    private Task DownloadBatchedFiles(IDownloadTask_GetVersion_NeededFiles neededFiles, BatchList batches, string filesPath) {
        using var span = this.Transaction?.StartChild(nameof(this.DownloadBatchedFiles));

        this.State = State.DownloadingFiles;
        this.SetStateData(0, 0);

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

        this.StateDataMax = (uint) neededFiles.Files.Files.Count;

        return Parallel.ForEachAsync(
            clonedBatches,
            new ParallelOptions {
                CancellationToken = this.CancellationToken.Token,
            },
            async (pair, token) => {
                var (batch, batchedFiles) = pair;

                // find which files from this batch we already have a hash for
                var toDuplicate = new HashSet<string>();
                foreach (var hash in batchedFiles.Keys) {
                    if (!this.ExistingHashes.Contains(hash)) {
                        continue;
                    }

                    toDuplicate.Add(hash);
                }

                // sort files in batch by offset, removing already-downloaded files
                var listOfFiles = batchedFiles
                    .Select(pair => (Hash: pair.Key, Info: pair.Value))
                    .Where(pair => !this.ExistingHashes.Contains(pair.Hash))
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

                    using (await SemaphoreGuard.WaitAsync(Plugin.DownloadSemaphore, token)) {
                        var counter = new StateCounter();
                        await Plugin.Resilience.ExecuteAsync(
                            async _ => {
                                // if we're retrying, remove the files that this task added
                                Interlocked.Add(ref this._stateData, UintHelper.OverflowSubtractValue(counter.Added));
                                counter.Added = 0;

                                await this.DownloadBatchedFile(neededFiles, filesPath, uri, rangeHeader, chunks, batchedFiles, counter);
                            },
                            token
                        );
                    }
                }

                foreach (var hash in toDuplicate) {
                    var joined = Path.Join(this.HashesPath, hash);
                    if (!File.Exists(joined)) {
                        Plugin.Log.Warning($"{joined} was supposed to be duplicated but no longer exists");
                        continue;
                    }

                    var gamePaths = neededFiles.Files.Files[hash];
                    var outputPaths = this.GetOutputPaths(gamePaths);

                    await this.DuplicateFile(filesPath, outputPaths, joined);

                    Interlocked.Increment(ref this._stateData);
                }
            }
        );
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
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Range = rangeHeader;

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
        } else if (chunks.Count == 1) {
            multipart = new SingleMultipartProvider(resp.Content);
        } else if (rangeHeader != null && resp.StatusCode == HttpStatusCode.PartialContent) {
            // we requested multiple chunks, but we didn't get the expected
            // multipart response. this is a cloudflare bug. work around this by
            // requesting a range containing the lowest and highest offsets.
            resp.Dispose();

            var minOffset = rangeHeader.Ranges.Select(range => range.From).Min();
            var maxOffset = rangeHeader.Ranges.Select(range => range.To).Max();
            var adjustedRangeHeader = new RangeHeaderValue(minOffset, maxOffset);

            // construct the request with the adjusted header
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = adjustedRangeHeader;

            // NOTE: this is not disposed as a convenience. making this
            // workaround able to cleanly dispose this response would be a pain
            // in the ass. fortunately, as of the time of writing (2025-01-20),
            // all a Response.Dispose call does is call Dispose on the Content
            // if necessary. since we have the disposeMultipart handler below,
            // this doesn't actually matter.
            var response = await Plugin.Client.SendAsync2(request, HttpCompletionOption.ResponseHeadersRead, this.CancellationToken.Token);

            // this is a special wrapper that will return a wrapped stream that
            // emulates the server returning the proper response. this will
            // waste bandwidth.
            multipart = new SingleMultipleMultipartProvider(response.Content, rangeHeader.Ranges);
        } else {
            throw new Exception("unexpected download response state");
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
                var outputPaths = this.GetOutputPaths(gamePaths);
                if (outputPaths.Length == 0) {
                    Plugin.Log.Warning($"file with hash {hash} has no output paths");
                    continue;
                }

                var batchedFileInfo = batchedFiles[hash];
                var path = Path.Join(filesPath, outputPaths[0]);
                await using var file = FileHelper.Create(path, true);
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
                await this.DuplicateFile(filesPath, outputPaths, path);

                Interlocked.Increment(ref this._stateData);
                counter.Added += 1;
            }
        }
    }

    private static string MakePathPartsSafe(string input, bool extreme) {
        var cleaned = input
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '\\', '/')
            .Select(part => MakeFileNameSafe(part, extreme));
        return string.Join(Path.DirectorySeparatorChar, cleaned);
    }

    private static string MakeFileNameSafe(string input, bool extreme) {
        var sb = new StringBuilder();
        if (extreme) {
            var allowed = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_ ";
            foreach (var ch in input) {
                sb.Append(
                    allowed.Contains(ch)
                        ? ch
                        : '-'
                );
            }
        } else {
            var invalid = Path.GetInvalidFileNameChars();

            foreach (var ch in input) {
                sb.Append(
                    Array.IndexOf(invalid, ch) == -1
                        ? ch
                        : '-'
                );
            }
        }

        var path = sb.ToString();
        return path.TrimEnd('.', ' ');
    }

    private string[] GetOutputPaths(Dictionary<Guid, List<NeededFile>> files) {
        var extreme = this.Plugin.Config.UseExtremePathSanitisation;
        return [.. files
            .SelectMany(entry => {
                var (containerId, files) = entry;

                return files.Select(file => {
                    var outputPath = file.ArchivePath;
                    if (outputPath != null) {
                        if (Path.GetExtension(outputPath) == string.Empty) {
                            // we need to add an extension or this can cause a crash
                            outputPath = Path.ChangeExtension(outputPath, Path.GetExtension(file.GamePath));
                        }

                        return MakePathPartsSafe(outputPath, extreme);
                    }

                    var groupName= DefaultFolder;
                    var optionName = DefaultFolder;
                    if (this.ContainerNames.TryGetValue(containerId, out var names)) {
                        groupName = names.Item1;
                        optionName = names.Item2;
                    }

                    var group = MakeFileNameSafe(groupName ?? DefaultFolder, extreme);
                    var option = MakeFileNameSafe(optionName ?? DefaultFolder, extreme);
                    var gamePath = MakePathPartsSafe(file.GamePath, extreme);

                    return Path.Join(group, option, gamePath);
                });
            })
            .Where(file => !string.IsNullOrEmpty(file))
            .Order()];
    }

    private async Task DuplicateFile(string filesDir, IEnumerable<string> outputPaths, string path) {
        using var guard = this.RequiresDuplicateMutex
            ? await SemaphoreGuard.WaitAsync(this.DuplicateMutex, this.CancellationToken.Token)
            : null;

        if (!this.SupportsHardLinks) {
            // If hard links aren't supported, copy the path to the first output
            // path.
            // This is done because things reference the first output path
            // assuming it will exist. A copy is made to not mess up the
            // validity of the ExistingPathHashes and ExistingHashPaths
            // dictionaries. The old file will be removed in the remove step if
            // necessary.
            var firstPath = outputPaths.FirstOrDefault();
            if (firstPath == null) {
                return;
            }

            var dest = Path.Join(filesDir, firstPath);
            if (dest.Equals(path, StringComparison.InvariantCultureIgnoreCase)) {
                return;
            }

            var parent = PathHelper.GetParent(dest);
            Plugin.Resilience.Execute(() => Directory.CreateDirectory(parent));

            if (!await PathHelper.WaitForDelete(dest)) {
                throw new DeleteFileException(dest);
            }

            // ReSharper disable once AccessToModifiedClosure
            Plugin.Resilience.Execute(() => File.Copy(path, dest));
            return;
        }

        foreach (var outputPath in outputPaths) {
            await DuplicateInner(outputPath);
        }

        return;

        async Task DuplicateInner(string dest) {
            dest = Path.Join(filesDir, dest);
            if (path.Equals(dest, StringComparison.InvariantCultureIgnoreCase)) {
                return;
            }

            if (!Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(dest)) {
                throw new Exception($"{path} or {dest} was not fully qualified");
            }

            if (!await PathHelper.WaitForDelete(dest)) {
                throw new DeleteFileException(dest);
            }

            var parent = PathHelper.GetParent(dest);
            Plugin.Resilience.Execute(() => Directory.CreateDirectory(parent));

            Plugin.Resilience.Execute(() => FileHelper.CreateHardLink(path, dest));
        }
    }

    private void RemoveOldFiles() {
        using var span = this.Transaction?.StartChild(nameof(this.RemoveOldFiles));

        this.State = State.RemovingOldFiles;
        this.SetStateData(0, 0);

        // find old, normal files no longer being used to remove
        var presentFiles = DirectoryHelper.GetFilesRecursive(this.FilesPath!)
            .Select(path => PathHelper.MakeRelativeSub(this.FilesPath!, path))
            .Where(path => !string.IsNullOrEmpty(path))
            .Cast<string>()
            .Select(path => path.ToLowerInvariant())
            .ToHashSet();

        // remove the files that we expect from the list of already-existing
        // files - these are the files to remove now
        presentFiles.ExceptWith(this.ExpectedFiles);

        var total = (uint) presentFiles.Count;
        this.SetStateData(0, total);

        var done = 0u;
        Parallel.ForEach(
            presentFiles,
            extra => {
                var extraPath = Path.Join(this.FilesPath, extra);
                Plugin.Log.Info($"removing extra file {extraPath}");
                Plugin.Resilience.Execute(() => {
                    if (this.Plugin.Config.UseRecycleBin) {
                        try {
                            FileSystem.DeleteFile(extraPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                        } catch (Exception ex) when (ex is IOException { HResult: Consts.UsedByAnotherProcess } io) {
                            var procs = RestartManager.GetLockingProcesses(extraPath);
                            throw new AlreadyInUseException(io, extraPath, procs);
                        }
                    } else {
                        FileHelper.Delete(extraPath);
                    }
                });

                done += 1;
                this.SetStateData(done, total);
            }
        );

        // remove any empty directories
        DirectoryHelper.RemoveEmptyDirectories(this.FilesPath!);
    }

    private async Task DownloadFile(Uri baseUri, string filesPath, string[] outputPaths, string hash) {
        using var span = this.Transaction?.StartChild(nameof(this.DownloadFile), true);
        span?.Inner.SetExtras(new Dictionary<string, object?> {
            [nameof(hash)] = hash,
            [nameof(outputPaths)] = outputPaths,
        });

        if (outputPaths.Length == 0) {
            return;
        }

        // check each path for containment breaks when joining
        foreach (var outputPath in outputPaths) {
            var joined = Path.GetFullPath(Path.Join(filesPath, outputPath));
            // check that this path is under the files path still
            if (PathHelper.MakeRelativeSub(filesPath, joined) == null) {
                throw new SecurityException($"path from mod was attempting to leave the files directory: '{joined}' is not within '{filesPath}'");
            }
        }

        // find an existing path that has this hash
        string validPath;
        if (this.ExistingHashes.Contains(hash)) {
            validPath = Path.Join(this.HashesPath, hash);
            goto Duplicate;
        }

        // no valid, existing file, so download instead
        var path = Path.Join(filesPath, outputPaths[0]);
        validPath = path;

        await Plugin.Resilience.ExecuteAsync(
            async _ => {
                var uri = new Uri(baseUri, hash).ToString();
                using var resp = await Plugin.Client.GetAsync2(uri, HttpCompletionOption.ResponseHeadersRead, this.CancellationToken.Token);
                resp.EnsureSuccessStatusCode();

                await using var file = FileHelper.Create(path, true);
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
        await this.DuplicateFile(filesPath, outputPaths, validPath);

        Interlocked.Increment(ref this._stateData);
    }

    private async Task ConstructModPack(IDownloadTask_GetVersion info) {
        using var span = this.Transaction?.StartChild(nameof(this.ConstructModPack));

        this.State = State.ConstructingModPack;
        this.SetStateData(0, 5);
        var hsMeta = await this.ConstructHeliosphereMeta(info);
        await this.ConstructMeta(info, hsMeta);
        var defaultMod = await this.ConstructDefaultMod(info);
        var groups = await this.ConstructGroups(info);
        await this.DuplicateUiFiles(defaultMod, groups);
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
        Interlocked.Increment(ref this._stateData);
    }

    private async Task<HeliosphereMeta> ConstructHeliosphereMeta(IDownloadTask_GetVersion info) {
        using var span = this.Transaction?.StartChild(nameof(this.ConstructHeliosphereMeta));

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
            IncludeTags = this.IncludeTags,
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

        Interlocked.Increment(ref this._stateData);

        return meta;
    }

    private static string GetReplacedPath(string? groupName, string? optionName, string gamePath, string? archivePath, bool extreme) {
        var replacedPath = archivePath == null
            ? Path.Join(
                MakeFileNameSafe(groupName ?? DefaultFolder, extreme),
                MakeFileNameSafe(optionName ?? DefaultFolder, extreme),
                MakePathPartsSafe(gamePath, extreme)
            )
            : MakePathPartsSafe(archivePath, extreme);

        if (Path.GetExtension(replacedPath) == string.Empty) {
            replacedPath = Path.ChangeExtension(replacedPath, Path.GetExtension(gamePath));
        }

        return replacedPath;
    }

    private async Task<DefaultMod> ConstructDefaultMod(IDownloadTask_GetVersion info) {
        using var span = this.Transaction?.StartChild(nameof(this.ConstructDefaultMod));
        var extreme = this.Plugin.Config.UseExtremePathSanitisation;

        var defaultMod = new DefaultMod {
            Manipulations = ManipTokensForOption(info.NeededFiles.DefaultManipulations),
            FileSwaps = info.DefaultOption?.FileSwaps.Swaps ?? [],
        };

        foreach (var neededContainers in info.NeededFiles.Files.Files.Values) {
            if (!neededContainers.TryGetValue(Guid.Empty, out var defaultFiles)) {
                continue;
            }

            foreach (var file in defaultFiles) {
                var replacedPath = this.SupportsHardLinks
                    ? GetReplacedPath(null, null, file.GamePath, file.ArchivePath, extreme)
                    : GetReplacedPath(null, null, defaultFiles[0].GamePath, defaultFiles[0].ArchivePath, extreme);

                defaultMod.Files[file.GamePath] = Path.Join("files", replacedPath);
                this.ExpectedFiles.Add(replacedPath.ToLowerInvariant());
            }
        }

        await this.SaveDefaultMod(defaultMod);
        Interlocked.Increment(ref this._stateData);

        return defaultMod;
    }

    private async Task SaveDefaultMod(DefaultMod defaultMod) {
        var json = JsonConvert.SerializeObject(defaultMod, Formatting.Indented);

        var path = Path.Join(this.PenumbraModPath, "default_mod.json");
        await using var output = FileHelper.Create(path);
        await output.WriteAsync(Encoding.UTF8.GetBytes(json), this.CancellationToken.Token);
    }

    private async Task<List<ModGroup>> ConstructGroups(IDownloadTask_GetVersion info) {
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
                var text = await FileHelper.ReadAllTextAsync(existing, this.CancellationToken.Token);
                ModGroup? group;
                try {
                    group = JsonConvert.DeserializeObject<StandardModGroup>(text);
                } catch {
                    try {
                        group = JsonConvert.DeserializeObject<ImcModGroup>(text);
                    } catch {
                        group = JsonConvert.DeserializeObject<CombiningModGroup>(text);
                    }
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
        var containers = new Dictionary<Guid, (string, string, IContainer)>();
        var modGroups = new Dictionary<string, ModGroup>(rawGroups.Count);
        foreach (var group in rawGroups) {
            ModGroup modGroup;
            switch (group) {
                case StandardGroup { Inner: var inner }: {
                    var standard = new StandardModGroup(group.Name, group.Description, group.GroupType.ToString()) {
                        Priority = group.Priority,
                        DefaultSettings = group.DefaultSettings,
                        OriginalIndex = (group.OriginalIndex, 0),
                    };

                    foreach (var option in inner.Options) {
                        var manipulations = ManipTokensForOption(option.Manipulations);
                        var item = new OptionItem {
                            Name = option.Name,
                            Description = option.Description,
                            Priority = option.Priority,
                            Manipulations = manipulations,
                            FileSwaps = option.FileSwaps.Swaps,
                            IsDefault = option.IsDefault,
                        };
                        standard.Options.Add(item);
                        containers[option.HsId] = (group.Name, option.Name, item);
                    }

                    modGroup = standard;

                    break;
                }
                case ImcGroup { Inner: var inner }: {
                    var identifier = JToken.Parse(inner.Identifier.GetRawText());
                    var defaultEntry = JToken.Parse(inner.DefaultEntry.GetRawText());
                    var imc = new ImcModGroup(group.Name, group.Description, identifier, inner.AllVariants, inner.OnlyAttributes, defaultEntry) {
                        Priority = group.Priority,
                        DefaultSettings = group.DefaultSettings,
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
                case CombiningGroup { Inner: var inner }: {
                    var combining = new CombiningModGroup(group.Name, group.Description, group.GroupType.ToString()) {
                        Priority = group.Priority,
                        DefaultSettings = group.DefaultSettings,
                        OriginalIndex = (group.OriginalIndex, 0),
                    };

                    foreach (var option in inner.Options) {
                        combining.Options.Add(new CombiningOption {
                            Name = option.Name,
                            Description = option.Description,
                            IsDefault = option.IsDefault,
                        });
                    }

                    for (int i = 0; i < inner.Containers.Count; i++) {
                        var container = inner.Containers[i];
                        var manipulations = ManipTokensForOption(container.Manipulations);
                        var item = new CombiningContainer {
                            Name = container.Name,
                            Manipulations = manipulations,
                            FileSwaps = container.FileSwaps.Swaps,
                        };
                        combining.Containers.Add(item);

                        var optionName = string.IsNullOrWhiteSpace(container.Name)
                            ? $"container-{i + 1}"
                            : container.Name;

                        containers[container.HsId] = (group.Name, optionName, item);
                    }

                    modGroup = combining;

                    break;
                }
                default:
                    throw new Exception("unknown mod group type");
            }

            modGroups[group.Name] = modGroup;
        }

        var extreme = this.Plugin.Config.UseExtremePathSanitisation;

        // add files to their respective containers
        foreach (var (_, neededContainers) in info.NeededFiles.Files.Files) {
            foreach (var (containerId, files) in neededContainers) {
                if (!containers.TryGetValue(containerId, out var entry)) {
                    continue;
                }

                var (groupName, optionName, container) = entry;

                foreach (var file in files) {
                    var replacedPath = this.SupportsHardLinks
                        ? GetReplacedPath(groupName, optionName, file.GamePath, file.ArchivePath, extreme)
                        : GetReplacedPath(groupName, optionName, files[0].GamePath, files[0].ArchivePath, extreme);
                    container.AddFile(file.GamePath, Path.Join("files", replacedPath));
                    this.ExpectedFiles.Add(replacedPath.ToLowerInvariant());
                }
            }
        }

        // split groups that have more than 32 options
        var splitGroups = SplitGroups(modGroups.Values);

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
            var installedPkgs = await this.Plugin.State.GetInstalled(this.CancellationToken.Token);
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
                ModPath = HeliosphereMeta.ModDirectoryName(info.Variant.Package.Id, info.Variant.Package.Name, info.Version, info.Variant.Id, this.Plugin.Config.UseExtremePathSanitisation),
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
                    CombiningModGroup { Options: var options } => options.Select(o => o.Name),
                    _ => throw new Exception("unexpected mod group type"),
                }).ToArray();
                var oldOptions = (oldGroup switch {
                    StandardModGroup { Options: var options } => options.Select(o => o.Name),
                    ImcModGroup { Options: var options } => options.Select(o => o.Name),
                    CombiningModGroup { Options: var options } => options.Select(o => o.Name),
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

            await this.SaveGroup(i, list[i]);
            Interlocked.Increment(ref this._stateData);
        }

        return list;
    }

    private async Task SaveGroup(int index, ModGroup group) {
        var invalidChars = Path.GetInvalidFileNameChars();
        var slug = group.Name.ToLowerInvariant()
            .Select(c => invalidChars.Contains(c) ? '-' : c)
            .Aggregate(new StringBuilder(), (sb, c) => sb.Append(c))
            .ToString();
        var json = JsonConvert.SerializeObject(group, Formatting.Indented);
        var path = Path.Join(this.PenumbraModPath, $"group_{index + 1:000}_{slug}.json");
        await using var file = FileHelper.Create(path);
        await file.WriteAsync(Encoding.UTF8.GetBytes(json), this.CancellationToken.Token);
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
                newGroup.DefaultSettings |= unchecked(1ul << optionIdx);
            }
        }

        return newGroups;
    }

    private static List<JToken> ManipTokensForOption(IReadOnlyList<System.Text.Json.JsonElement> rawManipulations) {
        var manipulations = rawManipulations
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

    private async Task DuplicateUiFiles(DefaultMod defaultMod, List<ModGroup> modGroups) {
        // check for any game path that starts with the ui prefix that is
        // referenced in more than one option. make its file name unique for
        // each reference.
        const string uiPrefix = "ui/";

        // first record unique references
        var references = new Dictionary<string, (uint, List<Action<string>>)>();
        UpdateReferences(defaultMod.Files);
        foreach (var group in modGroups) {
            if (group is StandardModGroup standard) {
                foreach (var option in standard.Options) {
                    UpdateReferences(option.Files);
                }
            } else if (group is CombiningModGroup combining) {
                foreach (var container in combining.Containers) {
                    UpdateReferences(container.Files);
                }
            }

        }

        // then find any uniquely referenced more than once
        foreach (var (joinedOutputPath, (refs, updatePathActions)) in references) {
            var outputPath = joinedOutputPath[6..];
            if (refs < 2) {
                continue;
            }

            // At this point, we have identified a path on disk that is
            // referenced more than once by differing options. This path needs
            // to be duplicated with a different file name to avoid crashes.
            // This process can be done using hard links if they're supported;
            // otherwise copy the file.

            Action<string, string> duplicateMethod = this.SupportsHardLinks
                ? FileHelper.CreateHardLink
                : File.Copy;

            var src = Path.Join(this.FilesPath, outputPath);
            for (var i = 0; i < refs; i++) {
                var ext = $".{i + 1}" + Path.GetExtension(outputPath);
                var newRelative = Path.ChangeExtension(outputPath, ext);
                var dst = Path.Join(this.FilesPath, newRelative);

                FileHelper.DeleteIfExists(dst);

                Plugin.Resilience.Execute(() => duplicateMethod(src, dst));

                // update the path
                updatePathActions[i](Path.Join("files", newRelative));
                this.ExpectedFiles.Add(newRelative.ToLowerInvariant());
            }

            // remove the original file
            Plugin.Resilience.Execute(() => File.Delete(src));
            this.ExpectedFiles.Remove(outputPath);
        }

        await this.SaveDefaultMod(defaultMod);

        for (var i = 0; i < modGroups.Count; i++) {
            await this.SaveGroup(i, modGroups[i]);
        }

        Interlocked.Increment(ref this._stateData);
        return;

        void UpdateReferences(Dictionary<string, string> files) {
            foreach (var (gamePath, outputPath) in files) {
                if (!gamePath.StartsWith(uiPrefix)) {
                    continue;
                }

                // normalise case of output path
                var normalised = outputPath.ToLowerInvariant();

                if (!references.TryGetValue(normalised, out var refs)) {
                    refs = (0, []);
                }

                refs.Item2.Add(path => {
                    files[gamePath] = path;
                });
                refs.Item1 += 1;

                references[normalised] = refs;
            }
        }
    }

    private void RemoveWorkingDirectories() {
        if (this.HashesPath == null) {
            return;
        }

        Plugin.Resilience.Execute(() => Directory.Delete(this.HashesPath, true));
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

            Interlocked.Increment(ref this._stateData);
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
        sb.Append("[code]\n");
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

        sb.Append("[/code]");

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
    SettingUpExistingFiles,
    DownloadingFiles,
    ConstructingModPack,
    RemovingOldFiles,
    AddingMod,
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
            State.SettingUpExistingFiles => "Setting up existing files",
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
            State.CheckingExistingFiles
                or State.SettingUpExistingFiles => Resourcer.Resource.AsStream("Heliosphere.Resources.hard-drives.png"),
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
