using System.Diagnostics;

namespace Heliosphere.Exceptions;

internal class AlreadyInUseException : IOException {
    internal AlreadyInUseException(IOException inner, string path, IEnumerable<Process> processes) : base(
        $"File '{path}' is already in use by {string.Join(", ", processes.Select(ProcessTitle))}",
        inner
    ) {
    }

    private static string ProcessTitle(Process p) {
        return string.IsNullOrWhiteSpace(p.MainWindowTitle)
            ? p.ProcessName
            : $"{p.MainWindowTitle} ({p.ProcessName})";
    }
}
