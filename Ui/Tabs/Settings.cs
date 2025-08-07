using System.Diagnostics;
using System.Net;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiNotification;
using gfoidl.Base64;
using Heliosphere.Util;
using Konscious.Security.Cryptography;

namespace Heliosphere.Ui.Tabs;

internal class Settings {
    private Plugin Plugin { get; }
    private PluginUi Ui => this.Plugin.PluginUi;

    private bool _sendingToDiscord;

    internal Settings(Plugin plugin) {
        this.Plugin = plugin;
    }

    internal static bool DrawPenumbraIntegrationSettings(Plugin plugin) {
        var anyChanged = false;

        anyChanged |= ImGui.Checkbox("Show mod image previews in Penumbra", ref plugin.Config.Penumbra.ShowImages);
        anyChanged |= ImGui.Checkbox("Show Heliosphere buttons in Penumbra", ref plugin.Config.Penumbra.ShowButtons);

        ImGui.TextUnformatted("Preview image size");
        ImGui.SetNextItemWidth(-1);
        anyChanged |= ImGui.DragFloat("###preview-image-size", ref plugin.Config.Penumbra.ImageSize, 0.001f, 0f, 1f);

        return anyChanged;
    }

    internal void Draw() {
        if (!ImGuiHelper.BeginTab(this.Ui, PluginUi.Tab.Settings)) {
            return;
        }

        using var endTabItem = new OnDispose(ImGui.EndTabItem);

        var anyChanged = false;

        if (ImGui.TreeNodeEx("Penumbra", ImGuiTreeNodeFlags.DefaultOpen)) {
            using var treePop = new OnDispose(ImGui.TreePop);

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
                512,
                help: "The folder in Penumbra to install new mods into. This can be set to blank for no folder, as well.\n\nNote that this is just the initial folder for newly-installed mods; you can move mods out of this folder after install."
            );

            anyChanged |= DrawPenumbraIntegrationSettings(this.Plugin);
        }

        ImGui.Spacing();

        if (ImGui.TreeNodeEx("User interface", ImGuiTreeNodeFlags.DefaultOpen)) {
            using var treePop = new OnDispose(ImGui.TreePop);

            using (ImGuiHelper.DisabledIf(this.Plugin.Config.UseNotificationProgress)) {
                ImGui.Checkbox("Preview download status window", ref this.Ui.StatusWindow.Preview);
                ImGui.SameLine();
                ImGuiHelper.Help("Shows fake mod downloads so you can position the status window where you like.");
            }

            if (ImGui.Checkbox("Use Dalamud notifications for download progress", ref this.Plugin.Config.UseNotificationProgress)) {
                anyChanged = true;
                if (this.Plugin.Config.UseNotificationProgress) {
                    this.Ui.StatusWindow.Preview = false;
                }
            }

            if (this.Plugin.Config.UseNotificationProgress) {
                ImGui.TreePush("notification-progress-sub");
                using var treePop2 = new OnDispose(ImGui.TreePop);

                anyChanged |= ImGui.Checkbox("Start progress notifications minimised", ref this.Plugin.Config.NotificationsStartMinimised);
            }
        }

        ImGui.Spacing();

        if (ImGui.TreeNodeEx("Installs and updates", ImGuiTreeNodeFlags.DefaultOpen)) {
            using var treePop = new OnDispose(ImGui.TreePop);

            ImGui.TextUnformatted("Login update behaviour");
            ImGui.SameLine();
            ImGuiHelper.Help("Controls if mods should be checked for updates/have updates applied on login.");

            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##login-update-mode", this.Plugin.Config.LoginUpdateMode.Name())) {
                using var endCombo = new OnDispose(ImGui.EndCombo);

                foreach (var mode in Enum.GetValues<LoginUpdateMode>()) {
                    if (ImGui.Selectable(mode.Name(), this.Plugin.Config.LoginUpdateMode == mode)) {
                        anyChanged = true;
                        this.Plugin.Config.LoginUpdateMode = mode;
                    }

                    ImGuiHelper.Tooltip(mode.Help());
                }
            }

            anyChanged |= ImGui.Checkbox("Include tags in Penumbra by default", ref this.Plugin.Config.IncludeTags);
            anyChanged |= ImGui.Checkbox("Open mods in Penumbra after fresh install", ref this.Plugin.Config.OpenPenumbraAfterInstall);

            anyChanged |= ImGui.Checkbox("Display breaking change summaries after updates", ref this.Plugin.Config.WarnAboutBreakingChanges);
            ImGui.SameLine();
            ImGuiHelper.Help("This option will cause a window to open with information about if an update to a mod caused Penumbra to reset your option choices.");

            anyChanged |= ImGui.Checkbox("Overwrite mod path name in Penumbra on updates", ref this.Plugin.Config.ReplaceSortName);
            ImGui.SameLine();
            ImGuiHelper.Help("Uncheck this if you change the name in the mod path to re-order your mods. Most users should keep this checked.");

