using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Plugin.Services;

namespace Heliosphere.Ui;

internal class NotificationProgressManager : IDisposable {
    private Plugin Plugin { get; }
    private Dictionary<Guid, IActiveNotification> Notifications { get; } = [];

    internal NotificationProgressManager(Plugin plugin) {
        this.Plugin = plugin;
        this.Plugin.Framework.Update += this.FrameworkUpdate;
    }

    public void Dispose() {
        this.Plugin.Framework.Update -= this.FrameworkUpdate;

        foreach (var (_, notif) in this.Notifications) {
            notif.DismissNow();
        }

        this.Notifications.Clear();
    }

    private void FrameworkUpdate(IFramework _) {
        this.Update();
    }

    internal void Update() {
        using var guard = this.Plugin.Downloads.Wait(0);
        if (guard == null) {
            return;
        }

        foreach (var task in guard.Data) {
            if (!this.Notifications.TryGetValue(task.TaskId, out var notif)) {
                if (task.State.IsDone()) {
                    continue;
                }

                notif = this.Plugin.NotificationManager.AddNotification(new Notification {
                    InitialDuration = TimeSpan.MaxValue,
                    ShowIndeterminateIfNoExpiry = false,
                });

                notif.Dismiss += args => {
                    if (args.Reason != NotificationDismissReason.Manual) {
                        return;
                    }

                    task.CancellationToken.Cancel();
                };

                this.Notifications[task.TaskId] = notif;
            }

            UpdateNotif(notif, task);

            if (task.State.IsDone()) {
                this.Notifications.Remove(task.TaskId);
            }
        }
    }

    private static void UpdateNotif(IActiveNotification notif, DownloadTask task) {
        notif.Content = task.StateDataMax == 0
            ? $"{task.State.Name()} ({task.StateData:N0}"
            : $"{task.State.Name()} ({task.StateData:N0} / {task.StateDataMax:N0})";
        notif.Progress = task.StateDataMax == 0
            ? 0
            : (float)task.StateData / task.StateDataMax;

        if (!task.State.IsDone()) {
            return;
        }

        notif.HardExpiry = DateTime.UtcNow + TimeSpan.FromSeconds(task.State == State.Errored ? 5 : 3);
        notif.Type = task.State switch {
            State.Finished => NotificationType.Success,
            State.Cancelled => NotificationType.Warning,
            State.Errored => NotificationType.Error,
            _ => NotificationType.Info,
        };
    }
}