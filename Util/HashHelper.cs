using System.Text;
using gfoidl.Base64;
using SHA3.Net;

namespace Heliosphere.Util;

internal static class HashHelper {
    internal static byte[] Hash(Sha3 sha3, byte[] input) {
        sha3.Initialize();
        return sha3.ComputeHash(input);
    }

    internal static byte[] Hash(Sha3 sha3, string input) {
        return Hash(sha3, Encoding.UTF8.GetBytes(input));
    }

    internal static string HashBase64(Sha3 sha3, byte[] input) {
        return Base64.Url.Encode(Hash(sha3, input));
    }

    internal static string HashBase64(Sha3 sha3, string input) {
        return HashBase64(sha3, Encoding.UTF8.GetBytes(input));
    }

    internal static string GetDiscriminator(List<string?> file) {
        using var sha3 = Sha3.Sha3224();
        return HashBase64(sha3, $"{file[0]}:{file[1]}:{file[2]}");
    }
}
