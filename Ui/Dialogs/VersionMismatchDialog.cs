using System.Numerics;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui.Dialogs;

internal class VersionMismatchDialog : Dialog {
    private Version Dalamud { get; }
    private Version Actual { get; }

    public VersionMismatchDialog(Version dalamud, Version actual) : base("Heliosphere##version-mismatch") {
        this.Dalamud = dalamud;
        this.Actual = actual;
    }

    protected override DrawStatus InnerDraw() {
        ImGuiHelper.TextUnformattedCentred("Version mismatch", PluginUi.TitleSize);

        ImGui.Spacing();

        ImGui.TextUnformatted($"Dalamud thinks you have version {this.Dalamud.ToString(3)} installed, but this is actually version {this.Actual.ToString(3)}.");
        ImGui.TextUnformatted($"To fix this, open the plugin installer and disable, delete, and reinstall {Plugin.Name}.");

        ImGui.Spacing();

        return ImGuiHelper.FullWidthButton("Close")
            ? DrawStatus.Finished
            : DrawStatus.Continue;
    }
}
