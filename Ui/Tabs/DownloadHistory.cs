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
        if (!ImGuiHelper.BeginTab(this.Ui, PluginUi.Tab.DownloadHistory)) {
            return;
        }

        ImGui.TextUnformatted("Click to remove from history.");

        using var guard = this.Plugin.Downloads.Wait(0);
        var downloads = guard?.Data ?? new List<DownloadTask>();

        var toRemove = -1;
        for (var i = 0; i < downloads.Count; i++) {
            var task = downloads[i];
            var packageName = task.PackageName == null
                ? string.Empty
                : $"{task.PackageName} - ";
            ImGui.ProgressBar(
                (float) task.StateData / task.StateDataMax,
                new Vector2(ImGui.GetContentRegionAvail().X, 25),
                $"{packageName}{task.State.Name()}: {task.StateData} / {task.StateDataMax}"
            );

            if (!task.State.IsDone() || !ImGui.IsItemClicked()) {
                continue;
            }

            toRemove = i;
        }

        if (toRemove > -1) {
            downloads.RemoveAt(toRemove);
        }

        ImGui.EndTabItem();
    }
}
