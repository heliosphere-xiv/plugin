using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Heliosphere.Ui;

namespace Heliosphere;

internal class LinkPayloads : IDisposable {
    private Plugin Plugin { get; }

    private Dictionary<Command, DalamudLinkPayload> Payloads { get; } = new();

    internal enum Command : uint {
        OpenChangelog = 1,
    }

    internal LinkPayloads(Plugin plugin) {
        this.Plugin = plugin;

        foreach (var command in Enum.GetValues<Command>()) {
            this.Payloads[command] = this.Plugin.Interface.AddChatLinkHandler((uint) command, this.HandleCommand);
        }
    }

    public void Dispose() {
        this.Plugin.Interface.RemoveChatLinkHandler();
    }

    internal DalamudLinkPayload this[Command command] => this.Payloads[command];

    private void HandleCommand(uint id, SeString message) {
        switch ((Command) id) {
            case Command.OpenChangelog: {
                this.OpenChangelog();
                break;
            }
            default: {
                break;
            }
        }
    }

    private void OpenChangelog() {
        this.Plugin.PluginUi.ForceOpen = PluginUi.Tab.LatestUpdate;
        this.Plugin.PluginUi.Visible = true;
    }
}
