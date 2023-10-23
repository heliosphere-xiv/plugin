using System.Numerics;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui;

internal class BreakingChangeWindow : IDisposable {
    private Plugin Plugin { get; }

    internal Guard<List<BreakingChange>> BreakingChanges { get; } = new(new List<BreakingChange>());

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
        if (!ImGui.Begin("[HS] Breaking changes after mod update(s)", ref visible)) {
            return;
        }

        if (!visible) {
            // user has requested to close
            changes.Data.Clear();
            return;
        }

        ImGui.PushTextWrapPos();
        using var pop = new OnDispose(ImGui.PopTextWrapPos);

        ImGui.TextUnformatted("Recent mod updates have breaking changes that have resulted in your saved settings potentially being reset. You can review these changes below.");
        ImGui.Separator();

        // draw each breaking change with a button to open that mod
        foreach (var change in changes.Data) {
            if (!ImGui.TreeNodeEx($"{change.ModName} ({change.VariantName})", ImGuiTreeNodeFlags.DefaultOpen)) {
                continue;
            }

            using var pop2 = new OnDispose(ImGui.TreePop);

            var buttonWidth = ImGui.GetContentRegionAvail().X;
            if (ImGui.Button("Open in Penumbra", new Vector2(buttonWidth, 0))) {
                this.Plugin.Framework.RunOnFrameworkThread(() => this.Plugin.Penumbra.OpenMod(change.ModPath));
            }

            if (change.RemovedGroups.Count > 0) {
                if (ImGui.TreeNodeEx("Removed option groups", ImGuiTreeNodeFlags.DefaultOpen)) {
                    using var pop3 = new OnDispose(ImGui.TreePop);
                    foreach (var group in change.RemovedGroups) {
                        ImGui.Bullet();
                        ImGui.SameLine();
                        ImGui.TextUnformatted(group);
                    }
                }
            }
        }
    }
}

internal class BreakingChange {
    internal required string ModName { get; init; }
    internal required string VariantName { get; init; }
    internal required string ModPath { get; init; }
    internal List<string> RemovedGroups { get; } = new();
}
