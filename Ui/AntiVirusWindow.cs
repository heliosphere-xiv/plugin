using System.Diagnostics;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui;

internal class AntiVirusWindow : IDrawable {
    private Plugin Plugin { get; }

    private bool _visible = true;

    internal AntiVirusWindow(Plugin plugin) {
        this.Plugin = plugin;
    }

    public void Dispose() {
    }

    public DrawStatus Draw() {
        if (!this._visible) {
            return DrawStatus.Finished;
        }

        using var end = new OnDispose(ImGui.End);
        if (!ImGui.Begin($"{Plugin.Name}##av-warning", ref this._visible, ImGuiWindowFlags.AlwaysAutoResize)) {
            return DrawStatus.Continue;
        }

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

        ImGui.TextUnformatted("If you have made exceptions and this warning still appears, please contact us in our Discord.");
        if (ImGui.Button("Join Discord")) {
            const string url = "https://discord.gg/3swpspafy2";
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
