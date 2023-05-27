using Dalamud.Interface.Internal.Notifications;
using Heliosphere.Model.Generated;
using Heliosphere.Util;
using ImGuiNET;
using ImGuiScene;

namespace Heliosphere.Ui;

internal class MultiPromptWindow : IDrawable {
    private Plugin Plugin { get; }
    private MultiPromptInfo[] Infos { get; }

    private bool _visible = true;
    private bool _includeTags;
    private string? _collection;

    private MultiPromptWindow(Plugin plugin, MultiPromptInfo[] infos) {
        this.Plugin = plugin;
        this.Infos = infos;

        this._includeTags = this.Plugin.Config.IncludeTags;
        this._collection = this.Plugin.Config.DefaultCollection;
    }

    public void Dispose() {
        foreach (var info in this.Infos) {
            info.Dispose();
        }
    }

    internal static async Task<MultiPromptWindow> Open(Plugin plugin, IEnumerable<InstallInfo> infos) {
        var retrieved = new List<MultiPromptInfo>();
        foreach (var info in infos) {
            var newInfo = await InstallerWindow.GetVersionInfo(info.VersionId);
            if (newInfo.Variant.Package.Id != info.PackageId) {
                throw new Exception("Invalid package install URI.");
            }

            TextureWrap? cover = null;
            if (newInfo.Variant.Package.Images.Count > 0) {
                var coverImage = newInfo.Variant.Package.Images[0];

                try {
                    using var resp = await DownloadTask.GetImage(info.PackageId, coverImage.Id);
                    var bytes = await resp.Content.ReadAsByteArrayAsync();
                    cover = await ImageHelper.LoadImageAsync(plugin.Interface.UiBuilder, bytes);
                } catch (Exception ex) {
                    ErrorHelper.Handle(ex, $"Could not load cover image for package {info.PackageId:N}");
                }
            }

            retrieved.Add(new MultiPromptInfo(
                info.PackageId,
                newInfo,
                info.VersionId,
                newInfo.Version,
                cover,
                info.DownloadCode
            ));
        }

        return new MultiPromptWindow(plugin, retrieved.ToArray());
    }

    internal static async Task OpenAndAdd(Plugin plugin, IEnumerable<InstallInfo> infos) {
        try {
            var window = await Open(plugin, infos);
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

        var modText = this.Infos.Length == 1 ? "mod" : "mods";
        var id = string.Join('-', this.Infos.Select(info => info.VersionId.ToString("N")));
        if (!ImGui.Begin($"Install {this.Infos.Length} {modText}?###multi-install-prompt-{id}", ref this._visible, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize)) {
            ImGui.End();
            return false;
        }

        ImGui.TextUnformatted("Do you want to install these mods?");

        if (ImGui.BeginTable("mod-info-multi-install-{id}", 4)) {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Author", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Variant", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthStretch);

            ImGui.TableNextRow();
            ImGui.TableHeadersRow();

            foreach (var info in this.Infos) {
                ImGui.TableNextRow();

                var data = new List<string>(4) {
                    info.Info.Variant.Package.Name,
                    info.Info.Variant.Package.User.Username,
                    info.Info.Variant.Name,
                    info.Info.Version,
                };

                for (var i = 0; i < data.Count; i++) {
                    if (ImGui.TableSetColumnIndex(i)) {
                        ImGui.TextUnformatted(data[i]);
                    }
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
                foreach (var info in this.Infos) {
                    this.Plugin.AddDownload(new DownloadTask(
                        this.Plugin,
                        modDir,
                        info.VersionId,
                        this._includeTags,
                        this._collection,
                        info.DownloadKey
                    ));
                }
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

internal class MultiPromptInfo : IDisposable {
    internal Guid PackageId { get; }
    internal IInstallerWindow_GetVersion Info { get; }
    internal Guid VersionId { get; }
    internal string Version { get; }

    internal string? DownloadKey { get; }
    internal TextureWrap? CoverImage { get; }

    internal MultiPromptInfo(Guid packageId, IInstallerWindow_GetVersion info, Guid versionId, string version, TextureWrap? coverImage, string? downloadKey) {
        this.PackageId = packageId;
        this.Info = info;
        this.VersionId = versionId;
        this.Version = version;
        this.CoverImage = coverImage;
        this.DownloadKey = downloadKey;
    }

    public void Dispose() {
        this.CoverImage?.Dispose();
    }
}
