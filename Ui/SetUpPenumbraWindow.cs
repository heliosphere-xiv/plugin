using System.Numerics;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui;

internal class SetUpPenumbraWindow : IDrawable {
    private Plugin Plugin { get; }

    private bool _visible = true;

    internal SetUpPenumbraWindow(Plugin plugin) {
        this.Plugin = plugin;
    }

    public void Dispose() {
    }

    public DrawStatus Draw() {
        if (!this._visible) {
            return DrawStatus.Finished;
        }

        using var end = new OnDispose(ImGui.End);
        if (!ImGui.Begin("[HS] Set up Penumbra", ref this._visible, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)) {
            return DrawStatus.Continue;
        }

        var width = ImGui.CalcTextSize("m").X * 60;
        using var textWrapPop = new OnDispose(ImGui.PopTextWrapPos);
        ImGui.PushTextWrapPos(width);

        ImGui.TextUnformatted("Heliosphere cannot function if Penumbra is not set up. Please set a mod directory in Penumbra, then try again.");

        var buttonWidth = ImGui.GetContentRegionAvail().X;
        if (ImGui.Button("Open Penumbra settings", new Vector2(buttonWidth, 0))) {
            this.Plugin.Penumbra.OpenSettings();
            return DrawStatus.Finished;
        }

        return DrawStatus.Continue;
    }
}
