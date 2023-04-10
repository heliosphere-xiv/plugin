using gfoidl.Base64;

namespace Heliosphere.Util;

internal static class Base64Ext {
    internal static bool TryDecode(this Base64 base64, string input, out byte[] output) {
        try {
            output = base64.Decode(input);
            return true;
        } catch {
            output = Array.Empty<byte>();
            return false;
        }
    }
}
