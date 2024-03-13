using System.Diagnostics;
using System.Numerics;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using Heliosphere.Model;
using Heliosphere.Model.Api;
using Heliosphere.Model.Generated;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui.Tabs;

internal class Manager : IDisposable {
    private Plugin Plugin { get; }
    private PluginUi Ui => this.Plugin.PluginUi;
    private bool _disposed;
    private bool _managerVisible;
    private bool _versionsTabVisible;
    private Guid _selected = Guid.Empty;
    private Guid _selectedVariant = Guid.Empty;

    private readonly Guard<HashSet<Guid>> _openingInstaller = new(new HashSet<Guid>());
    private readonly Guard<Dictionary<Guid, IVariantInfo>> _info = new(new Dictionary<Guid, IVariantInfo>());
    private readonly Guard<Dictionary<Guid, IReadOnlyList<IGetVersions_Package_Variants>>> _versions = new(new Dictionary<Guid, IReadOnlyList<IGetVersions_Package_Variants>>());
    private readonly Guard<HashSet<Guid>> _gettingInfo = new(new HashSet<Guid>());

    private bool _downloadingUpdates;
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

        this._gettingInfo.Dispose();
        this._versions.Dispose();
        this._info.Dispose();
        this._openingInstaller.Dispose();
    }

    internal void Draw() {
        if (this._disposed || !ImGuiHelper.BeginTab(this.Ui, PluginUi.Tab.Manager)) {
            this._managerVisible = false;
            return;
        }

        if (!this._managerVisible) {
            Task.Run(async () => await this.Plugin.State.UpdatePackages());
        }

        this._managerVisible = true;

        if (ImGui.BeginTable("manager-table", 2, ImGuiTableFlags.Resizable)) {
            ImGui.TableSetupColumn("mods", ImGuiTableColumnFlags.WidthFixed, 0.25f);
            ImGui.TableSetupColumn("content", ImGuiTableColumnFlags.WidthStretch, 0.75f);
            ImGui.TableNextRow();

            if (ImGui.TableSetColumnIndex(0)) {
                using var guard = this._info.Wait(0);
                if (guard != null) {
                    this.DrawPackageList(guard.Data);
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
        var ids = this.Plugin.State.Installed
            .Values
            .SelectMany(pkg => pkg.Variants)
            .Select(meta => meta.VariantId)
            .ToList();

        if (this._disposed) {
            return;
        }

        var info = await GraphQl.GetNewestVersions(ids);
        if (this._disposed) {
            return;
        }

        using (var guard = await this._info.WaitAsync()) {
            foreach (var variant in info) {
                guard.Data[variant.Id] = variant;
                ids.Remove(variant.Id);
            }
        }

        if (ids.Count <= 0) {
            return;
        }

        const string sep = "\n  ";
        var idStr = string.Join(sep, ids.Select(id => id.ToString("N")));
        Plugin.Log.Warning($"No information returned about the following variants (they no longer exist):{sep}{idStr}");
    }

    private async Task GetInfo(Guid variantId) {
        if (this._disposed) {
            return;
        }

        var info = await GraphQl.GetNewestVersion(variantId);
        if (this._disposed || info == null) {
            return;
        }

        using var guard = await this._info.WaitAsync();
        guard.Data[variantId] = info;
    }

    private void DrawPackageList(Dictionary<Guid, IVariantInfo> allInfo) {
        if (ImGuiHelper.IconButton(FontAwesomeIcon.Redo, tooltip: "Refresh")) {
            Task.Run(async () => await this.Plugin.State.UpdatePackages());
        }

        ImGui.SameLine();

        using (ImGuiHelper.WithDisabled(this._checkingForUpdates)) {
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
        }

        ImGui.SameLine();

        using (ImGuiHelper.WithDisabled(this._downloadingUpdates)) {
            if (ImGuiHelper.IconButton(FontAwesomeIcon.CloudDownloadAlt, tooltip: "Download updates")) {
                Task.Run(async () => await this.DownloadUpdates(false));
            }
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##package-filter", "Filter...", ref this._filter, 512);

        ImGui.Separator();

        var external = this.Plugin.State.ExternalNoBlock.Count;
        if (external > 0) {
            var plural = external == 1 ? "mod" : "mods";
            if (ImGui.Button($"Import {external:N0} new {plural}...")) {
                this.Ui.AddIfNotPresent(new ExternalImportWindow(this.Plugin));
            }

            ImGuiHelper.Tooltip("Detected Heliosphere mods installed without using the plugin. Click here for import options.");

            ImGui.Separator();
        }

        var size = ImGui.GetContentRegionAvail();
        size.Y -= ImGui.GetStyle().ItemSpacing.Y * 2
                  + ImGui.GetStyle().FramePadding.Y;
        if (!ImGui.BeginChild("package-list", size)) {
            ImGui.EndChild();
            return;
        }

        var toScan = Interlocked.CompareExchange(ref this.Plugin.State.DirectoriesToScan, 0, 0);
        if (toScan != -1) {
            var scanned = Interlocked.CompareExchange(ref this.Plugin.State.CurrentDirectory, 0, 0);
            ImGui.ProgressBar(
                (float) scanned / toScan,
                new Vector2(ImGui.GetContentRegionAvail().X, 25 * ImGuiHelpers.GlobalScale),
                $"Scanning - {scanned} / {toScan}"
            );
        }

        var lower = this._filter.ToLowerInvariant();
        foreach (var (pkgId, pkg) in this.Plugin.State.InstalledNoBlock.OrderBy(entry => entry.Value.Name)) {
            if (!pkg.Name.ToLowerInvariant().Contains(lower)) {
                continue;
            }

            var before = ImGui.GetCursorPos();

            var wrapWidth = ImGui.GetContentRegionAvail().X;
            var lineOne = pkg.Name;
            var variantPlural = pkg.Variants.Count == 1
                ? "variant"
                : "variants";
            var lineTwo = $"{pkg.Variants.Count} {variantPlural} • {pkg.Author}";
            var textSize = ImGui.CalcTextSize(lineOne, wrapWidth)
                           + ImGui.CalcTextSize(lineTwo, wrapWidth);
            textSize.X = ImGui.GetContentRegionAvail().X;
            textSize.Y += ImGui.GetStyle().ItemInnerSpacing.Y * 2 + ImGui.GetStyle().ItemSpacing.Y;
            if (ImGui.Selectable($"##{pkgId}", this._selected == pkgId, ImGuiSelectableFlags.None, textSize)) {
                this._selected = pkgId;
                this._selectedVariant = pkg.Variants.Count > 0
                    ? pkg.Variants[0].VariantId
                    : Guid.Empty;
            }

            before.Y += ImGui.GetStyle().ItemInnerSpacing.Y;
            ImGui.SetCursorPos(before);
            ImGui.PushTextWrapPos(wrapWidth);

            ImGui.TextUnformatted(lineOne);

            var numUpdates = pkg.Variants
                .Count(meta =>
                    allInfo.TryGetValue(meta.VariantId, out var info)
                    && info.Versions.Count > 0
                    && meta.IsUpdate(info.Versions[0].Version)
                );
            if (numUpdates > 0) {
                ImGui.PushFont(UiBuilder.IconFont);
                var icon = FontAwesomeIcon.CloudUploadAlt.ToIconString();
                var iconSize = ImGui.CalcTextSize(icon);
                var offset = ImGui.GetContentRegionAvail().X
                             - ImGui.GetStyle().ItemSpacing.X
                             - iconSize.X;
                ImGui.SameLine(offset);
                ImGui.TextUnformatted(icon);
                ImGui.PopFont();

                var updateTooltip = numUpdates == 1
                    ? "A new update is available"
                    : $"{numUpdates} new updates are available";
                ImGuiHelper.Tooltip(updateTooltip);
            }

            ImGuiHelper.TextUnformattedDisabled(lineTwo);

            ImGui.PopTextWrapPos();

            var after = ImGui.GetCursorPos();
            after.Y += ImGui.GetStyle().ItemInnerSpacing.Y;

            ImGui.SetCursorPos(after);
        }

        ImGui.EndChild();
    }

    private void DrawPackageInfo() {
        if (this._selected == Guid.Empty) {
            return;
        }

        var installed = this.Plugin.State.InstalledNoBlock
            .Values
            .FirstOrDefault(pkg => pkg.Id == this._selected);
        var meta = installed?.Variants.FirstOrDefault(v => v.VariantId == this._selectedVariant);
        if (installed == null || meta == null) {
            return;
        }

        var size = ImGui.GetContentRegionAvail();
        size.Y -= ImGui.GetStyle().ItemSpacing.Y * 2
                  + ImGui.GetStyle().FramePadding.Y;
        if (!ImGui.BeginChild("package-info", size)) {
            ImGui.EndChild();
            return;
        }

        ImGuiHelper.TextUnformattedCentred(meta.Name, PluginUi.TitleSize);
        ImGuiHelper.TextUnformattedCentred(meta.Variant);
        ImGuiHelper.TextUnformattedCentred($"v{meta.Version} by {meta.Author}");

        ImGui.Separator();

        if (!meta.FullInstall) {
            ImGui.TextUnformatted("(partially installed)");

            ImGui.Separator();
        }

        if (installed.Variants.Count > 1) {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##variant-picker", meta.Variant)) {
                foreach (var variant in installed.Variants.OrderBy(v => v.Variant)) {
                    var partial = variant.FullInstall ? "" : "*";
                    if (ImGui.Selectable($"{variant.Variant}{partial}##{variant.VariantId}", variant.VariantId == this._selectedVariant)) {
                        this._selectedVariant = variant.VariantId;
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.Separator();
        }

        if (installed.CoverImage is { } coverImage) {
            ImGuiHelper.ImageFullWidth(coverImage, centred: true);
            ImGui.Separator();
        }

        if (ImGui.BeginTabBar("package-info-tabs")) {
            this.DrawActionsTab(meta);
            this.DrawDescriptionTab(meta);
            DrawInstalledOptionsTab(meta);
            this.DrawVersionsTab(meta);

            ImGui.EndTabBar();
        }

        ImGui.EndChild();
    }

    private void DrawActionsTab(HeliosphereMeta pkg) {
        if (!ImGui.BeginTabItem("Actions")) {
            return;
        }

        if (ImGuiHelper.CentredWideButton("Download updates")) {
            Task.Run(async () => {
                var info = await GraphQl.GetNewestVersion(pkg.VariantId);
                if (info == null) {
                    return;
                }

                // these come from the server already-sorted
                if (info.Versions.Count == 0 || info.Versions[0].Version == pkg.Version) {
                    this.Plugin.Interface.UiBuilder.AddNotification(
                        $"{pkg.Name} is already up-to-date.",
                        Plugin.Name,
                        NotificationType.Info
                    );
                    return;
                }

                if (pkg.FullInstall) {
                    if (this.Plugin.Penumbra.TryGetModDirectory(out var modDir)) {
                        this.Plugin.DownloadCodes.TryGetCode(pkg.Id, out var code);
                        await this.Plugin.AddDownloadAsync(new DownloadTask(this.Plugin, modDir, info.Versions[0].Id, pkg.IncludeTags, false, null, code));
                    }
                } else {
                    this.Plugin.DownloadCodes.TryGetCode(pkg.Id, out var key);
                    await InstallerWindow.OpenAndAdd(new InstallerWindow.OpenOptions {
                        Plugin = this.Plugin,
                        PackageId = pkg.Id,
                        VersionId = pkg.VersionId,
                        SelectedOptions = pkg.SelectedOptions,
                        FullInstall = pkg.FullInstall,
                        IncludeTags = pkg.IncludeTags,
                        OpenInPenumbra = false,
                        DownloadKey = key,
                    });
                }
            });
        }

        using (var openingHandle = this._openingInstaller.Wait(0)) {
            var opening = openingHandle == null || openingHandle.Data.Contains(pkg.Id);

            using (ImGuiHelper.WithDisabled(opening)) {
                if (!pkg.IsSimple() && ImGuiHelper.CentredWideButton("Download different options") && openingHandle != null) {
                    openingHandle.Data.Add(pkg.Id);
                    Task.Run(async () => {
                        this.Plugin.DownloadCodes.TryGetCode(pkg.Id, out var key);
                        await InstallerWindow.OpenAndAdd(new InstallerWindow.OpenOptions {
                            Plugin = this.Plugin,
                            PackageId = pkg.Id,
                            VersionId = pkg.VersionId,
                            SelectedOptions = pkg.SelectedOptions,
                            FullInstall = pkg.FullInstall,
                            IncludeTags = pkg.IncludeTags,
                            OpenInPenumbra = this.Plugin.Config.OpenPenumbraAfterInstall,
                            DownloadKey = key,
                        }, pkg.Name);

                        using var guard = await this._openingInstaller.WaitAsync();
                        guard.Data.Remove(pkg.Id);
                    });
                }
            }
        }

        if (ImGuiHelper.CentredWideButton("Open in Penumbra")) {
            this.Plugin.Penumbra.OpenMod(pkg.ModDirectoryName());
        }

        if (ImGuiHelper.CentredWideButton("Open on Heliosphere website")) {
            var url = $"https://heliosphere.app/mod/{pkg.Id.ToCrockford()}";
            Process.Start(new ProcessStartInfo(url) {
                UseShellExecute = true,
            });
        }

        var ctrlShift = ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift;
        using (ImGuiHelper.WithDisabled(!ctrlShift)) {
            if (ImGuiHelper.CentredWideButton("Delete mod")) {
                var dir = pkg.ModDirectoryName();
                if (this.Plugin.Penumbra.DeleteMod(dir)) {
                    Task.Run(async () => await this.Plugin.State.UpdatePackages());
                }
            }
        }

        if (!ctrlShift && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted("Hold Ctrl + Shift to enable this button.");
            ImGui.EndTooltip();
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
        void DrawRefreshButton(HeliosphereMeta pkg, bool forceRefresh, Guard<HashSet<Guid>>.Handle? guard) {
            var checking = this._checkingForUpdates || guard == null || guard.Data.Contains(pkg.VariantId);

            if (checking) {
                ImGui.BeginDisabled();
            }

            if ((ImGui.Button("Refresh") || forceRefresh) && guard != null) {
                guard.Data.Add(pkg.VariantId);

                Task.Run(async () => {
                    Plugin.Log.Debug($"refreshing info and versions for {pkg.Id}");

                    // get normal info
                    await this.GetInfo(pkg.VariantId);

                    using (var guard = await this._gettingInfo.WaitAsync()) {
                        guard.Data.Remove(pkg.VariantId);
                    }

                    // get all versions
                    var versions = await GraphQl.GetAllVersions(pkg.Id);

                    using (var guard = await this._versions.WaitAsync()) {
                        guard.Data[pkg.Id] = versions;
                    }
                });
            }

            if (checking) {
                ImGui.EndDisabled();
            }
        }

        void DrawVersionList(HeliosphereMeta pkg, Guard<Dictionary<Guid, IReadOnlyList<IGetVersions_Package_Variants>>>.Handle? versionsHandle) {
            if (versionsHandle == null || !versionsHandle.Data.TryGetValue(pkg.Id, out var versions)) {
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
                        this.Plugin.DownloadCodes.TryGetCode(pkg.Id, out var key);
                        Task.Run(async () => await PromptWindow.OpenAndAdd(this.Plugin, pkg.Id, version.Id, key));
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
        using (var guard = this._gettingInfo.Wait(0)) {
            DrawRefreshButton(pkg, force, guard);
        }

        // list of versions with changelogs
        using (var guard = this._versions.Wait(0)) {
            DrawVersionList(pkg, guard);
        }

        ImGui.EndTabItem();
    }

    private async Task DownloadUpdates(bool useConfig) {
        this._downloadingUpdates = true;
        try {
            await this.DownloadUpdatesInner(useConfig);
        } finally {
            this._downloadingUpdates = false;
        }
    }

    private async Task DownloadUpdatesInner(bool useConfig) {
        this._checkingForUpdates = true;
        try {
            await this.GetInfo();
        } finally {
            this._checkingForUpdates = false;
        }

        if (this._disposed) {
            return;
        }

        List<(HeliosphereMeta meta, IVariantInfo? info)> withUpdates;
        using (var guard = await this._info.WaitAsync()) {
            withUpdates = this.Plugin.State.Installed.Values
                .SelectMany(pkg => pkg.Variants)
                .Select(meta => guard.Data.TryGetValue(meta.VariantId, out var info) ? (meta, info) : (meta, null))
                .Where(entry => entry.info is { Versions.Count: > 0 })
                .Where(entry => entry.meta.IsUpdate(entry.info!.Versions[0].Version))
                .ToList();
        }

        if (withUpdates.Count == 0) {
            return;
        }

        if (useConfig && !this.Plugin.Config.AutoUpdate) {
            var header = withUpdates.Count == 1
                ? "One mod has an update."
                : $"{withUpdates.Count} mods have updates.";
            this.Plugin.ChatGui.Print(header);

            foreach (var (installed, newest) in withUpdates) {
                this.Plugin.ChatGui.Print($"    》 {installed.Name} ({installed.Variant}): {installed.Version} → {newest!.Versions[0].Version}");
            }

            return;
        }

        if (!this.Plugin.Penumbra.TryGetModDirectory(out var modDir)) {
            return;
        }

        var summary = new UpdateSummary();

        var tasks = new List<Task<bool>>();
        foreach (var (installed, newest) in withUpdates) {
            var newId = newest!.Versions[0].Id;

            SentrySdk.AddBreadcrumb(
                "Adding download due to update",
                data: new Dictionary<string, string> {
                    ["VersionId"] = newId.ToCrockford(),
                }
            );

            if (installed.FullInstall) {
                // this was a fully-installed mod, so just download the entire
                // update
                this.Plugin.DownloadCodes.TryGetCode(installed.Id, out var code);
                var task = new DownloadTask(this.Plugin, modDir, newId, installed.IncludeTags, false, null, code);
                var downloadTask = await this.Plugin.AddDownloadAsync(task);
                if (downloadTask == null) {
                    Plugin.Log.Warning($"failed to add an update for {newId} - already in queue");
                    continue;
                }

                tasks.Add(Task.Run(async () => {
                    try {
                        await downloadTask;
                        return true;
                    } catch (Exception ex) {
                        ErrorHelper.Handle(ex, $"Error fully updating {installed.Name} ({installed.Variant} - {installed.Id})");
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

                    foreach (var (selGroup, selOptions) in installed.SelectedOptions) {
                        if (!groups.TryGetValue(selGroup, out var availOptions)) {
                            return false;
                        }

                        if (selOptions.Any(selOption => availOptions.All(avail => avail.Name != selOption))) {
                            return false;
                        }
                    }

                    this.Plugin.DownloadCodes.TryGetCode(installed.Id, out var code);
                    var task = new DownloadTask(this.Plugin, modDir, newId, installed.SelectedOptions, installed.IncludeTags, false, null, code);
                    var downloadTask = await this.Plugin.AddDownloadAsync(task);
                    if (downloadTask == null) {
                        Plugin.Log.Warning($"failed to add update for {newId} to queue - already in queue");
                        return false;
                    }

                    await downloadTask;
                    return true;
                } catch (Exception ex) {
                    ErrorHelper.Handle(ex, $"Error partially updating {installed.Name} ({installed.Variant} - {installed.Id})");
                    return false;
                }
            }));
        }

        // wait for all tasks to finish first
        await Task.WhenAll(tasks);

        summary.Finish();

        var updatedMods = new Dictionary<Guid, UpdatedMod>();
        var updateMessages = new List<(bool, string)>();
        for (var i = 0; i < tasks.Count; i++) {
            var task = tasks[i];
            var (old, upd) = withUpdates[i];

            var result = await task;

            if (!updatedMods.ContainsKey(old.Id)) {
                updatedMods.Add(old.Id, new UpdatedMod(old.Id, old.Name, upd!.Package.Name));
            }

            var versionInfo = upd!.Versions[0];
            var updatedInfo = new VariantUpdateInfo(versionInfo.Version, versionInfo.Changelog);
            updatedMods[old.Id].Variants.Add(new UpdatedVariant(
                old.VariantId,
                result ? UpdateStatus.Success : UpdateStatus.Fail,
                old.Variant,
                upd.Name,
                new List<VariantUpdateInfo> { updatedInfo }
            ));

            updateMessages.Add((
                result,
                result
                    ? $"    》 {old.Name} ({old.Variant}): {old.Version} → {versionInfo.Version}"
                    : $"    》 {old.Name} ({old.Variant}): failed - you may need to manually update"
            ));
        }

        foreach (var (_, updated) in updatedMods) {
            summary.Mods.Add(updated);
        }

        this.Plugin.PluginUi.AddSummary(summary);

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
            Plugin.Name,
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

        var moreInfo = new SeStringBuilder()
            .Add(this.Plugin.LinkPayloads[LinkPayloads.Command.OpenChangelog])
            .AddText("[")
            .AddUiForeground("Click to see more information", 32)
            .AddText("]")
            .Add(RawPayload.LinkTerminator)
            .Build();
        this.Plugin.ChatGui.Print(moreInfo);
    }

    private void Login() {
        if (this.Plugin.Interface.IsAutoUpdateComplete) {
            Task.Run(async () => await this.DownloadUpdates(true));
        } else {
            this.Plugin.Interface.ActivePluginsChanged += this.PluginsChanged;
        }
    }

    private void PluginsChanged(PluginListInvalidationKind kind, bool affectedThisPlugin) {
        if (kind != PluginListInvalidationKind.AutoUpdate) {
            return;
        }

        this.Plugin.Interface.ActivePluginsChanged -= this.PluginsChanged;

        Task.Run(async () => await this.DownloadUpdates(true));
    }
}
