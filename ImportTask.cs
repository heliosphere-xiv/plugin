using Blake3;
using gfoidl.Base64;
using Heliosphere.Exceptions;
using Heliosphere.Model;
using Heliosphere.Util;
using StrawberryShake;

namespace Heliosphere;

internal class ImportTask : IDisposable {
    internal ImportTaskState State { get; private set; } = ImportTaskState.NotRunning;
    internal uint StateCurrent { get; private set; }
    internal uint StateMax { get; private set; }
    internal FirstHalfData? Data { get; private set; }

    private Plugin Plugin { get; }
    private string DirectoryName { get; }
    private string ModName { get; }
    private Guid PackageId { get; }
    private Guid VariantId { get; }
    private Guid VersionId { get; }
    private string Version { get; }
    private string? DownloadKey { get; }

    private string? _penumbraPath;
    private string? _fullDirectory;

    internal ImportTask(
        Plugin plugin,
        string directoryName,
        string modName,
        Guid packageId,
        Guid variantId,
        Guid versionId,
        string version,
        string? downloadKey
    ) {
        this.Plugin = plugin;
        this.DirectoryName = directoryName;
        this.ModName = modName;
        this.PackageId = packageId;
        this.VariantId = variantId;
        this.VersionId = versionId;
        this.Version = version;
        this.DownloadKey = downloadKey;
    }

    /// <inheritdoc />
    public void Dispose() {
    }

    internal void Start() {
        Task.Factory.StartNew(async () => {
            try {
                this.State = ImportTaskState.Hashing;
                var hashes = await this.Hash();

                this.State = ImportTaskState.GettingFileList;
                var files = await this.GetFiles();

                this.State = ImportTaskState.Checking;
                var fileCounts = await this.Check(hashes, files);

                this.Data = new FirstHalfData {
                    Files = fileCounts,
                    HashedFiles = hashes,
                    NeededFiles = files,
                };

                this.State = ImportTaskState.WaitingForConfirmation;
                this.StateCurrent = this.StateMax = 0;
            } catch (Exception ex) {
                Plugin.Log.Error(ex, "Exception when running import task");
                this.State = ImportTaskState.Errored;
            }
        });
    }

    internal void Continue() {
        Task.Factory.StartNew(async () => {
            if (this.Data == null) {
                throw new InvalidOperationException("called Continue but Start was never called/did not complete successfully");
            }

            this.State = ImportTaskState.Renaming;
            this.Rename();

            this.State = ImportTaskState.Deleting;
            this.Delete();

            this.State = ImportTaskState.StartingDownload;
            await this.StartDownload();
        });
    }

    private async Task<Dictionary<string, List<string>>> Hash() {
        this.StateCurrent = this.StateMax = 0;

        if (!this.Plugin.Penumbra.TryGetModDirectory(out this._penumbraPath)) {
            throw new Exception("Penumbra is not set up or is not loaded");
        }

        using var semaphore = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);

        this._fullDirectory = Path.Join(this._penumbraPath, this.DirectoryName);
        var tasks = Directory.EnumerateFiles(this._fullDirectory, "*", SearchOption.AllDirectories)
            // ReSharper disable once AccessToDisposedClosure
            // disposed after this task has completed, so it's fine
            .Select(filePath => this.HashFile(semaphore, filePath));

