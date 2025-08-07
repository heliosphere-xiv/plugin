using System.Numerics;
using Dalamud.Bindings.ImGui;
using Heliosphere.Util;

namespace Heliosphere.Ui;

internal class BreakingChangeWindow : IDisposable {
    private Plugin Plugin { get; }

    internal Guard<List<BreakingChange>> BreakingChanges { get; } = new([]);

    internal BreakingChangeWindow(Plugin plugin) {
        this.Plugin = plugin;

        this.Plugin.Interface.UiBuilder.Draw += this.Draw;
    }

    public void Dispose() {
        this.Plugin.Interface.UiBuilder.Draw -= this.Draw;
        this.BreakingChanges.Dispose();
    }

    private void Draw() {
        using var changes = this.BreakingChanges.Wait(0);
        if (changes == null || changes.Data.Count == 0) {
            return;
        }

        var visible = true;
        using var end = new OnDispose(ImGui.End);
        ImGui.SetNextWindowSize(new Vector2(500, 350), ImGuiCond.Appearing);
        if (!ImGui.Begin("[HS] Breaking changes after mod updates", ref visible)) {
            return;
        }

        if (!visible) {
            // user has requested to close
            changes.Data.Clear();
            return;
        }

        ImGui.PushTextWrapPos();
        using var pop = new OnDispose(ImGui.PopTextWrapPos);

        if (ImGui.Checkbox("Check for breaking changes after mod updates", ref this.Plugin.Config.WarnAboutBreakingChanges)) {
            this.Plugin.SaveConfig();
        }

        ImGui.Separator();

        ImGui.TextUnformatted("Recent mod updates have breaking changes that have resulted in your saved settings potentially being reset or changed. You can review these changes below.");
        ImGui.Separator();

        // draw each breaking change with a button to open that mod
        foreach (var change in changes.Data) {
            if (!ImGui.TreeNodeEx($"{change.ModName} ({change.VariantName}): {change.OldVersion} \u2192 {change.NewVersion}", ImGuiTreeNodeFlags.DefaultOpen)) {
                continue;
            }

            using var pop2 = new OnDispose(ImGui.TreePop);

            if (ImGuiHelper.FullWidthButton("Open in Penumbra")) {
                this.Plugin.Framework.RunOnFrameworkThread(() => this.Plugin.Penumbra.OpenMod(change.ModPath));
            }

            if (change.RemovedGroups.Count > 0) {
                if (ImGui.TreeNodeEx("Removed option groups", ImGuiTreeNodeFlags.DefaultOpen)) {
                    using var pop3 = new OnDispose(ImGui.TreePop);

                    ImGui.SameLine();
                    ImGuiHelper.Help("These option groups are no longer available (but may be available under a different name), which means your settings for this group are no longer applied.");

                    foreach (var group in change.RemovedGroups) {
                        UnformattedBullet(group);
                    }
                }
            }

            if (change.ChangedType.Count > 0) {
                if (ImGui.TreeNodeEx("Changed group type", ImGuiTreeNodeFlags.DefaultOpen)) {
                    using var pop3 = new OnDispose(ImGui.TreePop);

                    ImGui.SameLine();
                    ImGuiHelper.Help("These option groups have gone from single-select to multi-select or vice versa, which can change your selected options in unexpected ways.");

                    foreach (var group in change.ChangedType) {
                        UnformattedBullet(group);
                    }
                }
            }

            if (change.TruncatedOptions.Count > 0) {
                if (ImGui.TreeNodeEx("Removed options", ImGuiTreeNodeFlags.DefaultOpen)) {
                    using var pop3 = new OnDispose(ImGui.TreePop);

                    ImGui.SameLine();
                    ImGuiHelper.Help("These option groups have had options removed from the end, which has unselected options you had enabled.");

                    foreach (var (group, options) in change.TruncatedOptions) {
                        if (!ImGui.TreeNodeEx(group, ImGuiTreeNodeFlags.DefaultOpen)) {
                            continue;
                        }

                        using var pop4 = new OnDispose(ImGui.TreePop);
                        foreach (var option in options) {
                            UnformattedBullet(option);
                        }
                    }
                }
            }

            if (change.DifferentOptionNames.Count > 0) {
                if (ImGui.TreeNodeEx("Changed option names", ImGuiTreeNodeFlags.DefaultOpen)) {
                    using var pop3 = new OnDispose(ImGui.TreePop);

                    ImGui.SameLine();
                    ImGuiHelper.Help("These option groups have had their option names changed, which may have unexpectedly changed what options you have selected.");

                    foreach (var (group, _, _) in change.DifferentOptionNames) {
                        UnformattedBullet(group);
                    }
                }
            }

            if (change.ChangedOptionOrder.Count > 0) {
                if (ImGui.TreeNodeEx("Changed option order", ImGuiTreeNodeFlags.DefaultOpen)) {
                    using var pop3 = new OnDispose(ImGui.TreePop);

                    ImGui.SameLine();
                    ImGuiHelper.Help("These option groups have had their options reordered, which may have unexpectedly changed what options you have selected.");
                    ImGui.Spacing();

                    foreach (var (group, _, _) in change.ChangedOptionOrder) {
                        UnformattedBullet(group);
                    }
                }
            }

            continue;

            void UnformattedBullet(string text) {
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextUnformatted(text);
            }
        }
    }
}

internal class BreakingChange {
    internal required string ModName { get; init; }
    internal required string VariantName { get; init; }
    internal required string OldVersion { get; init; }
    internal required string NewVersion { get; init; }
    internal required string ModPath { get; init; }
    internal List<string> RemovedGroups { get; } = [];
    internal List<string> ChangedType { get; } = [];
    internal List<(string Group, string[] RemovedOptions)> TruncatedOptions { get; } = [];
    internal List<(string Group, string[] Old, string[] New)> DifferentOptionNames { get; } = [];
    internal List<(string Group, string[] Old, string[] New)> ChangedOptionOrder { get; } = [];

    internal bool HasChanges => this.RemovedGroups.Count > 0
                                || this.ChangedType.Count > 0
                                || this.TruncatedOptions.Count > 0
                                || this.DifferentOptionNames.Count > 0
                                || this.ChangedOptionOrder.Count > 0;
}
