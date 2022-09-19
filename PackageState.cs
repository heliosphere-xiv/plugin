using System.Collections.Immutable;
using Dalamud.Logging;
using Heliosphere.Model;
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
            var parts = dir.Split('-');
            if (parts.Length < 1) {
                continue;
            }

            if (!Guid.TryParse(parts[^1], out var id)) {
                continue;
            }

            var metaPath = Path.Join(penumbraPath, dir, "heliosphere.json");
            if (!File.Exists(metaPath)) {
                continue;
            }

            HeliosphereMeta meta;
            try {
                var text = await File.ReadAllTextAsync(metaPath);
                meta = JsonConvert.DeserializeObject<HeliosphereMeta>(text)!;
            } catch {
                continue;
            }

            if (meta.Id != id) {
                continue;
            }

            var coverPath = Path.Join(penumbraPath, dir, "cover.jpg");
            TextureWrap? coverImage = null;
            if (File.Exists(coverPath)) {
                try {
                    coverImage = await this.Plugin.Interface.UiBuilder.LoadImageAsync(coverPath);
                } catch (Exception ex) {
                    PluginLog.LogError(ex, "Could not load cover image");
                }
            }

            this.InstalledInternal.Add(new Installed(meta, coverImage));
        }
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
