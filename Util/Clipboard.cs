using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.System.Ole;
using Windows.Win32.UI.Shell;

namespace Heliosphere.Util;

internal static class Clipboard {
    /// <summary>
    /// Copy files to the clipboard as if they were copied in Explorer.
    /// </summary>
    /// <param name="paths">Full paths to files to be copied.</param>
    /// <returns>Returns true on success.</returns>
    internal static unsafe bool CopyFiles(IEnumerable<string> paths) {
        var pathBytes = paths
            .Select(Encoding.Unicode.GetBytes)
            .ToArray();
        var pathBytesSize = pathBytes
            .Select(bytes => bytes.Length)
            .Sum();
        var sizeWithTerminators = pathBytesSize + pathBytes.Length * 2;

        var dropFilesSize = sizeof(DROPFILES);
        var hGlobal = PInvoke.GlobalAlloc_SafeHandle(
            GLOBAL_ALLOC_FLAGS.GHND,
            // struct size + size of encoded strings + null terminator for each
            // string + two null terminators for end of list
            (uint) (dropFilesSize + sizeWithTerminators + 4)
        );
        var dropFiles = (DROPFILES*) PInvoke.GlobalLock(hGlobal);

        *dropFiles = default;
        dropFiles->fWide = true;
        dropFiles->pFiles = (uint) dropFilesSize;

        var pathLoc = (byte*) ((nint) dropFiles + dropFilesSize);
        foreach (var bytes in pathBytes) {
            // copy the encoded strings
            for (var i = 0; i < bytes.Length; i++) {
                pathLoc[i] = bytes[i];
            }

            // null terminate
            pathLoc[bytes.Length] = 0;
            pathLoc[bytes.Length + 1] = 0;
            pathLoc += bytes.Length + 2;
        }

        // double null terminator for end of list
        for (var i = 0; i < 4; i++) {
            pathLoc[i] = 0;
        }

        PInvoke.GlobalUnlock(hGlobal);

        if (PInvoke.OpenClipboard(HWND.Null)) {
            PInvoke.SetClipboardData(
                (uint) CLIPBOARD_FORMAT.CF_HDROP,
                hGlobal
            );
            PInvoke.CloseClipboard();
            return true;
        }

        hGlobal.Dispose();
        return false;
    }
}
