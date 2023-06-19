using System.Numerics;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui.Tabs;

internal class DownloadHistory {
    private Plugin Plugin { get; }
    private PluginUi Ui => this.Plugin.PluginUi;

    internal DownloadHistory(Plugin plugin) {
        this.Plugin = plugin;
    }

    internal void Draw() {
        if (!ImGuiHelper.BeginTabItem("Downloads", this.Ui.ForceOpen == PluginUi.Tab.DownloadHistory)) {
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
