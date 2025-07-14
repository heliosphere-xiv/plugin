using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Textures.TextureWraps;
using Heliosphere.Model.Generated;
using Heliosphere.Util;
using ImGuiNET;
using StrawberryShake;

namespace Heliosphere.Ui;

internal class MultiVariantPromptWindow : IDrawable {
    private Guid WindowId { get; } = Guid.NewGuid();
    private Plugin Plugin { get; }
    private Guid PackageId { get; }
    private IMultiVariantInstall_Package Package { get; }
    private Dictionary<IMultiVariantInstall_Package_Variants, IMultiVariantInstall_Package_Variants_Versions> Variants { get; }

    private bool _visible = true;
    private bool _includeTags;
    private bool _openInPenumbra;
    private Guid? _collection;
    private readonly IDalamudTextureWrap? _coverImage;
    private LoginUpdateMode? _loginUpdateMode;
    private PackageSettings.UpdateSetting _manualUpdateMode = PackageSettings.UpdateSetting.Default;

    private MultiVariantPromptWindow(Plugin plugin, Guid packageId, IMultiVariantInstall_Package package, Dictionary<IMultiVariantInstall_Package_Variants, IMultiVariantInstall_Package_Variants_Versions> variants, IDalamudTextureWrap? cover) {
        this.Plugin = plugin;
        this.PackageId = packageId;
        this.Package = package;
        this.Variants = variants;
        this._coverImage = cover;
        this._includeTags = this.Plugin.Config.IncludeTags;
        this._openInPenumbra = this.Plugin.Config.OpenPenumbraAfterInstall;
        this._collection = this.Plugin.Config.DefaultCollectionId;
    }

    public DrawStatus Draw() {
        if (!this._visible) {
            return DrawStatus.Finished;
        }

        ImGui.PushID(this.WindowId.ToString());
        using var popId = new OnDispose(ImGui.PopID);

        var variantIds = string.Join(
            "-",
            this.Variants.Keys
                .Select(variant => variant.Id.ToString("N"))
        );
        if (!ImGui.Begin($"Install {this.Package.Name} by {this.Package.User.Username}?###multi-install-prompt-{this.PackageId}-{variantIds}-{this.WindowId}", ref this._visible, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize)) {
            ImGui.End();
            return DrawStatus.Continue;
        }

        ImGuiHelper.TextUnformattedCentred(this.Package.Name, this.Plugin.PluginUi.TitleSize);

        if (this._coverImage != null) {
            var maxHeight = ImGui.GetContentRegionAvail().Y / 2;
            ImGuiHelper.ImageFullWidth(this._coverImage, maxHeight, true);
        }

        ImGui.TextUnformatted("Do you want to install this mod?");

        var info = new List<(string, string)> {
            ("Name", this.Package.Name),
            ("Author", this.Package.User.Username),
            ("Variants", this.Variants.Count.ToString()),
        };

        if (ImGui.BeginTable($"mod-info-multi-install-{this.PackageId}-{variantIds}", 2)) {
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

        if (ImGui.Button("Install")) {
            ret = DrawStatus.Finished;
            if (this.Plugin.Penumbra.TryGetModDirectory(out var modDir)) {
                foreach (var (variant, version) in this.Variants) {
                    Task.Run(async () => await this.Plugin.AddDownloadAsync(new DownloadTask {
                        Plugin = this.Plugin,
                        PenumbraRoot = modDir,
                        PackageId = this.PackageId,
                        VariantId = variant.Id,
                        VersionId = version.Id,
                        IncludeTags = this._includeTags,
                        OpenInPenumbra = this._openInPenumbra,
                        PenumbraCollection = this._collection,
                        Notification = null,
                        LoginUpdateMode = this._loginUpdateMode,
                        ManualUpdateMode = this._manualUpdateMode,
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

    public void Dispose() {
        this._coverImage?.Dispose();
    }

    internal static async Task<MultiVariantPromptWindow> Open(Plugin plugin, Guid packageId, Guid[] variantIds, CancellationToken token = default) {
        var resp = await Plugin.GraphQl.MultiVariantInstall.ExecuteAsync(packageId, token);
        resp.EnsureNoErrors();

        var pkg = resp.Data?.Package;
        if (pkg == null) {
            throw new Exception("Invalid install request.");
        }

        var variants = pkg.Variants
            .Where(variant => variantIds.Contains(variant.Id) && variant.Versions.Count > 0)
            .ToDictionary(variant => variant, variant => variant.Versions[0]);
        if (variants.Count != variantIds.Length) {
            throw new Exception("Variants with no versions specified.");
        }

        IDalamudTextureWrap? cover = null;
        if (pkg.Images.Count > 0) {
            try {
                using var imgResp = await DownloadTask.GetImage(packageId, pkg.Images[0].Id, token);
                var bytes = await imgResp.Content.ReadAsByteArrayAsync(token);
                cover = await ImageHelper.LoadImageAsync(plugin.TextureProvider, bytes, token);
            } catch (Exception ex) {
                ErrorHelper.Handle(ex, $"Could not load cover image for package {packageId:N}");
            }
        }

        return new MultiVariantPromptWindow(plugin, packageId, pkg, variants, cover);
    }

    internal static async Task OpenAndAdd(Plugin plugin, Guid packageId, Guid[] variantIds, CancellationToken token) {
        try {
            var window = await Open(plugin, packageId, variantIds, token);
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
}
