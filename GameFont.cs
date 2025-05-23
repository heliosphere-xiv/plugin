using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Heliosphere.Util;

namespace Heliosphere;

internal class GameFont : IDisposable {
    private Plugin Plugin { get; }
    private readonly Dictionary<(uint, bool), IFontHandle> _fonts = [];

    internal GameFont(Plugin plugin) {
        this.Plugin = plugin;
    }

    public void Dispose() {
        foreach (var handle in this._fonts.Values) {
            handle.Dispose();
        }

        this._fonts.Clear();
    }

    internal IFontHandle? this[uint size, bool italic] {
        get {
            size *= 100;
            if (!this._fonts.TryGetValue((size, italic), out var handle)) {
                handle = this.Plugin.Interface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, (float) size / 100) {
                    Italic = italic,
                });
                this._fonts[(size, italic)] = handle;
            }

            return handle.Available ? handle : null;
        }
    }

    internal IFontHandle? this[float size, bool italic] {
        get {
            var asInt = (uint) Math.Truncate(size * 100);
            if (!this._fonts.TryGetValue((asInt, italic), out var handle)) {
                handle = this.Plugin.Interface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, size) {
                    Italic = italic,
                });
                this._fonts[(asInt, italic)] = handle;
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

    internal OnDispose? WithFont(float size, bool italic = false) {
        var font = this[size, italic];
        font?.Push();
        return font == null
            ? null
            : new OnDispose(font.Pop);
    }
}
