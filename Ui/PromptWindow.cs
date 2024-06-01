using System.Numerics;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Internal.Notifications;
using Heliosphere.Model.Generated;
using Heliosphere.Ui.Components;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui;

internal class PromptWindow : IDrawable {
    private Guid WindowId { get; } = Guid.NewGuid();
    private Plugin Plugin { get; }
    private Guid PackageId { get; }
    private Guid VariantId { get; }
    private Guid VersionId { get; }
    private IInstallerWindow_GetVersion Info { get; }
    private string Version { get; }
    private Importer Importer { get; }

    private bool _visible = true;
    private bool _includeTags;
    private bool _openInPenumbra;
    private Guid? _collection;
    private readonly string? _downloadKey;
    private readonly IDalamudTextureWrap? _coverImage;

    private PromptWindow(Plugin plugin, Guid packageId, IInstallerWindow_GetVersion info, Guid versionId, string version, IDalamudTextureWrap? coverImage, string? downloadKey) {
        this.Plugin = plugin;
        this.PackageId = packageId;
        this.Info = info;
        this.VersionId = versionId;
        this.Version = version;
        this.Importer = new Importer(
            this.Plugin,
            this.Info.Variant.Package.Name,
            this.Info.Variant.Package.Id,
            this.Info.Variant.Id,
            this.VersionId,
            this.Info.Version,
            this._downloadKey
        );
        this._coverImage = coverImage;
        this._includeTags = this.Plugin.Config.IncludeTags;
        this._openInPenumbra = this.Plugin.Config.OpenPenumbraAfterInstall;
        this._collection = this.Plugin.Config.DefaultCollectionId;
        this._downloadKey = downloadKey;
    }

    public void Dispose() {
        this._coverImage?.Dispose();
    }

    internal static async Task<PromptWindow> Open(Plugin plugin, Guid packageId, Guid versionId, string? downloadKey) {
        var info = await InstallerWindow.GetVersionInfo(versionId);
        if (info.Variant.Package.Id != packageId) {
            throw new Exception("Invalid package install URI.");
        }

        IDalamudTextureWrap? cover = null;
        if (info.Variant.Package.Images.Count > 0) {
            var coverImage = info.Variant.Package.Images[0];

            try {
                using var resp = await DownloadTask.GetImage(packageId, coverImage.Id);
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                cover = await ImageHelper.LoadImageAsync(plugin.Interface.UiBuilder, bytes);
            } catch (Exception ex) {
                ErrorHelper.Handle(ex, $"Could not load cover image for package {packageId:N}");
            }
        }

        return new PromptWindow(plugin, packageId, info, versionId, info.Version, cover, downloadKey);
    }

    internal static async Task OpenAndAdd(Plugin plugin, Guid packageId, Guid versionId, string? downloadKey) {
        try {
            var window = await Open(plugin, packageId, versionId, downloadKey);
            await plugin.PluginUi.AddToDrawAsync(window);
        } catch (Exception ex) {
            ErrorHelper.Handle(ex, "Error opening prompt window");
            plugin.NotificationManager.AddNotification(new Notification {
                Type = NotificationType.Error,
                Content = "Error opening installer prompt.",
                InitialDuration = TimeSpan.FromSeconds(5),
            });
        }
    }

    public DrawStatus Draw() {
        if (!this._visible) {
            return DrawStatus.Continue;
        }

        ImGui.PushID(this.WindowId.ToString());
        using var popId = new OnDispose(ImGui.PopID);

        ImGui.SetNextWindowSizeConstraints(
            new Vector2(350, 0),
            new Vector2(float.MaxValue)
        );

        if (!ImGui.Begin($"Install {this.Info.Variant.Package.Name} v{this.Version} by {this.Info.Variant.Package.User.Username}?###install-prompt-{this.PackageId}-{this.VersionId}-{this.WindowId}", ref this._visible, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize)) {
            ImGui.End();
            return DrawStatus.Continue;
        }

        ImGuiHelper.TextUnformattedCentred(this.Info.Variant.Package.Name, PluginUi.TitleSize);

        if (this._coverImage != null) {
            var maxHeight = ImGui.GetContentRegionAvail().Y / 2;
            ImGuiHelper.ImageFullWidth(this._coverImage, maxHeight, true);
        }

        ImGui.TextUnformatted("Do you want to install this mod?");

        var info = new List<(string, string)>(4) {
            ("Name", this.Info.Variant.Package.Name),
            ("Author", this.Info.Variant.Package.User.Username),
            ("Variant", this.Info.Variant.Name),
            ("Version", this.Version),
        };

        if (ImGui.BeginTable($"mod-info-install-{this.PackageId}-{this.VersionId}", 2)) {
            ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch);

            foreach (var (key, value) in info) {
                ImGui.TableNextRow();

                if (ImGui.TableSetColumnIndex(0)) {
                    ImGui.TextUnformatted(key);
                }

                if (ImGui.TableSetColumnIndex(1)) {
                    ImGui.TextUnformatted(value);
                }
            }

            ImGui.EndTable();
        }

