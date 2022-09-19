using Dalamud.Interface.Internal.Notifications;
using Dalamud.Logging;
using Heliosphere.Model.Generated;
using Heliosphere.Util;
using ImGuiNET;
using ImGuiScene;

namespace Heliosphere.Ui;

internal class PromptWindow : IDrawable {
    private Plugin Plugin { get; }
    private Guid PackageId { get; }
    private IInstallerWindow_GetVersion Info { get; }
    private int VersionId { get; }
    private string Version { get; }

    private bool _visible = true;
    private readonly TextureWrap? _coverImage;

    private PromptWindow(Plugin plugin, Guid packageId, IInstallerWindow_GetVersion info, int versionId, string version, TextureWrap? coverImage) {
        this.Plugin = plugin;
        this.PackageId = packageId;
        this.Info = info;
        this.VersionId = versionId;
        this.Version = version;
        this._coverImage = coverImage;
    }

    public void Dispose() {
        this._coverImage?.Dispose();
    }

    internal static async Task<PromptWindow> Open(Plugin plugin, Guid packageId, int versionId) {
        var info = await InstallerWindow.GetVersionInfo(versionId);
        if (info.Package.Id != packageId) {
            throw new Exception("Invalid package install URI.");
        }

        TextureWrap? cover = null;
        if (info.Package.Images.Count > 0) {
            var coverImage = info.Package.Images[0];

            try {
                var resp = await DownloadTask.GetImage(packageId, coverImage.Id);
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                cover = await plugin.Interface.UiBuilder.LoadImageAsync(bytes);
            } catch (Exception ex) {
                PluginLog.LogError(ex, $"Could not load cover image for package {packageId:N}");
            }
        }

        return new PromptWindow(plugin, packageId, info, versionId, info.Version, cover);
    }

    internal static async Task OpenAndAdd(Plugin plugin, Guid packageId, int versionId) {
        try {
            var window = await Open(plugin, packageId, versionId);
            await plugin.PluginUi.AddToDrawAsync(window);
        } catch (Exception ex) {
            PluginLog.LogError(ex, "Error opening prompt window");
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

        if (!ImGui.Begin($"Install {this.Info.Package.Name} v{this.Version} by {this.Info.Package.User.Username}?###install-prompt-{this.PackageId}-{this.VersionId}", ref this._visible, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize)) {
            ImGui.End();
            return false;
        }

        ImGuiHelper.TextUnformattedCentred(this.Info.Package.Name, PluginUi.TitleSize);

        if (this._coverImage != null) {
            var maxHeight = ImGui.GetContentRegionAvail().Y / 2;
            ImGuiHelper.ImageFullWidth(this._coverImage, maxHeight, true);
        }

        ImGui.TextUnformatted("Do you want to install this mod?");

        var info = new List<(string, string)>(3) {
            ("Name", this.Info.Package.Name),
            ("Author", this.Info.Package.User.Username),
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

        var ret = false;

        if (ImGui.Button("Install")) {
            ret = true;
            var modDir = this.Plugin.Penumbra.GetModDirectory();
            if (modDir != null) {
                this.Plugin.AddDownload(new DownloadTask(this.Plugin, modDir, this.VersionId));
            }
        }

        if (this.Info.Groups.Count > 0) {
            ImGui.SameLine();
            if (ImGui.Button("Choose options to install")) {
                ret = true;
                Task.Run(async () => await InstallerWindow.OpenAndAdd(new InstallerWindow.OpenOptions {
                    Plugin = this.Plugin,
                    PackageId = this.PackageId,
                    VersionId = this.VersionId,
                    Info = this.Info,
                }));
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
