using System.Runtime.InteropServices;
using Dalamud.Interface;
using ImGuiScene;
using WebP.Net.Natives;
using WebP.Net.Natives.Enums;
using WebP.Net.Natives.Structs;

namespace Heliosphere.Util;

internal static class WebPHelper {
    internal static async Task<TextureWrap?> LoadAsync(UiBuilder builder, byte[] imageBytes) {
        byte[] outputBuffer;
        WebPBitstreamFeatures features;
        const int bytesPerPixel = 4;

        unsafe {
            fixed (byte* bytes = imageBytes) {
                features = new WebPBitstreamFeatures();
                var vp8StatusCode = Native.WebPGetFeatures((nint) bytes, imageBytes.Length, ref features);
                if (vp8StatusCode != Vp8StatusCode.Ok) {
                    throw new ExternalException(vp8StatusCode.ToString());
                }

                var stride = features.Width * bytesPerPixel;
                var size = features.Height * stride;
                outputBuffer = new byte[size];
                fixed (byte* output = outputBuffer) {
                    var result = Native.WebPDecodeBgraInto((nint) bytes, imageBytes.Length, (nint) output, size, stride);

                    if (result == 0) {
                        throw new ExternalException();
                    }
                }
            }
        }

        return await builder.LoadImageRawAsync(outputBuffer, features.Width, features.Height, bytesPerPixel);
    }
}
