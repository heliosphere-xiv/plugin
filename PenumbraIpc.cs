using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;

namespace Heliosphere;

internal class PenumbraIpc {
    private Plugin Plugin { get; }
    private FuncSubscriber<string> GetModDirectorySubscriber { get; }
    private FuncSubscriber<string, PenumbraApiEc> AddModSubscriber { get; }
    private FuncSubscriber<string, string, PenumbraApiEc> ReloadModSubscriber { get; }
    private FuncSubscriber<string, string, string, PenumbraApiEc> SetModPathSubscriber { get; }
    private FuncSubscriber<string, string, PenumbraApiEc> DeleteModSubscriber { get; }

    internal PenumbraIpc(Plugin plugin) {
        this.Plugin = plugin;

        this.GetModDirectorySubscriber = Penumbra.Api.Ipc.GetModDirectory.Subscriber(this.Plugin.Interface);
        this.AddModSubscriber = Penumbra.Api.Ipc.AddMod.Subscriber(this.Plugin.Interface);
        this.ReloadModSubscriber = Penumbra.Api.Ipc.ReloadMod.Subscriber(this.Plugin.Interface);
        this.SetModPathSubscriber = Penumbra.Api.Ipc.SetModPath.Subscriber(this.Plugin.Interface);
        this.DeleteModSubscriber = Penumbra.Api.Ipc.DeleteMod.Subscriber(this.Plugin.Interface);

        this.RegisterDeleted();
    }

    private void RegisterDeleted() {
        Penumbra.Api.Ipc.ModDeleted.Subscriber(this.Plugin.Interface, _ => {
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
}
