using System.Diagnostics;

namespace Heliosphere.Exceptions;

internal class AlreadyInUseException : IOException {
    internal IReadOnlyList<Process> Processes { get; }

    internal AlreadyInUseException(IOException inner, string path, List<Process> processes) : base(
        $"File '{path}' is already in use by {string.Join(", ", processes.Select(ProcessTitle))}",
        inner
    ) {
        this.Processes = processes;
    }

    private static string ProcessTitle(Process p) {
        return string.IsNullOrWhiteSpace(p.MainWindowTitle)
            ? p.ProcessName
            : $"{p.MainWindowTitle} ({p.ProcessName})";
    }
}
