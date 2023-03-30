using Dalamud.Logging;
using Sentry;

namespace Heliosphere.Util;

internal static class ErrorHelper {
    internal static int? GetInnerHResult(this Exception? ex) {
        var hResult = ex.AsEnumerable()
            .Where(ex => ex.HResult != 0)
            .Select(ex => ex.HResult)
            .FirstOrDefault();
        if (hResult == 0) {
            return null;
        }

        return hResult;
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
            HResult = ex.GetInnerHResult(),
        });

        PluginLog.LogError(ex, $"[{errorId}] {message}");
    }
}
