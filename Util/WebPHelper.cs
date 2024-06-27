using Dalamud.Interface.Internal;
using Dalamud.Interface.Textures;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using WebPDotNet;

namespace Heliosphere.Util;

internal static class WebPHelper {
    internal static async Task<IDalamudTextureWrap?> LoadAsync(ITextureProvider provider, byte[] imageBytes) {
        const int bytesPerPixel = 4;

        using var image = WebP.WebPDecodeRGBA(imageBytes);
        if (image.NativePtr == nint.Zero) {
            Plugin.Log.Warning("webp image had a null data pointer");
            return null;
        }

        var outputBuffer = MemoryHelper.ReadRaw(image.NativePtr, image.Height * image.Width * bytesPerPixel);

        // https://learn.microsoft.com/en-us/windows/win32/api/dxgiformat/ne-dxgiformat-dxgi_format
        return await provider.CreateFromRawAsync(
            new RawImageSpecification(image.Width, image.Height, 30),
            outputBuffer,
            "image"
        );
    }
}
