using System.Numerics;
using ImGuiNET;

namespace Heliosphere.Ui.Tabs;

internal class DownloadHistory {
    private Plugin Plugin { get; }

    internal DownloadHistory(Plugin plugin) {
        this.Plugin = plugin;
    }

    internal void Draw() {
        if (!ImGui.BeginTabItem("Downloads")) {
            return;
        }

        ImGui.TextUnformatted("Click to remove from history.");

        var toRemove = -1;
        for (var i = 0; i < this.Plugin.Downloads.Count; i++) {
            var task = this.Plugin.Downloads[i];
            var packageName = task.PackageName == null
                ? string.Empty
                : $"{task.PackageName} - ";
            ImGui.ProgressBar(
                (float) task.StateData / task.StateDataMax,
                new Vector2(ImGui.GetContentRegionAvail().X, 25),
                $"{packageName}{task.State.Name()}: {task.StateData} / {task.StateDataMax}"
            );

            if (!ImGui.IsItemClicked()) {
                continue;
            }

            toRemove = i;
        }

        if (toRemove > -1) {
            this.Plugin.Downloads.RemoveAt(toRemove);
        }

        ImGui.EndTabItem();
    }
}
