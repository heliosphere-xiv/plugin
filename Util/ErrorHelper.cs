using Dalamud.Logging;
using Sentry;

namespace Heliosphere.Util;

internal static class ErrorHelper {
    internal static IEnumerable<int> GetHResults(this Exception? ex) {
        return ex.AsEnumerable()
            .Where(ex => ex.HResult != 0)
            .Select(ex => ex.HResult);
    }

    internal static IEnumerable<Exception> AsEnumerable(this Exception? ex) {
        do {
            if (ex != null) {
                yield return ex;
            }
        } while ((ex = ex?.InnerException) != null);
    }

    internal static void Handle(Exception ex, string message) {
        var errorId = SentrySdk.CaptureException(ex, scope => scope.Contexts["ErrorHelper"] = new {
            Message = message,
            HResults = ex.GetHResults().ToList(),
        });

        PluginLog.LogError(ex, $"[{errorId}] {message}");
    }
}
