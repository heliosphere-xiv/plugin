using System.Diagnostics;
using System.Net;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Logging;
using gfoidl.Base64;
using Heliosphere.Util;
using ImGuiNET;
using Konscious.Security.Cryptography;

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
        anyChanged |= ImGui.Checkbox("Include tags by default", ref this.Plugin.Config.IncludeTags);

        anyChanged |= ImGui.Checkbox("Overwrite mod path name in Penumbra on updates", ref this.Plugin.Config.ReplaceSortName);
        ImGui.SameLine();
        ImGuiHelper.Help("Uncheck this if you change the name in the mod path to re-order your mods. Most users should keep this checked.");

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

        if (ImGui.CollapsingHeader("One-click install")) {
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
                anyChanged = true;

                ImGui.SetClipboardText(Base64.Default.Encode(password));
                this.Plugin.Interface.UiBuilder.AddNotification(
                    "Code copied to clipboard. Paste it on the Heliosphere website.",
                    this.Plugin.Name,
                    NotificationType.Info
                );
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
            ImGuiHelper.Help("This is the collection that mods installed via one-click will be enabled in by default.");

            ImGui.SetNextItemWidth(-1);
            var combo = this.Plugin.Config.OneClickCollection ?? "<none>";
            if (ImGui.BeginCombo("##one-click-default-collection", combo)) {
                if (ImGui.Selectable("<none>", this.Plugin.Config.OneClickCollection == null)) {
                    this.Plugin.Config.OneClickCollection = null;
                    anyChanged = true;
                }

                ImGui.Separator();

                if (this.Plugin.Penumbra.GetCollections() is { } collections) {
                    foreach (var collection in collections) {
                        if (!ImGui.Selectable(collection, this.Plugin.Config.OneClickCollection == collection)) {
                            continue;
                        }

                        this.Plugin.Config.OneClickCollection = collection;
                        anyChanged = true;
                    }
                }

                ImGui.EndCombo();
            }

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

        if (!this.Plugin.Server.Listening && ImGui.Button("Try starting server")) {
            ImGui.Separator();

            try {
                this.Plugin.Server.StartServer();
            } catch (HttpListenerException ex) {
                PluginLog.LogError(ex, "Could not start server");
                this.Plugin.Interface.UiBuilder.AddNotification(
                    "Could not start server",
                    this.Plugin.Name,
                    NotificationType.Error
                );
            }
        }

        ImGui.EndTabItem();

        if (anyChanged) {
            this.Plugin.SaveConfig();
        }
    }
}
