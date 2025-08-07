using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Textures.TextureWraps;
using Heliosphere.Model.Api;
using Heliosphere.Model.Generated;
using Heliosphere.Ui.Components;
using Heliosphere.Util;

namespace Heliosphere.Ui;

internal class PromptWindow : IDrawable {
    private Guid WindowId { get; } = Guid.NewGuid();
    private Plugin Plugin { get; }
    private Guid PackageId { get; }
    private Guid VariantId { get; }
    private Guid VersionId { get; }
    private IGetBasicInfo_GetVersion Info { get; }
    private string Version { get; }
    private Importer Importer { get; }

    private bool _visible = true;
    private bool _includeTags;
    private bool _openInPenumbra;
    private LoginUpdateMode? _loginUpdateMode;
    private PackageSettings.UpdateSetting _manualUpdateMode = PackageSettings.UpdateSetting.Default;
    private Guid? _collection;
    private readonly IDalamudTextureWrap? _coverImage;

    private PromptWindow(Plugin plugin, Guid packageId, IGetBasicInfo_GetVersion info, Guid versionId, string version, IDalamudTextureWrap? coverImage) {
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
            (uint) this.Info.Variant.ShortId,
            this.VersionId,
            this.Info.Version
        );
        this._coverImage = coverImage;
        this._includeTags = this.Plugin.Config.IncludeTags;
        this._openInPenumbra = this.Plugin.Config.OpenPenumbraAfterInstall;
        this._collection = this.Plugin.Config.DefaultCollectionId;
    }

    public void Dispose() {
        this._coverImage?.Dispose();
    }

    internal static async Task<PromptWindow> Open(Plugin plugin, Guid packageId, Guid versionId, CancellationToken token = default) {
        var info = await GraphQl.GetBasicInfo(versionId, token);
        if (info.Variant.Package.Id != packageId) {
            throw new Exception("Invalid package install URI.");
        }

        IDalamudTextureWrap? cover = null;
        if (info.Variant.Package.Images.Count > 0) {
            var coverImage = info.Variant.Package.Images[0];

            try {
                using var resp = await DownloadTask.GetImage(packageId, coverImage.Id, token);
                var bytes = await resp.Content.ReadAsByteArrayAsync(token);
                cover = await ImageHelper.LoadImageAsync(plugin.TextureProvider, bytes, token);
            } catch (Exception ex) {
                ErrorHelper.Handle(ex, $"Could not load cover image for package {packageId:N}");
            }
        }

        return new PromptWindow(plugin, packageId, info, versionId, info.Version, cover);
    }

    internal static async Task OpenAndAdd(Plugin plugin, Guid packageId, Guid versionId, CancellationToken token = default) {
        try {
            var window = await Open(plugin, packageId, versionId, token);
            await plugin.PluginUi.AddToDrawAsync(window, token);
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

        ImGuiHelper.TextUnformattedCentred(this.Info.Variant.Package.Name, this.Plugin.PluginUi.TitleSize);

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

        PromptWindow.DrawUpdateCombos(ref this._loginUpdateMode, ref this._manualUpdateMode);

        var ret = DrawStatus.Continue;

        var widthAvail = ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X;
        if (ImGui.Button("Install", new Vector2(widthAvail / 2, 0))) {
            ret = DrawStatus.Finished;
            if (this.Plugin.Penumbra.TryGetModDirectory(out var modDir)) {
                Task.Run(async () => await this.Plugin.AddDownloadAsync(new DownloadTask {
                    Plugin = this.Plugin,
                    PenumbraRoot = modDir,
                    PackageId = this.PackageId,
                    VariantId = this.VariantId,
                    VersionId = this.VersionId,
                    IncludeTags = this._includeTags,
                    OpenInPenumbra = this._openInPenumbra,
                    PenumbraCollection = this._collection,
                    Notification = null,
                    LoginUpdateMode = this._loginUpdateMode,
                    ManualUpdateMode = this._manualUpdateMode,
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
        }

        ImGui.End();
        return ret;
    }

    internal static void DrawUpdateCombos(ref LoginUpdateMode? login, ref PackageSettings.UpdateSetting manual) {
        if (ImGui.BeginTable("##update-behaviour-table", 2)) {
            using var endTable = new OnDispose(ImGui.EndTable);

            ImGui.TextUnformatted("Login update behaviour");
            ImGui.SameLine();
            ImGuiHelper.Help("Controls if this mod should be checked for updates/have updates applied on login. Overrides the global setting.");

            ImGuiHelper.LoginUpdateModeCombo("##login-behaviour-combo", true, ref login);

            ImGui.TableNextColumn();

            ImGui.TextUnformatted("Manual update behaviour");
            ImGui.SameLine();
            ImGuiHelper.Help("Controls what this mod will do when you manually run updates.");

            ImGuiHelper.ManualUpdateModeCombo("##manual-update-combo", true, ref manual);
        }
    }
}
