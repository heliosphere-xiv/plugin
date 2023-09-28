using Dalamud.Game.Command;

namespace Heliosphere;

internal class CommandHandler : IDisposable {
    private static readonly string[] CommandNames = {
        "/heliosphere",
        "/hs",
    };

    private Plugin Plugin { get; }

    internal CommandHandler(Plugin plugin) {
        this.Plugin = plugin;

        foreach (var name in CommandNames) {
            this.Plugin.CommandManager.AddHandler(name, new CommandInfo(this.Command) {
                HelpMessage = $"Toggle the {Plugin.Name} interface",
            });
        }
    }

    public void Dispose() {
        foreach (var name in CommandNames) {
            this.Plugin.CommandManager.RemoveHandler(name);
        }
    }

    private void Command(string command, string args) {
        this.Plugin.PluginUi.Visible ^= true;
    }
}