        var rawHashes = await Task.WhenAll(tasks);
        return rawHashes
            .GroupBy(tuple => tuple.hash)
            .ToDictionary(
                g => g.Key,
                g => g.Select(tuple => tuple.filePath).ToList()
            );
    }

    private async Task<(string hash, string filePath)> HashFile(SemaphoreSlim semaphore, string filePath) {
        using var guard = await SemaphoreGuard.WaitAsync(semaphore);

        using var hasher = new Blake3HashAlgorithm();
        hasher.Initialize();

        await using var file = FileHelper.OpenRead(filePath);
        var hashBytes = await hasher.ComputeHashAsync(file);
        var hash = Base64.Url.Encode(hashBytes);

        this.StateCurrent += 1;

        return (hash, filePath);
    }

    private async Task<FileList> GetFiles() {
        this.StateCurrent = this.StateMax = 0;

        var result = await Plugin.GraphQl.Importer.ExecuteAsync(this.VersionId, this.DownloadKey);
        result.EnsureNoErrors();

        var files = result.Data?.GetVersion?.NeededFiles.Files ?? throw new MissingVersionException(this.VersionId);

        // NOTE: meta files will always have to be redownloaded, since penumbra
        //       deletes them after import, so there's no reason to check for
        //       them

        var filtered = new FileList {
            Files = new Dictionary<string, List<List<string?>>>(),
        };

        foreach (var (hash, list) in files.Files) {
            var filteredList = list
                .Where(item => item[2] != null && !item[2]!.EndsWith(".meta"))
                .ToList();
            if (filteredList.Count > 0) {
                filtered.Files[hash] = list;
            }
        }

        return filtered;
    }

    private Task<(uint Have, uint Needed)> Check(
        IReadOnlyDictionary<string, List<string>> hashes,
        FileList files
    ) {
        var needed = (uint) files.Files.Count;
        var have = 0u;

        this.StateCurrent = 0;
        this.StateMax = needed;

        foreach (var (hash, _) in files.Files) {
            if (hashes.ContainsKey(hash)) {
                have += 1;
            }

            this.StateCurrent += 1;
        }

        return Task.FromResult((have, needed));
    }

    private void Rename() {
        this.StateCurrent = 0;
        this.StateMax = this.Data!.Files.Have;

        // first create the files directory
        var filesPath = Path.Join(this._fullDirectory!, "files");
        Directory.CreateDirectory(filesPath);

        // rename all the files we have and need to their hashes
        foreach (var (hash, files) in this.Data.NeededFiles.Files) {
            if (!this.Data.HashedFiles.TryGetValue(hash, out var paths)) {
                continue;
            }

            // note that the DownloadTask will duplicate the files for us, so we
            // only need to rename once to one extension
            var ext = files
                .Where(item => item[2] != null)
                .Select(item => Path.GetExtension(item[2]))
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
            if (ext == null) {
                Plugin.Log.Warning($"file with no extension: {hash}");
                continue;
            }

            var newPath = Path.ChangeExtension(Path.Join(filesPath, hash), ext);
            File.Move(paths[0], newPath);

            this.StateCurrent += 1;
        }

        // lastly, rename the directory itself
        var newDirName = HeliosphereMeta.ModDirectoryName(this.PackageId, this.ModName, this.Version, this.VariantId);
        var newDirPath = Path.Join(this._penumbraPath!, newDirName);
        Directory.Move(this._fullDirectory!, newDirPath);

        this._fullDirectory = newDirPath;
    }

    private void Delete() {
        // the DownloadTask will create all the necessary metadata for us, so
        // we can delete everything outside the files directory - the
        // DownloadTask will delete anything extra inside the files directory

        this.StateCurrent = 0;
        this.StateMax = 0;

        // delete all non-"files" directories
        foreach (var dirPath in Directory.EnumerateDirectories(this._fullDirectory!)) {
            if (Path.GetFileName(dirPath) == "files") {
                continue;
            }

            Directory.Delete(dirPath, true);
        }

        // delete all top-level files
        foreach (var filePath in Directory.EnumerateFiles(this._fullDirectory!)) {
            FileHelper.Delete(filePath);
        }

        // delete the old mod from penumbra
        this.Plugin.Penumbra.DeleteMod(this.DirectoryName);

        // copy the settings from the old mod to the new one
        this.Plugin.Penumbra.CopyModSettings(this.DirectoryName, Path.GetFileName(this._fullDirectory!));
    }

    private async Task StartDownload() {
        this.StateCurrent = 0;
        this.StateMax = 1;

        await this.Plugin.AddDownloadAsync(new DownloadTask(
            this.Plugin,
            this._penumbraPath!,
            this.VersionId,
            this.Plugin.Config.IncludeTags,
            this.Plugin.Config.OpenPenumbraAfterInstall,
            this.Plugin.Config.OneClickCollection,
            this.DownloadKey
        ));

        this.StateCurrent += 1;
    }
}

internal class FirstHalfData {
    internal required (uint Have, uint Needed) Files { get; init; }
    internal required FileList NeededFiles { get; init; }
    internal required Dictionary<string, List<string>> HashedFiles { get; init; }
}

internal enum ImportTaskState {
    NotRunning,
    Hashing,
    GettingFileList,
    Checking,
    WaitingForConfirmation,
    Renaming,
    Deleting,
    StartingDownload,
    Errored,
}