        ImGui.Checkbox("Include tags in Penumbra", ref this._includeTags);
        ImGui.Checkbox("Open in Penumbra after install", ref this._openInPenumbra);

        ImGui.TextUnformatted("Automatically enable in collection");
        ImGui.SetNextItemWidth(-1);
        ImGuiHelper.CollectionChooser(
            this.Plugin.Penumbra,
            "##collection",
            ref this._collection
        );

        var ret = DrawStatus.Continue;

        var widthAvail = ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X;
        if (ImGui.Button("Install", new Vector2(widthAvail / 2, 0))) {
            ret = DrawStatus.Finished;
            if (this.Plugin.Penumbra.TryGetModDirectory(out var modDir)) {
                Task.Run(async () => await this.Plugin.AddDownloadAsync(new DownloadTask {
                    Plugin = this.Plugin,
                    ModDirectory = modDir,
                    PackageId = this.PackageId,
                    VariantId = this.VariantId,
                    VersionId = this.VersionId,
                    IncludeTags = this._includeTags,
                    OpenInPenumbra = this._openInPenumbra,
                    PenumbraCollection = this._collection,
                    DownloadKey = this._downloadKey,
                    Full = true,
                    Options = [],
                }));
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(widthAvail / 2, 0))) {
            ret = DrawStatus.Finished;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Advanced options")) {
            using var popWrap = ImGuiHelper.TextWrap();

            if (this.Importer.Draw()) {
                ret = DrawStatus.Finished;
            }

            // ---

            if (this.Info.BasicGroups.Count > 0 && ImGui.CollapsingHeader("Choose options to install")) {
                var shiftHeld = ImGui.GetIO().KeyShift;
                using (ImGuiHelper.DisabledUnless(shiftHeld)) {
                    if (ImGuiHelper.FullWidthButton("Choose options to install##actual-button")) {
                        ret = DrawStatus.Finished;
                        Task.Run(async () => await InstallerWindow.OpenAndAdd(new InstallerWindow.OpenOptions {
                            Plugin = this.Plugin,
                            PackageId = this.PackageId,
                            VariantId = this.VariantId,
                            VersionId = this.VersionId,
                            Info = this.Info,
                            IncludeTags = this._includeTags,
                            OpenInPenumbra = this._openInPenumbra,
                            PenumbraCollection = this._collection,
                            DownloadKey = this._downloadKey,
                            SelectedOptions = null,
                            FullInstall = false,
                        }));
                    }
                }

                if (!shiftHeld) {
                    ImGuiHelper.Tooltip("Hold the Shift key to enable this dangerous button.", ImGuiHoveredFlags.AllowWhenDisabled);
                }

                using (ImGuiHelper.WithWarningColour()) {
                    ImGui.TextUnformatted("Warning! You likely do not want to use this option. This is for advanced users who know what they're doing. You are very likely to break mods if you use this option incorrectly.");
                }

                ImGuiHelper.TextUnformattedColour(
                    "Choose specific options to download and install. This may result in a partial or invalid mod install if not used correctly.",
                    ImGuiCol.TextDisabled
                );

                ImGuiHelper.TextUnformattedColour(
                    "If you install a mod using this option, please do not look for support; reinstall the mod normally first.",
                    ImGuiCol.TextDisabled
                );
            }
        }

        ImGui.End();
        return ret;
    }
}
