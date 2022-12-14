using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Logging;
using Heliosphere.Model.Generated;
using Heliosphere.Util;
using ImGuiNET;
using ImGuiScene;
using StrawberryShake;

namespace Heliosphere.Ui;

internal class InstallerWindow : IDrawable {
    private Plugin Plugin { get; }
    private Guid PackageId { get; }
    private IInstallerWindow_GetVersion Info { get; }
    private int VersionId { get; }
    private string Version { get; }
    private bool IncludeTags { get; }

    private SemaphoreSlim ImagesMutex { get; } = new(1, 1);
    private Dictionary<string, TextureWrap> HashImages { get; } = new();
    private Dictionary<string, string> PathHashes { get; } = new();

    private bool _disposed;
    private int _imagesDownloading;
    private int _page;
    private bool _visible = true;
    private int _optionHovered;
    private readonly Dictionary<string, List<string>> _options;

    internal InstallerWindow(Plugin plugin, Guid packageId, IInstallerWindow_GetVersion info, int versionId, string version, bool includeTags, Dictionary<string, List<string>>? options = null) {
        this.Plugin = plugin;
        this.PackageId = packageId;
        this.Info = info;
        this.VersionId = versionId;
        this.Version = version;
        this.IncludeTags = includeTags;
        this._options = options ?? new Dictionary<string, List<string>>();

        Task.Run(async () => {
            // grab all the image hashes from the server
            var images = this.Info.InstallerImages;
            this._imagesDownloading = images.Images.Images.Count;

            using var semaphore = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);
            var tasks = images.Images.Images
                .Select(entry => Task.Run(async () => {
                    var hash = entry.Key;
                    var paths = entry.Value;

                    // ReSharper disable once AccessToDisposedClosure
                    await semaphore.WaitAsync();
                    try {
                        var hashUri = new Uri(new Uri(images.BaseUri), hash);
                        var resp = await Plugin.Client.GetAsync(hashUri, HttpCompletionOption.ResponseHeadersRead);
                        resp.EnsureSuccessStatusCode();

                        var imageBytes = await resp.Content.ReadAsByteArrayAsync();
                        var image = await this.Plugin.Interface.UiBuilder.LoadImageAsync(imageBytes);
                        if (this._disposed) {
                            return;
                        }

                        await this.ImagesMutex.WaitAsync();

                        this.HashImages[hash] = image;
                        foreach (var path in paths) {
                            this.PathHashes[path] = hash;
                        }

                        this.ImagesMutex.Release();
                    } catch (Exception ex) {
                        PluginLog.LogError(ex, $"Error downloading image {hash}");
                    } finally {
                        // ReSharper disable once AccessToDisposedClosure
                        semaphore.Release();
                        this._imagesDownloading -= 1;
                    }
                }));

            await Task.WhenAll(tasks);
        });
    }

    public void Dispose() {
        if (this._disposed) {
            return;
        }

        GC.SuppressFinalize(this);
        this._disposed = true;
        this.ImagesMutex.Dispose();
        foreach (var image in this.HashImages.Values) {
            image.Dispose();
        }
    }

    internal static async Task<IInstallerWindow_GetVersion> GetVersionInfo(int versionId) {
        var resp = await Plugin.GraphQl.InstallerWindow.ExecuteAsync(versionId);
        resp.EnsureNoErrors();

        return resp.Data!.GetVersion!;
    }

    private static async Task<InstallerWindow> Open(OpenOptions options) {
        var info = options.Info ?? await GetVersionInfo(options.VersionId);
        var selectedOptions = options.FullInstall
            ? info.Groups.ToDictionary(
                e => e.Name,
                e => e.Options.Select(o => o.Name).ToList()
            )
            : options.SelectedOptions;

        return new InstallerWindow(
            options.Plugin,
            options.PackageId,
            info,
            options.VersionId,
            info.Version,
            options.IncludeTags,
            selectedOptions
        );
    }

    internal static async Task OpenAndAdd(OpenOptions options, string? packageName = null) {
        try {
            var window = await Open(options);
            await options.Plugin.PluginUi.AddToDrawAsync(window);
        } catch (Exception ex) {
            PluginLog.LogError(ex, "Could not open installer window");
            options.Plugin.Interface.UiBuilder.AddNotification(
                $"Could not open installer window for {options.Info?.Package.Name ?? packageName}. Check that it still exists and that your internet connection is working.",
                $"[{options.Plugin.Name}] Error opening installer",
                NotificationType.Error,
                5_000
            );
        }
    }

    internal class OpenOptions {
        internal Plugin Plugin { get; init; }
        internal Guid PackageId { get; init; }
        internal int VersionId { get; init; }
        internal Dictionary<string, List<string>>? SelectedOptions { get; init; }
        internal bool FullInstall { get; init; }
        internal bool IncludeTags { get; init; }
        internal IInstallerWindow_GetVersion? Info { get; init; }
    }

    public bool Draw() {
        if (!this._visible) {
            return true;
        }

        ImGui.SetNextWindowSize(new Vector2(750, 450), ImGuiCond.Appearing);
        if (!ImGui.Begin($"Download {this.Info.Package.Name} v{this.Version} by {this.Info.Package.User.Username}###download-{this.PackageId}", ref this._visible, ImGuiWindowFlags.NoSavedSettings)) {
            ImGui.End();
            return false;
        }

        ImGui.PushID($"installer-{this.PackageId}-{this.VersionId}");

        if (this._imagesDownloading > 0) {
            ImGui.TextUnformatted($"Downloading images ({this._imagesDownloading:N0} remaining)");
        }

        var tableSize = ImGui.GetContentRegionAvail();
        tableSize.Y -= ImGuiHelpers.GetButtonSize("A").Y + ImGui.GetStyle().ItemSpacing.Y;

        var group = this.Info.Groups[this._page];
        var options = group.Options;
        if (ImGui.BeginChild("table-child", tableSize)) {
            this.DrawInstallerChildContents(tableSize, options, group);
        }

        ImGui.EndChild();

        var page = this._page;
        var atZero = page <= 0;
        var atEnd = page >= this.Info.Groups.Count - 1;

        var nextSize = ImGuiHelpers.GetButtonSize(atEnd ? "Download" : "Next");
        var offset = ImGui.GetContentRegionAvail().X - nextSize.X + ImGui.GetStyle().ItemSpacing.X;

        if (atZero) {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Previous")) {
            this._page -= 1;
        }

        if (atZero) {
            ImGui.EndDisabled();
        }

        ImGui.SameLine(offset);

        var ret = false;
        if (atEnd) {
            if (ImGui.Button("Download") && this.Plugin.Penumbra.GetModDirectory() is { } dir) {
                this.Plugin.AddDownload(new DownloadTask(this.Plugin, dir, this.VersionId, this._options, this.IncludeTags));
                ret = true;
            }
        } else {
            if (ImGui.Button("Next")) {
                this._page += 1;
                this._optionHovered = 0;
            }
        }

        ImGui.PopID();
        ImGui.End();
        return ret;
    }

    private void DrawInstallerChildContents(Vector2 tableSize, IReadOnlyList<IInstallerWindow_GetVersion_Groups_Options> options, IInstallerWindow_GetVersion_Groups group) {
        if (!ImGui.BeginTable("installer-table", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.NoHostExtendX | ImGuiTableFlags.NoHostExtendY, tableSize)) {
            return;
        }

        ImGui.TableSetupColumn("##image-and-desc");
        ImGui.TableSetupColumn("##options");

        ImGui.TableNextRow();

        if (ImGui.TableSetColumnIndex(0)) {
            this.DrawTableColumn1(options);
        }

        if (ImGui.TableSetColumnIndex(1)) {
            this.DrawTableColumn2(options, group);
        }

        ImGui.EndTable();
    }

    private void DrawTableColumn1(IReadOnlyList<IInstallerWindow_GetVersion_Groups_Options> options) {
        ImGui.PushTextWrapPos();

        if (this._optionHovered > -1 && this._optionHovered < options.Count) {
            var hovered = options[this._optionHovered];

            if (hovered.ImagePath is { } path) {
                this.ImagesMutex.Wait();
                try {
                    if (this.GetImage(path) is { } wrap) {
                        var descriptionHeight = hovered.Description == null
                            ? 0.0
                            : ImGui.CalcTextSize(hovered.Description).Y;
                        // account for item spacing from image and separator
                        descriptionHeight += ImGui.GetStyle().ItemSpacing.Y * 2
                                             + ImGui.GetStyle().FrameBorderSize;
                        var contentAvail = ImGui.GetContentRegionAvail();

                        // either use three quarters of the available space or
                        // the available space less the description, whichever
                        // is bigger
                        var maxHeight = (float) Math.Max(contentAvail.Y * 0.75, contentAvail.Y - descriptionHeight);
                        ImGuiHelper.ImageFullWidth(wrap, maxHeight, true);
                    } else {
                        ImGui.TextUnformatted("No image, still downloading, or an error occurred");
                    }
                } finally {
                    this.ImagesMutex.Release();
                }

                ImGui.Separator();
            }

            if (hovered.Description != null) {
                if (ImGui.BeginChild("description-child")) {
                    ImGui.PushTextWrapPos();
                    ImGui.TextUnformatted(hovered.Description);
                    ImGui.PopTextWrapPos();
                }

                ImGui.EndChild();
            }
        }

        ImGui.PopTextWrapPos();
    }

    private void DrawTableColumn2(IReadOnlyList<IInstallerWindow_GetVersion_Groups_Options> options, IInstallerWindow_GetVersion_Groups group) {
        ImGui.PushTextWrapPos();

        ImGui.TextUnformatted(group.Name);
        ImGui.Separator();

        if (ImGui.BeginChild("options-child")) {
            ImGui.PushTextWrapPos();
            for (var i = 0; i < options.Count; i++) {
                var option = options[i];
                var isSelected = this.IsOptionSelected(group, option);
                if (ImGui.Checkbox($"{option.Name}##{i}", ref isSelected)) {
                    this.HandleOption(group.Name, option.Name, isSelected);
                }

                if (ImGui.IsItemHovered()) {
                    this._optionHovered = i;
                }
            }

            ImGui.PopTextWrapPos();
        }

        ImGui.EndChild();

        ImGui.PopTextWrapPos();
    }

    private TextureWrap? GetImage(string path) {
        if (!this.PathHashes.TryGetValue(path, out var hash)) {
            return null;
        }

        return this.HashImages.TryGetValue(hash, out var wrap) ? wrap : null;
    }

    private bool IsOptionSelected(IInstallerWindow_GetVersion_Groups group, IInstallerWindow_GetVersion_Groups_Options option) {
        return this._options.TryGetValue(group.Name, out var chosen) && chosen.Contains(option.Name);
    }

    private void HandleOption(string groupName, string optionName, bool selected) {
        if (selected) {
            // adding
            if (!this._options.ContainsKey(groupName)) {
                this._options[groupName] = new List<string>();
            }

            if (!this._options[groupName].Contains(optionName)) {
                this._options[groupName].Add(optionName);
            }

            return;
        }

        // removing
        if (!this._options.TryGetValue(groupName, out var chosen)) {
            return;
        }

        chosen.Remove(optionName);

        if (chosen.Count == 0) {
            this._options.Remove(groupName);
        }
    }
}
