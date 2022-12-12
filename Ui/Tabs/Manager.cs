using System.Diagnostics;
using Dalamud.Interface;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Logging;
using Heliosphere.Model;
using Heliosphere.Model.Api;
using Heliosphere.Model.Generated;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui.Tabs;

internal class Manager : IDisposable {
    private Plugin Plugin { get; }
    private bool _disposed;
    private bool _managerVisible;
    private bool _versionsTabVisible;
    private (Guid, int) _selected = (Guid.Empty, 0);

    private readonly SemaphoreSlim _openingMutex = new(1, 1);
    private readonly HashSet<Guid> _openingInstaller = new();

    private readonly SemaphoreSlim _infoMutex = new(1, 1);
    private readonly Dictionary<int, IGetNewestVersionInfo_GetVersion_Variant> _info = new();

    private readonly SemaphoreSlim _versionsMutex = new(1, 1);
    private readonly Dictionary<Guid, IReadOnlyList<IGetVersions_Package_Variants>> _versions = new();

    private readonly SemaphoreSlim _gettingInfoMutex = new(1, 1);
    private readonly HashSet<int> _gettingInfo = new();

    private bool _checkingForUpdates;
    private string _filter = string.Empty;

    internal Manager(Plugin plugin) {
        this.Plugin = plugin;

        this.Plugin.ClientState.Login += this.Login;
    }

    public void Dispose() {
        if (this._disposed) {
            return;
        }

        this._disposed = true;

        this.Plugin.ClientState.Login -= this.Login;

        this._gettingInfoMutex.Dispose();
        this._versionsMutex.Dispose();
        this._infoMutex.Dispose();
        this._openingMutex.Dispose();
    }

    internal void Draw() {
        if (this._disposed || !ImGui.BeginTabItem("Manager")) {
            this._managerVisible = false;
            return;
        }

        if (!this._managerVisible) {
            Task.Run(async () => await this.Plugin.State.UpdatePackages());
        }

        this._managerVisible = true;

        if (ImGui.BeginTable("manager-table", 2, ImGuiTableFlags.Resizable)) {
            ImGui.TableSetupColumn("mods", ImGuiTableColumnFlags.WidthFixed, 1);
            ImGui.TableSetupColumn("content", ImGuiTableColumnFlags.WidthFixed, 3);
            ImGui.TableNextRow();

            if (ImGui.TableSetColumnIndex(0)) {
                this._infoMutex.Wait();
                try {
                    this.DrawPackageList();
                } finally {
                    this._infoMutex.Release();
                }
            }

            if (ImGui.TableSetColumnIndex(1)) {
                this.DrawPackageInfo();
            }

            ImGui.EndTable();
        }

        ImGui.EndTabItem();
    }

    private async Task GetInfo() {
        var tasks = this.Plugin.State.Installed
            .Select(installed => Task.Run(async () => {
                try {
                    await this.GetInfo(installed.Meta.VariantId, installed.Meta.VersionId);
                } catch (Exception ex) {
                    PluginLog.LogError(ex, $"Error getting info for {installed.Meta.Id:N} ({installed.Meta.Name})");
                }
            }));

        await Task.WhenAll(tasks);
    }

    private async Task GetInfo(int variantId, int versionId) {
        if (this._disposed) {
            return;
        }

        var info = await GraphQl.GetNewestVersion(versionId);

        if (this._disposed) {
            return;
        }

        await this._infoMutex.WaitAsync();
        this._info[variantId] = info;
        this._infoMutex.Release();
    }

