using Dalamud.Interface;
using Dalamud.Logging;
using Dalamud.Memory;
using ImGuiScene;
using WebPDotNet;

namespace Heliosphere.Util;

internal static class WebPHelper {
    internal static async Task<TextureWrap?> LoadAsync(UiBuilder builder, byte[] imageBytes) {
        const int bytesPerPixel = 4;

        using var image = WebP.WebPDecodeRGBA(imageBytes);
        if (image.NativePtr == nint.Zero) {
            PluginLog.LogWarning("webp image had a null data pointer");
            return null;
        }

        var outputBuffer = MemoryHelper.ReadRaw(image.NativePtr, image.Height * image.Width * bytesPerPixel);

        return await builder.LoadImageRawAsync(outputBuffer, image.Width, image.Height, bytesPerPixel);
    }
}
