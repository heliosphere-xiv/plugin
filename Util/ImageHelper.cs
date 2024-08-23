using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace Heliosphere.Util;

internal static class ImageHelper {
    internal static async Task<IDalamudTextureWrap?> LoadImageAsync(ITextureProvider provider, byte[] buffer, CancellationToken token = default) {
        if (buffer.Length >= 12 && buffer.AsSpan()[..4].SequenceEqual("RIFF"u8) && buffer.AsSpan()[8..12].SequenceEqual("WEBP"u8)) {
            return await WebPHelper.LoadAsync(provider, buffer, token);
        }

        return await provider.CreateFromImageAsync(buffer, cancellationToken: token);
    }
}
