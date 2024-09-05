using System.Diagnostics;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui;

internal class FirstTimeSetupWindow : IDisposable {
    private Plugin Plugin { get; }

    internal bool Visible;

    internal FirstTimeSetupWindow(Plugin plugin) {
        this.Plugin = plugin;
        this.Plugin.Interface.UiBuilder.Draw += this.Draw;
    }

    public void Dispose() {
        this.Plugin.Interface.UiBuilder.Draw += this.Draw;
    }

    private void Draw() {
        if (!this.Visible) {
            return;
        }

        // TODO: SetNextWindowSize

        using var end = new OnDispose(ImGui.End);
        if (!ImGui.Begin($"{Plugin.Name} first-time setup")) {
            return;
        }

        using var popTextWrapPos = new OnDispose(ImGui.PopTextWrapPos);
        ImGui.PushTextWrapPos();

        ImGuiHelper.TextUnformattedCentred("Welcome to Heliosphere!", PluginUi.TitleSize);

        ImGui.Spacing();

        ImGui.TextUnformatted("To get everything set up, open the first-time setup window by clicking the button below. It will open in your default web browser.");

        if (ImGuiHelper.CentredWideButton("Open first-time setup")) {
            var url = new UriBuilder("https://heliosphere.app/setup") {
                Fragment = this.Plugin.FirstTimeSetupKey,
            };

            Process.Start(new ProcessStartInfo(url.Uri.ToString()) {
                UseShellExecute = true,
            });
        }

        if (ImGui.SmallButton("Skip (not recommended)")) {
            this.Plugin.EndFirstTimeSetup();
        }
    }
}
