using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui;

internal class FirstTimeSetupWindow : IDisposable {
    private Plugin Plugin { get; }

    internal bool Visible;

    internal FirstTimeSetupWindow(Plugin plugin) {
        this.Plugin = plugin;
        this.Plugin.Interface.UiBuilder.Draw += this.Draw;
    }

    public void Dispose() {
        this.Plugin.Interface.UiBuilder.Draw += this.Draw;
    }

    private void Draw() {
        if (!this.Visible) {
            return;
        }

        // TODO: SetNextWindowSize

        using var end = new OnDispose(ImGui.End);
        if (!ImGui.Begin($"{Plugin.Name} first-time setup", ref this.Visible)) {
            return;
        }

        var anyChanged = false;
        anyChanged |= ImGuiHelper.BooleanYesNo(
            "Do you want mods to automatically update when you log in?",
            ref this.Plugin.Config.AutoUpdate
        );

        if (anyChanged) {
            this.Plugin.SaveConfig();
        }
    }
}
