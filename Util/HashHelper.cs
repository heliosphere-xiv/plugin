using System.Text;
using Blake3;
using gfoidl.Base64;
using Konscious.Security.Cryptography;

namespace Heliosphere.Util;

internal static class HashHelper {
    internal static byte[] Hash(Blake3HashAlgorithm blake3, byte[] input) {
        blake3.Initialize();
        return blake3.ComputeHash(input);
    }

    internal static byte[] Hash(Blake3HashAlgorithm blake3, string input) {
        return Hash(blake3, Encoding.UTF8.GetBytes(input));
    }

    internal static string HashBase64(Blake3HashAlgorithm blake3, byte[] input) {
        return Base64.Url.Encode(Hash(blake3, input));
    }

    internal static string HashBase64(Blake3HashAlgorithm blake3, string input) {
        return HashBase64(blake3, Encoding.UTF8.GetBytes(input));
    }

    internal static string GetDiscriminator(List<string?> file) {
        var text = $"{file[0]}:{file[1]}:{file[2]}";
        var output = new byte[28];
        Hasher.Hash(Encoding.UTF8.GetBytes(text), output);
        return Base64.Url.Encode(output);
    }

    internal static byte[] Argon2id(byte[] salt, byte[] password) {
        return new Argon2id(password) {
            Iterations = 3,
            MemorySize = 65536,
            DegreeOfParallelism = 4,
            Salt = salt,
        }.GetBytes(128);
    }
}
