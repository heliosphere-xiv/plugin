using System.Numerics;
using System.Text;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Plugin.Services;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui;

internal class NotificationProgressManager : IDisposable {
    private Plugin Plugin { get; }
    private Dictionary<Guid, IActiveNotification> Notifications { get; } = [];
    private Dictionary<Guid, State> LastSeenState { get; } = [];
    // private Dictionary<State, IDalamudTextureWrap> Icons { get; } = [];

    internal NotificationProgressManager(Plugin plugin) {
        this.Plugin = plugin;
        this.Plugin.Framework.Update += this.FrameworkUpdate;

        // TODO: https://github.com/goatcorp/Dalamud/issues/1738
        // foreach (var state in Enum.GetValues<State>()) {
        //     try {
        //         using var stream = state.GetIconStream();
        //         using var memory = new MemoryStream();
        //         stream.CopyTo(memory);
        //         var img = this.Plugin.Interface.UiBuilder.LoadImage(memory.ToArray());
        //         this.Icons[state] = img;
        //     } catch (Exception ex) {
        //         Plugin.Log.Warning(ex, "could not load state image");
        //     }
        // }
    }

    private IDalamudTextureWrap? GetStateIcon(State state) {
        try {
            using var stream = state.GetIconStream();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return this.Plugin.Interface.UiBuilder.LoadImage(memory.ToArray());
        } catch {
            return null;
        }
    }

    public void Dispose() {
        this.Plugin.Framework.Update -= this.FrameworkUpdate;

        foreach (var (_, notif) in this.Notifications) {
            notif.DismissNow();
        }

        this.Notifications.Clear();

        // foreach (var texture in this.Icons.Values) {
        //     texture.Dispose();
        // }

        // this.Icons.Clear();
    }

    private void FrameworkUpdate(IFramework _) {
        this.Update();
    }

    private void Update() {
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
                    Minimized = this.Plugin.Config.NotificationsStartMinimised,
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

            var state = this.UpdateNotif(notif, task);
            this.LastSeenState[task.TaskId] = state;
            if (state.IsDone()) {
                this.Notifications.Remove(task.TaskId);
                this.LastSeenState.Remove(task.TaskId);
            }
        }
    }

    private State UpdateNotif(IActiveNotification notif, DownloadTask task) {
        var state = task.State;
        var sData = task.StateData;
        var sMax = task.StateDataMax;

        var setIcon = !(this.LastSeenState.TryGetValue(task.TaskId, out var lastState) && lastState == state);
        if (setIcon && this.GetStateIcon(state) is { } icon) {
            notif.SetIconTexture(icon);
        }

        var sb = new StringBuilder();
        if (task.PackageName is { } packageName) {
            sb.Append(packageName);

            if (task.VariantName is {  } variantName) {
                if (variantName != Consts.DefaultVariant || !this.Plugin.Config.HideDefaultVariant) {
                    sb.Append(" (");
                    sb.Append(variantName);
                    sb.Append(')');
                }
            }
        }

        var title = sb.ToString();

        notif.Title = string.IsNullOrWhiteSpace(title) ? null : title;
        notif.Content = sMax == 0
            ? $"{state.Name()} {sData:N0}"
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

        // NOTE: this should only run once, since completed tasks get removed
        //       from the notification list. if this ever changes, this += has
        //       to have another guard to make sure it doesn't add more and more
        //       event handlers
        if (state == State.Finished) {
            notif.DrawActions += args => {
                var widthAvail = args.MaxCoord.X - args.MinCoord.X;

                ImGui.PushID($"notif-download-{task.TaskId}");
                using var popId = new OnDispose(ImGui.PopID);

                if (ImGui.Button("Open in Penumbra", new Vector2(widthAvail, 0))) {
                    task.OpenModInPenumbra();
                }
            };
        }

        return state;
    }
}
