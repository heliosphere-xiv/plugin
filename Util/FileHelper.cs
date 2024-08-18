using System.Runtime.InteropServices;
using Heliosphere.Exceptions;
using Windows.Win32;

namespace Heliosphere.Util;

internal static class FileHelper {
    /// <summary>
    /// Try to open a file for shared reading. If the file doesn't exist,
    /// returns null.
    /// <br/>
    /// Throws the same exceptions as <see cref="File.OpenRead"/> aside from
    /// <see cref="DirectoryNotFoundException"/> and
    /// <see cref="FileNotFoundException"/>.
    /// </summary>
    /// <param name="path">path to open</param>
    /// <returns>FileStream if the file exists</returns>
    /// <exception cref="AlreadyInUseException"/>
    internal static FileStream? OpenSharedReadIfExists(string path) {
        return Wrap(path, path => {
            try {
                return File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            } catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) {
                return null;
            }
        });
    }

    internal static FileStream OpenRead(string path) {
        return Wrap(path, File.OpenRead);
    }

    /// <summary>
    /// Create a file at the given path. See <see cref="File.Create(string)"/>.
    /// </summary>
    /// <param name="path">path to file to create</param>
    /// <returns>FileStream of created file</returns>
    /// <exception cref="AlreadyInUseException"/>
    internal static FileStream Create(string path, bool createParents = false) {
        if (createParents) {
            var parent = PathHelper.GetParent(path);
            Directory.CreateDirectory(parent);
        }

        return Wrap(path, File.Create);
    }

    internal static string ReadAllText(string path) {
        return Wrap(path, File.ReadAllText);
    }

    internal static async Task<string> ReadAllTextAsync(string path) {
        return await WrapAsync(path, path => File.ReadAllTextAsync(path));
    }

    internal static async Task<byte[]> ReadAllBytesAsync(string path) {
        return await WrapAsync(path, path => File.ReadAllBytesAsync(path));
    }

    internal static void WriteAllText(string path, string text) {
        Wrap(path, path => File.WriteAllText(path, text));
    }

    internal static void Delete(string path) {
        Wrap(path, File.Delete);
    }

    private static T Wrap<T>(string path, Func<string, T> action) {
        try {
            return action(path);
        } catch (Exception ex) when (ex is IOException { HResult: Consts.UsedByAnotherProcess } io) {
            var procs = RestartManager.GetLockingProcesses(path);
            throw new AlreadyInUseException(io, path, procs);
        }
    }

    private static void Wrap(string path, Action<string> action) {
        try {
            action(path);
        } catch (Exception ex) when (ex is IOException { HResult: Consts.UsedByAnotherProcess } io) {
            var procs = RestartManager.GetLockingProcesses(path);
            throw new AlreadyInUseException(io, path, procs);
        }
    }

    private static async Task<T> WrapAsync<T>(string path, Func<string, Task<T>> action) {
        try {
            return await action(path);
        } catch (Exception ex) when (ex is IOException { HResult: Consts.UsedByAnotherProcess } io) {
            var procs = RestartManager.GetLockingProcesses(path);
            throw new AlreadyInUseException(io, path, procs);
        }
    }

    internal static void CreateHardLink(string source, string destination) {
        const string prefix = @"\\?\";

        var prefixedSource = source.StartsWith(prefix)
            ? source
            : prefix + source;
        var prefixedDestination = destination.StartsWith(prefix)
            ? destination
            : prefix + destination;

        if (PInvoke.CreateHardLink(prefixedDestination, prefixedSource)) {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        throw new IOException($"Failed to create hard link (0x{error:X}): {source} -> {destination}");
    }
}
