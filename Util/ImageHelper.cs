using Dalamud.Interface;
using ImGuiScene;

namespace Heliosphere.Util;

internal static class ImageHelper {
    internal static async Task<TextureWrap> LoadImageAsync(UiBuilder builder, byte[] buffer) {
        if (buffer.Length >= 12 && buffer.AsSpan()[..4].SequenceEqual("RIFF"u8) && buffer.AsSpan()[8..12].SequenceEqual("WEBP"u8)) {
            return await WebPHelper.LoadAsync(builder, buffer);
        }

        return await builder.LoadImageAsync(buffer);
    }
}
