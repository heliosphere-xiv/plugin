namespace Heliosphere.Util;

internal static class DirectoryHelper {
    internal static void RemoveEmptyDirectories(string root) {
        foreach (var path in Directory.GetDirectories(root)) {
            RemoveEmptyDirectories(path);
            if (!Directory.EnumerateFileSystemEntries(path).Any()) {
                Directory.Delete(path, false);
            }
        }
    }
}
