using System.Diagnostics;

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

    internal static string ChangeExtension(string path, string? ext) {
        return Path.ChangeExtension(GetBaseName(path), ext);
    }

    /// <summary>
    /// Wait until <see cref="Directory.Exists"/> returns true for a path.
    /// </summary>
    /// <param name="path">Path to check</param>
    /// <param name="timeout">Maximum time to wait before returning (default: 5 seconds)</param>
    /// <param name="wait">Time to wait between each check (default: 100 milliseconds)</param>
    /// <returns>Final result of <see cref="Directory.Exists"/></returns>
    internal static async Task<bool> WaitToExist(string path, TimeSpan? timeout = null, TimeSpan? wait = null) {
        var max = timeout ?? TimeSpan.FromSeconds(5);

        var waiting = new Stopwatch();
        waiting.Start();

        while (!Directory.Exists(path) && waiting.Elapsed <= max) {
            await Task.Delay(wait ?? TimeSpan.FromMilliseconds(100));
        }

        return Directory.Exists(path);
    }
}
