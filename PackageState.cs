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
    private SemaphoreSlim InstalledLock { get; } = new(1, 1);
    private List<Installed> InstalledInternal { get; } = new();

    internal IReadOnlyList<Installed> Installed {
        get {
            this.InstalledLock.Wait();
            try {
                return this.InstalledInternal.ToImmutableList();
            } finally {
                this.InstalledLock.Release();
            }
        }
    }

    internal PackageState(Plugin plugin) {
        this.Plugin = plugin;
    }

    public void Dispose() {
        this.InstalledLock.Dispose();
    }

    internal async Task UpdatePackages() {
        await this.InstalledLock.WaitAsync();
        try {
            await this.UpdatePackagesInternal();
        } finally {
            this.InstalledLock.Release();
        }
    }

    private async Task UpdatePackagesInternal() {
        this.InstalledInternal.RemoveAll(entry => {
            entry.Dispose();
            return true;
        });

        if (this.PenumbraPath is not { } penumbraPath) {
            return;
        }

        var dirs = Directory.EnumerateDirectories(penumbraPath)
            .Select(Path.GetFileName)
            .Where(dir => !string.IsNullOrEmpty(dir))
            .Cast<string>()
            .Where(dir => dir.StartsWith("hs-"));

        foreach (var dir in dirs) {
            var directory = dir;

            var parts = directory.Split('-');
            if (parts.Length < 1) {
                continue;
            }

            if (!Guid.TryParse(parts[^1], out var packageId)) {
                continue;
            }

            var metaPath = Path.Join(penumbraPath, directory, "heliosphere.json");
            if (!File.Exists(metaPath)) {
                continue;
            }

            var meta = await HeliosphereMeta.Load(metaPath);
            if (meta == null || meta.Id != packageId) {
                continue;
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
                continue;
            }

            if (meta.VariantId != variantId) {
                continue;
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

            this.InstalledInternal.Add(new Installed(meta, coverImage));
        }
    }

    private async Task RenameDirectory(HeliosphereMeta meta, string penumbraPath, string directory) {
        var correctName = meta.ModDirectoryName();
        if (directory == correctName) {
            return;
        }

        PluginLog.Log($"Fixing incorrect folder name for {directory}");

        var oldPath = Path.Join(penumbraPath, directory);
        var newPath = Path.Join(penumbraPath, correctName);

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

internal class Installed : IDisposable {
    internal HeliosphereMeta Meta { get; }
    internal TextureWrap? CoverImage { get; }

    internal Installed(HeliosphereMeta meta, TextureWrap? coverImage) {
        this.Meta = meta;
        this.CoverImage = coverImage;
    }

    public void Dispose() {
        this.CoverImage?.Dispose();
    }
}
