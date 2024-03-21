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

                    if (task.State.IsDone()) {
                        return;
                    }

                    task.CancellationToken.Cancel();
                };

                this.Notifications[task.TaskId] = notif;
            }

            var state = UpdateNotif(notif, task);
            if (state.IsDone()) {
                this.Notifications.Remove(task.TaskId);
            }
        }
    }

    private static State UpdateNotif(IActiveNotification notif, DownloadTask task) {
        var state = task.State;
        var sData = task.StateData;
        var sMax = task.StateDataMax;

        notif.Title = task.PackageName;
        notif.Content = sMax == 0
            ? $"{state.Name()} ({sData:N0}"
            : $"{state.Name()} ({sData:N0} / {sMax:N0})";
        notif.Progress = sMax == 0
            ? 0
            : (float) sData / sMax;

        if (!state.IsDone()) {
            return state;
        }

        notif.InitialDuration = TimeSpan.FromSeconds(state == State.Errored ? 5 : 3);
        notif.Type = state switch {
            State.Finished => NotificationType.Success,
            State.Cancelled => NotificationType.Warning,
            State.Errored => NotificationType.Error,
            _ => NotificationType.Info,
        };

        return state;
    }
}
