using System.CommandLine;
using Dalamud.Game.Command;
using Heliosphere.Ui;

namespace Heliosphere;

internal class CommandHandler : IDisposable {
    private static readonly string[] CommandNames = [
        "/heliosphere",
        "/hs",
    ];

    private Plugin Plugin { get; }
    private RootCommand Root { get; }

    internal CommandHandler(Plugin plugin) {
        this.Plugin = plugin;
        this.Root = this.BuildCommand();

        foreach (var name in CommandNames) {
            this.Plugin.CommandManager.AddHandler(name, new CommandInfo(this.Command) {
                HelpMessage = $"Control {Plugin.Name} - try \"{name} help\"",
            });
        }
    }

    public void Dispose() {
        foreach (var name in CommandNames) {
            this.Plugin.CommandManager.RemoveHandler(name);
        }
    }

    private void Command(string command, string args) {
        this.Root.Invoke(args);
    }

    private RootCommand BuildCommand() {
        var root = new RootCommand($"Control {Plugin.Name}");
        var toggle = new Command("toggle", "Toggle the plugin interface");
        toggle.SetHandler(() => {
            this.Plugin.PluginUi.Visible ^= true;
        });

        var close = new Command("close", "Close the plugin interface");
        close.SetHandler(() => {
            this.Plugin.PluginUi.Visible = false;
        });

        root.Add(toggle);
        root.Add(this.BuildOpenCommand());
        root.Add(close);

        return root;
    }

    private Command BuildOpenCommand() {
        var open = new Command("open", "Open the plugin interface or a specific tab");
        open.SetHandler(() => {
            this.Plugin.PluginUi.Visible = true;
        });

        var manager = new Command("manager", "Open the Manager tab");
        manager.AddAlias("main");
        manager.SetHandler(() => {
            ForceOpen(PluginUi.Tab.Manager);
        });

        var latestUpdate = new Command("latest-update", "Open the Latest Update tab");
        latestUpdate.AddAlias("latestupdate");
        latestUpdate.AddAlias("latest_update");
        latestUpdate.AddAlias("latest");
        latestUpdate.AddAlias("update");
        latestUpdate.AddAlias("updates");
        latestUpdate.SetHandler(() => {
            ForceOpen(PluginUi.Tab.LatestUpdate);
        });

        var downloadHistory = new Command("download-history", "Open the Download History tab");
        downloadHistory.AddAlias("downloadhistory");
        downloadHistory.AddAlias("download_history");
        downloadHistory.AddAlias("downloads");
        downloadHistory.AddAlias("history");
        downloadHistory.SetHandler(() => {
            ForceOpen(PluginUi.Tab.DownloadHistory);
        });

        var settings = new Command("settings", "Open the Settings tab");
        manager.AddAlias("config");
        settings.SetHandler(() => {
            ForceOpen(PluginUi.Tab.Settings);
        });

        open.Add(manager);
        open.Add(latestUpdate);
        open.Add(downloadHistory);
        open.Add(settings);

        return open;

        void ForceOpen(PluginUi.Tab tab) {
            this.Plugin.PluginUi.ForceOpen = tab;
            this.Plugin.PluginUi.Visible = true;
        }
    }
}
