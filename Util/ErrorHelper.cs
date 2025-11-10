using System.Diagnostics;
using System.Text;
using Heliosphere.Exceptions;
using Newtonsoft.Json;

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

    internal static void Handle(Exception ex, string message, ISpan? span = null) {
        var errorId = SentrySdk.CaptureException(ex, scope => {
            if (span != null) {
                scope.Span = span;
            }

            var json = JsonConvert.SerializeObject(Configuration.CloneAndRedact(Plugin.Instance.Config), Formatting.Indented);
            scope.AddAttachment(Encoding.UTF8.GetBytes(json), "config.json", AttachmentType.Default, "application/json");

            scope.SetTag("multibox", GetMultiBoxStatus().ToString());
            scope.SetTag("unsupported-options", Plugin.Instance.Config.Unsupported.AnyEnabled().ToString());

            var drive = GetPenumbraDriveInfo();
            var driveInfo = drive == null
                ? null
                : new {
                    drive.DriveFormat,
                    DriveType = Enum.GetName(drive.DriveType),
                    drive.Name,
                    drive.IsReady,
                    drive.TotalSize,
                    drive.AvailableFreeSpace,
                    drive.TotalFreeSpace,
                };

            object? errorCode = null;
            if (ex.GetType().GetProperty("ErrorCode") is { } prop) {
                errorCode = prop.GetValue(ex);
            }

            scope.Contexts["Error Helper"] = new {
                Message = message,
                HResults = ex.GetHResults()
                    .Select(hr => unchecked((uint) hr))
                    .Select(hr => $"0x{hr:X8}")
                    .ToList(),
                LoadReason = Enum.GetName(Plugin.PluginInterface.Reason),
                PenumbraDriveInfo = driveInfo,
                ErrorCode = errorCode,
            };
        });

        Plugin.Log.Error(ex, $"[{errorId}] {message}");
    }

    internal static bool IsAntiVirus(this Exception ex) {
        switch (ex) {
            // could not create directory after waiting, av probably blocked
            case DirectoryNotFoundException:
            // could not delete file after waiting, av probably blocked
            case DeleteFileException:
            // being used by another process or access denied
            case IOException { HResult: Consts.UsedByAnotherProcess or unchecked((int) 0x80070005) }:
                return true;
            default:
                return false;
        }
    }

    internal static string ToBbCode(this Exception error) {
        var unsupportedMarker = Plugin.Instance.Config.Unsupported.AnyEnabled() ? "U" : "";

        var sb = new StringBuilder();
        sb.Append("[code]\n");
        var i = 0;
        foreach (var ex in error.AsEnumerable()) {
            if (i != 0) {
                sb.Append('\n');
            }

            i += 1;

            sb.Append($"Error type: {ex.GetType().FullName}\n");
            sb.Append($"   Message: {ex.Message}\n");
            sb.Append($"   HResult: 0x{unchecked((uint) ex.HResult):X8}{unsupportedMarker}\n");
            if (ex.StackTrace is { } trace) {
                sb.Append(trace);
                sb.Append('\n');
            }
        }

        sb.Append("[/code]");

        return sb.ToString();
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
            Plugin.Log.Error(ex, "Could not get drive info for error reporting");
        }

        return null;
    }
}
