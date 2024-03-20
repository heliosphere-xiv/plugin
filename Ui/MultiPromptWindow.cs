using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Internal.Notifications;
using Heliosphere.Model.Generated;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui;

internal class MultiPromptWindow : IDrawable {
    private Guid WindowId { get; } = Guid.NewGuid();
    private Plugin Plugin { get; }
    private MultiPromptInfo[] Infos { get; }

    private bool _visible = true;
    private bool _includeTags;
    private bool _openInPenumbra;
    private string? _collection;

    private MultiPromptWindow(Plugin plugin, MultiPromptInfo[] infos) {
        this.Plugin = plugin;
        this.Infos = infos;

        this._includeTags = this.Plugin.Config.IncludeTags;
        this._openInPenumbra = this.Plugin.Config.OpenPenumbraAfterInstall;
        this._collection = this.Plugin.Config.DefaultCollection;
    }

    public void Dispose() {
    }

    internal static async Task<MultiPromptWindow> Open(Plugin plugin, IEnumerable<InstallInfo> infos) {
        var retrieved = new List<MultiPromptInfo>();
        foreach (var info in infos) {
            var newInfo = await InstallerWindow.GetVersionInfo(info.VersionId);
            if (newInfo.Variant.Package.Id != info.PackageId) {
                throw new Exception("Invalid package install URI.");
            }

            retrieved.Add(new MultiPromptInfo(
                info.PackageId,
                newInfo,
                info.VersionId,
                newInfo.Version,
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
            plugin.NotificationManager.AddNotification(new Notification {
                Type = NotificationType.Error,
                Content = "Error opening installer prompt.",
            });
        }
    }

    public DrawStatus Draw() {
        if (!this._visible) {
            return DrawStatus.Finished;
        }

        ImGui.PushID(this.WindowId.ToString());
        using var popId = new OnDispose(ImGui.PopID);

        var modText = this.Infos.Length == 1 ? "mod" : "mods";
        var id = string.Join('-', this.Infos.Select(info => info.VersionId.ToString("N")));
        if (!ImGui.Begin($"Install {this.Infos.Length} {modText}?###multi-install-prompt-{id}-{this.WindowId}", ref this._visible, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize)) {
            ImGui.End();
            return DrawStatus.Continue;
        }

        ImGui.TextUnformatted("Do you want to install these mods?");

        if (ImGui.BeginTable($"mod-info-multi-install-{id}", 4)) {
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
        ImGui.Checkbox("Open first mod in Penumbra after install", ref this._openInPenumbra);

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
                foreach (var info in this.Infos) {
                    Task.Run(async () => await this.Plugin.AddDownloadAsync(new DownloadTask(
                        this.Plugin,
                        modDir,
                        info.VersionId,
                        this._includeTags,
                        info.VersionId == this.Infos[0].VersionId,
                        this._collection,
                        info.DownloadKey
                    )));
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel")) {
            ret = DrawStatus.Finished;
        }

        ImGui.End();
        return ret;
    }
}

internal class MultiPromptInfo {
    internal Guid PackageId { get; }
    internal IInstallerWindow_GetVersion Info { get; }
    internal Guid VersionId { get; }
    internal string Version { get; }

    internal string? DownloadKey { get; }

    internal MultiPromptInfo(Guid packageId, IInstallerWindow_GetVersion info, Guid versionId, string version, string? downloadKey) {
        this.PackageId = packageId;
        this.Info = info;
        this.VersionId = versionId;
        this.Version = version;
        this.DownloadKey = downloadKey;
    }
}
