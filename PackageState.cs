using System.Collections.Immutable;
using Dalamud.Interface.Textures.TextureWraps;
using Heliosphere.Exceptions;
using Heliosphere.Model;
using Heliosphere.Util;

namespace Heliosphere;

internal class PackageState : IDisposable {
    private Plugin Plugin { get; }

    private string? PenumbraPath => this.Plugin.Penumbra.GetModDirectory();
    private Guard<Dictionary<Guid, InstalledPackage>> InstalledInternal { get; } = new([]);
    private Guard<Dictionary<Guid, InstalledPackage>> ExternalInternal { get; } = new([]);
    private SemaphoreSlim UpdateMutex { get; } = new(1, 1);

    internal int DirectoriesToScan = -1;
    internal int CurrentDirectory;

    private int _updateNum;

    internal IReadOnlyDictionary<Guid, InstalledPackage> Installed {
        get {
            using var guard = this.InstalledInternal.Wait();
            return guard.Data.ToImmutableDictionary(
                entry => entry.Key,
                entry => entry.Value
            );
        }
    }

    internal IReadOnlyDictionary<Guid, InstalledPackage> InstalledNoBlock {
        get {
            using var guard = this.InstalledInternal.Wait(0);
            if (guard == null) {
                return ImmutableDictionary<Guid, InstalledPackage>.Empty;
            }

            return guard.Data.ToImmutableDictionary(
                entry => entry.Key,
                entry => entry.Value
            );
        }
    }

    /// <summary>
    /// <para>
    /// Returns an immutable Dictionary of "external" Heliosphere mods.
    /// </para>
    /// <para>
    /// External mods are mods installed via means other than the plugin.
    /// Specifically, these are mods that are in directories not starting with
    /// <c>"hs-"</c> and that contain a heliosphere.json file.
    /// </para>
    /// </summary>
    internal IReadOnlyDictionary<Guid, InstalledPackage> External {
        get {
            using var guard = this.ExternalInternal.Wait();
            return guard.Data.ToImmutableDictionary(
                entry => entry.Key,
                entry => entry.Value
            );
        }
    }

    /// <summary>
    /// Same as <see cref="External"/> but returns an empty Dictionary if
    /// accessing the data would have blocked.
    /// </summary>
    internal IReadOnlyDictionary<Guid, InstalledPackage> ExternalNoBlock {
        get {
            using var guard = this.ExternalInternal.Wait(0);
            if (guard == null) {
                return ImmutableDictionary<Guid, InstalledPackage>.Empty;
            }

            return guard.Data.ToImmutableDictionary(
                entry => entry.Key,
                entry => entry.Value
            );
        }
    }

    internal PackageState(Plugin plugin) {
        this.Plugin = plugin;
    }

    public void Dispose() {
        this.UpdateMutex.Dispose();
        this.InstalledInternal.Dispose();
    }

    internal async Task<IReadOnlyDictionary<Guid, InstalledPackage>> GetInstalled(CancellationToken token = default) {
        using var guard = await this.InstalledInternal.WaitAsync(token);
        return guard.Data.ToImmutableDictionary(
            entry => entry.Key,
            entry => entry.Value
        );
    }

