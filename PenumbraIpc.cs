using System.Diagnostics.CodeAnalysis;
using Heliosphere.Ui;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;

namespace Heliosphere;

internal class PenumbraIpc : IDisposable {
    private Plugin Plugin { get; }
    private FuncSubscriber<string> GetModDirectorySubscriber { get; }
    private FuncSubscriber<string, PenumbraApiEc> AddModSubscriber { get; }
    private FuncSubscriber<string, string, PenumbraApiEc> ReloadModSubscriber { get; }
    private FuncSubscriber<string, string, string, PenumbraApiEc> SetModPathSubscriber { get; }
    private FuncSubscriber<string, string, PenumbraApiEc> DeleteModSubscriber { get; }
    private FuncSubscriber<string, string, string, PenumbraApiEc> CopyModSettingsSubscriber { get; }
    private FuncSubscriber<IList<string>> GetCollectionsSubscriber { get; }
    private FuncSubscriber<string, string, string, bool, PenumbraApiEc> TrySetModSubscriber { get; }
    private FuncSubscriber<string, string, (PenumbraApiEc, string, bool)> GetModPathSubscriber { get; }
    private FuncSubscriber<TabType, string, string, PenumbraApiEc> OpenMainWindowSubscriber { get; }
    private FuncSubscriber<string, string, string, bool, (PenumbraApiEc, (bool, int, IDictionary<string, IList<string>>, bool)?)> GetCurrentModSettingsSubscriber { get; set; }

    private EventSubscriber<string>? ModAddedEvent { get; set; }
    private EventSubscriber<string>? ModDeletedEvent { get; set; }
    private EventSubscriber<string, string>? ModMovedEvent { get; set; }

    internal PenumbraIpc(Plugin plugin) {
        this.Plugin = plugin;

        this.GetModDirectorySubscriber = Penumbra.Api.Ipc.GetModDirectory.Subscriber(this.Plugin.Interface);
        this.AddModSubscriber = Penumbra.Api.Ipc.AddMod.Subscriber(this.Plugin.Interface);
        this.ReloadModSubscriber = Penumbra.Api.Ipc.ReloadMod.Subscriber(this.Plugin.Interface);
        this.SetModPathSubscriber = Penumbra.Api.Ipc.SetModPath.Subscriber(this.Plugin.Interface);
        this.DeleteModSubscriber = Penumbra.Api.Ipc.DeleteMod.Subscriber(this.Plugin.Interface);
        this.CopyModSettingsSubscriber = Penumbra.Api.Ipc.CopyModSettings.Subscriber(this.Plugin.Interface);
        this.GetCollectionsSubscriber = Penumbra.Api.Ipc.GetCollections.Subscriber(this.Plugin.Interface);
        this.TrySetModSubscriber = Penumbra.Api.Ipc.TrySetMod.Subscriber(this.Plugin.Interface);
        this.GetModPathSubscriber = Penumbra.Api.Ipc.GetModPath.Subscriber(this.Plugin.Interface);
        this.OpenMainWindowSubscriber = Penumbra.Api.Ipc.OpenMainWindow.Subscriber(this.Plugin.Interface);
        this.GetCurrentModSettingsSubscriber = Penumbra.Api.Ipc.GetCurrentModSettings.Subscriber(this.Plugin.Interface);

        this.RegisterEvents();
    }

    public void Dispose() {
        this.ModMovedEvent?.Dispose();
        this.ModDeletedEvent?.Dispose();
        this.ModAddedEvent?.Dispose();
    }

    private void RegisterEvents() {
        this.ModAddedEvent = Penumbra.Api.Ipc.ModAdded.Subscriber(this.Plugin.Interface, _ => {
            Task.Run(async () => await this.Plugin.State.UpdatePackages());
        });

        this.ModDeletedEvent = Penumbra.Api.Ipc.ModDeleted.Subscriber(this.Plugin.Interface, _ => {
            Task.Run(async () => await this.Plugin.State.UpdatePackages());
        });

        this.ModMovedEvent = Penumbra.Api.Ipc.ModMoved.Subscriber(this.Plugin.Interface, (_, _) => {
            Task.Run(async () => await this.Plugin.State.UpdatePackages());
        });
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

    internal bool CopyModSettings(string from, string to) {
        try {
            return this.CopyModSettingsSubscriber.Invoke("", from, to) == PenumbraApiEc.Success;
        } catch (Exception) {
            return false;
        }
    }

    internal IList<string>? GetCollections() {
        try {
            return this.GetCollectionsSubscriber.Invoke();
        } catch (Exception) {
            return null;
        }
    }

    internal bool TrySetMod(string collection, string directory, bool enabled) {
        try {
            return this.TrySetModSubscriber.Invoke(collection, directory, "", enabled) == PenumbraApiEc.Success;
        } catch (Exception) {
            return false;
        }
    }

    internal string? GetModPath(string directoryName) {
        try {
            var (status, path, _) = this.GetModPathSubscriber.Invoke(directoryName, "");
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

    internal (PenumbraApiEc, CurrentModSettings?)? GetCurrentModSettings(string collectionName, string modDirectory, bool allowInheritance) {
        try {
            var (ec, tuple) = this.GetCurrentModSettingsSubscriber.Invoke(collectionName, modDirectory, "", allowInheritance);
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
}

internal struct CurrentModSettings {
    internal required bool Enabled { get; init; }
    internal required int Priority { get; init; }

    /// <summary>
    /// A dictionary of option group names and lists of enabled option names.
    /// </summary>
    internal required IDictionary<string, IList<string>> EnabledOptions { get; init; }

    internal required bool Inherited { get; init; }
}
