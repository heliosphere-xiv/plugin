using System.Numerics;
using Heliosphere.Ui.Dialogs;
using Heliosphere.Ui.Tabs;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui;

internal class PluginUi : IDisposable {
    internal const int TitleSize = 36;

    internal enum Tab {
        Manager,
        DownloadHistory,
        Settings,
        LatestUpdate,
    }

    private Plugin Plugin { get; }

    internal bool Visible;
    internal Tab? ForceOpen;
    internal Guid? ForceOpenVariant;

    private Guard<List<IDrawable>> ToDraw { get; } = new([]);
    private List<IDrawable> ToDispose { get; } = [];
    private Manager Manager { get; }
    private DownloadHistory DownloadHistory { get; }
    private LatestUpdate LatestUpdate { get; }
    private Settings Settings { get; }
    internal DownloadStatusWindow StatusWindow { get; }
    internal BreakingChangeWindow BreakingChangeWindow { get; }

    internal PluginUi(Plugin plugin) {
        this.Plugin = plugin;
        this.Manager = new Manager(this.Plugin);
        this.DownloadHistory = new DownloadHistory(this.Plugin);
        this.LatestUpdate = new LatestUpdate(this.Plugin);
        this.Settings = new Settings(this.Plugin);
        this.StatusWindow = new DownloadStatusWindow(this.Plugin);
        this.BreakingChangeWindow = new BreakingChangeWindow(this.Plugin);

        this.Plugin.Interface.UiBuilder.Draw += this.Draw;
        this.Plugin.Interface.UiBuilder.OpenConfigUi += this.OpenConfig;
    }

    public void Dispose() {
        this.Plugin.Interface.UiBuilder.OpenConfigUi -= this.OpenConfig;
        this.Plugin.Interface.UiBuilder.Draw -= this.Draw;

        this.BreakingChangeWindow.Dispose();
        this.StatusWindow.Dispose();
        this.LatestUpdate.Dispose();
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

    internal void OpenAntiVirusWarning() {
        Task.Run(async () => await this.AddIfNotPresentAsync(new AntiVirusDialog(this.Plugin)));
    }

    internal bool ShouldForceOpen(Tab tab) {
        var should = this.ForceOpen == tab;
        if (should) {
            this.ForceOpen = null;
        }

        return should;
    }

    internal void AddSummary(UpdateSummary summary) {
        this.LatestUpdate.Summaries.Add(summary);
    }

    internal void AddToDraw(IDrawable drawable) {
        using var guard = this.ToDraw.Wait();
        guard.Data.Add(drawable);
    }

    internal async Task AddToDrawAsync(IDrawable drawable, CancellationToken token = default) {
        using var guard = await this.ToDraw.WaitAsync(token);
        guard.Data.Add(drawable);
    }

    internal void AddIfNotPresent<T>(T drawable) where T : IDrawable {
        Task.Run(async () => await this.AddIfNotPresentAsync(drawable));
    }

    internal void AddUniqueDialog(string title, Func<DrawStatus> draw, ImGuiWindowFlags flags = ImGuiWindowFlags.None, CancellationToken token = default) {
        Task.Run(async () => await this.AddUniqueDialogAsync(title, draw, flags, token), token);
    }

    internal async Task AddUniqueDialogAsync(string title, Func<DrawStatus> draw, ImGuiWindowFlags flags = ImGuiWindowFlags.None, CancellationToken token = default) {
        using var guard = await this.ToDraw.WaitAsync(token);
        if (guard.Data.Any(x => x is Dialog { Title: var other } && other == title)) {
            return;
        }

        guard.Data.Add(new GenericDialog(title, draw, flags));
    }

    internal async Task AddIfNotPresentAsync<T>(T drawable, CancellationToken token = default) where T : IDrawable {
        using var guard = await this.ToDraw.WaitAsync(token);
        if (guard.Data.All(x => x is not T)) {
            guard.Data.Add(drawable);
        }
    }

    private void Draw() {
        using var font = Plugin.GameFont.WithFont(16);
        if (font == null) {
            return;
        }

        this.Inner();
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
                    if (ret == DrawStatus.Continue) {
                        return false;
                    }

                    this.ToDispose.Add(draw);
                    return true;
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
        if (!ImGui.Begin(Plugin.Name, ref this.Visible)) {
            ImGui.End();
            return;
        }

        if (ImGui.BeginTabBar("heliosphere-tabs")) {
            this.Manager.Draw();
            this.LatestUpdate.Draw();
            this.DownloadHistory.Draw();
            this.Settings.Draw();

            ImGui.EndTabBar();
        }

        ImGui.End();
    }
}