    /// <summary>
    /// <para>
    /// Update the package state with the packages that are installed on disk,
    /// potentially running migrations.
    /// </para>
    /// <para>
    /// Note that the effect of migrations will be applied even if
    /// applyMigrations is set to false when that migration has been run once
    /// already.
    /// </para>
    /// </summary>
    /// <param name="applyMigrations">If true, force the scan to continue when a migration would be applied. If false, a <see cref="MigrationRequiredException"/> will be thrown instead.</param>
    internal async Task UpdatePackages(bool applyMigrations, CancellationToken token = default) {
        using var span = SentryHelper.StartTransaction("PackageState", "UpdatePackages");

        // get the current update number. if this changes by the time this task
        // gets a lock on the update mutex, the update that this task was queued
        // for is already complete
        var updateNum = Interlocked.CompareExchange(ref this._updateNum, 0, 0);

        // first wait until all downloads are completed
        var timesDelayed = 0;
        while (true) {
            bool anyRunning;
            using (var downloads = await this.Plugin.Downloads.WaitAsync(token)) {
                anyRunning = downloads.Data.Any(task => !task.State.IsDone());
            }

            if (anyRunning) {
                timesDelayed += 1;
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            } else {
                break;
            }
        }

        span.Inner.SetExtra("timesDelayed", timesDelayed);

        // get a lock on the update guard so no other updates can continue
        using var updateGuard = await SemaphoreGuard.WaitAsync(this.UpdateMutex, token);
        // get a lock on the downloads so that no one can add any until the update is complete
        using var downloadGuard = await this.Plugin.Downloads.WaitAsync(token);

        // check if this task is redundant
        if (updateNum != Interlocked.CompareExchange(ref this._updateNum, 0, 0)) {
            span.Inner.SetExtra("wasRedundant", true);
            return;
        }

        span.Inner.SetExtra("wasRedundant", false);

        int numPreviouslyInstalled;
        using (var guard = await this.InstalledInternal.WaitAsync(token)) {
            numPreviouslyInstalled = guard.Data.Count;
            // dispose and remove existing packages
            foreach (var (_, package) in guard.Data) {
                package.Dispose();
            }

            guard.Data.Clear();
        }

        using (var externalGuard = await this.ExternalInternal.WaitAsync(token)) {
            externalGuard.Data.Clear();
        }

        if (this.PenumbraPath is not { } penumbraPath) {
            return;
        }

        var dirs = Directory.EnumerateDirectories(penumbraPath)
            .Select(Path.GetFileName)
            .Where(dir => !string.IsNullOrEmpty(dir))
            .Cast<string>()
            .ToList();

        Interlocked.Exchange(ref this.CurrentDirectory, 0);
        Interlocked.Exchange(ref this.DirectoriesToScan, dirs.Count);

        await Parallel.ForEachAsync(
            dirs,
            new ParallelOptions {
                CancellationToken = token,
            },
            async (dir, token) => {
                Interlocked.Increment(ref this.CurrentDirectory);

                if (dir.StartsWith("hs-")) {
                    try {
                        await this.LoadPackage(dir, penumbraPath, applyMigrations, token);
                    } catch (Exception ex) {
                        if (ex is MigrationRequiredException) {
                            throw;
                        }

                        ErrorHelper.Handle(ex, "Could not load package");
                    }
                } else {
                    try {
                        await this.LoadExternalPackage(dir, penumbraPath, token);
                    } catch (Exception ex) {
                        ErrorHelper.Handle(ex, "Could not load external package");
                    }
                }
            }
        );

        Interlocked.Exchange(ref this.DirectoriesToScan, -1);

        Interlocked.Add(ref this._updateNum, 1);
    }

    private static async Task<HeliosphereMeta?> LoadMeta(string penumbraPath, string directory, CancellationToken token = default) {
        var metaPath = Path.Join(penumbraPath, directory, "heliosphere.json");

        try {
            return await HeliosphereMeta.Load(metaPath, token);
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            return null;
        } catch (Exception ex) {
            // downgrading these to a warning - most of the time it just doesn't
            // matter, and I can't be fucked handling every bad meta json out
            // there to prevent sentry being mad
            Plugin.Log.Warning(ex, "Could not load heliosphere.json");
            return null;
        }
    }

    private static InstalledPackage CreateInstalledPackage(
        string penumbraPath,
        string directory,
        HeliosphereMeta meta,
        Guard<Dictionary<Guid, InstalledPackage>>.Handle guard
    ) {
        InstalledPackage package;
        if (guard.Data.TryGetValue(meta.Id, out var existing)) {
            package = existing;

            if (existing.Variants.Any(variant => variant.VersionId == meta.VersionId)) {
                Plugin.Log.Warning($"P:{meta.Id.ToCrockford()} V:{meta.VersionId.ToCrockford()} duplicate found - weird behaviour ahead");
            }

            existing.InternalVariants.Add(meta);
        } else {
            var coverJpgPath = Path.Join(penumbraPath, directory, "cover.jpg");
            var coverWebpPath = Path.Join(penumbraPath, directory, "cover.webp");

            var coverPath = File.Exists(coverWebpPath)
                ? coverWebpPath
                : coverJpgPath;

            package = new InstalledPackage(
                meta.Id,
                meta.Name,
                meta.Author,
                [meta],
                coverPath
            );
        }

        return package;
    }

    private async Task LoadExternalPackage(string directory, string penumbraPath, CancellationToken token = default) {
        var meta = await LoadMeta(penumbraPath, directory, token);
        if (meta == null) {
            return;
        }

        using var guard = await this.ExternalInternal.WaitAsync(token);
        var package = CreateInstalledPackage(penumbraPath, directory, meta, guard);
        guard.Data[meta.Id] = package;
    }

    private async Task LoadPackage(string directory, string penumbraPath, bool applyMigrations, CancellationToken token = default) {
        // always attempt to load the hs meta file
        var meta = await LoadMeta(penumbraPath, directory, token);
        if (meta == null) {
            return;
        }

        if (directory.StartsWith("hs-") && Guid.TryParse(directory.Split('-')[^2], out _)) {
            // second-to-last part should be the version in the new scheme, so
            // this is the old naming scheme
            if (!applyMigrations && this.Plugin.Config.LatestMigration < 1) {
                throw new MigrationRequiredException(this.Plugin.Config.LatestMigration, 1);
            }

            directory = await this.MigrateOldDirectory(meta, penumbraPath, directory, token);
        }

        if (HeliosphereMeta.ParseDirectory(directory) is not { } info) {
            return;
        }

        if (meta.ShortVariantId != info.ShortVariantId) {
            return;
        }

        // always make sure path is correct
        await this.RenameDirectory(meta, penumbraPath, directory, token);

        using var guard = await this.InstalledInternal.WaitAsync(token);
        var package = CreateInstalledPackage(penumbraPath, directory, meta, guard);
        guard.Data[meta.Id] = package;
    }

