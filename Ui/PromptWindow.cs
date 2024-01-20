using Dalamud.Interface.Internal;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Interface.Style;
using Heliosphere.Model.Generated;
using Heliosphere.Ui.Components;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui;

internal class PromptWindow : IDrawable {
    private Plugin Plugin { get; }
    private Guid PackageId { get; }
    private IInstallerWindow_GetVersion Info { get; }
    private Guid VersionId { get; }
    private string Version { get; }
    private ModChooser ModChooser { get; }

    private bool _visible = true;
    private bool _includeTags;
    private bool _openInPenumbra;
    private string? _collection;
    private readonly string? _downloadKey;
    private readonly IDalamudTextureWrap? _coverImage;

    private PromptWindow(Plugin plugin, Guid packageId, IInstallerWindow_GetVersion info, Guid versionId, string version, IDalamudTextureWrap? coverImage, string? downloadKey) {
        this.Plugin = plugin;
        this.PackageId = packageId;
        this.Info = info;
        this.VersionId = versionId;
        this.Version = version;
        this.ModChooser = new ModChooser(this.Plugin);
        this._coverImage = coverImage;
        this._includeTags = this.Plugin.Config.IncludeTags;
        this._openInPenumbra = this.Plugin.Config.OpenPenumbraAfterInstall;
        this._collection = this.Plugin.Config.DefaultCollection;
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
            plugin.Interface.UiBuilder.AddNotification(
                "Error opening installer prompt.",
                Plugin.Name,
                NotificationType.Error
            );
        }
    }

    public DrawStatus Draw() {
        if (!this._visible) {
            return DrawStatus.Continue;
        }

        if (!ImGui.Begin($"Install {this.Info.Variant.Package.Name} v{this.Version} by {this.Info.Variant.Package.User.Username}?###install-prompt-{this.PackageId}-{this.VersionId}", ref this._visible, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize)) {
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

        if (ImGui.Button("Install")) {
            ret = DrawStatus.Finished;
            if (this.Plugin.Penumbra.TryGetModDirectory(out var modDir)) {
                Task.Run(async () => await this.Plugin.AddDownloadAsync(new DownloadTask(this.Plugin, modDir, this.VersionId, this._includeTags, this._openInPenumbra, this._collection, this._downloadKey)));
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel")) {
            ret = DrawStatus.Finished;
        }

        if (this.Info.Groups.Count > 0 && ImGui.CollapsingHeader("Advanced options")) {
            using var popWrap = ImGuiHelper.TextWrap();

            if (ImGui.CollapsingHeader("Import from existing mod")) {
                if (this.ModChooser.Draw() is var (directory, name)) {
                    Plugin.Log.Info(directory);
                    Plugin.Log.Info(name);
                    // TODO: - run through all the files and hash them
                    //       - check if all the hashes are there
                    //       - display results to user (x/y expected files found, proceed?)
                    //       - rename files and delete any left over
                    //       - download any missing files
                    //       - create heliosphere meta
                    //       - replace group files
                    //       - remove and re-add mod in penumbra
                }
            }

            ImGuiHelper.TextUnformattedColour(
                $"If you already have this mod installed, you can use this to attempt to convert it to a {Plugin.Name} mod, skipping most or all of the download.",
                ImGuiCol.TextDisabled
            );

            // ---

            var shiftHeld = ImGui.GetIO().KeyShift;
            using (ImGuiHelper.WithDisabled(!shiftHeld)) {
                if (ImGui.Button("Choose options to install")) {
                    ret = DrawStatus.Finished;
                    Task.Run(async () => await InstallerWindow.OpenAndAdd(new InstallerWindow.OpenOptions {
                        Plugin = this.Plugin,
                        PackageId = this.PackageId,
                        VersionId = this.VersionId,
                        Info = this.Info,
                        IncludeTags = this._includeTags,
                        OpenInPenumbra = this._openInPenumbra,
                        PenumbraCollection = this._collection,
                        DownloadKey = this._downloadKey,
                    }));
                }
            }

            if (!shiftHeld) {
                ImGuiHelper.Tooltip("Hold the Shift key to enable this dangerous button.", ImGuiHoveredFlags.AllowWhenDisabled);
            }

            var model = StyleModel.GetConfiguredStyle() ?? StyleModel.GetFromCurrent();
            var orange = model.BuiltInColors?.DalamudOrange;

            const string warningText = "Warning! You likely do not want to use this option. This is for advanced users who know what they're doing. You are very likely to break mods if you use this option incorrectly.";

            if (orange == null) {
                ImGui.TextUnformatted(warningText);
            } else {
                ImGuiHelper.TextUnformattedColour(warningText, orange.Value);
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

        ImGui.End();
        return ret;
    }
}
