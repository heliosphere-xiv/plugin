using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Plugin.Services;

namespace Heliosphere.Util;

internal static class NotificationExt {
    internal static IActiveNotification AddOrUpdate(
        this IActiveNotification? existing,
        INotificationManager manager,
        Action<INotification, bool> func
    ) {
        if (existing == null || existing.DismissReason != null) {
            var notif = new Notification();
            func(notif, true);
            return manager.AddNotification(notif);
        }

        func(existing, false);
        return existing;
    }

    internal static IActiveNotification AddOrUpdate(
        this IActiveNotification? existing,
        INotificationManager manager,
        NotificationType? type = null,
        string? title = null,
        string? content = null,
        TimeSpan? initialDuration = null,
        bool autoDuration = false
    ) {
        return existing.AddOrUpdate(
            manager,
            (notif, _) => {
                if (type != null) {
                    notif.Type = type.Value;

                    if (autoDuration) {
                        notif.InitialDuration = notif.Type switch {
                            NotificationType.Error => TimeSpan.FromSeconds(5),
                            _ => TimeSpan.FromSeconds(3),
                        };
                    }
                }

                if (title != null) {
                    notif.Title = title;
                }

                if (content != null) {
                    notif.Content = content;
                }

                if (initialDuration != null) {
                    notif.InitialDuration = initialDuration.Value;
                }
            }
        );
    }
}

