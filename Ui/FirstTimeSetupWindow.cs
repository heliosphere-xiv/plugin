using System.Diagnostics;
using System.Numerics;
using Dalamud.Interface.Utility;
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

        var newUser = this.Plugin.State.InstalledNoBlock.Count == 0;

        ImGui.SetNextWindowSize(new Vector2(450, 200) * ImGuiHelpers.GlobalScale, ImGuiCond.Appearing);

        using var end = new OnDispose(ImGui.End);
        if (!ImGui.Begin($"{Plugin.Name} first-time setup")) {
            return;
        }

        using var popTextWrapPos = new OnDispose(ImGui.PopTextWrapPos);
        ImGui.PushTextWrapPos();

        var welcomeLabel = newUser
            ? "Welcome to Heliosphere!"
            : "Heliosphere update";
        ImGuiHelper.TextUnformattedCentred(welcomeLabel, this.Plugin.PluginUi.TitleSize);

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

        const string skipLabel = "Skip (not recommended)";
        var skipSize = ImGuiHelpers.GetButtonSize(skipLabel);

        var avail = ImGui.GetContentRegionAvail();
        var moveY = avail.Y - skipSize.Y;
        if (moveY > 0) {
            var current = ImGui.GetCursorPosY();
            ImGui.SetCursorPosY(current + moveY);
        }

        var setX = avail.X / 2 - skipSize.X / 2;
        ImGui.SetCursorPosX(setX);

        if (ImGui.Button("Skip (not recommended)")) {
            this.Plugin.EndFirstTimeSetup();
        }
    }
}
