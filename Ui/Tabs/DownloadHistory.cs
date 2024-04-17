using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Internal.Notifications;
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
        ImGui.TextUnformatted("Right-click on errored downloads to copy error information.");

        using var guard = this.Plugin.Downloads.Wait(0);
        var downloads = guard?.Data ?? [];

        var toRemove = -1;
        for (var i = 0; i < downloads.Count; i++) {
            var task = downloads[i];
            var info = new {
                task.PackageName,
                task.State,
                task.StateData,
                task.StateDataMax,
            };

            var packageName = info.PackageName == null
                ? string.Empty
                : $"{info.PackageName} - ";
            ImGuiHelper.FullWidthProgressBar(
                info.StateDataMax == 0
                    ? 0
                    : (float) info.StateData / info.StateDataMax,
                $"{packageName}{info.State.Name()}: {info.StateData} / {info.StateDataMax}"
            );

            if (!info.State.IsDone()) {
                continue;
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) {
                toRemove = i;
            } else if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) {
                if (task.GetErrorInformation() is { } errorInfo) {
                    ImGui.SetClipboardText(errorInfo);
                    this.Plugin.NotificationManager.AddNotification(new Notification {
                        Type = NotificationType.Info,
                        Content = "Error information copied to clipboard.",
                    });
                }
            }
        }

        if (toRemove > -1) {
            downloads.RemoveAt(toRemove);
        }

        ImGui.EndTabItem();
    }
}
