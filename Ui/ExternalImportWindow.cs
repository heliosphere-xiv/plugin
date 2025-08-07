using System.Numerics;
using Dalamud.Bindings.ImGui;
using Heliosphere.Util;

namespace Heliosphere.Ui;

internal class ExternalImportWindow : IDrawable {
    private Plugin Plugin { get; }

    private bool _visible = true;
    private bool _processing;

    private HashSet<Guid> Selected { get; } = [];

    internal ExternalImportWindow(Plugin plugin) {
        this.Plugin = plugin;
    }

    public void Dispose() {
    }

    public DrawStatus Draw() {
        if (!this._visible) {
            return DrawStatus.Finished;
        }

        using var end = new OnDispose(ImGui.End);
        ImGui.SetNextWindowSize(new Vector2(500, 350), ImGuiCond.Appearing);
        if (!ImGui.Begin("[HS] Import external mods", ref this._visible)) {
            return DrawStatus.Continue;
        }

        var external = this.Plugin.State.ExternalNoBlock;

        ImGui.PushTextWrapPos();
        using var pop = new OnDispose(ImGui.PopTextWrapPos);

        ImGui.TextUnformatted("Mods that have been installed without using the Heliosphere plugin may be imported into Heliosphere and benefit from automatic updates. You can choose which mods you would like to import below.");
        ImGui.Separator();

        using var disabled = ImGuiHelper.DisabledIf(this._processing);
        if (ImGui.Button("Select all")) {
            foreach (var id in external.Keys) {
                this.Selected.Add(id);
            }
        }

        ImGui.SameLine();

        if (ImGui.Button("Select none")) {
            this.Selected.Clear();
        }

        foreach (var (id, mod) in external) {
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
        if (ImGui.Button($"{label}###import") && this.Plugin.Penumbra.TryGetModDirectory(out var penumbra)) {
            var tasks = new List<Task>();
            foreach (var id in this.Selected) {
                if (!external.TryGetValue(id, out var info)) {
                    continue;
                }

                var directory = Path.GetDirectoryName(info.CoverImagePath);
                if (string.IsNullOrWhiteSpace(directory)) {
                    continue;
                }

                directory = Path.GetFileName(directory);
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
                    await this.Plugin.State.UpdatePackages(false);
                } finally {
                    this._processing = false;
                }
            });
        }

        return DrawStatus.Continue;
    }
}
