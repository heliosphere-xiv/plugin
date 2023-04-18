using Dalamud.Interface.GameFonts;
using Dalamud.Logging;

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

    public bool PreRenderFonts(List<(uint, bool)> fonts) {
        try {
            foreach (var (size, italic) in fonts) {
                try {
                    GameFontHandle handle;
                    if (this._fonts.ContainsKey((size, italic)) == false) {
                        handle = this.Plugin.Interface.UiBuilder.GetGameFontHandle(new GameFontStyle(GameFontFamily.Axis, size) {
                            Italic = italic,
                        });
                        this._fonts[(size, italic)] = handle;
                    } else {
                        // This is in case the fonts are rebuilt for whatever reason.
                        // I am not sure if you have to refresh the font in question.
                        handle = this.Plugin.Interface.UiBuilder.GetGameFontHandle(new GameFontStyle(GameFontFamily.Axis, size) {
                            Italic = italic,
                        });
                        this._fonts[(size, italic)] = handle;
                    }
                } catch (Exception exception) {
                    // Catch for whatever reason is also probably unnecessary.
                    PluginLog.Error(exception, "Failed to add font: ({0}, {1})", size, italic);
                }
            }
            // Fallback font (probably unnecessary).
            if (this._fonts.ContainsKey((12, false)) == false) {
                GameFontHandle handle = this.Plugin.Interface.UiBuilder.GetGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis12) {
                    Italic = false,
                });
                this._fonts[(12, false)] = handle;
            } else {
                // This is in case the fonts are rebuilt for whatever reason.
                // I am not sure if you have to refresh the font in question.
                //
                // Though unlike the previous of the above statements,
                // If the fallback is unnecessary then this is too.
                GameFontHandle handle = this.Plugin.Interface.UiBuilder.GetGameFontHandle(new GameFontStyle(GameFontFamily.Axis, 12) {
                    Italic = false,
                });
                this._fonts[(12, false)] = handle;
            }
        } catch (Exception exception) {
            // Catch for whatever reason is also probably unnecessary.
            PluginLog.Error(exception, "Failed to add fonts.");
        }

        return true;
    }

    internal GameFontHandle? this[uint size, bool italic] {
        get {
            GameFontHandle handle;
            if (this._fonts.ContainsKey((size, italic))) {
                handle = this._fonts[(size, italic)];
            } else {
                // Return the fallback. The key, however, should exist.
                // I have taken into account all literal calls of font size, and
                // italics including the markdown header.
                handle = this._fonts[(12, false)];
            }

            return handle.Available ? handle : null;
        }
    }

    internal GameFontHandle? this[uint size] => this[size, false];
}
