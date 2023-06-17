using Dalamud.Interface.Internal.Notifications;
using Heliosphere.Model.Generated;
using Heliosphere.Util;
using ImGuiNET;
using ImGuiScene;

namespace Heliosphere.Ui;

internal class PromptWindow : IDrawable {
    private Plugin Plugin { get; }
    private Guid PackageId { get; }
    private IInstallerWindow_GetVersion Info { get; }
    private Guid VersionId { get; }
    private string Version { get; }

    private bool _visible = true;
    private bool _includeTags;
    private string? _collection;
    private readonly string? _downloadKey;
    private readonly TextureWrap? _coverImage;

    private PromptWindow(Plugin plugin, Guid packageId, IInstallerWindow_GetVersion info, Guid versionId, string version, TextureWrap? coverImage, string? downloadKey) {
        this.Plugin = plugin;
        this.PackageId = packageId;
        this.Info = info;
        this.VersionId = versionId;
        this.Version = version;
        this._coverImage = coverImage;
        this._includeTags = this.Plugin.Config.IncludeTags;
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

        TextureWrap? cover = null;
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
                plugin.Name,
                NotificationType.Error
            );
        }
    }

    public bool Draw() {
        if (!this._visible) {
            return true;
        }

        if (!ImGui.Begin($"Install {this.Info.Variant.Package.Name} v{this.Version} by {this.Info.Variant.Package.User.Username}?###install-prompt-{this.PackageId}-{this.VersionId}", ref this._visible, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize)) {
            ImGui.End();
            return false;
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

        ImGui.TextUnformatted("Automatically enable in collection");
        ImGui.SetNextItemWidth(-1);
        ImGuiHelper.CollectionChooser(
            this.Plugin.Penumbra,
            "##collection",
            ref this._collection
        );

        var ret = false;

        if (ImGui.Button("Install")) {
            ret = true;
            var modDir = this.Plugin.Penumbra.GetModDirectory();
            if (!string.IsNullOrWhiteSpace(modDir)) {
                this.Plugin.AddDownload(new DownloadTask(this.Plugin, modDir, this.VersionId, this._includeTags, this._collection, this._downloadKey));
            }
        }

        if (this.Info.Groups.Count > 0) {
            ImGui.SameLine();

            var shiftHeld = ImGui.GetIO().KeyShift;
            using (ImGuiHelper.WithDisabled(!shiftHeld)) {
                if (ImGui.Button("Choose options to install")) {
                    ret = true;
                    Task.Run(async () => await InstallerWindow.OpenAndAdd(new InstallerWindow.OpenOptions {
                        Plugin = this.Plugin,
                        PackageId = this.PackageId,
                        VersionId = this.VersionId,
                        Info = this.Info,
                        IncludeTags = this._includeTags,
                        PenumbraCollection = this._collection,
                        DownloadKey = this._downloadKey,
                    }));
                }
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
                var text = "This is an advanced option.\n\nUsing this option will allow you to only partially install this mod, potentially breaking it. Only use this if you know what you're doing.";
                if (!shiftHeld) {
                    text += "\n\nHold the Shift key to enable this button.";
                }

                ImGuiHelper.Tooltip(text);
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel")) {
            ret = true;
        }

        ImGui.End();
        return ret;
    }
}
