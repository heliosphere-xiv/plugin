using System.Diagnostics;
using System.Text;
using Dalamud.Logging;
using Heliosphere.Exceptions;
using Newtonsoft.Json;
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
        var errorId = SentrySdk.CaptureException(ex, scope => {
            var json = JsonConvert.SerializeObject(Plugin.Instance.Config, Formatting.Indented);
            scope.AddAttachment(Encoding.UTF8.GetBytes(json), "config.json", AttachmentType.Default, "application/json");

            var multiBox = GetMultiBoxStatus();
            scope.SetTag("multibox", multiBox.ToString());

            scope.Contexts["Error Helper"] = new {
                Message = message,
                HResults = ex.GetHResults()
                    .Select(hr => (uint) hr)
                    .Select(hr => $"0x{hr:X8}")
                    .ToList(),
                LoadReason = Enum.GetName(Plugin.PluginInterface.Reason),
                DriveInfo = GetPenumbraDriveInfo(),
            };
        });

        PluginLog.LogError(ex, $"[{errorId}] {message}");
    }

    internal static bool IsAntiVirus(this Exception ex) {
        switch (ex) {
            // could not create directory after waiting, av probably blocked
            case DirectoryNotFoundException:
            // could not delete file after waiting, av probably blocked
            case DeleteFileException:
            // being used by another process or access denied
            case IOException { HResult: unchecked((int) 0x80070020) or unchecked((int) 0x80070005) }:
                return true;
            default:
                return false;
        }
    }

    private static bool GetMultiBoxStatus() => Process.GetProcessesByName("ffxiv_dx11").Length > 1;

    private static DriveInfo? GetPenumbraDriveInfo() {
        try {
            var modDir = Plugin.Instance.Penumbra.GetModDirectory();
            if (!string.IsNullOrWhiteSpace(modDir)) {
                var dirInfo = new DirectoryInfo(modDir);
                return new DriveInfo(dirInfo.Root.FullName);
            }
        } catch (Exception ex) {
            PluginLog.LogError(ex, "Could not get drive info for error reporting");
        }

        return null;
    }
}
