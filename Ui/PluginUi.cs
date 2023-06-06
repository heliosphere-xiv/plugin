using System.Diagnostics;
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
    internal bool ShowAvWarning;

    private Guard<List<IDrawable>> ToDraw { get; } = new(new List<IDrawable>());
    private List<IDrawable> ToDispose { get; } = new();
    private Manager Manager { get; }
    private DownloadHistory DownloadHistory { get; }
    private Settings Settings { get; }
    internal DownloadStatusWindow StatusWindow { get; }

    internal PluginUi(Plugin plugin) {
        this.Plugin = plugin;
        this.Manager = new Manager(this.Plugin);
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

        if (this.ShowAvWarning) {
            try {
                this.DrawAvWarning();
            } catch (Exception ex) {
                ErrorHelper.Handle(ex, "Error in DrawAvWarning");
            }
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
            this.DownloadHistory.Draw();
            this.Settings.Draw();

            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    private void DrawAvWarning() {
        if (!ImGui.Begin($"{this.Plugin.Name}##av-warning", ref this.ShowAvWarning, ImGuiWindowFlags.AlwaysAutoResize)) {
            ImGui.End();
            return;
        }

        ImGuiHelper.TextUnformattedCentred("Warning", TitleSize);

        ImGui.Separator();

        ImGui.TextUnformatted("Your antivirus program is most likely interfering with Heliosphere's operation.");
        ImGui.TextUnformatted("Please allowlist or make an exception for Dalamud and Heliosphere.");
        if (ImGui.Button("Open instructions")) {
            const string url = "https://goatcorp.github.io/faq/xl_troubleshooting#q-how-do-i-whitelist-xivlauncher-and-dalamud-so-my-antivirus-leaves-them-alone";
            Process.Start(new ProcessStartInfo(url) {
                UseShellExecute = true,
            });
        }

        ImGui.TextUnformatted("After following those instructions, please reinstall Heliosphere.");

        ImGui.Separator();

        ImGui.TextUnformatted("If you have made exceptions and this warning still appears, please contact us in our Discord.");
        if (ImGui.Button("Join Discord")) {
            const string url = "https://discord.gg/3swpspafy2";
            Process.Start(new ProcessStartInfo(url) {
                UseShellExecute = true,
            });
        }

        ImGui.Separator();

        if (ImGui.Button("Close")) {
            this.ShowAvWarning = false;
        }

        ImGui.End();
    }
}
