using Heliosphere.Model;
using Heliosphere.Util;
using ImGuiNET;
using OtterGui.Widgets;

namespace Heliosphere.Ui;

internal class PenumbraWindowIntegration {
    private Plugin Plugin { get; }

    internal PenumbraWindowIntegration(Plugin plugin) {
        this.Plugin = plugin;
    }

    private (InstalledPackage Package, HeliosphereMeta Meta)? ParseDirectory(string directory) {
        if (HeliosphereMeta.ParseDirectory(Path.GetFileName(directory)) is not { } info) {
            return null;
        }

        if (!this.Plugin.State.InstalledNoBlock.TryGetValue(info.PackageId, out var pkg)) {
            return null;
        }

        var meta = pkg.Variants.FirstOrDefault(v => v.VariantId == info.VariantId);
        if (meta == null) {
            return null;
        }

        return (pkg, meta);
    }

    internal void PreSettingsTabBarDraw(string directory, float width, float titleWidth) {
        if (this.ParseDirectory(directory) is not var (pkg, meta)) {
            return;
        }

        if (pkg.CoverImage is { } img) {
            var maxHeight = width * 16 / 9;
            ImGuiHelper.ImageFullWidth(img, maxHeight, true);

            if (ImGui.IsItemHovered()) {
                ImGui.BeginTooltip();
                using var endTooltip = new OnDispose(ImGui.EndTooltip);

                ImGuiHelper.ImageFullWidth(img);
            }
        }
    }

    internal void PostEnabledDraw(string directory) {
        if (this.ParseDirectory(directory) is not var (pkg, meta)) {
            return;
        }

        Widget.BeginFramedGroup("Heliosphere");
        using (new OnDispose(() => Widget.EndFramedGroup())) {
            if (ImGui.Button("Download updates")) {
                meta.DownloadUpdates(this.Plugin);
            }
        }

        ImGui.Spacing();
    }
}