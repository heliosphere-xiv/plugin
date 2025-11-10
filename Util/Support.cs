using System.Diagnostics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiNotification;
using Newtonsoft.Json;

namespace Heliosphere.Util;

internal class Support {
    private Plugin Plugin { get; }

    internal Support(Plugin plugin) {
        this.Plugin = plugin;
    }

    internal void CopyTroubleshootingInfo(bool markdown) {
        var info = new StringBuilder(markdown ? "```\n" : "[code]\n");

        info.Append("Support ID...: ");
        info.Append(this.Plugin.Config.UserId.ToString("N"));

        info.Append('\n');
        info.Append("Version......: ");
        info.Append(Plugin.Version ?? "<null>");

        info.Append('\n');
        info.Append("Unsupported..: ");
        info.Append(this.Plugin.Config.Unsupported.AnyEnabled());

        info.Append('\n');
        info.Append("Penumbra root: ");
        var root = this.Plugin.Penumbra.GetModDirectory();
        if (root != null) {
            info.Append(root);

            info.Append('\n');
            info.Append("Normalized...: ");
            try {
                var dir = new DirectoryInfo(root);
                info.Append(Path.GetFullPath(dir.FullName));
            } catch (Exception ex) {
                info.Append(ex.GetType().Name);
                info.Append(": ");
                info.Append(ex.Message);
            }
        } else {
            info.Append("<null>");
        }

        info.Append(markdown ? "\n```" : "\n[/code]");

        ImGui.SetClipboardText(info.ToString());
        this.Plugin.NotificationManager.AddNotification(new Notification {
            Type = NotificationType.Info,
            Content = "Troubleshooting info copied to clipboard.",
        });
    }

    internal void CopyConfig(bool markdown) {
        var redacted = Configuration.CloneAndRedact(this.Plugin.Config);

        var json = JsonConvert.SerializeObject(redacted, Formatting.Indented);
        var toCopy = markdown
            ? $"```json\n{json}\n```"
            : $"[code=json]\n{json}\n[/code]";
        ImGui.SetClipboardText(toCopy);

        this.Plugin.NotificationManager.AddNotification(new Notification {
            Type = NotificationType.Info,
            Content = "Config copied to clipboard.",
        });
    }

    internal void CopyDalamudLog() {
        var logPath = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher",
            "dalamud.log"
        );
        var result = Clipboard.CopyFiles([logPath]);
        var type = result
            ? NotificationType.Info
            : NotificationType.Error;
        var message = result
            ? "dalamud.log file copied to clipboard."
            : "Failed to copy file to clipboard";

        this.Plugin.NotificationManager.AddNotification(new Notification {
            Type = type,
            Content = message,
        });
    }

    internal void OpenDalamudLogFolder() {
        var logPath = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher",
            "dalamud.log"
        );

        Process.Start(new ProcessStartInfo("explorer.exe") {
            Arguments = $"/select,{Path.GetFullPath(logPath)}",
        });
    }
}
