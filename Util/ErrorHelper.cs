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
        var errorId = SentrySdk.CaptureException(ex, scope => scope.Contexts["Error Helper"] = new {
            Message = message,
            HResults = ex.GetHResults()
                .Select(hr => (uint) hr)
                .Select(hr => $"0x{hr:X8}")
                .ToList(),
        });

        PluginLog.LogError(ex, $"[{errorId}] {message}");
    }
}
