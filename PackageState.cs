using System.Collections.Immutable;
using System.Text;
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

        // dispose and remove existing packages
        foreach (var (_, package) in guard.Data) {
            package.Dispose();
        }

        guard.Data.Clear();

        if (this.PenumbraPath is not { } penumbraPath) {
            return;
        }

        var dirs = Directory.EnumerateDirectories(penumbraPath)
            .Select(Path.GetFileName)
            .Where(dir => !string.IsNullOrEmpty(dir))
            .Cast<string>()
            .Where(dir => dir.StartsWith("hs-"));

        foreach (var dir in dirs) {
            try {
                await this.LoadPackage(dir, penumbraPath, guard);
            } catch (Exception ex) {
                ErrorHelper.Handle(ex, "Could not load package");
            }
        }
    }

    private async Task LoadPackage(string directory, string penumbraPath, Guard<Dictionary<Guid, InstalledPackage>>.GuardHandle guard) {
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

        var coverPath = Path.Join(penumbraPath, directory, "cover.jpg");
        TextureWrap? coverImage = null;
        if (File.Exists(coverPath)) {
            try {
                var imageBytes = await File.ReadAllBytesAsync(coverPath);
                coverImage = await ImageHelper.LoadImageAsync(this.Plugin.Interface.UiBuilder, imageBytes);
            } catch (Exception ex) {
                ErrorHelper.Handle(ex, "Could not load cover image");
            }
        }

        InstalledPackage package;
        if (guard.Data.TryGetValue(meta.Id, out var existing)) {
            package = existing;
            existing.InternalVariants.Add(meta);
        } else {
            package = new InstalledPackage(
                meta.Id,
                meta.Name,
                meta.Author,
                new List<HeliosphereMeta> { meta },
                coverImage
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
    internal Guid Id { get; }
    internal string Name { get; }
    internal string Author { get; }
    internal TextureWrap? CoverImage { get; }
    internal List<HeliosphereMeta> InternalVariants { get; }
    internal IReadOnlyList<HeliosphereMeta> Variants => this.InternalVariants.ToImmutableList();

    internal InstalledPackage(Guid id, string name, string author, List<HeliosphereMeta> variants, TextureWrap? coverImage) {
        this.Id = id;
        this.Name = name;
        this.Author = author;
        this.InternalVariants = variants;
        this.CoverImage = coverImage;
    }

    public void Dispose() {
        this.CoverImage?.Dispose();
    }

    public override int GetHashCode() {
        return this.Id.GetHashCode();
    }

    public override bool Equals(object? obj) {
        return obj is InstalledPackage pkg && pkg.Id == this.Id;
    }
}
