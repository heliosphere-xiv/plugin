using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui.Components;

internal class ModChooser {
    private Plugin Plugin { get; }
    private IList<(string Directory, string Name)>? Mods { get; set; }
    private IList<(string Directory, string Name)> Filtered { get; set; } = Array.Empty<(string, string)>();
    private string _query = string.Empty;
    private (string Directory, string Name)? _selected;

    internal ModChooser(Plugin plugin) {
        this.Plugin = plugin;
        this.Refresh();
    }

    private void Refresh() {
        this.Mods = this.Plugin.Penumbra.GetMods();
        this.Filter();
    }

    private void Filter() {
        if (this.Mods == null) {
            this.Filtered = Array.Empty<(string, string)>();
            return;
        }

        if (string.IsNullOrWhiteSpace(this._query)) {
            this.Filtered = this.Mods;
            return;
        }

        var query = this._query.ToLowerInvariant();
        this.Filtered = this.Mods
            .Where(tuple => tuple.Name.ToLowerInvariant().Contains(query))
            .ToList();
    }

    internal (string Directory, string Name)? Draw() {
        if (this.Mods == null) {
            return null; // FIXME
        }

        var outsideAvail = ImGui.GetContentRegionAvail();

        var changed = false;

        ImGui.TextUnformatted("Penumbra mod");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##combo", "Preview")) {
            using var endCombo = new OnDispose(ImGui.EndCombo);

            Vector2 buttonSize;
            using (ImGuiHelper.WithFont(UiBuilder.IconFont)) {
                buttonSize = ImGuiHelpers.GetButtonSize(FontAwesomeIcon.RedoAlt.ToIconString());
            }

            var textBoxWidth = outsideAvail.X - buttonSize.X - ImGui.GetStyle().ItemSpacing.X;
            ImGui.SetNextItemWidth(textBoxWidth);
            ImGui.SetKeyboardFocusHere(1);
            if (ImGui.InputTextWithHint("##query", "Search...", ref this._query, 256, ImGuiInputTextFlags.AutoSelectAll)) {
                this.Filter();
            }

            ImGui.SameLine();
            if (ImGuiHelper.IconButton(FontAwesomeIcon.RedoAlt)) {
                this.Refresh();
            }

            ImGui.Separator();

            foreach (var (directory, name) in this.Filtered) {
                if (ImGui.Selectable($"{name}##{directory}", this._selected == (directory, name))) {
                    this._selected = (directory, name);
                    changed = true;
                }
            }
        }

        return changed ? this._selected : null;
    }
}
