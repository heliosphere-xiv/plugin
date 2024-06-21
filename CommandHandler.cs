using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Reflection;
using System.Text;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Plugin.Services;
using Heliosphere.Ui;
using Humanizer;
using SimpleBase;

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
        var root = new Command(CommandNames[0], $"Control {Plugin.Name}");
        root.AddAliases(CommandNames.Skip(1));

        var version = new Option<bool>("--version", "Display the plugin version");
        version.AddAlias("-V");
        root.AddOption(version);

        root.SetHandler(
            (version) => {
                if (version) {
                    this.Plugin.ChatGui.PrintHs($"Version: {Plugin.Version ?? "???"}");
                    return;
                }

                this.Plugin.PluginUi.Visible ^= true;
            },
            version
        );

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
        root.Add(this.BuildSetCommand());
        root.Add(this.BuildInstallCommand());
        root.Add(this.BuildSupportCommand());

        return new CommandLineBuilder(root)
            .UseHelp()
            .UseTypoCorrections()
            .UseParseErrorReporting()
            .UseExceptionHandler()
            .CancelOnProcessTermination()
            .EnablePosixBundling()
            .UseHelpBuilder(_ => new DalamudHelpBuilder(this.Plugin, LocalizationResources.Instance, 80))
            .UseHelp(ctx => {
                ctx.HelpBuilder.CustomizeLayout(_ => this.GetHelpLayout());
            })
            .Build();
    }

    private IEnumerable<HelpSectionDelegate> GetHelpLayout() {
        yield return ctx => Formatters.SynopsisSection(ctx, this.Plugin.ChatGui);
        yield return ctx => Formatters.UsageSection(ctx, this.Plugin.ChatGui);
        yield return ctx => Formatters.ArgumentsSection(ctx, this.Plugin.ChatGui);
        yield return ctx => Formatters.OptionsSection(ctx, this.Plugin.ChatGui);
        yield return ctx => Formatters.SubcommandsSection(ctx, this.Plugin.ChatGui);
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
        latestUpdate.AddAliases([
            "latest",
            "update",
            "updates",
        ]);
        latestUpdate.SetHandler(() => {
            ForceOpen(PluginUi.Tab.LatestUpdate);
        });

        var downloadHistory = new Command("download-history", "Open the Download History tab");
        downloadHistory.AddAliases([
            "downloads",
            "history",
        ]);
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
            "name",
            "The name of the setting to change"
        );
        settingNameArg.AddCompletions([
            .. typeof(Configuration)
                .GetFields()
                .Select(field => field.Name)
                .Where(name => !ExcludedConfigFields.Contains(name))
                .Select(name => name.Kebaberize())
                .Concat(SpecialCaseConfigs)
                .Order(),
        ]);
        set.AddArgument(settingNameArg);

        var settingValueArg = new Argument<string>(
            "value",
            "The new value of the setting being changed"
        );
        set.AddArgument(settingValueArg);

        set.AddAliases([
            "setting",
            "settings",
            "config",
            "configure",
        ]);

        set.SetHandler(
            (name, value) => {
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
            },
            settingNameArg,
            settingValueArg
        );

        return set;
    }

    private Command BuildInstallCommand() {
        var install = new Command("install", "Install a mod");

        var noConfirmOption = new Option<bool>("--no-confirm", "Disable confirmation prompt (install like one-click is enabled)");
        install.AddOption(noConfirmOption);

        var variantOption = new Option<string?>("--variant", "The variant to install (defaults to first choice)");
        install.AddOption(variantOption);

        var versionOption = new Option<string?>("--version", "The version to install (defaults to newest)");
        install.AddOption(versionOption);

        var selectorArg = new Argument<string>("id", "The mod URL or ID");
        install.AddArgument(selectorArg);

        install.SetHandler(
            (oneClick, variantName, versionNumber, selector) => {
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
                        null,
                        notif
                    );
                });
            },
            noConfirmOption,
            variantOption,
            versionOption,
            selectorArg
        );

        return install;
    }

    private Command BuildSupportCommand() {
        var support = new Command("support", "Perform various support/troubleshooting tasks");

        var copyLog = new Command("copy-log", "Copy the dalamud.log file to the clipboard");
        copyLog.SetHandler(() => {
            this.Plugin.Support.CopyDalamudLog();
        });
        support.AddCommand(copyLog);

        var copyConfig = new Command("copy-config", "Copy the plugin config file to the clipboard");
        copyConfig.SetHandler(() => {
            this.Plugin.Support.CopyConfig();
        });
        support.AddCommand(copyConfig);

        var copyInfo = new Command("copy-info", "Copy troubleshooting info to the clipboard");
        copyInfo.AddAlias("copy-troubleshooting-info");
        copyInfo.SetHandler(() => {
            this.Plugin.Support.CopyTroubleshootingInfo();
        });
        support.AddCommand(copyInfo);

        return support;
    }

    private sealed class DalamudHelpBuilder(
        Plugin plugin,
        LocalizationResources localizationResources,
        int maxWidth = int.MaxValue
    ) : HelpBuilder(localizationResources, maxWidth) {
        private Plugin Plugin { get; } = plugin;

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

internal static class Formatters {
    private const string Indent = "    ";
    private const uint MaxWidth = 30;

    private static IEnumerable<string> Wrap(this string input, uint indentLevel = 0) {
        var sb = new StringBuilder();
        AddIndent();

        foreach (var word in input.Split(' ')) {
            if (sb.Length + word.Length > MaxWidth) {
                yield return Finalise();
                AddIndent();
            }

            sb.Append(word);
            sb.Append(' ');
        }

        if (sb.Length > 0) {
            yield return Finalise();
        }

        yield break;

        string Finalise() {
            if (sb.Length > 0) {
                sb.Length -= 1;
            }

            var built = sb.ToString();
            sb.Clear();
            return built;
        }

        void AddIndent() {
            for (var indent = 0; indent < indentLevel; indent++) {
                sb.Append(Indent);
            }
        }
    }

    private static IEnumerable<SeString> WrappedColouredList(
        IReadOnlyList<string> items,
        SeStringBuilder? builder = null,
        string separator = ", ",
        uint indentLevel = 0,
        Colour? itemColour = Colour.Grey,
        Colour? separatorColour = null
    ) {
        builder ??= NewSeStringBuilder();

        var length = builder.BuiltString.TextValue.Length;
        for (var i = 0; i < items.Count; i++) {
            if (i >= 1) {
                if (separatorColour != null) {
                    builder.AddUiForeground((ushort) separatorColour.Value);
                }

                builder.AddText(separator);
                length += separator.Length;

                if (separatorColour != null) {
                    builder.AddUiForegroundOff();
                }
            }

            var item = items[i];
            if (length + item.Length + separator.Length > MaxWidth) {
                yield return builder.Build();
                builder = NewSeStringBuilder();
            }

            if (itemColour != null) {
                builder.AddUiForeground((ushort) itemColour.Value);
            }

            builder.AddText(item);
            length += item.Length;

            if (itemColour != null) {
                builder.AddUiForegroundOff();
            }
        }

        var last = builder.Build();
        if (last.Payloads.Count > 0) {
            yield return last;
        }

        yield break;

        SeStringBuilder NewSeStringBuilder() {
            var builder = new SeStringBuilder();
            for (var i = 0; i < indentLevel; i++) {
                builder.AddText(Indent);
            }

            length = (int) indentLevel * Indent.Length;
            return builder;
        }
    }

    internal static void SynopsisSection(HelpContext ctx, IChatGui chat) {
        var fullCommandName = string.Join(
            ' ',
            ctx.Command
                .Chain()
                .Reverse()
                .Select(cmd => cmd.Name)
        );
        chat.PrintHs(fullCommandName.Wrap(), Colour.White);

        if (ctx.Command.Aliases.Count > 1) {
            var aliasesWrapped = WrappedColouredList(
                ctx.Command.Aliases.Skip(1).ToArray(),
                new SeStringBuilder().AddText($"{Indent}Aliases: "),
                indentLevel: 2
            );
            chat.PrintHs(aliasesWrapped);
        }

        if (!string.IsNullOrWhiteSpace(ctx.Command.Description)) {
            chat.PrintHs(ctx.Command.Description.Wrap(1));
        }

        chat.PrintHs("");
    }

    internal static void UsageSection(HelpContext ctx, IChatGui chat) {
        var command = ctx.Command;
        var parts = GetUsageParts()
            .Where(part => !string.IsNullOrWhiteSpace(part));
        var msg = string.Join(" ", parts);
        chat.PrintHs("Usage:", Colour.LightGrey);
        chat.PrintHs(msg.Wrap(1));

        chat.PrintHs("");
        return;

        IEnumerable<string> GetUsageParts() {
            var displayOptionTitle = false;
            var enumerable = command
                .RecurseWhileNotNull(c => c.Parents.OfType<Command>().FirstOrDefault())
                .Reverse();
            foreach (var parentCommand in enumerable) {
                if (!displayOptionTitle) {
                    // missing IsGlobal from original x.IsGlobal && !x.IsHidden
                    displayOptionTitle = parentCommand.Options.Any(x => !x.IsHidden);
                }

                yield return parentCommand.Name;
                yield return FormatArgumentUsage(parentCommand.Arguments);
            }

            if (command.Subcommands.Any(x => !x.IsHidden)) {
                yield return LocalizationResources.Instance.HelpUsageCommand();
            }

            if (displayOptionTitle || command.Options.Any(x => !x.IsHidden)) {
                yield return LocalizationResources.Instance.HelpUsageOptions();
            }

            if (!command.TreatUnmatchedTokensAsErrors) {
                yield return LocalizationResources.Instance.HelpUsageAdditionalArguments();
            }
        }
    }

    private static string FormatArgumentUsage(IEnumerable<Argument> arguments) {
        var stringBuilder = new StringBuilder();
        Stack<char>? stack = null;
        foreach (var argument in arguments) {
            if (argument.IsHidden) {
                continue;
            }

            var value = argument.Arity.MaximumNumberOfValues > 1 ? "..." : "";
            if (IsOptional(argument)) {
                stringBuilder.Append($"[<{argument.Name}>{value}");
                (stack ??= new Stack<char>()).Push(']');
            } else {
                stringBuilder.Append($"<{argument.Name}>{value}");
            }

            stringBuilder.Append(' ');
        }

        if (stringBuilder.Length > 0) {
            stringBuilder.Length--;
            if (stack != null) {
                while (stack.Count > 0) {
                    stringBuilder.Append(stack.Pop());
                }
            }
        }

        return stringBuilder.ToString();

        static bool IsOptional(Argument argument) {
            return argument.Arity.MinimumNumberOfValues == 0;
        }
    }

    private static IEnumerable<T> RecurseWhileNotNull<T>(
        this T? source,
        Func<T, T?> next)
        where T : class {
        while (source is not null) {
            yield return source;
            source = next(source);
        }
    }

    internal static void ArgumentsSection(HelpContext ctx, IChatGui chat) {
        var arguments = ctx.Command.Chain()
            .Reverse()
            .SelectMany(cmd => cmd.Arguments)
            .Where(arg => !arg.IsHidden)
            .ToArray();

        if (arguments.Length == 0) {
            return;
        }

        chat.PrintHs("Arguments:", Colour.LightGrey);
        foreach (var arg in arguments) {
            chat.PrintHs(arg.Name);

            if (arg.Description is { } desc) {
                chat.PrintHs(desc.Wrap(1));
            }

            var completions = arg.Completions
                .SelectMany(source => source.GetCompletions(null!))
                .Select(item => item.Label)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();
            if (completions.Length > 0) {
                var lines = WrappedColouredList(
                    completions,
                    builder: new SeStringBuilder().AddText($"{Indent}Accepted: "),
                    indentLevel: 2
                );
                chat.PrintHs(lines);
            }

            if (arg.HasDefaultValue) {
                chat.PrintHs($"{Indent}[Default: {arg.GetDefaultValue()}]");
            }

            chat.PrintHs("");
        }
    }

    internal static void OptionsSection(HelpContext ctx, IChatGui chat) {
        var options = ctx.Command.Chain()
            .Reverse()
            .SelectMany(cmd => cmd.Options)
            .Where(arg => !arg.IsHidden)
            .ToArray();

        if (options.Length == 0) {
            return;
        }

        chat.PrintHs("Options:", Colour.LightGrey);
        foreach (var option in options) {
            var value = option.Arity.MaximumNumberOfValues switch {
                0 => string.Empty,
                1 when option.Arity.MinimumNumberOfValues == 0 => " [<value>]",
                1 => " <value>",
                _ when option.Arity.MinimumNumberOfValues == 0 => " [<value>...]",
                _ => " <value>...",
            };
            var name = option.Aliases.Count > 0
                ? option.Aliases.First()
                : option.Name;
            chat.PrintHs($"{name}{value}".Wrap());

            if (option.Aliases.Count > 1) {
                var aliasesWrapped = WrappedColouredList(
                    option.Aliases.Skip(1).ToArray(),
                    new SeStringBuilder().AddText($"{Indent}Aliases: "),
                    indentLevel: 2
                );
                chat.PrintHs(aliasesWrapped);
            }

            if (option.Description is { } desc) {
                chat.PrintHs(desc.Wrap(1));
            }

            var completions = option.GetCompletions()
                .Select(item => item.Label)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();
            if (option.Arity.MaximumNumberOfValues > 0 && completions.Length > 0) {
                var lines = WrappedColouredList(
                    completions,
                    builder: new SeStringBuilder().AddText($"{Indent}Accepted: "),
                    indentLevel: 2
                );
                chat.PrintHs(lines);
            }

            chat.PrintHs("");
        }
    }

    internal static void SubcommandsSection(HelpContext ctx, IChatGui chat) {
        var subcommands = ctx.Command.Subcommands;
        if (subcommands.Count == 0) {
            return;
        }

        chat.PrintHs("Subcommands:", Colour.LightGrey);
        foreach (var subcommand in subcommands) {
            chat.PrintHs(subcommand.Name.Wrap());

            if (subcommand.Description is { } desc) {
                chat.PrintHs(desc.Wrap(1));
            }
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

internal static class IdentifierSymbolExt {
    internal static void AddAliases(this IdentifierSymbol sym, IEnumerable<string> aliases) {
        foreach (var alias in aliases) {
            sym.AddAlias(alias);
        }
    }
}

internal static class CommandExt {
    internal static IEnumerable<Command> Chain(this Command command) {
        yield return command;

        while (true) {
            var parent = command.Parents
                .OfType<Command>()
                .FirstOrDefault();
            if (parent == null) {
                break;
            }

            command = parent;
            yield return parent;
        }
    }
}
