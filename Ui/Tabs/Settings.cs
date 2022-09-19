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

        ImGui.EndTabItem();

        if (anyChanged) {
            this.Plugin.SaveConfig();
        }
    }
}