            anyChanged |= ImGui.Checkbox("Overwrite mod name in Penumbra on updates", ref this.Plugin.Config.ReplaceModName);
            ImGui.SameLine();
            ImGuiHelper.Help("Uncheck this if you change the names of mods and would like them to be left alone on updates.");

            anyChanged |= ImGui.Checkbox("Hide variant names in Penumbra when they are \"Default\"", ref this.Plugin.Config.HideDefaultVariant);
            ImGui.SameLine();
            ImGuiHelper.Help("This only affects mods installed after changing the setting.");

            ImGui.TextUnformatted("Default install collection");
            ImGui.SameLine();
            ImGuiHelper.Help("This is the collection that will be selected by default in the installation prompt.");
            anyChanged |= ImGuiHelper.CollectionChooser(
                this.Plugin.Penumbra,
                "##default-collection",
                ref this.Plugin.Config.DefaultCollectionId
            );

            anyChanged |= ImGui.Checkbox("Use Recycle Bin when deleting old files", ref this.Plugin.Config.UseRecycleBin);
            ImGui.SameLine();
            ImGuiHelper.Help("Heliosphere permanently deletes unused files from the files folder inside all of the mods it manages. Check this box to make it send those files to your Recycle Bin instead.");
        }

        ImGui.Spacing();

        if (ImGui.TreeNodeEx("Commands")) {
            using var treePop = new OnDispose(ImGui.TreePop);

            anyChanged |= ImGui.Checkbox("Allow installing mods with chat commands", ref this.Plugin.Config.AllowCommandInstalls);

            anyChanged |= ImGui.Checkbox("Allow installs without confirmation with chat commands", ref this.Plugin.Config.AllowCommandOneClick);
            ImGui.SameLine();
            ImGuiHelper.Help("This is disabled by default to prevent malicious plugins, scripts, or instructions from having an easy way to install mods automatically.");
        }

        ImGui.Spacing();

        if (ImGui.TreeNodeEx("Download speed limits")) {
            using var treePop = new OnDispose(ImGui.TreePop);

            anyChanged |= ImGuiHelper.InputULongVertical(
                "Max download speed in KiB/s (0 for unlimited)",
                "##max-download-speed",
                ref this.Plugin.Config.MaxKibsPerSecond
            );

            anyChanged |= ImGuiHelper.InputULongVertical(
                "Alternate max download speed in KiB/s (0 for unlimited)",
                "##alt-download-speed",
                ref this.Plugin.Config.AltMaxKibsPerSecond
            );

            void DrawLimitCombo(string title, string id, ref Configuration.SpeedLimit limit) {
                ImGui.TextUnformatted(title);
                if (!ImGui.BeginCombo(id, Enum.GetName(limit))) {
                    return;
                }

                foreach (var option in Enum.GetValues<Configuration.SpeedLimit>()) {
                    if (ImGui.Selectable(Enum.GetName(option), option == limit)) {
                        limit = option;
                        anyChanged = true;
                    }
                }

                ImGui.EndCombo();
            }

            DrawLimitCombo("Speed limit (default)", "##speed-limit-normal", ref this.Plugin.Config.LimitNormal);
            DrawLimitCombo("Speed limit (in instance)", "##speed-limit-instance", ref this.Plugin.Config.LimitInstance);
            DrawLimitCombo("Speed limit (in combat)", "##speed-limit-combat", ref this.Plugin.Config.LimitCombat);
            DrawLimitCombo("Speed limit (in party)", "##speed-limit-party", ref this.Plugin.Config.LimitParty);
        }

        ImGui.Spacing();

        if (ImGui.TreeNodeEx("One-click install")) {
            using var treePop = new OnDispose(ImGui.TreePop);

            anyChanged |= ImGui.Checkbox("Enable", ref this.Plugin.Config.OneClick);

            if (!this.Plugin.Config.OneClick) {
                ImGui.BeginDisabled();
            }

            ImGui.PushTextWrapPos();
            try {
                if (this.Plugin.Config.OneClick) {
                    ImGui.TextUnformatted(
                        "You may hold Shift when clicking install on the " +
                        "Heliosphere website to temporarily turn off one-click " +
                        "installs."
                    );
                }

                ImGui.Separator();

                ImGui.TextUnformatted(
                    "One-click installs require you to generate a code in this " +
                    "window, then paste it into the Heliosphere website."
                );
            } finally {
                ImGui.PopTextWrapPos();
            }

            var label = this.Plugin.Config.OneClickHash == null
                ? "Generate code"
                : "Generate new code";
            if (ImGui.Button(label)) {
                var password = this.GenerateOneClickKey();
                anyChanged = true;

                ImGui.SetClipboardText(password);
                this.Plugin.NotificationManager.AddNotification(new Notification {
                    Type = NotificationType.Info,
                    Content = "Code copied to clipboard. Paste it on the Heliosphere website.",
                });
            }

            ImGui.SameLine();

            if (ImGui.Button("Open Heliosphere website")) {
                Process.Start(new ProcessStartInfo("https://heliosphere.app/settings/one-click") {
                    UseShellExecute = true,
                });
            }

            ImGui.Separator();

            ImGui.TextUnformatted("One-click default collection");
            ImGui.SameLine();
            ImGuiHelper.Help("This is the collection that mods installed via one-click will be enabled in by default. This overrides the default collection setting above.");

            anyChanged |= ImGuiHelper.CollectionChooser(
                this.Plugin.Penumbra,
                "##one-click-default-collection",
                ref this.Plugin.Config.OneClickCollectionId
            );

            if (!this.Plugin.Config.OneClick) {
                ImGui.EndDisabled();
            }

            if (ImGui.TreeNodeEx("Nerd info")) {
                ImGui.PushTextWrapPos();
                try {
                    ImGui.TextUnformatted(
                        "One-click installs use a password system. The button " +
                        "above will generate a password, hash it, then copy the " +
                        "password to your clipboard. The plugin doesn't store " +
                        "the password. By pasting the password into the " +
                        "Heliosphere website, the website can provide it to the " +
                        "plugin during install requests. If the hash of the " +
                        "provided password is the same as the stored hash in " +
                        "the plugin, one-click installs will proceed. " +
                        "Otherwise, the normal download prompt will be shown. " +
                        "This is to prevent other, unauthorised programs from " +
                        "issuing install requests that will automatically go " +
                        "through."
                    );
                } finally {
                    ImGui.PopTextWrapPos();
                }

                ImGui.TreePop();
            }
        }

        ImGui.Spacing();

        if (ImGui.TreeNodeEx("Miscellaneous")) {
            using var treePop = new OnDispose(ImGui.TreePop);

            if (this.Plugin.Config.FirstTimeSetupComplete && ImGuiHelper.CentredWideButton("Reset first-time setup")) {
                anyChanged = true;
                this.Plugin.Config.FirstTimeSetupComplete = false;
                this.Plugin.DoFirstTimeSetup();
            }

            if (!this.Plugin.Server.Listening && ImGuiHelper.CentredWideButton("Try starting server")) {
                try {
                    this.Plugin.Server.StartServer();
                } catch (HttpListenerException ex) {
                    ErrorHelper.Handle(ex, "Could not start server");
                    this.Plugin.NotificationManager.AddNotification(new Notification {
                        Type = NotificationType.Error,
                        MinimizedText = "Could not start server",
                        Content = $"Could not start server. {Plugin.Name} will not be able to work with the website.",
                        InitialDuration = TimeSpan.FromSeconds(5),
                    });
                }
            }
        }

        ImGui.Spacing();

        if (ImGui.TreeNodeEx("Support")) {
            using var treePop = new OnDispose(ImGui.TreePop);

            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted("When getting support, you may be asked to click these buttons and send what they copy to your clipboard.");
            ImGui.PopTextWrapPos();

            if (ImGuiHelper.CentredWideButton("Copy troubleshooting info")) {
                this.Plugin.Support.CopyTroubleshootingInfo(this._sendingToDiscord);
            }

            if (ImGuiHelper.CentredWideButton("Copy dalamud.log file")) {
                this.Plugin.Support.CopyDalamudLog();
            }

            if (ImGuiHelper.CentredWideButton("Reveal dalamud.log file")) {
                this.Plugin.Support.OpenDalamudLogFolder();
            }

            if (ImGuiHelper.CentredWideButton("Copy config")) {
                this.Plugin.Support.CopyConfig(this._sendingToDiscord);
            }

            var tracingLabel = this.Plugin.TracingEnabled
                ? "Disable tracing"
                : "Enable tracing";
            if (ImGuiHelper.CentredWideButton(tracingLabel)) {
                this.Plugin.TracingEnabled ^= true;
            }

            ImGui.Checkbox("Sending to Discord", ref this._sendingToDiscord);
        }

        ImGui.Separator();

        var version = Plugin.Version ?? "???";
        var vert = ImGui.GetContentRegionAvail().Y;
        if (vert > 0) {
            var dims = ImGui.CalcTextSize(version);
            ImGui.Dummy(new Vector2(1, vert - dims.Y - ImGui.GetStyle().ItemSpacing.Y));
        }

        ImGuiHelper.TextUnformattedDisabled(version);

        if (anyChanged) {
            this.Plugin.SaveConfig();
        }
    }

    internal string GenerateOneClickKey() {
        var salt = new byte[8];
        Random.Shared.NextBytes(salt);

        var password = new byte[16];
        Random.Shared.NextBytes(password);

        var hash = new Argon2id(password) {
            Iterations = 3,
            MemorySize = 65536,
            DegreeOfParallelism = 4,
            Salt = salt,
        }.GetBytes(128);

        this.Plugin.Config.OneClickSalt = salt;
        this.Plugin.Config.OneClickHash = Base64.Default.Encode(hash);

        return Base64.Default.Encode(password);
    }
}
