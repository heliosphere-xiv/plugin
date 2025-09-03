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

            ImGui.BeginDisabled();
            using var endDisabled = new OnDispose(ImGui.EndDisabled);
            var text = folderOverride ?? plugin.Config.PenumbraFolder;
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##folder-override-disabled", ref text);
            return;
        }

        if (ImGuiHelper.IconButton(FontAwesomeIcon.Undo, tooltip: "Reset")) {
            folderOverride = null;
        }

        if (folderOverride == null) {
            // check again in case they've clicked reset
            return;
        }

        int TextCallback(scoped ref ImGuiInputTextCallbackData data) {
            if (allFolders == null) {
                return 0;
            }

            ImGui.OpenPopup("folder-options", ImGuiPopupFlags.NoOpenOverExistingPopup);
            return 0;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##folder-override", ref folderOverride, callback: TextCallback, flags: ImGuiInputTextFlags.CallbackEdit);
        if (ImGui.IsItemActivated()) {
            ImGui.OpenPopup("folder-options", ImGuiPopupFlags.NoOpenOverExistingPopup);
        }

        var close = ImGui.IsItemDeactivated();

        if (allFolders == null) {
            return;
        }

        ImGui.SetNextWindowPos(ImGui.GetCursorScreenPos());
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.ChildWindow
            | ImGuiWindowFlags.NavFlattened
            | ImGuiWindowFlags.NoNav;
        if (!ImGui.BeginPopup("folder-options", flags)) {
            return;
        }

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

        if (!ImGui.IsWindowFocused() && close) {
            ImGui.CloseCurrentPopup();
        }
    }
}
