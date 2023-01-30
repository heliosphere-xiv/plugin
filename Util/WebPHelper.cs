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
        int stride;

        unsafe {
            fixed (byte* bytes = imageBytes) {
                features = new WebPBitstreamFeatures();
                var vp8StatusCode = Native.WebPGetFeatures((nint) bytes, imageBytes.Length, ref features);
                if (vp8StatusCode != Vp8StatusCode.Ok) {
                    throw new ExternalException(vp8StatusCode.ToString());
                }

                stride = features.Has_alpha > 0 ? 4 : 3;
                var size = features.Height * features.Width * stride;
                outputBuffer = new byte[size];
                fixed (byte* output = outputBuffer) {
                    if (features.Has_alpha > 0) {
                        Native.WebPDecodeBgraInto((nint) bytes, imageBytes.Length, (nint) output, size, stride);
                    } else {
                        Native.WebPDecodeBgrInto((nint) bytes, imageBytes.Length, (nint) output, size, stride);
                    }
                }
            }
        }

        return await builder.LoadImageRawAsync(outputBuffer, features.Width, features.Height, stride);
    }
}
