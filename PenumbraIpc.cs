using System.Diagnostics.CodeAnalysis;
using Heliosphere.Ui;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;

namespace Heliosphere;

internal class PenumbraIpc : IDisposable {
    private Plugin Plugin { get; }
    private PenumbraWindowIntegration WindowIntegration { get; }

    /// <inheritdoc cref="ApiVersion" />
    private ApiVersion ApiVersionSubscriber { get; }

    /// <inheritdoc cref="Penumbra.Api.IpcSubscribers.GetModDirectory" />
    private GetModDirectory GetModDirectorySubscriber { get; }

    /// <inheritdoc cref="Penumbra.Api.IpcSubscribers.AddMod" />
    private AddMod AddModSubscriber { get; }

    /// <inheritdoc cref="Penumbra.Api.IpcSubscribers.ReloadMod" />
    private ReloadMod ReloadModSubscriber { get; }

    /// <inheritdoc cref="Penumbra.Api.IpcSubscribers.SetModPath" />
    private SetModPath SetModPathSubscriber { get; }

    /// <inheritdoc cref="Penumbra.Api.IpcSubscribers.DeleteMod" />
    private DeleteMod DeleteModSubscriber { get; }

    /// <inheritdoc cref="Penumbra.Api.IpcSubscribers.CopyModSettings" />
    private CopyModSettings CopyModSettingsSubscriber { get; }

    /// <inheritdoc cref="Penumbra.Api.IpcSubscribers.GetCollections" />
    private GetCollections GetCollectionsSubscriber { get; }

    /// <inheritdoc cref="Penumbra.Api.IpcSubscribers.TrySetMod" />
    private TrySetMod TrySetModSubscriber { get; }

    /// <inheritdoc cref="Penumbra.Api.IpcSubscribers.GetModPath" />
    private GetModPath GetModPathSubscriber { get; }

    /// <inheritdoc cref="OpenMainWindow" />
    private OpenMainWindow OpenMainWindowSubscriber { get; }

    /// <inheritdoc cref="Penumbra.Api.IpcSubscribers.GetCurrentModSettings" />
    private GetCurrentModSettings GetCurrentModSettingsSubscriber { get; set; }

    /// <inheritdoc cref="GetModList" />
    private GetModList GetModListSubscriber { get; }

    // events

    /// <inheritdoc cref="Initialized" />
    private EventSubscriber InitializedEvent { get; set; }

    /// <inheritdoc cref="ModAdded" />
    private EventSubscriber<string>? ModAddedEvent { get; set; }

    /// <inheritdoc cref="ModDeleted" />
    private EventSubscriber<string>? ModDeletedEvent { get; set; }

    /// <inheritdoc cref="ModMoved" />
    private EventSubscriber<string, string>? ModMovedEvent { get; set; }

    /// <inheritdoc cref="PostEnabledDraw" />
    private EventSubscriber<string>? PostEnabledDrawEvent { get; set; }

    /// <inheritdoc cref="PreSettingsTabBarDraw" />
    private EventSubscriber<string, float, float>? PreSettingsTabBarDrawEvent { get; set; }

    /// <inheritdoc cref="PreSettingsDraw" />
    private EventSubscriber<string>? PreSettingsDrawEvent { get; set; }

    internal PenumbraIpc(Plugin plugin) {
        this.Plugin = plugin;
        this.WindowIntegration = new PenumbraWindowIntegration(this.Plugin);


        this.ApiVersionSubscriber = new ApiVersion(this.Plugin.Interface);
        this.GetModDirectorySubscriber = new GetModDirectory(this.Plugin.Interface);
        this.AddModSubscriber = new AddMod(this.Plugin.Interface);
        this.ReloadModSubscriber = new ReloadMod(this.Plugin.Interface);
        this.SetModPathSubscriber = new SetModPath(this.Plugin.Interface);
        this.DeleteModSubscriber = new DeleteMod(this.Plugin.Interface);
        this.CopyModSettingsSubscriber = new CopyModSettings(this.Plugin.Interface);
        this.GetCollectionsSubscriber = new GetCollections(this.Plugin.Interface);
        this.TrySetModSubscriber = new TrySetMod(this.Plugin.Interface);
        this.GetModPathSubscriber = new GetModPath(this.Plugin.Interface);
        this.OpenMainWindowSubscriber = new OpenMainWindow(this.Plugin.Interface);
        this.GetCurrentModSettingsSubscriber = new GetCurrentModSettings(this.Plugin.Interface);
        this.GetModListSubscriber = new GetModList(this.Plugin.Interface);

        this.RegisterEvents();
    }

