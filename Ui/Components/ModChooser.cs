using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Heliosphere.Util;

namespace Heliosphere.Ui.Components;

internal class ModChooser {
    private Plugin Plugin { get; }
    private IDictionary<string, string>? Mods { get; set; }
    private IDictionary<string, string> Filtered { get; set; } = new Dictionary<string, string>();
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
            this.Filtered = new Dictionary<string, string>();
            return;
        }

        if (string.IsNullOrWhiteSpace(this._query)) {
            this.Filtered = this.Mods;
            return;
        }

        var query = this._query.ToLowerInvariant();
        this.Filtered = this.Mods
            .Where(tuple => tuple.Value.ToLowerInvariant().Contains(query))
            .ToDictionary(
                e => e.Key,
                e => e.Value
            );
    }

    internal (string Directory, string Name)? Draw() {
        if (this.Mods == null) {
            ImGui.TextUnformatted("Penumbra is not installed or is not set up properly.");
            return null;
        }

        var outsideAvail = ImGui.GetContentRegionAvail();

        var changed = false;

        ImGui.TextUnformatted("Penumbra mod");
        ImGui.SetNextItemWidth(-1);
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(0, 275),
            outsideAvail with {
                Y = 275,
            }
        );

        var preview = this._selected?.Name
                      ?? $"{this.Filtered.Count:N0} mod" + (this.Filtered.Count == 1
                          ? ""
                          : "s");
        if (ImGui.BeginCombo("##combo", preview, ImGuiComboFlags.HeightLarge)) {
            using var endCombo = new OnDispose(ImGui.EndCombo);

            Vector2 buttonSize;
            using (ImGuiHelper.WithFont(UiBuilder.IconFont)) {
                buttonSize = ImGuiHelpers.GetButtonSize(FontAwesomeIcon.RedoAlt.ToIconString());
            }

            var textBoxWidth = outsideAvail.X
                               - buttonSize.X
                               - ImGui.GetStyle().ItemSpacing.X
                               - ImGui.GetStyle().FramePadding.X * 2;
            ImGui.SetNextItemWidth(textBoxWidth);
            if (ImGui.IsWindowAppearing()) {
                ImGui.SetKeyboardFocusHere();
            }

            if (ImGui.InputTextWithHint("##query", "Search...", ref this._query, 256, ImGuiInputTextFlags.AutoSelectAll)) {
                this.Filter();
            }

            ImGui.SameLine();
            if (ImGuiHelper.IconButton(FontAwesomeIcon.RedoAlt)) {
                this.Refresh();
            }

            ImGui.Separator();

            using var endChild = new OnDispose(ImGui.EndChild);
            var childSize = ImGui.GetContentRegionAvail() with {
                X = -1,
            };
            if (ImGui.BeginChild("##mod-list", childSize, false, ImGuiWindowFlags.HorizontalScrollbar)) {
                foreach (var (directory, name) in this.Filtered) {
                    if (!ImGui.Selectable($"{name}##{directory}", this._selected == (directory, name))) {
                        continue;
                    }

                    this._selected = (directory, name);
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        return changed ? this._selected : null;
    }
}
