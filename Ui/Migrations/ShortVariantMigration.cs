using System.Numerics;
using System.Text;
using Heliosphere.Ui.Dialogs;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui.Migrations;

internal class ShortVariantMigration(Plugin plugin) : Dialog($"{Plugin.Name} migration##short-variant", ImGuiWindowFlags.NoSavedSettings, new Vector2(450, 300)) {
    private Plugin Plugin { get; } = plugin;

    private Task? _task;

    protected override DrawStatus InnerDraw() {
        ImGui.PushTextWrapPos();
        using var popTextWrapPos = new OnDispose(ImGui.PopTextWrapPos);

        ImGui.TextUnformatted($"{Plugin.Name} needs to change the naming scheme used for mods managed by it. This will not impact your mods. If you do not run this change, {Plugin.Name} will cease to work, disabling auto-updates and other features, though your mods will continue to load in Penumbra.");
        ImGui.Spacing();
        ImGui.TextUnformatted($"Running this migration may take a few minutes depending on the amount of mods you have installed. If you need to, you can do this later by opening {Plugin.Name} and navigating to the Manager tab. While running, the migration may slow your game down and make it difficult to play. It is recommended that you leave the game alone while the migration is running.");

        ImGui.Separator();

        if (this._task == null) {
            if (ImGuiHelper.ChooseYesNo("Would you like to run the migration now?", out var yes)) {
                if (yes) {
                    this._task = Task.Run(async () => {
                        await this.Plugin.State.UpdatePackages(true);
                        this.Plugin.Config.LatestMigration = 1;
                        this.Plugin.SaveConfig();
                    });
                } else {
                    return DrawStatus.Finished;
                }
            }

            return DrawStatus.Continue;
        }

        // ui while running goes here

        if (this._task.IsCompleted) {
            if (this._task.IsCompletedSuccessfully) {
                ImGui.TextUnformatted("Migration successfully completed.");
                if (ImGuiHelper.CentredWideButton("Close")) {
                    return DrawStatus.Finished;
                }
            } else {
                ImGui.TextUnformatted("Migration failed.");
                if (this._task.Exception is { } error) {
                    if (ImGuiHelper.CentredWideButton("Copy exception info")) {
                        var sb = new StringBuilder();
                        sb.Append("[code]\n");
                        var i = 0;
                        foreach (var ex in error.AsEnumerable()) {
                            if (i != 0) {
                                sb.Append('\n');
                            }

                            i += 1;

                            sb.Append($"Error type: {ex.GetType().FullName}\n");
                            sb.Append($"   Message: {ex.Message}\n");
                            sb.Append($"   HResult: 0x{unchecked((uint) ex.HResult):X8}\n");
                            if (ex.StackTrace is { } trace) {
                                sb.Append(trace);
                                sb.Append('\n');
                            }
                        }

                        sb.Append("[/code]");

                        ImGui.SetClipboardText($"[code]\n{sb}\n[/code]");
                    }
                }

                if (ImGuiHelper.CentredWideButton("Retry")) {
                    this._task = null;
                }

                if (ImGuiHelper.CentredWideButton("Close (try again later)")) {
                    return DrawStatus.Finished;
                }
            }
        } else {
            var toScan = Interlocked.CompareExchange(ref this.Plugin.State.DirectoriesToScan, 0, 0);
            if (toScan != -1) {
                var scanned = Interlocked.CompareExchange(ref this.Plugin.State.CurrentDirectory, 0, 0);
                ImGuiHelper.FullWidthProgressBar(
                    (float) scanned / toScan,
                    $"Migrating - {scanned} / {toScan}"
                );
            }
        }

        return DrawStatus.Continue;
    }
}
