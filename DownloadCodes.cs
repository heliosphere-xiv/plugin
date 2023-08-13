using System.Text;
using gfoidl.Base64;
using Heliosphere.Util;
using Newtonsoft.Json;

namespace Heliosphere;

[Serializable]
internal class DownloadCodes : IDisposable {
    private string FilePath { get; set; } = null!;
    private SemaphoreSlim Mutex { get; } = new(1, 1);

    public string Key { get; set; } = string.Empty;
    public Dictionary<string, string> Codes { get; set; } = new();

    internal static DownloadCodes Create(string path) {
        var key = new byte[32];
        Random.Shared.NextBytes(key);

        return new DownloadCodes {
            FilePath = path,
            Key = Base64.Default.Encode(key),
        };
    }

    internal static DownloadCodes? Load(string path) {
        try {
            var json = File.ReadAllText(path);
            var codes = JsonConvert.DeserializeObject<DownloadCodes>(json);
            if (codes == null) {
                return null;
            }

            codes.FilePath = path;
            return codes;
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            return null;
        } catch (Exception ex) {
            ErrorHelper.Handle(ex, "could not load download codes");
            return null;
        }
    }

    public void Dispose() {
        this.Mutex.Dispose();
    }

    internal void Save() {
        string json;
        using (SemaphoreGuard.Wait(this.Mutex)) {
            json = JsonConvert.SerializeObject(this);
        }

        File.WriteAllText(this.FilePath, json);
    }

    internal bool TryGetCode(Guid packageId, out string? code) {
        code = null;

        string? enc;
        using (SemaphoreGuard.Wait(this.Mutex)) {
            if (!this.Codes.TryGetValue(packageId.ToString("N"), out enc)) {
                return false;
            }
        }

        if (!Base64.Default.TryDecode(this.Key, out var key)) {
            return false;
        }

        if (!Base64.Default.TryDecode(enc, out var encBytes)) {
            return false;
        }

        QuoteUnquoteEncrypt(key, encBytes);

        try {
            code = Encoding.UTF8.GetString(encBytes);
            return true;
        } catch {
            return false;
        }
    }

    internal bool TryInsert(Guid packageId, string code) {
        if (!Base64.Default.TryDecode(this.Key, out var key)) {
            return false;
        }

        var codeBytes = Encoding.UTF8.GetBytes(code);
        QuoteUnquoteEncrypt(key, codeBytes);

        var enc = Base64.Default.Encode(codeBytes);

        using (SemaphoreGuard.Wait(this.Mutex)) {
            this.Codes[packageId.ToString("N")] = enc;
        }

        return true;
    }

    /// <summary>
    /// Just XOR with the key. This isn't designed to stop someone dedicated.
    /// </summary>
    private static void QuoteUnquoteEncrypt(byte[] key, byte[] data) {
        for (var i = 0; i < data.Length; i++) {
            data[i] = (byte) (data[i] ^ key[i % key.Length]);
        }
    }
}
