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

        this.RegisterEvents();
    }

    public void Dispose() {
        this.ModMovedEvent?.Dispose();
        this.ModDeletedEvent?.Dispose();
    }

    private void RegisterEvents() {
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
}
