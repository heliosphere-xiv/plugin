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

    internal static IEnumerable<string> GetFilesRecursive(string root) {
        return GetEntriesRecursive(root, true);
    }

    internal static IEnumerable<string> GetDirectoriesRecursive(string root) {
        return GetEntriesRecursive(root, false);
    }

    private static IEnumerable<string> GetEntriesRecursive(string root, bool files) {
        var result = files ? 0 : FileAttributes.Directory;
        return Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Where(path => {
                try {
                    return (File.GetAttributes(path) & FileAttributes.Directory) == result;
                } catch {
                    return false;
                }
            });
    }
}
