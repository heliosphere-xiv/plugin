namespace Heliosphere.Util;

internal static class FileHelper {
    /// <summary>
    /// Try to open a file for reading. If the file doesn't exist, returns null.
    /// <br/>
    /// Throws the same exceptions as <see cref="File.OpenRead"/> aside from
    /// <see cref="DirectoryNotFoundException"/> and
    /// <see cref="FileNotFoundException"/>.
    /// </summary>
    /// <param name="path">path to open</param>
    /// <returns>FileStream if the file exists</returns>
    internal static FileStream? OpenRead(string path) {
        try {
            return File.OpenRead(path);
        } catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) {
            return null;
        }
    }
}