    private void DrawPackageList() {
        if (ImGuiHelper.IconButton(FontAwesomeIcon.Redo, tooltip: "Refresh")) {
            Task.Run(async () => await this.Plugin.State.UpdatePackages());
        }

        ImGui.SameLine();

        var checking = this._checkingForUpdates;
        if (checking) {
            ImGui.BeginDisabled();
        }

        if (ImGuiHelper.IconButton(FontAwesomeIcon.Search, tooltip: "Check for updates")) {
            this._checkingForUpdates = true;
            Task.Run(async () => {
                try {
                    await this.GetInfo();
                } finally {
                    this._checkingForUpdates = false;
                }
            });
        }

        if (checking) {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();

        if (ImGuiHelper.IconButton(FontAwesomeIcon.CloudDownloadAlt, tooltip: "Download updates")) {
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##package-filter", "Filter...", ref this._filter, 512);

        ImGui.Separator();

        var size = ImGui.GetContentRegionAvail();
        size.Y -= ImGui.GetStyle().ItemSpacing.Y * 2
                  + ImGui.GetStyle().FramePadding.Y;
        if (!ImGui.BeginChild("package-list", size)) {
            ImGui.EndChild();
            return;
        }

        var lower = this._filter.ToLowerInvariant();
        foreach (var installed in this.Plugin.State.Installed) {
            if (!installed.Meta.Name.ToLowerInvariant().Contains(lower)) {
                continue;
            }

            var package = installed.Meta;
            var before = ImGui.GetCursorPos();

            var wrapWidth = ImGui.GetContentRegionAvail().X;
            var lineOne = package.Name;
            var lineTwo = package.Variant;
            var lineThree = $"{package.Version} - {package.Author}";
            var textSize = ImGui.CalcTextSize(lineOne, wrapWidth)
                           + ImGui.CalcTextSize(lineTwo, wrapWidth)
                           + ImGui.CalcTextSize(lineThree, wrapWidth);
            textSize.X = ImGui.GetContentRegionAvail().X;
            textSize.Y += ImGui.GetStyle().ItemInnerSpacing.Y * 3 + ImGui.GetStyle().ItemSpacing.Y;
            if (ImGui.Selectable($"##{package.Id}-{package.VariantId}", this._selected == (package.Id, package.VariantId), ImGuiSelectableFlags.None, textSize)) {
                this._selected = (package.Id, package.VariantId);
            }

            before.Y += ImGui.GetStyle().ItemInnerSpacing.Y;
            ImGui.SetCursorPos(before);
            ImGui.PushTextWrapPos(wrapWidth);

            ImGui.TextUnformatted(lineOne);

            if (this._info.TryGetValue(package.VariantId, out var info) && info.Versions.Count > 0 && package.IsUpdate(info.Versions[0].Version)) {
                ImGui.PushFont(UiBuilder.IconFont);
                var icon = FontAwesomeIcon.CloudUploadAlt.ToIconString();
                var iconSize = ImGui.CalcTextSize(icon);
                var offset = ImGui.GetContentRegionAvail().X
                             - ImGui.GetStyle().ItemSpacing.X
                             - iconSize.X;
                ImGui.SameLine(offset);
                ImGui.TextUnformatted(icon);
                ImGui.PopFont();

                ImGuiHelper.Tooltip($"A new update is available: {info.Versions[0].Version}");
            }

            var disabledColour = ImGui.GetStyle().Colors[(int) ImGuiCol.TextDisabled];
            ImGui.PushStyleColor(ImGuiCol.Text, disabledColour);
            try {
                ImGui.TextUnformatted(lineTwo);
                ImGui.TextUnformatted(lineThree);
            } finally {
                ImGui.PopStyleColor();
            }

            ImGui.PopTextWrapPos();

            var after = ImGui.GetCursorPos();
            after.Y += ImGui.GetStyle().ItemInnerSpacing.Y;

            ImGui.SetCursorPos(after);
        }

        ImGui.EndChild();
    }

    private void DrawPackageInfo() {
        if (this._selected == (Guid.Empty, 0)) {
            return;
        }

        var installed = this.Plugin.State.Installed.FirstOrDefault(pkg => pkg.Meta.Id == this._selected.Item1 && pkg.Meta.VariantId == this._selected.Item2);
        if (installed == null) {
            return;
        }

        var pkg = installed.Meta;

        var size = ImGui.GetContentRegionAvail();
        size.Y -= ImGui.GetStyle().ItemSpacing.Y * 2
                  + ImGui.GetStyle().FramePadding.Y;
        if (!ImGui.BeginChild("package-info", size)) {
            ImGui.EndChild();
            return;
        }

        ImGuiHelper.TextUnformattedCentred(pkg.Name, PluginUi.TitleSize);
        ImGuiHelper.TextUnformattedCentred($"v{pkg.Version} by {pkg.Author}");

        ImGui.Separator();

        if (installed.CoverImage is { } coverImage) {
            ImGuiHelper.ImageFullWidth(coverImage, centred: true);
            ImGui.Separator();
        }

        if (ImGui.BeginTabBar("package-info-tabs")) {
            this.DrawActionsTab(pkg);
            this.DrawDescriptionTab(pkg);
            DrawInstalledOptionsTab(pkg);
            this.DrawVersionsTab(pkg);

            ImGui.EndTabBar();
        }

        ImGui.EndChild();
    }

    private void DrawActionsTab(HeliosphereMeta pkg) {
        if (!ImGui.BeginTabItem("Actions")) {
            return;
        }

        if (ImGui.Button("Download updates")) {
            Task.Run(async () => {
                var info = await GraphQl.GetNewestVersion(pkg.VersionId);
                // these come from the server already-sorted
                if (info.Versions.Count == 0 || info.Versions[0].Version == pkg.Version) {
                    this.Plugin.Interface.UiBuilder.AddNotification(
                        $"{pkg.Name} is already up-to-date.",
                        this.Plugin.Name,
                        NotificationType.Info
                    );
                    return;
                }

                if (pkg.FullInstall) {
                    var modDir = this.Plugin.Penumbra.GetModDirectory();
                    if (modDir != null) {
                        this.Plugin.AddDownload(new DownloadTask(this.Plugin, modDir, info.Versions[0].Id, pkg.IncludeTags));
                    }
                } else {
                    await InstallerWindow.OpenAndAdd(new InstallerWindow.OpenOptions {
                        Plugin = this.Plugin,
                        PackageId = pkg.Id,
                        VersionId = pkg.VersionId,
                        SelectedOptions = pkg.SelectedOptions,
                        FullInstall = pkg.FullInstall,
                    });
                }
            });
        }

        this._openingMutex.Wait();
        var opening = this._openingInstaller.Contains(pkg.Id);

        if (opening) {
            ImGui.BeginDisabled();
        }

        if (!pkg.IsSimple() && ImGui.Button("Download different options")) {
            this._openingInstaller.Add(pkg.Id);
            Task.Run(async () => {
                await InstallerWindow.OpenAndAdd(new InstallerWindow.OpenOptions {
                    Plugin = this.Plugin,
                    PackageId = pkg.Id,
                    VersionId = pkg.VersionId,
                    SelectedOptions = pkg.SelectedOptions,
                    FullInstall = pkg.FullInstall,
                }, pkg.Name);

                await this._openingMutex.WaitAsync();
                this._openingInstaller.Remove(pkg.Id);
                this._openingMutex.Release();
            });
        }

        if (opening) {
            ImGui.EndDisabled();
        }

        this._openingMutex.Release();

        if (ImGui.Button("Open on Heliosphere website")) {
            var url = $"https://heliosphere.app/mod/{pkg.Id.ToCrockford()}";
            Process.Start(new ProcessStartInfo(url) {
                UseShellExecute = true,
            });
        }

        var ctrlShift = ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift;
        if (!ctrlShift) {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Delete mod")) {
            var dir = pkg.ModDirectoryName();
            if (this.Plugin.Penumbra.DeleteMod(dir)) {
                Task.Run(async () => await this.Plugin.State.UpdatePackages());
            }
        }

        if (!ctrlShift) {
            ImGui.EndDisabled();

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted("Hold Ctrl + Shift to enable this button.");
                ImGui.EndTooltip();
            }
        }

        ImGui.EndTabItem();
    }

    private void DrawDescriptionTab(HeliosphereMeta pkg) {
        if (!ImGui.BeginTabItem("Description")) {
            return;
        }

        ImGuiHelper.Markdown(pkg.Description);
        ImGui.EndTabItem();
    }

    private static void DrawInstalledOptionsTab(HeliosphereMeta pkg) {
        if (!ImGui.BeginTabItem("Installed options")) {
            return;
        }

        if (pkg.IsSimple()) {
            ImGui.TextUnformatted("Simple mod - no options available.");
        } else if (pkg.SelectedOptions.Count == 0) {
            ImGui.TextUnformatted("No options installed.");
        }

        foreach (var (group, options) in pkg.SelectedOptions) {
            if (!ImGui.TreeNodeEx(group)) {
                continue;
            }

            foreach (var option in options) {
                ImGui.TextUnformatted(option);
            }

            ImGui.TreePop();
        }

        ImGui.EndTabItem();
    }

    private void DrawVersionsTab(HeliosphereMeta pkg) {
        void DrawRefreshButton(HeliosphereMeta pkg, bool forceRefresh) {
            var checking = this._checkingForUpdates || this._gettingInfo.Contains(pkg.VariantId);
            if (checking) {
                ImGui.BeginDisabled();
            }

            if (ImGui.Button("Refresh") || forceRefresh) {
                this._gettingInfo.Add(pkg.VariantId);
                Task.Run(async () => {
                    PluginLog.Debug($"refreshing info and versions for {pkg.Id}");

                    // get normal info
                    await this.GetInfo(pkg.VariantId, pkg.VersionId);

                    await this._gettingInfoMutex.WaitAsync();
                    this._gettingInfo.Remove(pkg.VariantId);
                    this._gettingInfoMutex.Release();

                    // get all versions
                    var versions = await GraphQl.GetAllVersions(pkg.Id);

                    await this._versionsMutex.WaitAsync();
                    this._versions[pkg.Id] = versions;
                    this._versionsMutex.Release();
                });
            }

            if (checking) {
                ImGui.EndDisabled();
            }
        }

        void DrawVersionList(HeliosphereMeta pkg) {
            if (!this._versions.TryGetValue(pkg.Id, out var versions)) {
                return;
            }

            var showVariants = versions.Count > 1;
            foreach (var variant in versions) {
                if (showVariants) {
                    var currentVariant = variant.Versions.Any(version => version.Id == pkg.VersionId);
                    var currentVariantText = currentVariant ? " (installed)" : "";
                    if (!ImGui.TreeNodeEx($"{variant.Name}{currentVariantText}###{pkg.Id}-variant-{variant.Id}")) {
                        continue;
                    }
                }

                foreach (var version in variant.Versions) {
                    var current = pkg.VersionId == version.Id;
                    var currentText = current ? " (installed)" : "";

                    if (!ImGui.TreeNodeEx($"{version.Version}{currentText}###{pkg.Id}-{version.Id}")) {
                        continue;
                    }

                    var installText = current ? "Reinstall" : "Install";
                    if (ImGui.Button(installText)) {
                        Task.Run(async () => await PromptWindow.OpenAndAdd(this.Plugin, pkg.Id, version.Id));
                    }

                    ImGuiHelper.Markdown(version.Changelog ?? "No changelog.");

                    ImGui.TreePop();
                }

                if (showVariants) {
                    ImGui.TreePop();
                }
            }
        }

        if (!ImGui.BeginTabItem("Versions")) {
            this._versionsTabVisible = false;
            return;
        }

        var force = !this._versionsTabVisible;
        this._versionsTabVisible = true;

        // refresh button
        this._gettingInfoMutex.Wait();
        try {
            DrawRefreshButton(pkg, force);
        } finally {
            this._gettingInfoMutex.Release();
        }

        // list of versions with changelogs
        this._versionsMutex.Wait();
        try {
            DrawVersionList(pkg);
        } finally {
            this._versionsMutex.Release();
        }

        ImGui.EndTabItem();
    }

    private async void Login(object? sender, EventArgs eventArgs) {
        this._checkingForUpdates = true;
        try {
            await this.GetInfo();
        } finally {
            this._checkingForUpdates = false;
        }

        if (this._disposed) {
            return;
        }

        await this._infoMutex.WaitAsync();
        var withUpdates = this.Plugin.State.Installed
            .Select(installed => this._info.TryGetValue(installed.Meta.VariantId, out var info) ? (installed, info) : (installed, null))
            .Where(entry => entry.info is { Versions.Count: > 0 })
            .Where(entry => entry.installed.Meta.IsUpdate(entry.info!.Versions[0].Version))
            .ToList();
        this._infoMutex.Release();

        if (withUpdates.Count == 0) {
            return;
        }

        if (!this.Plugin.Config.AutoUpdate) {
            var header = withUpdates.Count == 1
                ? "One mod has an update."
                : $"{withUpdates.Count} mods have updates.";
            this.Plugin.ChatGui.Print(header);

            foreach (var (installed, newest) in withUpdates) {
                this.Plugin.ChatGui.Print($"    》 {installed.Meta.Name} ({installed.Meta.Variant}): {installed.Meta.Version} → {newest!.Versions[0].Version}");
            }

            return;
        }

        var modDir = this.Plugin.Penumbra.GetModDirectory();
        if (modDir == null) {
            return;
        }

        var tasks = new List<Task<bool>>();
        foreach (var (installed, newest) in withUpdates) {
            var newId = newest!.Versions[0].Id;
            if (installed.Meta.FullInstall) {
                // this was a fully-installed mod, so just download the entire
                // update
                var task = new DownloadTask(this.Plugin, modDir, newId, installed.Meta.IncludeTags);
                this.Plugin.Downloads.Add(task);
                tasks.Add(Task.Run(async () => {
                    try {
                        await task.Start();
                        return true;
                    } catch (Exception ex) {
                        PluginLog.LogError(ex, $"Error fully updating {installed.Meta.Name} ({installed.Meta.Variant} - {installed.Meta.Id})");
                        return false;
                    }
                }));
                continue;
            }

            tasks.Add(Task.Run(async () => {
                try {
                    // check to make sure the update still has all the same options
                    var groups = newest.Versions[0].Groups
                        .ToDictionary(g => g.Name, g => g.Options);

                    foreach (var (selGroup, selOptions) in installed.Meta.SelectedOptions) {
                        if (!groups.TryGetValue(selGroup, out var availOptions)) {
                            return false;
                        }

                        if (selOptions.Any(selOption => availOptions.All(avail => avail.Name != selOption))) {
                            return false;
                        }
                    }

                    var task = new DownloadTask(this.Plugin, modDir, newId, installed.Meta.SelectedOptions, installed.Meta.IncludeTags);
                    this.Plugin.Downloads.Add(task);
                    await task.Start();

                    return true;
                } catch (Exception ex) {
                    PluginLog.LogError(ex, $"Error partially updating {installed.Meta.Name} ({installed.Meta.Variant} - {installed.Meta.Id})");
                    return false;
                }
            }));
        }

        // wait for all tasks to finish first
        await Task.WhenAll(tasks);

        var updateMessages = new List<(bool, string)>();
        for (var i = 0; i < tasks.Count; i++) {
            var task = tasks[i];
            var (old, upd) = withUpdates[i];

            var result = await task;
            updateMessages.Add((
                result,
                result
                    ? $"    》 {old.Meta.Name} ({old.Meta.Variant}): {old.Meta.Version} → {upd!.Versions[0].Version}"
                    : $"    》 {old.Meta.Name} ({old.Meta.Variant}): failed - you may need to manually update"
            ));
        }

        var successful = updateMessages.Count(m => m.Item1);
        var numMods = successful == updateMessages.Count
            ? $"{successful}"
            : $"{successful}/{updateMessages.Count}";
        var plural = updateMessages.Count == 1
            ? ""
            : "s";
        var autoHeader = $"{numMods} mod{plural} auto-updated successfully.";
        this.Plugin.Interface.UiBuilder.AddNotification(
            autoHeader,
            this.Plugin.Name,
            NotificationType.Success
        );
        this.Plugin.ChatGui.Print(autoHeader);

        foreach (var (success, message) in updateMessages) {
            if (success) {
                this.Plugin.ChatGui.Print(message);
            } else {
                this.Plugin.ChatGui.PrintError(message);
            }
        }
    }
}
