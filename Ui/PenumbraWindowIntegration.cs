using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Heliosphere.Model;
using Heliosphere.Ui.Tabs;
using Heliosphere.Util;

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

            ImGui.GetForegroundDrawList().AddImage(img.Handle, min, max);
        }
    }

    internal void PostEnabledDraw(string directory) {
        if (!this.Plugin.Config.Penumbra.ShowButtons) {
            return;
        }

        if (this.ParseDirectory(directory) is not var (pkg, meta)) {
            return;
        }

        ImGui.PushID($"heliosphere-penumbra-integration-{meta.VersionId}");
        using var popId = new OnDispose(ImGui.PopID);

        ImGui.Spacing();

        ImGui.BeginGroup();
        using (new OnDispose(ImGui.EndGroup)) {
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, ImGui.GetStyle().FramePadding);
            using var popStyle = new OnDispose(ImGui.PopStyleVar);

            var flags = this.Plugin.Config.Penumbra.ExpandSettingsDefault
                ? ImGuiTreeNodeFlags.DefaultOpen
                : ImGuiTreeNodeFlags.None;
            if (ImGui.TreeNodeEx("Heliosphere", flags)) {
                using var treePop = new OnDispose(ImGui.TreePop);

                if (ImGuiHelper.WideButton("Check for and download updates")) {
                    meta.DownloadUpdates(this.Plugin);
                }

                if (ImGuiHelper.WideButton("Open in Heliosphere")) {
                    this.Plugin.PluginUi.ForceOpen = PluginUi.Tab.Manager;
                    this.Plugin.PluginUi.ForceOpenVariant = meta.VariantId;
                    this.Plugin.PluginUi.Visible = true;
                }

                if (ImGui.TreeNodeEx("Update settings")) {
                    using var treePop2 = new OnDispose(ImGui.TreePop);

                    if (!this.Plugin.Config.PackageSettings.TryGetValue(meta.Id, out var settings)) {
                        settings = new() {
                            LoginUpdateMode = null,
                            Update = PackageSettings.UpdateSetting.Default,
                        };

                        this.Plugin.Config.PackageSettings[meta.Id] = settings;
                    }

                    var anyChanged = false;

                    ImGui.TextUnformatted("Login update behaviour");
                    ImGui.SameLine();
                    ImGuiHelper.Help("Controls if this mod should be checked for updates/have updates applied on login. Overrides the global setting.");

                    anyChanged |= ImGuiHelper.LoginUpdateModeCombo("##login-behaviour-combo", false, ref settings.LoginUpdateMode);

                    ImGui.TextUnformatted("Manual update behaviour");
                    ImGui.SameLine();
                    ImGuiHelper.Help("Controls what this mod will do when you manually run updates.");

                    anyChanged |= ImGuiHelper.ManualUpdateModeCombo("##manual-update-combo", false, ref settings.Update);

                    if (anyChanged) {
                        this.Plugin.SaveConfig();
                    }
                }
            }
        }

        if (ImGui.BeginPopupContextItem("context")) {
            using var endPopup = new OnDispose(ImGui.EndPopup);

            var anyChanged = Settings.DrawPenumbraIntegrationSettings(this.Plugin);
            if (anyChanged) {
                this.Plugin.SaveConfig();
            }
        }

        ImGui.Spacing();
    }
}
