using System.Diagnostics;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Heliosphere.Model;
using Heliosphere.Ui.Tabs;
using Heliosphere.Util;
using ImGuiNET;

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

        var found = this.Plugin.State.InstalledNoBlock.Values
            .Select(pkg => new { Package = pkg, HeliosphereMeta = pkg.Variants.FirstOrDefault(variant => variant.ShortVariantId == info.ShortVariantId) })
            .FirstOrDefault(pair => pair.HeliosphereMeta != null);
        if (found?.HeliosphereMeta == null) {
            return null;
        }

        return (found.Package, found.HeliosphereMeta);
    }

    internal void PreSettingsTabBarDraw(string directory, float width, float titleWidth) {
        if (!this.Plugin.Config.Penumbra.ShowImages) {
            return;
        }

        if (this.ParseDirectory(directory) is not var (pkg, meta)) {
            return;
        }

        if (pkg.CoverImage is not { } img) {
            return;
        }

        var maxHeight = width * this.Plugin.Config.Penumbra.ImageSize;

        ImGuiHelper.ImageFullWidth(img, maxHeight, true);

        if (ImGui.IsItemHovered()) {
            ImGui.BeginTooltip();
            using var endTooltip = new OnDispose(ImGui.EndTooltip);

            ImGui.TextUnformatted("Click to open this image. Hold right-click to zoom.");
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) {
            Process.Start(new ProcessStartInfo(pkg.CoverImagePath) {
                UseShellExecute = true,
            });
        }

        if (ImGui.IsItemHovered() && ImGui.IsMouseDown(ImGuiMouseButton.Right)) {
            var winSize = ImGuiHelpers.MainViewport.WorkSize;
            var imgSize = new Vector2(img.Width, img.Height);

            if (imgSize.X > winSize.X || imgSize.Y > winSize.Y) {
                var ratio = Math.Min(winSize.X / img.Width, winSize.Y / img.Height);
                imgSize *= ratio;
            }

            var min = new Vector2(winSize.X / 2 - imgSize.X / 2, winSize.Y / 2 - imgSize.Y / 2);
            var max = new Vector2(winSize.X / 2 + imgSize.X / 2, winSize.Y / 2 + imgSize.Y / 2);

            ImGui.GetForegroundDrawList().AddImage(img.ImGuiHandle, min, max);
        }
    }

    internal void PostEnabledDraw(string directory) {
        if (!this.Plugin.Config.Penumbra.ShowButtons) {
            return;
        }

        if (this.ParseDirectory(directory) is not var (pkg, meta)) {
            return;
        }

        ImGui.Spacing();

        var cursor = ImGui.GetCursorPos();
        ImGuiHelper.BeginFramedGroup("Heliosphere");
        using (new OnDispose(() => ImGuiHelper.EndFramedGroup())) {
            if (ImGuiHelper.WideButton("Check for and download updates")) {
                meta.DownloadUpdates(this.Plugin);
            }

            if (ImGuiHelper.WideButton("Open in Heliosphere")) {
                this.Plugin.PluginUi.ForceOpen = PluginUi.Tab.Manager;
                this.Plugin.PluginUi.ForceOpenVariant = meta.VariantId;
                this.Plugin.PluginUi.Visible = true;
            }
        }

        var afterCursor = ImGui.GetCursorPos();

        var groupWidth = ImGui.GetItemRectSize().X;
        ImGui.SetCursorPos(cursor with {
            X = cursor.X + groupWidth + ImGui.GetStyle().ItemSpacing.X,
        });

        var popupId = $"penumbra-{meta.VersionId}-hs-settings";
        if (ImGuiHelper.IconButton(FontAwesomeIcon.ChevronDown)) {
            ImGui.OpenPopup(popupId);
        }

        if (ImGui.BeginPopup(popupId)) {
            using var endPopup = new OnDispose(ImGui.EndPopup);

            var anyChanged = Settings.DrawPenumbraIntegrationSettings(this.Plugin);
            if (anyChanged) {
                this.Plugin.SaveConfig();
            }
        }

        ImGui.SetCursorPos(afterCursor);

        ImGui.Spacing();
    }
}
