using Dalamud.Interface.Internal.Notifications;
using Heliosphere.Model.Generated;
using Heliosphere.Util;
using ImGuiNET;
using ImGuiScene;
using StrawberryShake;

namespace Heliosphere.Ui;

internal class MultiVariantPromptWindow : IDrawable {
    private Plugin Plugin { get; }
    private Guid PackageId { get; }
    private IMultiVariantInstall_Package Package { get; }
    private Dictionary<IMultiVariantInstall_Package_Variants, IMultiVariantInstall_Package_Variants_Versions> Variants { get; }

    private bool _visible = true;
    private bool _includeTags;
    private bool _openInPenumbra;
    private string? _collection;
    private readonly TextureWrap? _coverImage;
    private readonly string? _downloadKey;

    private MultiVariantPromptWindow(Plugin plugin, Guid packageId, IMultiVariantInstall_Package package, Dictionary<IMultiVariantInstall_Package_Variants, IMultiVariantInstall_Package_Variants_Versions> variants, TextureWrap? cover, string? downloadKey) {
        this.Plugin = plugin;
        this.PackageId = packageId;
        this.Package = package;
        this.Variants = variants;
        this._coverImage = cover;
        this._includeTags = this.Plugin.Config.IncludeTags;
        this._openInPenumbra = this.Plugin.Config.OpenPenumbraAfterInstall;
        this._collection = this.Plugin.Config.DefaultCollection;
        this._downloadKey = downloadKey;
    }

    public bool Draw() {
        if (!this._visible) {
            return true;
        }

        var variantIds = string.Join(
            "-",
            this.Variants.Keys
                .Select(variant => variant.Id.ToString("N"))
        );
        if (!ImGui.Begin($"Install {this.Package.Name} by {this.Package.User.Username}?###multi-install-prompt-{this.PackageId}-{variantIds}", ref this._visible, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize)) {
            ImGui.End();
            return false;
        }

        ImGuiHelper.TextUnformattedCentred(this.Package.Name, PluginUi.TitleSize);

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

        var ret = false;

        if (ImGui.Button("Install")) {
            ret = true;
            var modDir = this.Plugin.Penumbra.GetModDirectory();
            if (!string.IsNullOrWhiteSpace(modDir)) {
                foreach (var version in this.Variants.Values) {
                    Task.Run(async () => await this.Plugin.AddDownloadAsync(new DownloadTask(this.Plugin, modDir, version.Id, this._includeTags, this._openInPenumbra, this._collection, this._downloadKey)));
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

    public void Dispose() {
        this._coverImage?.Dispose();
    }

    internal static async Task<MultiVariantPromptWindow> Open(Plugin plugin, Guid packageId, Guid[] variantIds, string? downloadKey) {
        var resp = await Plugin.GraphQl.MultiVariantInstall.ExecuteAsync(packageId);
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

        TextureWrap? cover = null;
        if (pkg.Images.Count > 0) {
            try {
                using var imgResp = await DownloadTask.GetImage(packageId, pkg.Images[0].Id);
                var bytes = await imgResp.Content.ReadAsByteArrayAsync();
                cover = await ImageHelper.LoadImageAsync(plugin.Interface.UiBuilder, bytes);
            } catch (Exception ex) {
                ErrorHelper.Handle(ex, $"Could not load cover image for package {packageId:N}");
            }
        }

        return new MultiVariantPromptWindow(plugin, packageId, pkg, variants, cover, downloadKey);
    }

    internal static async Task OpenAndAdd(Plugin plugin, Guid packageId, Guid[] variantIds, string? downloadKey) {
        try {
            var window = await Open(plugin, packageId, variantIds, downloadKey);
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
}
