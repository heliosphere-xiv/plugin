using Dalamud.Interface.Internal.Notifications;
using Heliosphere.Model.Generated;
using Heliosphere.Util;
using ImGuiNET;
using ImGuiScene;
using StrawberryShake;

namespace Heliosphere.Ui;

internal class MultiPromptWindow : IDrawable {
    private Plugin Plugin { get; }
    private Guid PackageId { get; }
    private IMultiInstall_Package Package { get; }
    private Dictionary<IMultiInstall_Package_Variants, IMultiInstall_Package_Variants_Versions> Variants { get; }

    private bool _visible = true;
    private bool _includeTags;
    private string? _collection;
    private readonly TextureWrap? _coverImage;

    private MultiPromptWindow(Plugin plugin, Guid packageId, IMultiInstall_Package package, Dictionary<IMultiInstall_Package_Variants, IMultiInstall_Package_Variants_Versions> variants, TextureWrap? cover) {
        this.Plugin = plugin;
        this.PackageId = packageId;
        this.Package = package;
        this.Variants = variants;
        this._coverImage = cover;
        this._includeTags = this.Plugin.Config.IncludeTags;
        this._collection = this.Plugin.Config.DefaultCollection;
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
            if (modDir != null) {
                foreach (var version in this.Variants.Values) {
                    this.Plugin.AddDownload(new DownloadTask(this.Plugin, modDir, version.Id, this._includeTags, this._collection));
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

    internal static async Task<MultiPromptWindow> Open(Plugin plugin, Guid packageId, Guid[] variantIds) {
        var resp = await Plugin.GraphQl.MultiInstall.ExecuteAsync(packageId);
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
                var imgResp = await DownloadTask.GetImage(packageId, pkg.Images[0].Id);
                var bytes = await imgResp.Content.ReadAsByteArrayAsync();
                cover = await ImageHelper.LoadImageAsync(plugin.Interface.UiBuilder, bytes);
            } catch (Exception ex) {
                ErrorHelper.Handle(ex, $"Could not load cover image for package {packageId:N}");
            }
        }

        return new MultiPromptWindow(plugin, packageId, pkg, variants, cover);
    }

    internal static async Task OpenAndAdd(Plugin plugin, Guid packageId, Guid[] variantIds) {
        try {
            var window = await Open(plugin, packageId, variantIds);
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
