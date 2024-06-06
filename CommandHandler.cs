using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Text;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using Heliosphere.Ui;

namespace Heliosphere;

internal class CommandHandler : IDisposable {
    private static readonly string[] CommandNames = [
        "/heliosphere",
        "/hs",
    ];

    private Plugin Plugin { get; }
    private Parser Root { get; }

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

    private Parser BuildCommand() {
        var root = new RootCommand($"Control {Plugin.Name}");
        root.SetHandler(() => {
            this.Plugin.PluginUi.Visible ^= true;
        });

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

        return new CommandLineBuilder(root)
            .UseHelp()
            .UseTypoCorrections()
            .UseParseErrorReporting()
            .UseExceptionHandler()
            .CancelOnProcessTermination()
            .EnablePosixBundling()
            .UseHelpBuilder(_ => new DalamudHelpBuilder(this.Plugin, LocalizationResources.Instance, 80))
            .UseHelp(ctx => {
                ctx.HelpBuilder.CustomizeLayout(_ => GetHelpLayout());
            })
            .Build();
    }

    private static IEnumerable<HelpSectionDelegate> GetHelpLayout() {
        // yield return HelpBuilder.Default.SynopsisSection();
        // yield return HelpBuilder.Default.CommandUsageSection();
        yield return HelpBuilder.Default.CommandArgumentsSection();
        yield return HelpBuilder.Default.OptionsSection();
        yield return HelpBuilder.Default.SubcommandsSection();
        yield return HelpBuilder.Default.AdditionalArgumentsSection();
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
        settings.AddAlias("config");
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

    private sealed class DalamudHelpBuilder : HelpBuilder {
        private Plugin Plugin { get; }

        public DalamudHelpBuilder(Plugin plugin, LocalizationResources localizationResources, int maxWidth = int.MaxValue) : base(localizationResources, maxWidth) {
            this.Plugin = plugin;
        }

        public override void Write(HelpContext context) {
            var newCtx = new HelpContext(context.HelpBuilder, context.Command, new ChatTextWriter(this.Plugin.ChatGui));
            base.Write(newCtx);
        }
    }

    private sealed class ChatTextWriter : TextWriter {
        public override Encoding Encoding => Encoding.UTF8;
        private IChatGui Chat { get; }

        internal ChatTextWriter(IChatGui chat) {
            this.Chat = chat;
        }

        public override void Write(string? value) {
            if (value == null) {
                return;
            }

            this.Chat.Print(value, Plugin.Name, 577);
        }
    }
}
