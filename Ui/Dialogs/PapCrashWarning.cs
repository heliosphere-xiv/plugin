using System.Numerics;
using Heliosphere.Model;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui.Dialogs;

internal class PapCrashWarning(HeliosphereMeta meta, string penumbraRoot, string[] paths) : Dialog($"[{Plugin.Name}] Crash warning##v{meta.VersionId}", ImGuiWindowFlags.NoSavedSettings, new Vector2(450, 300)) {
    private HeliosphereMeta Meta { get; } = meta;
    private string PenumbraRoot { get; } = penumbraRoot;
    private string[] Paths { get; } = paths;

    protected override DrawStatus InnerDraw() {
        ImGui.PushTextWrapPos();
        using var popTextWrapPos = new OnDispose(ImGui.PopTextWrapPos);

        ImGuiHelper.TextUnformattedCentred("Crash warning");

        var variantName = this.Meta.Variant == Consts.DefaultVariant
            ? ""
            : $" ({this.Meta.Variant})";
        ImGuiHelper.TextUnformattedCentred($"{this.Meta.Name}{variantName} v{this.Meta.Version}");

        ImGui.Separator();

        ImGui.TextUnformatted("Penumbra cannot currently handle PAP files with paths that are longer than 260 characters. If any of the files below are loaded by the game, you will crash.");

        if (ImGui.TreeNodeEx($"Crashing paths ({this.Paths.Length})")) {
            using var treePop = new OnDispose(ImGui.TreePop);

            foreach (var path in this.Paths) {
                ImGui.TextUnformatted($"• {path}");
            }
        }

        ImGui.Separator();

        ImGui.TextUnformatted("This is likely caused because your Penumbra root is nested too deeply.");
        ImGui.TextUnformatted($"Your Penumbra root at the time of this warning was {this.PenumbraRoot}");

        return ImGuiHelper.CentredWideButton("I understand")
            ? DrawStatus.Finished
            : DrawStatus.Continue;
    }
}
