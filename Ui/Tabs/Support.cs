using System.Diagnostics;
using Dalamud.Bindings.ImGui;
using Heliosphere.Util;

namespace Heliosphere.Ui.Tabs;

internal class Support {
    private Plugin Plugin { get; }
    private PluginUi Ui => this.Plugin.PluginUi;

    private bool _sendingToDiscord;

    internal Support(Plugin plugin) {
        this.Plugin = plugin;
    }

    internal void Draw() {
        if (!ImGuiHelper.BeginTab(this.Ui, PluginUi.Tab.Support)) {
            return;
        }

        using var endTabItem = new OnDispose(ImGui.EndTabItem);

        ImGui.PushTextWrapPos();
        using var popTextWrapPos = new OnDispose(ImGui.PopTextWrapPos);

        ImGui.TextUnformatted("Support is offered on our official forums. Click the button below to open them.");

        if (ImGuiHelper.CentredWideButton("Open Heliosphere Forums")) {
            Process.Start(new ProcessStartInfo("https://forums.heliosphere.app/") {
                UseShellExecute = true,
            });
        }

        ImGui.Separator();

        ImGui.TextUnformatted("When getting support, you may be asked to click these buttons and send what they copy to your clipboard.");

        if (ImGuiHelper.CentredWideButton("Copy troubleshooting info")) {
            this.Plugin.Support.CopyTroubleshootingInfo(this._sendingToDiscord);
        }

        if (ImGuiHelper.CentredWideButton("Copy dalamud.log file")) {
            this.Plugin.Support.CopyDalamudLog();
        }

        if (ImGuiHelper.CentredWideButton("Reveal dalamud.log file")) {
            this.Plugin.Support.OpenDalamudLogFolder();
        }

        if (ImGuiHelper.CentredWideButton("Copy config")) {
            this.Plugin.Support.CopyConfig(this._sendingToDiscord);
        }

        var tracingLabel = this.Plugin.TracingEnabled
            ? "Disable tracing"
            : "Enable tracing";
        if (ImGuiHelper.CentredWideButton(tracingLabel)) {
            this.Plugin.TracingEnabled ^= true;
        }

        ImGui.Spacing();

        ImGui.TextUnformatted("Support location (changes what is copied to clipboard)");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##destination", this._sendingToDiscord ? "Discord" : "Forums")) {
            using var endCombo = new OnDispose(ImGui.EndCombo);

            if (ImGui.Selectable("Forums", !this._sendingToDiscord)) {
                this._sendingToDiscord = false;
            }

            if (ImGui.Selectable("Discord", this._sendingToDiscord)) {
                this._sendingToDiscord = true;
            }
        }
    }
}
