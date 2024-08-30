using System.Diagnostics;
using System.Numerics;
using Heliosphere.Exceptions;
using Heliosphere.Model;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui.Dialogs;

internal class MultipleModDirectoriesDialog : Dialog {
    private Plugin Plugin { get; }
    private MultipleModDirectoriesException Info { get; }

    private List<(string, string?)> DirectoryVersions { get; }
    private Dictionary<string, bool> PathStatus { get; }

    private Stopwatch Stopwatch { get; } = Stopwatch.StartNew();

    internal MultipleModDirectoriesDialog(Plugin plugin, MultipleModDirectoriesException info) : base($"{Plugin.Name}##mmdd-{info.PackageName}-{info.VariantName}-{info.Version}") {
        this.Plugin = plugin;
        this.Info = info;

        this.DirectoryVersions = [
            .. this.Info.Directories
                .Select(path => (path, HeliosphereMeta.ParseDirectory(Path.GetFileName(path))?.Version))
                .OrderBy(tuple => tuple.Version)
        ];

        this.UpdateStatuses();
    }

    private void UpdateStatuses() {
        foreach (var path in this.Info.Directories) {
            try {
                this.PathStatus[path] = Directory.Exists(path);
            } catch {
                this.PathStatus.Remove(path);
            }
        }
    }

    protected override DrawStatus InnerDraw() {
        ImGui.SetWindowSize(new Vector2(450, 300), ImGuiCond.Appearing);

        if (this.Stopwatch.Elapsed >= TimeSpan.FromSeconds(3)) {
            this.Stopwatch.Reset();
            this.UpdateStatuses();
        }

        using var popWrap = new OnDispose(ImGui.PopTextWrapPos);
        ImGui.PushTextWrapPos();

        var name = this.Info.PackageName;
        if (!this.Plugin.Config.HideDefaultVariant || this.Info.VariantName != Consts.DefaultVariant) {
            name += $" ({this.Info.VariantName})";
        }

        ImGui.TextUnformatted($"While installing {name} v{this.Info.Version}, Heliosphere encountered two or more directories already present for that mod. This is an indication that something has gone wrong.");
        ImGui.TextUnformatted("To fix this, you will need to delete all but one directory. Usually this means deleting directories for old versions.");

        ImGui.Spacing();

        foreach (var (path, version) in this.DirectoryVersions) {
            ImGui.Bullet();

            if (!this.PathStatus.TryGetValue(path, out var exists)) {
                exists = false;
            }

            using (ImGuiHelper.DisabledUnless(exists)) {
                ImGui.SameLine();
                ImGui.TextUnformatted($"v{version} -");

                ImGui.SameLine();
                if (ImGui.SmallButton("Open")) {
                    Process.Start(new ProcessStartInfo(path) {
                        UseShellExecute = true,
                    });
                }

                ImGui.SameLine();
                ImGui.TextUnformatted($" - {path}");
            }
        }

        return DrawStatus.Continue;
    }
}
