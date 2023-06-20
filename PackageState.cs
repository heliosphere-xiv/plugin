using System.Collections.Immutable;
using System.Text;
using Blake3;
using Dalamud.Logging;
using Heliosphere.Model;
using Heliosphere.Util;
using ImGuiScene;
using Newtonsoft.Json;

namespace Heliosphere;

internal class PackageState : IDisposable {
    private Plugin Plugin { get; }

    private string? PenumbraPath => this.Plugin.Penumbra.GetModDirectory();
    private Guard<Dictionary<Guid, InstalledPackage>> InstalledInternal { get; } = new(new Dictionary<Guid, InstalledPackage>());

    internal int DirectoriesToScan = -1;
    internal int CurrentDirectory;

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
                return ImmutableDictionary.Create<Guid, InstalledPackage>();
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
        this.InstalledInternal.Dispose();
    }

    internal async Task UpdatePackages() {
        using var guard = await this.InstalledInternal.WaitAsync();

        var numPreviouslyInstalled = guard.Data.Count;

        // dispose and remove existing packages
        foreach (var (_, package) in guard.Data) {
            package.Dispose();
        }

        guard.Data.Clear();

        if (this.PenumbraPath is not { } penumbraPath) {
            return;
        }

        using (var cached = await InstalledPackage.CoverImages.WaitAsync()) {
            // more images are cached than mods were installed, clear cache
            if (numPreviouslyInstalled < cached.Data.Count) {
                PluginLog.LogVerbose("clearing cover image cache");

                foreach (var wrap in cached.Data.Values) {
                    wrap.Dispose();
                }

                cached.Data.Clear();
            }
        }

        var dirs = Directory.EnumerateDirectories(penumbraPath)
            .Select(Path.GetFileName)
            .Where(dir => !string.IsNullOrEmpty(dir))
            .Cast<string>()
            .Where(dir => dir.StartsWith("hs-"))
            .ToList();

        Interlocked.Exchange(ref this.CurrentDirectory, 0);
        Interlocked.Exchange(ref this.DirectoriesToScan, dirs.Count);

        foreach (var dir in dirs) {
            Interlocked.Increment(ref this.CurrentDirectory);

            try {
                await this.LoadPackage(dir, penumbraPath, guard);
            } catch (Exception ex) {
                ErrorHelper.Handle(ex, "Could not load package");
            }
        }

        Interlocked.Exchange(ref this.DirectoriesToScan, -1);
    }

    private async Task LoadPackage(string directory, string penumbraPath, Guard<Dictionary<Guid, InstalledPackage>>.Handle guard) {
        var parts = directory.Split('-');
        if (parts.Length < 1) {
            return;
        }

        if (!Guid.TryParse(parts[^1], out var packageId)) {
            return;
        }

        var metaPath = Path.Join(penumbraPath, directory, "heliosphere.json");
        if (!File.Exists(metaPath)) {
            return;
        }

        HeliosphereMeta? meta;
        try {
            meta = await HeliosphereMeta.Load(metaPath);
        } catch (Exception ex) {
            ErrorHelper.Handle(ex, "Could not load heliosphere.json");
            return;
        }

        if (meta == null || meta.Id != packageId) {
            return;
        }

        if (parts.Length == 4) {
            // no variant
            try {
                (directory, parts) = await this.MigrateOldDirectory(meta, penumbraPath, directory);
            } catch (Exception ex) {
                ErrorHelper.Handle(ex, "Error while migrating old directory");
            }
        }

        // always make sure path is correct
        await this.RenameDirectory(meta, penumbraPath, directory);

        if (!Guid.TryParse(parts[^2], out var variantId)) {
            return;
        }

        if (meta.VariantId != variantId) {
            return;
        }

        InstalledPackage package;
        if (guard.Data.TryGetValue(meta.Id, out var existing)) {
            package = existing;
            existing.InternalVariants.Add(meta);
        } else {
            var coverPath = Path.Join(penumbraPath, directory, "cover.jpg");

            package = new InstalledPackage(
                meta.Id,
                meta.Name,
                meta.Author,
                new List<HeliosphereMeta> { meta },
                coverPath
            );
        }

        guard.Data[meta.Id] = package;
    }

    private async Task RenameDirectory(HeliosphereMeta meta, string penumbraPath, string directory) {
        var correctName = meta.ModDirectoryName();
        if (directory == correctName) {
            return;
        }

        PluginLog.Log($"Fixing incorrect folder name for {directory}");

        var oldPath = Path.Join(penumbraPath, directory);
        var newPath = Path.Join(penumbraPath, correctName);
        if (Directory.Exists(newPath)) {
            throw new ModAlreadyExistsException(oldPath, newPath);
        }

        PluginLog.Debug($"    {oldPath} -> {newPath}");
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

    private async Task<(string, string[])> MigrateOldDirectory(HeliosphereMeta meta, string penumbraPath, string directory) {
        PluginLog.Debug($"Migrating old folder name layout for {directory}");
        var variant = await Plugin.GraphQl.GetVariant.ExecuteAsync(meta.VersionId);
        if (variant.Data?.GetVersion == null) {
            throw new Exception($"no variant for version id {meta.VersionId}");
        }

        meta.Variant = variant.Data.GetVersion.Variant.Name;
        meta.VariantId = variant.Data.GetVersion.Variant.Id;

        var newName = meta.ModDirectoryName();
        var oldPath = Path.Join(penumbraPath, directory);
        var newPath = Path.Join(penumbraPath, newName);

        var parts = newName.Split('-');

        PluginLog.Debug($"    {oldPath} -> {newPath}");
        Directory.Move(oldPath, newPath);
        await this.Plugin.Framework.RunOnFrameworkThread(() => {
            this.Plugin.Penumbra.AddMod(newName);
            this.Plugin.Penumbra.ReloadMod(directory);
        });

        PluginLog.Debug("    writing new meta");
        var json = JsonConvert.SerializeObject(meta, Formatting.Indented);
        var path = Path.Join(penumbraPath, newName, "heliosphere.json");
        await using var file = File.Create(path);
        await file.WriteAsync(Encoding.UTF8.GetBytes(json));

        return (newName, parts);
    }
}