    public void Dispose() {
        this.UnregisterEvents();
    }

    private void ReregisterEvents() {
        this.UnregisterEvents();
        this.RegisterEvents();
    }

    private void UnregisterEvents() {
        this.PreSettingsDrawEvent?.Dispose();
        this.PreSettingsTabBarDrawEvent?.Dispose();
        this.PostEnabledDrawEvent?.Dispose();
        this.ModMovedEvent?.Dispose();
        this.ModDeletedEvent?.Dispose();
        this.ModAddedEvent?.Dispose();
        this.InitializedEvent?.Dispose();
    }

    private void RegisterEvents() {
        this.InitializedEvent = Initialized.Subscriber(this.Plugin.Interface, this.ReregisterEvents);

        this.ModAddedEvent = ModAdded.Subscriber(this.Plugin.Interface, _ => {
            Task.Run(async () => await this.Plugin.State.UpdatePackages());
        });

        this.ModDeletedEvent = ModDeleted.Subscriber(this.Plugin.Interface, _ => {
            Task.Run(async () => await this.Plugin.State.UpdatePackages());
        });

        this.ModMovedEvent = ModMoved.Subscriber(this.Plugin.Interface, (_, _) => {
            Task.Run(async () => await this.Plugin.State.UpdatePackages());
        });

        this.PostEnabledDrawEvent = PostEnabledDraw.Subscriber(this.Plugin.Interface, this.WindowIntegration.PostEnabledDraw);
        this.PreSettingsTabBarDrawEvent = PreSettingsTabBarDraw.Subscriber(this.Plugin.Interface, this.WindowIntegration.PreSettingsTabBarDraw);
    }

    internal bool AtLeastVersion((int breaking, int features) tuple) {
        return this.AtLeastVersion(tuple.breaking, tuple.features);
    }

    internal bool AtLeastVersion(int breaking, int features) {
        if (this.GetApiVersions() is not var (installedBreaking, installedFeatures)) {
            return false;
        }

        if (installedBreaking > breaking) {
            return true;
        }

        return installedBreaking == breaking && installedFeatures >= features;
    }

    private (int Breaking, int Features)? GetApiVersions() {
        try {
            return this.ApiVersionSubscriber.Invoke();
        } catch (Exception) {
            return null;
        }
    }

