using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Heliosphere.Util;

namespace Heliosphere;

internal class GameFont : IDisposable {
    private Plugin Plugin { get; }
    private readonly Dictionary<(uint, bool), IFontHandle> _fonts = new();

    internal GameFont(Plugin plugin) {
        this.Plugin = plugin;
    }

    public void Dispose() {
        foreach (var handle in this._fonts.Values) {
            handle.Dispose();
        }
    }

    internal IFontHandle? this[uint size, bool italic] {
        get {
            IFontHandle handle;
            if (this._fonts.ContainsKey((size, italic))) {
                handle = this._fonts[(size, italic)];
            } else {
                handle = this.Plugin.Interface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, size) {
                    Italic = italic,
                });
                this._fonts[(size, italic)] = handle;
            }

            return handle.Available ? handle : null;
        }
    }

    internal IFontHandle? this[uint size] => this[size, false];

    internal OnDispose? WithFont(uint size, bool italic = false) {
        var font = this[size, italic];
        font?.Push();
        return font == null
            ? null
            : new OnDispose(font.Pop);
    }
}
