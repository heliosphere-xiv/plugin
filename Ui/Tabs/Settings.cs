using System.Net;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Logging;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui.Tabs;

internal class Settings {
    private Plugin Plugin { get; }
    private PluginUi Ui { get; }

    internal Settings(PluginUi ui, Plugin plugin) {
        this.Plugin = plugin;
        this.Ui = ui;
    }

    internal void Draw() {
        if (!ImGui.BeginTabItem("Settings")) {
            return;
        }

        ImGui.Checkbox("Preview download status window", ref this.Ui.StatusWindow.Preview);

        var anyChanged = false;
        anyChanged |= ImGui.Checkbox("Auto-update mods on login", ref this.Plugin.Config.AutoUpdate);
        anyChanged |= ImGui.Checkbox("Include tags by default", ref this.Plugin.Config.IncludeTags);
        anyChanged |= ImGuiHelper.InputTextVertical(
            "Penumbra mod title prefix",
            "##title-prefix",
            ref this.Plugin.Config.TitlePrefix,
            128
        );
        anyChanged |= ImGuiHelper.InputTextVertical(
            "Penumbra folder",
            "##penumbra-folder",
            ref this.Plugin.Config.PenumbraFolder,
            512
        );

        if (!this.Plugin.Server.Listening && ImGui.Button("Try starting server")) {
            ImGui.Separator();

            try {
                this.Plugin.Server.StartServer();
            } catch (HttpListenerException ex) {
                PluginLog.LogError(ex, "Could not start server");
                this.Plugin.Interface.UiBuilder.AddNotification(
                    "Could not start server",
                    this.Plugin.Name,
                    NotificationType.Error
                );
            }
        }

        ImGui.EndTabItem();

        if (anyChanged) {
            this.Plugin.SaveConfig();
        }
    }
}
