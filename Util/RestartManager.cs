using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.RestartManager;

namespace Heliosphere.Util;

internal static class RestartManager {
    /// <summary>
    /// Find out which processes have a lock on the specified file.
    /// </summary>
    /// <param name="path">Path of the file.</param>
    /// <returns>Processes locking the file</returns>
    internal static unsafe List<Process> GetLockingProcesses(string path) {
        try {
            return GetLockingProcessesInner(path);
        } catch (Exception ex) {
            Plugin.Log.Warning(ex, "Could not get list of locking processes");
            SentrySdk.CaptureException(ex);

            return [];
        }
    }

    private static unsafe List<Process> GetLockingProcessesInner(string path) {
        // var key = Guid.NewGuid().ToString().ToCharArray();
        // var res = PInvoke.RmStartSession(out var handle, key.AsSpan());
        var key = Guid.NewGuid().ToString();
        var keyHandle = GCHandle.Alloc(key, GCHandleType.Pinned);
        using var freeKeyHandle = new OnDispose(() => {
            if (keyHandle.IsAllocated) {
                keyHandle.Free();
            }
        });

        var res = PInvoke.RmStartSession(out var handle, (char*) keyHandle.AddrOfPinnedObject());

        if (res != WIN32_ERROR.NO_ERROR) {
            throw new Exception("Could not begin restart session. Unable to determine file locker.");
        }

        using var endSession = new OnDispose(() => PInvoke.RmEndSession(handle));


        fixed (char* pathPtr = path) {
            var paths = new PCWSTR [] { pathPtr };
            res = PInvoke.RmRegisterResources(handle, paths, null, null);
        }
        // res = PInvoke.RmRegisterResources(handle, new Span<string>(ref path), [], []);

        if (res != WIN32_ERROR.NO_ERROR) {
            throw new Exception("Could not register resource.");
        }

        var numInfo = 0u;
        RM_PROCESS_INFO[] processInfoArray;
        while (true) {
            uint needed;
            processInfoArray = new RM_PROCESS_INFO[numInfo];
            fixed (RM_PROCESS_INFO* arrayPtr = processInfoArray) {
                res = PInvoke.RmGetList(handle, out needed, ref numInfo, arrayPtr, out _);
            }

            if (res == WIN32_ERROR.NO_ERROR) {
                break;
            }

            if (res != WIN32_ERROR.ERROR_MORE_DATA) {
                throw new Exception("Could not list processes locking resource. Failed to get size of result.");
            }

            numInfo = needed;
        }

        var processes = new List<Process>(processInfoArray.Length);
        foreach (var info in processInfoArray) {
            try {
                processes.Add(Process.GetProcessById((int) info.Process.dwProcessId));
            } catch {
                // ignore closed processes
            }
        }

        return processes;
    }
}