    internal string? GetModDirectory() {
        try {
            return this.GetModDirectorySubscriber.Invoke();
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>
    /// Gets the mod directory from Penumbra. Will open a warning popup to users
    /// who have not set Penumbra up correctly.
    /// </summary>
    /// <param name="modDirectory">The mod directory</param>
    /// <returns>true if the mod directory is valid, false if invalid or Penumbra's IPC could not be contacted</returns>
    internal bool TryGetModDirectory([NotNullWhen(true)] out string? modDirectory) {
        modDirectory = this.GetModDirectory();
        if (modDirectory?.Trim() == string.Empty) {
            Task.Run(async () => await this.Plugin.PluginUi.AddIfNotPresentAsync(new SetUpPenumbraWindow(this.Plugin)));
        }

        return !string.IsNullOrWhiteSpace(modDirectory);
    }

    internal bool AddMod(string path) {
        try {
            return this.AddModSubscriber.Invoke(path) == PenumbraApiEc.Success;
        } catch (Exception) {
            return false;
        }
    }

    internal bool ReloadMod(string directoryName) {
        try {
            return this.ReloadModSubscriber.Invoke(directoryName, "") == PenumbraApiEc.Success;
        } catch (Exception) {
            return false;
        }
    }

    internal bool SetModPath(string directoryName, string newPath) {
        try {
            return this.SetModPathSubscriber.Invoke(directoryName, "", newPath) == PenumbraApiEc.Success;
        } catch (Exception) {
            return false;
        }
    }

    internal bool DeleteMod(string directoryName) {
        try {
            return this.DeleteModSubscriber.Invoke(directoryName, "") == PenumbraApiEc.Success;
        } catch (Exception) {
            return false;
        }
    }

/// <inheritdoc cref="Penumbra.Api.IpcSubscribers.CopyModSettings"/>
    internal bool CopyModSettings(string from, string to) {
        try {
            return this.CopyModSettingsSubscriber.Invoke(Guid.Empty, from, to) == PenumbraApiEc.Success;
        } catch (Exception) {
            return false;
        }
    }

    /// <inheritdoc cref="Penumbra.Api.IpcSubscribers.GetCollections"/>
    internal IReadOnlyDictionary<Guid, string>? GetCollections() {
        try {
            return this.GetCollectionsSubscriber.Invoke();
        } catch (Exception) {
            return null;
        }
    }

    /// <inheritdoc cref="Penumbra.Api.IpcSubscribers.TrySetMod"/>
    internal bool TrySetMod(Guid collectionId, string directory, bool enabled) {
        try {
            return this.TrySetModSubscriber.Invoke(collectionId, directory, enabled, "") == PenumbraApiEc.Success;
        } catch (Exception) {
            return false;
        }
    }

    /// <inheritdoc cref="Penumbra.Api.IpcSubscribers.GetModPath"/>
    internal string? GetModPath(string directoryName) {
        try {
            var (status, path, _, _) = this.GetModPathSubscriber.Invoke(directoryName, "");
            return status == PenumbraApiEc.Success ? path : null;
        } catch (Exception) {
            return null;
        }
    }

    internal void OpenMod(string modDirectory) {
        try {
            this.OpenMainWindowSubscriber.Invoke(TabType.Mods, modDirectory, "");
        } catch (Exception) {
            // no-op
        }
    }

    internal void OpenSettings() {
        try {
            this.OpenMainWindowSubscriber.Invoke(TabType.Settings, "", "");
        } catch (Exception) {
            // no-op
        }
    }

    /// <inheritdoc cref="Penumbra.Api.IpcSubscribers.GetCurrentModSettings"/>
    internal (PenumbraApiEc, CurrentModSettings?)? GetCurrentModSettings(Guid collectionId, string modDirectory, bool ignoreInheritance) {
        try {
            var (ec, tuple) = this.GetCurrentModSettingsSubscriber.Invoke(collectionId, modDirectory, "", ignoreInheritance);
            if (tuple == null) {
                return (ec, null);
            }

            return (ec, new CurrentModSettings {
                Enabled = tuple.Value.Item1,
                Priority = tuple.Value.Item2,
                EnabledOptions = tuple.Value.Item3,
                Inherited = tuple.Value.Item4,
            });
        } catch (Exception) {
            return null;
        }
    }

    /// <inheritdoc cref="Penumbra.Api.IpcSubscribers.GetModList"/>
    internal IDictionary<string, string>? GetMods() {
        try {
            return this.GetModListSubscriber.Invoke();
        } catch (Exception) {
            return null;
        }
    }
}

internal struct CurrentModSettings {
    internal required bool Enabled { get; init; }
    internal required int Priority { get; init; }

    /// <summary>
    /// A dictionary of option group names and lists of enabled option names.
    /// </summary>
    internal required Dictionary<string, List<string>> EnabledOptions { get; init; }

    internal required bool Inherited { get; init; }
}
