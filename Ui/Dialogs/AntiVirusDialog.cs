using System.Diagnostics;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui.Dialogs;

internal class AntiVirusDialog : Dialog {
    private Plugin Plugin { get; }

    internal AntiVirusDialog(Plugin plugin) : base($"{Plugin.Name}##av-warning", ImGuiWindowFlags.AlwaysAutoResize) {
        this.Plugin = plugin;
    }

    protected override DrawStatus InnerDraw() {
        ImGuiHelper.TextUnformattedCentred("Warning", PluginUi.TitleSize);

        ImGui.Separator();

        ImGui.TextUnformatted("Your antivirus program is most likely interfering with Heliosphere's operation.");
        ImGui.TextUnformatted("Please allowlist or make an exception for Dalamud and Heliosphere.");
        if (ImGui.Button("Open instructions")) {
            const string url = "https://goatcorp.github.io/faq/xl_troubleshooting#q-how-do-i-whitelist-xivlauncher-and-dalamud-so-my-antivirus-leaves-them-alone";
            Process.Start(new ProcessStartInfo(url) {
                UseShellExecute = true,
            });
        }

        if (this.Plugin.IntegrityFailed) {
            ImGui.TextUnformatted("After following those instructions, please reinstall Heliosphere.");
            ImGui.TextUnformatted("If you do not reinstall, Heliosphere will not work correctly.");
        }

        ImGui.Separator();

        ImGui.TextUnformatted("If you have made exceptions and this warning still appears, please contact us on our forums.");
        if (ImGui.Button("Open forums")) {
            const string url = "https://forums.heliosphere.app/";
            Process.Start(new ProcessStartInfo(url) {
                UseShellExecute = true,
            });
        }

        ImGui.Separator();

        return ImGui.Button("Close")
            ? DrawStatus.Finished
            : DrawStatus.Continue;
    }
}
