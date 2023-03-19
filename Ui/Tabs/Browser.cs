using System.Numerics;
using Heliosphere.Model.Generated;
using Heliosphere.Util;
using ImGuiNET;
using ImGuiScene;
using StrawberryShake;

namespace Heliosphere.Ui.Tabs;

internal class Browser : IDisposable {
    private Plugin Plugin { get; }

    private Guard<List<IPackageInfo>> Featured { get; } = new(new List<IPackageInfo>());
    private Guard<List<IPackageInfo>> Newest { get; } = new(new List<IPackageInfo>());
    private Guard<List<IPackageInfo>> RecentlyUpdated { get; } = new(new List<IPackageInfo>());

    private Guard<Dictionary<string, TextureWrap?>> Images { get; } = new(new Dictionary<string, TextureWrap?>());

    internal Browser(Plugin plugin) {
        this.Plugin = plugin;

        Task.Run(async () => {
            var featured = this.GetFeatured(1);
            var recentlyUpdated = this.GetRecentlyUpdated(1);
            await Task.WhenAll(featured, recentlyUpdated);
        });
    }

    public void Dispose() {
        var images = this.Images.Deconstruct();
        foreach (var image in images.Values) {
            image.Dispose();
        }
    }

    private async Task GetFeatured(int page) {
        var info = await Plugin.GraphQl.GetFeatured.ExecuteAsync(page, new RestrictedInfoInput());
        info.EnsureNoErrors();

        if (info.Data == null) {
            return;
        }

        using var guard = await this.Featured.WaitAsync();
        guard.Data.Clear();
        guard.Data.AddRange(info.Data.FeaturedPackages.Packages);
    }

    private async Task GetRecentlyUpdated(int page) {
        var info = await Plugin.GraphQl.GetRecentlyUpdated.ExecuteAsync(page, new RestrictedInfoInput());
        info.EnsureNoErrors();

        if (info.Data == null) {
            return;
        }

        using var guard = await this.RecentlyUpdated.WaitAsync();
        guard.Data.Clear();
        guard.Data.AddRange(info.Data.RecentlyUpdatedPackages.Packages.Select(p => p.Package));
    }

    private async Task DownloadImage(string hash) {
        using var resp = await Plugin.Client.GetAsync($"https://data.heliosphere.app/images/{hash}", HttpCompletionOption.ResponseHeadersRead);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        var image = await ImageHelper.LoadImageAsync(this.Plugin.Interface.UiBuilder, bytes);

        using var guard = await this.Images.WaitAsync();
        guard.Data[hash] = image;
    }

    private TextureWrap? GetImage(string hash) {
        var guard = this.Images.Wait(0);
        if (guard == null) {
            return null;
        }

        return guard.Data.TryGetValue(hash, out var tex) ? tex : null;
    }

    private void DrawCard(IPackageInfo pkg, string uwu, float width) {
        ImGui.PushID($"pkg-{pkg.Id}-{uwu}");
        using var guard = new OnDispose(ImGui.PopID);

        using var child = new OnDispose(ImGui.EndChild);
        if (!ImGui.BeginChild("child", new Vector2(width, -1))) {
            return;
        }

        ImGuiHelper.TextUnformattedCentred(pkg.Name, PluginUi.SubtitleSize);
        if (pkg.Images.Count > 0 && this.GetImage(pkg.Images[0].Hash) is { } image) {
            ImGuiHelper.ImageFullWidth(image, centred: true);
        }
    }

    internal void Draw() {
        if (!ImGui.BeginTabItem("Browser")) {
            return;
        }

        using var tabGuard = new OnDispose(ImGui.EndTabItem);

        var cardWidth = ImGui.GetContentRegionAvail().X / 3 - ImGui.GetStyle().ItemSpacing.X * 2;
        if (this.Featured.Wait(0) is { } featured) {
            for (var i = 0; i < featured.Data.Count; i++) {
                var pkg = featured.Data[i];
                this.DrawCard(pkg, "featured", cardWidth);

                if (i != 2) {
                    ImGui.SameLine();
                }
            }
        }
    }
}
