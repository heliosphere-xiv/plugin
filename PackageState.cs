using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Logging;
using Heliosphere.Model;
using ImGuiScene;
using Newtonsoft.Json;
using WebP.Net.Natives;
using WebP.Net.Natives.Enums;
using WebP.Net.Natives.Structs;

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

            HeliosphereMeta meta;
            try {
                var text = await File.ReadAllTextAsync(metaPath);
                meta = JsonConvert.DeserializeObject<HeliosphereMeta>(text)!;
            } catch {
                continue;
            }

            if (meta.Id != packageId) {
                continue;
            }

            if (parts.Length == 4) {
                // no variant
                try {
                    (directory, parts) = await this.MigrateOldDirectory(meta, penumbraPath, directory);
                } catch (Exception ex) {
                    PluginLog.LogError(ex, "Error while migrating old directory");
                }
            }

            if (!int.TryParse(parts[^2], out var variantId)) {
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
                    if (imageBytes.Length >= 12 && imageBytes.AsSpan()[..4] == "RIFF"u8 && imageBytes.AsSpan()[8..12] == "WEBP"u8) {
                        // webp
                        byte[] outputBuffer;
                        WebPBitstreamFeatures features;
                        int stride;

                        unsafe {
                            fixed (byte* bytes = imageBytes) {
                                features = new WebPBitstreamFeatures();
                                var vp8StatusCode = Native.WebPGetFeatures((nint) bytes, imageBytes.Length, ref features);
                                if (vp8StatusCode != Vp8StatusCode.Ok) {
                                    throw new ExternalException(vp8StatusCode.ToString());
                                }

                                stride = features.Has_alpha > 0 ? 4 : 3;
                                var size = features.Height * features.Width * stride;
                                outputBuffer = new byte[size];
                                fixed (byte* output = outputBuffer) {
                                    if (features.Has_alpha > 0) {
                                        Native.WebPDecodeBgraInto((nint) bytes, imageBytes.Length, (nint) output, size, stride);
                                    } else {
                                        Native.WebPDecodeBgrInto((nint) bytes, imageBytes.Length, (nint) output, size, stride);
                                    }
                                }
                            }
                        }

                        coverImage = await this.Plugin.Interface.UiBuilder.LoadImageRawAsync(outputBuffer, features.Width, features.Height, stride);
                    } else {
                        // jpeg
                        coverImage = await this.Plugin.Interface.UiBuilder.LoadImageAsync(coverPath);
                    }
                } catch (Exception ex) {
                    PluginLog.LogError(ex, "Could not load cover image");
                }
            }

            this.InstalledInternal.Add(new Installed(meta, coverImage));
        }
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