    internal async Task RenameDirectory(HeliosphereMeta meta, string penumbraPath, string directory, CancellationToken token = default) {
        var correctName = meta.ModDirectoryName();
        if (directory == correctName) {
            return;
        }

        Plugin.Log.Info($"Fixing incorrect folder name for {directory}");

        var oldPath = Path.Join(penumbraPath, directory);
        var newPath = Path.Join(penumbraPath, correctName);
        if (Directory.Exists(newPath)) {
            throw new ModAlreadyExistsException(oldPath, newPath);
        }

        Plugin.Log.Debug($"    {oldPath} -> {newPath}");
        Directory.Move(oldPath, newPath);
        if (!await PathHelper.WaitToExist(newPath)) {
            throw new DirectoryNotFoundException($"Directory '{newPath}' could not be found after waiting");
        }

        await this.Plugin.Framework.RunOnFrameworkThread(() => {
            var oldPath = this.Plugin.Penumbra.GetModPath(directory);
            this.Plugin.Penumbra.DeleteMod(directory);
            this.Plugin.Penumbra.AddMod(correctName);
            this.Plugin.Penumbra.CopyModSettings(directory, correctName);
            if (oldPath != null) {
                this.Plugin.Penumbra.SetModPath(correctName, oldPath);
            }
        });
    }

    private async Task<string> MigrateOldDirectory(HeliosphereMeta meta, string penumbraPath, string directory, CancellationToken token = default) {
        await this.RenameDirectory(meta, penumbraPath, directory, token);
        return meta.ModDirectoryName();
    }
}

internal class ModAlreadyExistsException : Exception {
    private string OldPath { get; }
    private string NewPath { get; }
    public override string Message => $"Could not move old mod to new path because new path already exists ({this.OldPath} -> {this.NewPath})";

    internal ModAlreadyExistsException(string oldPath, string newPath) {
        this.OldPath = oldPath;
        this.NewPath = newPath;
    }
}

internal class InstalledPackage : IDisposable {
    internal Guid Id { get; }
    internal string Name { get; }
    internal string Author { get; }
    internal string CoverImagePath { get; }

    private bool _loading;

    internal IDalamudTextureWrap? CoverImage {
        get {
            if (this._loading) {
                return null;
            }

            if (Plugin.Instance.CoverImages.TryGet(this.CoverImagePath, out var img)) {
                return img;
            }

            this._loading = true;
            Task.Run(async () => {
                try {
                    await this.AttemptLoad();
                } finally {
                    this._loading = false;
                }
            });

            return null;
        }
    }

    internal List<HeliosphereMeta> InternalVariants { get; }
    internal IReadOnlyList<HeliosphereMeta> Variants => this.InternalVariants.ToImmutableList();

    internal InstalledPackage(Guid id, string name, string author, List<HeliosphereMeta> variants, string coverImagePath) {
        this.Id = id;
        this.Name = name;
        this.Author = author;
        this.CoverImagePath = coverImagePath;
        this.InternalVariants = variants;
    }

    public void Dispose() {
        // no-op
    }

    public override int GetHashCode() {
        return this.Id.GetHashCode();
    }

    public override bool Equals(object? obj) {
        return obj is InstalledPackage pkg && pkg.Id == this.Id;
    }

    private async Task AttemptLoad(CancellationToken token = default) {
        using var guard = await SemaphoreGuard.WaitAsync(Plugin.ImageLoadSemaphore, token);

        try {
            var img = await Plugin.Resilience.ExecuteAsync(async token => await this.AttemptLoadSingle(token), token);
            Plugin.Instance.CoverImages.AddOrUpdate(this.CoverImagePath, img);
        } catch {
            Plugin.Instance.CoverImages.AddOrUpdate(this.CoverImagePath, null);
            throw;
        }
    }

    private async Task<IDalamudTextureWrap?> AttemptLoadSingle(CancellationToken token = default) {
        byte[] bytes;
        try {
            bytes = await FileHelper.ReadAllBytesAsync(this.CoverImagePath, token);
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            return null;
        }

        var wrap = await ImageHelper.LoadImageAsync(Plugin.Instance.TextureProvider, bytes, token)
                   ?? throw new Exception("image was null");

        return wrap;
    }
}
