namespace Heliosphere.Util;

internal static class PathHelper {
    internal static string GetBaseName(string path) {
        string before;
        var after = path;

        do {
            before = after;
            after = Path.ChangeExtension(before, null);
        } while (before != after);

        return after;
    }
}
