using Dalamud.Interface.GameFonts;

namespace Heliosphere;

internal class GameFont : IDisposable {
    private Plugin Plugin { get; }
    private readonly Dictionary<(uint, bool), GameFontHandle> _fonts = new();

    internal GameFont(Plugin plugin) {
        this.Plugin = plugin;
    }

    public void Dispose() {
        foreach (var handle in this._fonts.Values) {
            handle.Dispose();
        }
    }

    internal GameFontHandle? this[uint size, bool italic] {
        get {
            GameFontHandle handle;
            if (this._fonts.ContainsKey((size, italic))) {
                handle = this._fonts[(size, italic)];
            } else {
                handle = this.Plugin.Interface.UiBuilder.GetGameFontHandle(new GameFontStyle(GameFontFamily.Axis, size) {
                    Italic = italic,
                });
                this._fonts[(size, italic)] = handle;
            }

            return handle.Available ? handle : null;
        }
    }

    internal GameFontHandle? this[uint size] => this[size, false];
}
