using Blake3;
using WebPDotNet;

namespace Heliosphere;

internal static class DependencyLoader {
    internal static void Load() {
        // load blake3 native library before any multi-threaded code tries to.
        // this hopefully will prevent issues where two threads both try to load
        // the native library at the same time and it shits itself
        using (new Blake3HashAlgorithm()) {
        }

        // do the same for webp
        WebP.WebPGetDecoderVersion();
    }
}
