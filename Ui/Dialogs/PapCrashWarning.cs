using Heliosphere.Model;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui.Dialogs;

internal class PapCrashWarning(HeliosphereMeta meta, string penumbraRoot, string[] paths) : Dialog($"[{Plugin.Name}] Crash warning##v{meta.VersionId}") {
    private HeliosphereMeta Meta { get; } = meta;
    private string PenumbraRoot { get; } = penumbraRoot;
    private string[] Paths { get; } = paths;

    protected override DrawStatus InnerDraw() {
        ImGuiHelper.TextUnformattedCentred("Crash warning");

        var variantName = this.Meta.Variant == Consts.DefaultVariant
            ? ""
            : $" ({this.Meta.Variant})";
        ImGuiHelper.TextUnformattedCentred($"{this.Meta.Name}{variantName} v{this.Meta.Version}");

        ImGui.Separator();

        ImGui.TextUnformatted("Penumbra cannot currently handle PAP files with paths that are longer than 260 characters. If any of the files below are loaded by the game, you will crash.");
        foreach (var path in this.Paths) {
            ImGui.Indent();
            using var unindent = new OnDispose(ImGui.Unindent);

            ImGui.TextUnformatted($"• {path}");
        }

        ImGui.Separator();

        ImGui.TextUnformatted("This is likely caused because your Penumbra root is nested too deeply.");
        ImGui.TextUnformatted($"Your Penumbra root at the time of this warning was {this.PenumbraRoot}");

        return ImGuiHelper.CentredWideButton("I understand")
            ? DrawStatus.Finished
            : DrawStatus.Continue;
    }
}
