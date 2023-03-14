using Dalamud.Logging;
using Sentry;

namespace Heliosphere.Util;

internal static class ErrorHelper {
    internal static void Handle(Exception ex, string message) {
        int? HResult(Exception? ex) {
            do {
                if (ex?.HResult > 0) {
                    return ex.HResult;
                }
            } while ((ex = ex?.InnerException) != null);

            return null;
        }

        var errorId = SentrySdk.CaptureException(ex, scope => scope.Contexts["ErrorHelper"] = new {
            Message = message,
            HResult = HResult(ex),
        });

        PluginLog.LogError(ex, $"[{errorId}] {message}");
    }
}
