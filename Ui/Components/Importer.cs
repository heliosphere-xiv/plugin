using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui.Components;

internal class Importer {
    private Plugin Plugin { get; }
    private string ModName { get; }
    private Guid PackageId { get; }
    private Guid VariantId { get; }
    private Guid VersionId { get; }
    private string Version { get; }
    private ModChooser ModChooser { get; }
    private ImportTask? Task { get; set; }

    internal Importer(Plugin plugin, string modName, Guid packageId, Guid variantId, Guid versionId, string version) {
        this.Plugin = plugin;
        this.ModName = modName;
        this.PackageId = packageId;
        this.VariantId = variantId;
        this.VersionId = versionId;
        this.Version = version;
        this.ModChooser = new ModChooser(this.Plugin);
    }

    /// <returns>true if import succeeded, false if not started/failed</returns>
    internal bool Draw() {
        var installed = this.Plugin.State.InstalledNoBlock.Any(entry => entry.Value.Variants.Any(variant => variant.VersionId == this.VersionId));
        using var disabled = ImGuiHelper.DisabledIf(installed);

        if (installed) {
            ImGui.SetNextItemOpen(false);
        }

        if (!ImGui.CollapsingHeader("Import from existing mod")) {
            return this.Task?.State == ImportTaskState.StartingDownload;
        }

        if (this.ModChooser.Draw() is var (directory, _)) {
            this.Task = new ImportTask(
                this.Plugin,
                directory,
                this.ModName,
                this.PackageId,
                this.VariantId,
                this.VersionId,
                this.Version
            );
            this.Task.Start();
        }

        if (this.Task is { } task) {
            ImGui.TextUnformatted(EnumHelper.PrettyName(task.State));
            var overlay = $"{task.StateCurrent:N0}";
            if (task.StateMax != 0) {
                overlay += $" / {task.StateMax:N0}";
            }

            if (task.State != ImportTaskState.WaitingForConfirmation) {
                ImGuiHelper.FullWidthProgressBar(
                    task.StateMax == 0 ? 0 : (float) task.StateCurrent / task.StateMax,
                    overlay
                );
            } else if (task is { Data: { } data, State: ImportTaskState.WaitingForConfirmation }) {
                if (data.Files.Needed == 1) {
                    var label = data.Files.Have == 1 ? "has" : "does not have";
                    ImGui.TextUnformatted($"This mod needs one file, and the selected mod {label} it. Proceed?");
                } else {
                    ImGui.TextUnformatted($"Of the {data.Files.Needed:N0} files this mod needs, the selected mod has {data.Files.Have:N0} of them. Proceed?");
                }

                if (ImGuiHelper.FullWidthButton("Proceed")) {
                    this.Task.Continue();
                }

                using (ImGuiHelper.WithWarningColour()) {
                    ImGui.TextUnformatted("Warning! Clicking Proceed will permanently convert the selected mod to a Heliosphere mod, renaming all needed files and deleting all files inside of it that Heliosphere does not need.");
                }
            }
        }

        ImGuiHelper.TextUnformattedColour(
            $"If you already have this mod installed, you can use this to attempt to convert it to a {Plugin.Name} mod, skipping most or all of the download.",
            ImGuiCol.TextDisabled
        );

        return this.Task?.State == ImportTaskState.StartingDownload;
    }
}
