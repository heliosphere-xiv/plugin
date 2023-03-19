using System.Numerics;
using Heliosphere.Ui.Tabs;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui;

internal class PluginUi : IDisposable {
    internal const int TitleSize = 36;
    internal const int SubtitleSize = 24;

    private Plugin Plugin { get; }

    internal bool Visible;
    private Guard<List<IDrawable>> ToDraw { get; } = new(new List<IDrawable>());
    private List<IDrawable> ToDispose { get; } = new();
    private Manager Manager { get; }
    private Browser Browser { get; }
    private DownloadHistory DownloadHistory { get; }
    private Settings Settings { get; }
    internal DownloadStatusWindow StatusWindow { get; }

    internal PluginUi(Plugin plugin) {
        this.Plugin = plugin;
        this.Manager = new Manager(this.Plugin);
        this.Browser = new Browser(this.Plugin);
        this.DownloadHistory = new DownloadHistory(this.Plugin);
        this.Settings = new Settings(this, this.Plugin);
        this.StatusWindow = new DownloadStatusWindow(this.Plugin);

        this.Plugin.Interface.UiBuilder.Draw += this.Draw;
        this.Plugin.Interface.UiBuilder.OpenConfigUi += this.OpenConfig;
    }

    public void Dispose() {
        this.Plugin.Interface.UiBuilder.OpenConfigUi -= this.OpenConfig;
        this.Plugin.Interface.UiBuilder.Draw -= this.Draw;

        this.StatusWindow.Dispose();
        this.Browser.Dispose();
        this.Manager.Dispose();

        this.ToDraw.Dispose();

        foreach (var drawable in this.ToDispose) {
            drawable.Dispose();
        }

        this.ToDispose.Clear();

        // NOTE: don't dispose ToDraw, since it could lead to a crash from
        //       TextureWraps. once Dalamud has a fix for this, this will be
        //       something we don't have to think about
    }

    private void OpenConfig() {
        this.Visible = true;
    }

    internal void AddToDraw(IDrawable drawable) {
        using var guard = this.ToDraw.Wait();
        guard.Data.Add(drawable);
    }

    internal async Task AddToDrawAsync(IDrawable drawable, CancellationToken token = default) {
        using var guard = await this.ToDraw.WaitAsync(token);
        guard.Data.Add(drawable);
    }

    private void Draw() {
        var font = Plugin.GameFont[16];
        if (font == null) {
            return;
        }

        ImGui.PushFont(font.ImFont);
        try {
            this.Inner();
        } finally {
            ImGui.PopFont();
        }
    }

    private void Inner() {
        // to account for TextureWrap/ImGui shenanigans (until a fix is
        // implemented in Dalamud), put IDrawables that have finished into a
        // separate list, then Dispose them next frame (and clear the secondary
        // list, obviously)
        foreach (var drawable in this.ToDispose) {
            drawable.Dispose();
        }

        this.ToDispose.Clear();

        using (var guard = this.ToDraw.Wait(0)) {
            guard?.Data.RemoveAll(draw => {
                try {
                    var ret = draw.Draw();
                    if (ret) {
                        this.ToDispose.Add(draw);
                    }

                    return ret;
                } catch (Exception ex) {
                    ErrorHelper.Handle(ex, "Error in IDrawable.Draw");
                    return false;
                }
            });
        }

        if (!this.Visible) {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(585, 775), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin(this.Plugin.Name, ref this.Visible)) {
            ImGui.End();
            return;
        }

        if (ImGui.BeginTabBar("heliosphere-tabs")) {
            this.Manager.Draw();
            this.Browser.Draw();
            this.DownloadHistory.Draw();
            this.Settings.Draw();

            ImGui.EndTabBar();
        }

        ImGui.End();
    }
}
