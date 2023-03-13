using Dalamud.Logging;
using Sentry;

namespace Heliosphere.Util;

internal static class ErrorHelper {
    internal static void Handle(Exception ex, string message) {
        var errorId = SentrySdk.CaptureException(ex, scope => scope.Contexts["ErrorHelper"] = new {
            Message = message,
            HResult = (int?) (ex is IOException io ? io.HResult : null),
        });

        PluginLog.LogError(ex, $"[{errorId}] {message}");
    }
}
