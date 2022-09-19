using System.Diagnostics;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Logging;
using Heliosphere.Ui;
using ImGuiNET;

namespace Heliosphere;

internal class UriSniffer : IDisposable {
    private Plugin Plugin { get; }
    private Stopwatch Stopwatch { get; } = new();

    internal UriSniffer(Plugin plugin) {
        this.Plugin = plugin;
        this.Stopwatch.Start();

        this.Plugin.Interface.UiBuilder.Draw += this.Sniff;
    }

    public void Dispose() {
        this.Plugin.Interface.UiBuilder.Draw -= this.Sniff;
    }

    private void Sniff() {
        if (this.Stopwatch.ElapsedMilliseconds < 100) {
            return;
        }

        this.Stopwatch.Restart();

        string? clipboard;
        try {
            clipboard = ImGui.GetClipboardText();
        } catch {
            clipboard = null;
        }

        if (clipboard == null || !UriInfo.TryParse(clipboard, out var info)) {
            return;
        }

        if (!(info.Open ?? false)) {
            return;
        }

        info.Open = null;
        ImGui.SetClipboardText(info.ToUri().ToString());

        Task.Run(async () => {
            try {
                this.Plugin.Interface.UiBuilder.AddNotification(
                    "Opening mod installer, please wait...",
                    this.Plugin.Name,
                    NotificationType.Info
                );
                var window = await PromptWindow.Open(this.Plugin, info.Id, info.VersionId);
                await this.Plugin.PluginUi.AddToDrawAsync(window);
            } catch (Exception ex) {
                PluginLog.LogError(ex, "Error opening prompt window");
                this.Plugin.Interface.UiBuilder.AddNotification(
                    "Error opening installer prompt.",
                    this.Plugin.Name,
                    NotificationType.Error
                );
            }
        });
    }
}
