using Dalamud.Interface.ImGuiNotification;
using Heliosphere.Model.Api;
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
    private Guid? _collection;

    private MultiPromptWindow(Plugin plugin, MultiPromptInfo[] infos) {
        this.Plugin = plugin;
        this.Infos = infos;

        this._includeTags = this.Plugin.Config.IncludeTags;
        this._openInPenumbra = this.Plugin.Config.OpenPenumbraAfterInstall;
        this._collection = this.Plugin.Config.DefaultCollectionId;
    }

    public void Dispose() {
    }

    internal static async Task<MultiPromptWindow> Open(Plugin plugin, IEnumerable<InstallInfo> infos, CancellationToken token = default) {
        var retrieved = new List<MultiPromptInfo>();
        foreach (var info in infos) {
            var newInfo = await GraphQl.GetBasicInfo(info.VersionId, token);
            if (newInfo.Variant.Package.Id != info.PackageId) {
                throw new Exception("Invalid package install URI.");
            }

            retrieved.Add(new MultiPromptInfo(
                info.PackageId,
                info.VariantId,
                info.VersionId,
                newInfo,
                newInfo.Version
            ));
        }

        return new MultiPromptWindow(plugin, retrieved.ToArray());
    }

    internal static async Task OpenAndAdd(Plugin plugin, IEnumerable<InstallInfo> infos, CancellationToken token = default) {
        try {
            var window = await Open(plugin, infos, token);
            await plugin.PluginUi.AddToDrawAsync(window, token);
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
                    Task.Run(async () => await this.Plugin.AddDownloadAsync(new DownloadTask {
                        Plugin = this.Plugin,
                        PenumbraRoot = modDir,
                        PackageId = info.PackageId,
                        VariantId = info.VariantId,
                        VersionId = info.VersionId,
                        IncludeTags = this._includeTags,
                        OpenInPenumbra = info.VersionId == this.Infos[0].VersionId,
                        PenumbraCollection = this._collection,
                        Notification = null,
                    }));
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
    internal Guid VariantId { get; }
    internal Guid VersionId { get; }
    internal IGetBasicInfo_GetVersion Info { get; }
    internal string Version { get; }


    internal MultiPromptInfo(Guid packageId, Guid variantId, Guid versionId, IGetBasicInfo_GetVersion info, string version) {
        this.PackageId = packageId;
        this.VariantId = variantId;
        this.VersionId = versionId;
        this.Info = info;
        this.Version = version;
    }
}
