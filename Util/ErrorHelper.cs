using Dalamud.Logging;
using Sentry;

namespace Heliosphere.Util;

internal static class ErrorHelper {
    internal static void Handle(Exception ex, string message) {
        SentrySdk.CaptureException(ex, scope => scope.Contexts["Message"] = message);
        PluginLog.LogError(ex, message);
    }
}
