using System.Text;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Internal.Notifications;
using ImGuiNET;
using Newtonsoft.Json;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.System.Ole;
using Windows.Win32.UI.Shell;

namespace Heliosphere.Util;

internal class Support {
    private Plugin Plugin { get; }

    internal Support(Plugin plugin) {
        this.Plugin = plugin;
    }

    internal void CopyTroubleshootingInfo() {
        ImGui.SetClipboardText($"```\nSupport ID: {this.Plugin.Config.UserId:N}\nVersion: {Plugin.Version ?? "???"}\n```");
        this.Plugin.NotificationManager.AddNotification(new Notification {
            Type = NotificationType.Info,
            Content = "Troubleshooting info copied to clipboard.",
        });
    }

    internal void CopyConfig() {
        var redacted = Configuration.CloneAndRedact(this.Plugin.Config);

        var json = JsonConvert.SerializeObject(redacted, Formatting.Indented);
        ImGui.SetClipboardText($"```json\n{json}\n```");

        this.Plugin.NotificationManager.AddNotification(new Notification {
            Type = NotificationType.Info,
            Content = "Config copied to clipboard.",
        });
    }

    internal void CopyDalamudLog() {
        var logPath = Path.Join(
            this.Plugin.Interface.DalamudAssetDirectory.Parent!.FullName,
            "dalamud.log"
        );
        var pathBytes = Encoding.Unicode.GetBytes(logPath);

        unsafe {
            var dropFilesSize = sizeof(DROPFILES);
            var hGlobal = PInvoke.GlobalAlloc_SafeHandle(
                GLOBAL_ALLOC_FLAGS.GHND,
                (uint) (dropFilesSize + pathBytes.Length + 2)
            );
            var dropFiles = (DROPFILES*) PInvoke.GlobalLock(hGlobal);

            *dropFiles = new DROPFILES();
            dropFiles->fWide = true;
            dropFiles->pFiles = (uint) dropFilesSize;

            var pathLoc = (byte*) ((nint) dropFiles + dropFilesSize);
            for (var i = 0; i < pathBytes.Length; i++) {
                pathLoc[i] = pathBytes[i];
            }

            pathLoc[pathBytes.Length] = 0;
            pathLoc[pathBytes.Length + 1] = 0;

            PInvoke.GlobalUnlock(hGlobal);

            if (PInvoke.OpenClipboard(HWND.Null)) {
                PInvoke.SetClipboardData(
                    (uint) CLIPBOARD_FORMAT.CF_HDROP,
                    hGlobal
                );
                PInvoke.CloseClipboard();

                this.Plugin.NotificationManager.AddNotification(new Notification {
                    Type = NotificationType.Info,
                    Content = "dalamud.log file copied to clipboard.",
                });
            }
        }
    }
}