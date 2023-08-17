using System.Numerics;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui;

internal class ExternalImportWindow : IDrawable {
    private Plugin Plugin { get; }

    private bool _visible = true;
    private bool _processing;

    private HashSet<Guid> Selected { get; } = new();

    internal ExternalImportWindow(Plugin plugin) {
        this.Plugin = plugin;
    }

    public void Dispose() {
    }

    public bool Draw() {
        if (!this._visible) {
            return true;
        }

        using var end = new OnDispose(ImGui.End);
        ImGui.SetNextWindowSize(new Vector2(500, 650), ImGuiCond.Appearing);
        if (!ImGui.Begin("Import external mods | Heliosphere", ref this._visible)) {
            return false;
        }

        ImGui.PushTextWrapPos();
        using var pop = new OnDispose(ImGui.PopTextWrapPos);

        ImGui.TextUnformatted("Mods that have been installed without using the Heliosphere plugin may be imported into Heliosphere and benefit from automatic updates. You can choose which mods you would like to import below.");
        ImGui.Separator();

        using var disabled = ImGuiHelper.WithDisabled(this._processing);
        if (ImGui.Button("Select all")) {
            foreach (var id in this.Plugin.State.ExternalNoBlock.Keys) {
                this.Selected.Add(id);
            }
        }

        ImGui.SameLine();

        if (ImGui.Button("Select none")) {
            this.Selected.Clear();
        }

        foreach (var (id, mod) in this.Plugin.State.ExternalNoBlock) {
            var check = this.Selected.Contains(id);
            var plural = mod.Variants.Count == 1 ? "variant" : "variants";
            if (ImGui.Checkbox($"{mod.Name} ({mod.Variants.Count} {plural})", ref check)) {
                if (check) {
                    this.Selected.Add(id);
                } else {
                    this.Selected.Remove(id);
                }
            }
        }

        ImGui.Separator();
        var label = this._processing
            ? "Working..."
            : "Import";
        if (ImGui.Button($"{label}###import") && this.Plugin.Penumbra.GetModDirectory() is { } penumbra && !string.IsNullOrWhiteSpace(penumbra)) {
            var tasks = new List<Task>();
            foreach (var id in this.Selected) {
                var info = this.Plugin.State.ExternalNoBlock[id];
                var directory = Path.GetDirectoryName(info.CoverImagePath);
                if (string.IsNullOrWhiteSpace(directory)) {
                    continue;
                }

                foreach (var meta in info.Variants) {
                    tasks.Add(this.Plugin.State.RenameDirectory(meta, penumbra, directory));
                }
            }

            this._processing = true;
            Task.Run(async () => {
                try {
                    await Task.WhenAll(tasks);
                    await this.Plugin.State.UpdatePackages();
                } finally {
                    this._processing = false;
                }
            });
        }

        return false;
    }
}