internal class ModAlreadyExistsException : Exception {
    private string OldPath { get; }
    private string NewPath { get; }
    public override string Message => $"Could not move old mod to new path because new path already exists ({this.OldPath} -> {this.NewPath})";

    public ModAlreadyExistsException(string oldPath, string newPath) {
        this.OldPath = oldPath;
        this.NewPath = newPath;
    }
}

internal class InstalledPackage : IDisposable {
    internal static Guard<Dictionary<string, TextureWrap>> CoverImages { get; } = new(new Dictionary<string, TextureWrap>());

    internal Guid Id { get; }
    internal string Name { get; }
    internal string Author { get; }
    private string CoverImagePath { get; }

    internal TextureWrap? CoverImage { get; private set; }

    internal List<HeliosphereMeta> InternalVariants { get; }
    internal IReadOnlyList<HeliosphereMeta> Variants => this.InternalVariants.ToImmutableList();

    private int _coverImageAttempts;

    internal InstalledPackage(Guid id, string name, string author, List<HeliosphereMeta> variants, string coverImagePath) {
        this.Id = id;
        this.Name = name;
        this.Author = author;
        this.CoverImagePath = coverImagePath;
        this.InternalVariants = variants;

        Task.Run(async () => await this.AttemptLoad());
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

    private async Task AttemptLoad() {
        using var guard = await SemaphoreGuard.WaitAsync(Plugin.ImageLoadSemaphore);

        while (this._coverImageAttempts < 3) {
            if (this.CoverImage != null) {
                return;
            }

            this._coverImageAttempts += 1;

            try {
                if (await this.AttemptLoadSingle()) {
                    return;
                }
            } catch (Exception ex) {
                ErrorHelper.Handle(ex, "Could not load image");
            }
        }
    }

    private async Task<bool> AttemptLoadSingle() {
        if (!File.Exists(this.CoverImagePath)) {
            return true;
        }

        var bytes = await File.ReadAllBytesAsync(this.CoverImagePath);

        using var blake3 = new Blake3HashAlgorithm();
        blake3.Initialize();
        var hash = Convert.ToBase64String(blake3.ComputeHash(bytes));

        using (var guard = await CoverImages.WaitAsync()) {
            if (guard.Data.TryGetValue(hash, out var cached)) {
                this.CoverImage = cached;
                return true;
            }
        }

        var wrap = await ImageHelper.LoadImageAsync(Plugin.Instance.Interface.UiBuilder, bytes);
        if (wrap == null) {
            return false;
        }

        using (var guard = await CoverImages.WaitAsync()) {
            guard.Data[hash] = wrap;
        }

        this.CoverImage = wrap;
        return true;
    }
}
