using Heliosphere.Ui.Tabs;

namespace Heliosphere.Ui;

internal class PenumbraSettingsIntegration : IDisposable {
    private Plugin Plugin { get; }
    private PenumbraIpc Penumbra { get; }

    internal PenumbraSettingsIntegration(Plugin plugin, PenumbraIpc penumbra) {
        this.Plugin = plugin;
        this.Penumbra = penumbra;
    }

    public void Dispose() {
        this.Unregister();
    }

    internal void Register() {
        this.Penumbra.RegisterSettingsSection(this.Draw);
    }

    internal void Unregister() {
        this.Penumbra.UnregisterSettingsSection(this.Draw);
    }

    private void Draw() {
        Settings.DrawPenumbraIntegrationSettings(this.Plugin);
    }
}
