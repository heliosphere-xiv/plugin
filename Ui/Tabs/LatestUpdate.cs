using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui.Tabs;

internal class LatestUpdate : IDisposable {
    private Plugin Plugin { get; }
    private PluginUi Ui => this.Plugin.PluginUi;

    internal List<UpdateSummary> Summaries { get; } = new();

    internal LatestUpdate(Plugin plugin) {
        this.Plugin = plugin;
    }

    public void Dispose() {
    }

    internal void Draw() {
        if (!ImGuiHelper.BeginTab(this.Ui, PluginUi.Tab.LatestUpdate)) {
            return;
        }

        using var end = new OnDispose(ImGui.EndTabItem);

        foreach (var summary in this.Summaries) {
            DrawSummary(summary);
        }
    }

    private static void DrawSummary(UpdateSummary summary) {
        using var summaryId = ImGuiHelper.WithId($"##{summary.Started}-{summary.Finished}");
        var duration = summary.Finished - summary.Started;
        var number = summary.Mods.Count == 1
            ? "one mod"
            : $"{summary.Mods.Count} mods";
        if (!ImGui.TreeNodeEx($"{summary.Started:G} ({number}, {duration:g})")) {
            return;
        }

        using var pop1 = new OnDispose(ImGui.TreePop);

        foreach (var mod in summary.Mods) {
            using var modId = ImGuiHelper.WithId($"##{mod.Id}");
            var numVariants = mod.Variants.Count == 1
                ? "one variant"
                : $"{mod.Variants.Count} variants";

            if (!ImGui.TreeNodeEx($"{mod.NewName} ({numVariants})", ImGuiTreeNodeFlags.DefaultOpen)) {
                continue;
            }

            using var pop2 = new OnDispose(ImGui.TreePop);

            if (mod.OldName != mod.NewName) {
                ImGui.TextUnformatted($"Renamed from {mod.OldName}");
            }

            foreach (var variant in mod.Variants) {
                using var variantId = ImGuiHelper.WithId($"##{variant.Id}");
                var numVersions = variant.VersionHistory.Count == 1
                    ? "one version"
                    : $"{variant.VersionHistory.Count} versions";
                if (!ImGui.TreeNodeEx($"{variant.NewName} ({numVersions})", ImGuiTreeNodeFlags.DefaultOpen)) {
                    continue;
                }

                using var pop3 = new OnDispose(ImGui.TreePop);

                if (variant.OldName != variant.NewName) {
                    ImGui.TextUnformatted($"Renamed from {variant.OldName}");
                }

                foreach (var version in variant.VersionHistory) {
                    if (!ImGui.TreeNodeEx(version.Version.ToString(), ImGuiTreeNodeFlags.DefaultOpen)) {
                        continue;
                    }

                    using var pop4 = new OnDispose(ImGui.TreePop);

                    ImGui.TextUnformatted(version.Changelog ?? "No changelog");
                }
            }
        }
    }
}
