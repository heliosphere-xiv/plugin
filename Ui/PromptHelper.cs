using System.Collections.Immutable;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Heliosphere.Util;

namespace Heliosphere.Ui;

internal static class PromptHelper {
    internal static void DrawSortFolderInputs(
        Plugin plugin,
        ref string? folderOverride,
        ImmutableSortedSet<string>? allFolders
    ) {
        ImGui.TextUnformatted($"Penumbra sort folder");
        ImGui.SameLine();
        if (folderOverride == null) {
            if (ImGuiHelper.IconButton(FontAwesomeIcon.Edit, tooltip: "Edit")) {
                folderOverride = plugin.Config.PenumbraFolder;
            }

            ImGui.TextUnformatted(folderOverride ?? plugin.Config.PenumbraFolder);
            return;
        }

        if (ImGuiHelper.IconButton(FontAwesomeIcon.Undo, tooltip: "Reset")) {
            folderOverride = null;
        }

        if (folderOverride == null) {
            // check again in case they've clicked reset
            return;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##folder-override", ref folderOverride);

        int TextCallback(scoped ref ImGuiInputTextCallbackData data) {
            if (allFolders == null) {
                return 0;
            }

            ImGui.OpenPopup("folder-options");
            return 0;
        }

        ImGui.InputText("##folder-override", ref folderOverride, callback: TextCallback, flags: ImGuiInputTextFlags.CallbackEdit);
        if (allFolders != null && ImGui.BeginPopup("folder-options")) {
            using var endPopup = new OnDispose(ImGui.EndPopup);
            foreach (var folder in allFolders) {
                if (!folder.ContainsIgnoreCase(folderOverride)) {
                    continue;
                }

                if (ImGui.Selectable(folder)) {
                    folderOverride = folder;
                    ImGui.CloseCurrentPopup();
                }
            }
        }
    }
}
