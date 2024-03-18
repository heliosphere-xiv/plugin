using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui.Dialogs;

internal class VersionMismatchDialog(Version dalamud, Version actual) : Dialog("Heliosphere##version-mismatch") {
    private Version Dalamud { get; } = dalamud;
    private Version Actual { get; } = actual;

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
