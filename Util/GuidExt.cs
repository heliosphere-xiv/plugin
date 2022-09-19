namespace Heliosphere.Util;

internal static class GuidExt {
    internal static byte[] ToProperByteArray(this Guid guid) {
        return Convert.FromHexString($"{guid:N}");
    }

    internal static string ToCrockford(this Guid guid) {
        var idBytes = guid.ToProperByteArray();
        return SimpleBase.Base32.Crockford
            .Encode(idBytes)
            .ToLowerInvariant();
    }
}
