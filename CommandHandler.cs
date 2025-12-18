using System.CommandLine;
using System.Reflection;
using System.Text;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using Heliosphere.Ui;
using Heliosphere.Util;
using Humanizer;
using SimpleBase;

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
        var parse = this.Root.Parse(args, new ParserConfiguration {
            EnablePosixBundling = true,
        });

        parse.Invoke(new InvocationConfiguration {
            Output = new ChatTextWriter(this.Plugin.ChatGui),
            Error = new ChatTextWriter(this.Plugin.ChatGui),
        });
    }

    private RootCommand BuildCommand() {
        var root = new RootCommand($"Control {Plugin.Name}");
        root.Aliases.AddRange(CommandNames);

        for (var i = 0; i < root.Options.Count; i++) {
            if (root.Options[i] is not VersionOption) {
                continue;
            }

            root.Options.RemoveAt(i);
            break;
        }

        var version = new Option<bool>("--version", "-V") {
            Description = "Display the plugin version",
        };
        root.Options.Add(version);

        root.SetAction(parse => {
            if (parse.GetValue(version)) {
                this.Plugin.ChatGui.PrintHs($"Version: {Plugin.Version ?? "???"}");
                return;
            }

            this.Plugin.PluginUi.Visible ^= true;
        });

        var toggle = new Command("toggle", "Toggle the plugin interface");
        toggle.SetAction(parse => {
            this.Plugin.PluginUi.Visible ^= true;
        });

        var close = new Command("close", "Close the plugin interface");
        close.SetAction(parse => {
            this.Plugin.PluginUi.Visible = false;
        });

        root.Add(toggle);
        root.Add(this.BuildOpenCommand());
        root.Add(close);
        root.Add(this.BuildSetCommand());
        root.Add(this.BuildInstallCommand());
        root.Add(this.BuildSupportCommand());

        return root;
    }

    private Command BuildOpenCommand() {
        var open = new Command("open", "Open the plugin interface or a specific tab");
        open.SetAction(parse => {
            this.Plugin.PluginUi.Visible = true;
        });

        var manager = new Command("manager", "Open the Manager tab");
        manager.Aliases.Add("main");
        manager.SetAction(parse => {
            ForceOpen(PluginUi.Tab.Manager);
        });

        var latestUpdate = new Command("latest-update", "Open the Latest Update tab");
        latestUpdate.Aliases.AddRange([
            "latest",
            "update",
            "updates",
        ]);
        latestUpdate.SetAction(parse => {
            ForceOpen(PluginUi.Tab.LatestUpdate);
        });

        var downloadHistory = new Command("download-history", "Open the Download History tab");
        downloadHistory.Aliases.AddRange([
            "downloads",
            "history",
        ]);
        downloadHistory.SetAction(range => {
            ForceOpen(PluginUi.Tab.DownloadHistory);
        });

        var settings = new Command("settings", "Open the Settings tab");
        settings.Aliases.Add("config");
        settings.SetAction(parse => {
            ForceOpen(PluginUi.Tab.Settings);
        });

        var support = new Command("support", "Open the Support tab");
        settings.SetAction(parse => {
            ForceOpen(PluginUi.Tab.Support);
        });

        open.Add(manager);
        open.Add(latestUpdate);
        open.Add(downloadHistory);
        open.Add(settings);
        open.Add(support);

        return open;

        void ForceOpen(PluginUi.Tab tab) {
            this.Plugin.PluginUi.ForceOpen = tab;
            this.Plugin.PluginUi.Visible = true;
        }
    }

    private static readonly string[] Truthy = [
        "y",
        "yes",
        "on",
        "enable",
        "enabled",
    ];

    private static readonly string[] Falsey = [
        "n",
        "no",
        "off",
        "disable",
        "disabled",
    ];

    private static readonly string[] ExcludedConfigFields = [
        nameof(Configuration.UserId),
        nameof(Configuration.OneClickCollectionId),
        nameof(Configuration.OneClickHash),
        nameof(Configuration.OneClickSalt),
        nameof(Configuration.DefaultCollectionId),
        nameof(Configuration.Penumbra),
        // disabled for security
        nameof(Configuration.AllowCommandInstalls),
        nameof(Configuration.AllowCommandOneClick),
    ];

    private static readonly string[] SpecialCaseConfigs = [
        "penumbra-show-images",
        "penumbra-show-buttons",
        "penumbra-image-size",
        "one-click-collection",
        "default-collection",
        "preview-download-status",
    ];

    private Command BuildSetCommand() {
        var set = new Command("set", "Change Heliosphere settings");

        var settingNameArg = new Argument<string>(
            "name"
        ) {
            Description = "The name of the setting to change"
        };
        settingNameArg.CompletionSources.Add(ctx => {
            return [
                .. typeof(Configuration)
                .GetFields()
                .Select(field => field.Name)
                .Where(name => !ExcludedConfigFields.Contains(name))
                .Select(name => name.Kebaberize())
                .Concat(SpecialCaseConfigs)
                .Order()
            ];
        });
        set.Arguments.Add(settingNameArg);

        var settingValueArg = new Argument<string>(
            "value"
        ) {
            Description = "The new value of the setting being changed",
        };
        set.Arguments.Add(settingValueArg);

        set.Aliases.AddRange([
            "setting",
            "settings",
            "config",
            "configure",
        ]);

        set.SetAction(parse => {
            var name = parse.GetRequiredValue(settingNameArg);
            var value = parse.GetRequiredValue(settingValueArg);

            var chat = this.Plugin.ChatGui;

            name = name.Replace("-", "_").Pascalize();
            // handle special cases
            switch (name) {
                case "PenumbraShowImages":
                case "PenumbraShowButtons": {
                    if (ParseBool(value) is not { } b) {
                        PrintInvalidValue();
                        return;
                    }

                    if (name == "PenumbraShowImages") {
                        this.Plugin.Config.Penumbra.ShowImages = b;
                    } else {
                        this.Plugin.Config.Penumbra.ShowButtons = b;
                    }

                    SaveAndPrintSuccess();
                    return;
                }
                case "PenumbraImageSize": {
                    if (!float.TryParse(value, out var f)) {
                        PrintInvalidValue();
                        return;
                    }

                    this.Plugin.Config.Penumbra.ImageSize = f;
                    SaveAndPrintSuccess();
                    return;
                }
                case "DefaultCollection":
                case "OneClickCollection": {
                    if (this.Plugin.Penumbra.GetCollections() is not { } collections) {
                        chat.PrintHs("Could not get list of collections from Penumbra.", Colour.Error);
                        return;
                    }

                    Guid? collectionId = null;
                    foreach (var (id, collectionName) in collections) {
                        if (string.Compare(collectionName, value, StringComparison.InvariantCultureIgnoreCase) != 0) {
                            continue;
                        }

                        collectionId = id;
                    }

                    if (collectionId == null) {
                        chat.PrintHs("Could not find a collection with that name.", Colour.Error);
                        return;
                    }

                    if (name == "DefaultCollection") {
                        this.Plugin.Config.DefaultCollectionId = collectionId.Value;
                    } else {
                        this.Plugin.Config.OneClickCollectionId = collectionId.Value;
                    }

                    SaveAndPrintSuccess();
                    return;
                }
                case "PreviewDownloadStatus": {
                    if (ParseBool(value) is not { } b) {
                        PrintInvalidValue();
                        return;
                    }

                    this.Plugin.PluginUi.StatusWindow.Preview = b;
                    SaveAndPrintSuccess(false);
                    return;
                }
            }

            var field = typeof(Configuration).GetField(name, BindingFlags.Instance | BindingFlags.Public);
            if (field == null || ExcludedConfigFields.Contains(name)) {
                chat.PrintHs("Invalid setting name.", Colour.Error);
                return;
            }

            var type = field.FieldType;
            if (type == typeof(string)) {
                field.SetValue(this.Plugin.Config, value);
            } else if (type == typeof(bool)) {
                if (ParseBool(value) is not { } b) {
                    PrintInvalidValue();
                    return;
                }

                field.SetValue(this.Plugin.Config, b);
            } else if (type == typeof(ulong)) {
                if (ulong.TryParse(value, out var u)) {
                    field.SetValue(this.Plugin.Config, u);
                } else {
                    PrintInvalidValue();
                    return;
                }
            } else {
                PrintInvalidValue();
                return;
            }

            SaveAndPrintSuccess();
            return;

            void SaveAndPrintSuccess(bool save = true) {
                if (save) {
                    this.Plugin.SaveConfig();
                }

                chat.PrintHs("Setting changed.");
            }

            void PrintInvalidValue() {
                chat.PrintHs("Invalid value.", Colour.Error);
            }

            bool? ParseBool(string input) {
                if (Truthy.Contains(input)) {
                    return true;
                }

                if (Falsey.Contains(input)) {
                    return false;
                }

                if (bool.TryParse(input, out var b)) {
                    return b;
                }

                return null;
            }
        });

        return set;
    }

    private Command BuildInstallCommand() {
        var install = new Command("install", "Install a mod");

        var noConfirmOption = new Option<bool>("--no-confirm") {
            Description = "Disable confirmation prompt (install like one-click is enabled)",
        };
        install.Options.Add(noConfirmOption);

        var variantOption = new Option<string?>("--variant") {
            Description = "The variant to install (defaults to first choice)",
        };
        install.Options.Add(variantOption);

        var versionOption = new Option<string?>("--version") {
            Description = "The version to install (defaults to newest)",
        };
        install.Options.Add(versionOption);

        var selectorArg = new Argument<string>("id") {
            Description = "The mod URL or ID",
        };
        install.Arguments.Add(selectorArg);

        install.SetAction(parse => {
            var oneClick = parse.GetValue(noConfirmOption);
            var variantName = parse.GetValue(variantOption);
            var versionNumber = parse.GetValue(versionOption);
            var selector = parse.GetRequiredValue(selectorArg);

            var chat = this.Plugin.ChatGui;

            if (!this.Plugin.Config.AllowCommandInstalls) {
                chat.PrintHs("Chat command installs are disabled in the settings.", Colour.Error);
                return;
            }

            if (oneClick && !this.Plugin.Config.AllowCommandOneClick) {
                chat.PrintHs("One-click chat command installs are disabled in the settings.", Colour.Error);
                return;
            }

            Guid? id = null;

            var endBit = selector.Trim();
            if (Uri.TryCreate(selector, UriKind.Absolute, out var uri)) {
                if (uri.Host is "heliosphere.app" or "hsp.re") {
                    var last = uri.AbsolutePath.Split('/').LastOrDefault();
                    if (!string.IsNullOrWhiteSpace(last)) {
                        endBit = last.Trim();
                    }
                }
            }

            if (Guid.TryParse(endBit, out var guid)) {
                id = guid;
            } else {
                var output = new byte[16];
                if (Base32.Crockford.TryDecode(endBit, output, out var length)) {
                    var hex = Convert.ToHexString(output[..length]);
                    if (Guid.TryParse(hex, out var guid2)) {
                        id = guid2;
                    }
                }
            }

            chat.PrintHs("Starting install process. See notifications for more information.");

            Task.Run(async () => {
                var notif = this.Plugin.NotificationManager.AddNotification(new Notification {
                    Type = NotificationType.Info,
                    Content = "Fetching mod information...",
                    InitialDuration = TimeSpan.MaxValue,
                });

                if (id == null) {
                    var check = await Plugin.GraphQl.CheckVanityUrl.ExecuteAsync(endBit);
                    if (check == null || check.Errors.Count > 0) {
                        notif.Type = NotificationType.Error;
                        notif.Content = "Could not check vanity URL.";
                        notif.InitialDuration = TimeSpan.FromSeconds(5);
                        return;
                    }

                    id = check.Data?.CheckVanityUrl;
                }

                if (id == null) {
                    notif.Type = NotificationType.Error;
                    notif.Content = "Invalid URL or ID.";
                    notif.InitialDuration = TimeSpan.FromSeconds(5);
                    return;
                }

                var info = await Plugin.GraphQl.GetNewestVersionInfoAllVariants.ExecuteAsync(id.Value);
                if (info == null || info.Errors.Count > 0) {
                    notif.Type = NotificationType.Error;
                    notif.Content = "Could not fetch mod information.";
                    notif.InitialDuration = TimeSpan.FromSeconds(5);
                    return;
                }

                var variants = info.Data?.Package?.Variants;
                if (variants == null || variants.Count == 0) {
                    notif.Type = NotificationType.Error;
                    notif.Content = "Mod had no variants.";
                    notif.InitialDuration = TimeSpan.FromSeconds(5);
                    return;
                }

                var variant = variantName == null
                    ? variants[0]
                    : variants.FirstOrDefault(variant => variant.Name == variantName);
                if (variant == null) {
                    notif.Type = NotificationType.Error;
                    notif.Content = "No such variant found.";
                    notif.InitialDuration = TimeSpan.FromSeconds(5);
                    return;
                }

                if (variants[0].Versions.Count == 0) {
                    notif.Type = NotificationType.Error;
                    notif.Content = "Mod had no versions.";
                    notif.InitialDuration = TimeSpan.FromSeconds(5);
                    return;
                }

                var version = versionNumber == null
                    ? variant.Versions[0].Id
                    : variant.Versions.FirstOrDefault(version => version.Version == versionNumber)?.Id;
                if (version == null) {
                    notif.Type = NotificationType.Error;
                    notif.Content = "No such version found.";
                    notif.InitialDuration = TimeSpan.FromSeconds(5);
                    return;
                }

                Server.StartInstall(
                    this.Plugin,
                    oneClick && this.Plugin.Config.AllowCommandOneClick,
                    id.Value,
                    variant.Id,
                    version.Value,
                    notif
                );
            });
        });

        return install;
    }

    private Command BuildSupportCommand() {
        var support = new Command("support", "Perform various support/troubleshooting tasks");

        var discordOption = new Option<bool>("--discord") {
            Description = "Copy text formatted for Discord",
        };

        var copyLog = new Command("copy-log", "Copy the dalamud.log file to the clipboard");
        copyLog.SetAction(parse => this.Plugin.Support.CopyDalamudLog());
        support.Subcommands.Add(copyLog);

        var revealLog = new Command("reveal-log", "Open the folder containing the dalamud.log file with it selected");
        revealLog.SetAction(parse => this.Plugin.Support.OpenDalamudLogFolder());
        support.Subcommands.Add(revealLog);

        var copyConfig = new Command("copy-config", "Copy the plugin config file to the clipboard");
        copyConfig.Options.Add(discordOption);

        copyConfig.SetAction(parse => {
            var discord = parse.GetValue(discordOption);
            this.Plugin.Support.CopyConfig(discord);
        });
        support.Subcommands.Add(copyConfig);

        var copyInfo = new Command("copy-info", "Copy troubleshooting info to the clipboard");
        copyInfo.Aliases.Add("copy-troubleshooting-info");
        copyInfo.Options.Add(discordOption);

        copyInfo.SetAction(parse => {
            var discord = parse.GetValue(discordOption);
            this.Plugin.Support.CopyTroubleshootingInfo(discord);
        });
        support.Subcommands.Add(copyInfo);

        return support;
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

internal enum Colour : ushort {
    Heliosphere = 577,
    Grey = 3,
    Error = 17,
    White = 1,
    LightGrey = 571,
}

internal static class ChatGuiExt {
    internal static void PrintHs(
        this IChatGui chat,
        IEnumerable<string> text,
        Colour? colour = null
    ) {
        foreach (var item in text) {
            if (colour == null) {
                chat.PrintHs(item);
            } else {
                chat.PrintHs(item, colour.Value);
            }
        }
    }

    internal static void PrintHs(this IChatGui chat, IEnumerable<SeString> strings) {
        foreach (var seString in strings) {
            chat.PrintHs(seString);
        }
    }

    internal static void PrintHs(this IChatGui chat, string text, Colour colour) {
        var msg = new SeStringBuilder()
            .AddUiForeground((ushort) colour)
            .AddText(text)
            .AddUiForegroundOff()
            .Build();
        chat.PrintHs(msg);
    }

    internal static void PrintHs(this IChatGui chat, SeString msg) {
        chat.Print(msg, Plugin.Name, (ushort) Colour.Heliosphere);
    }

    internal static void PrintHs(this IChatGui chat, string msg) {
        chat.Print(msg, Plugin.Name, (ushort) Colour.Heliosphere);
    }
}
