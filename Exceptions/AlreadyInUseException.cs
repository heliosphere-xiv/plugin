using System.Diagnostics;

internal class AlreadyInUseException : IOException {
    internal AlreadyInUseException(IOException inner, string path, List<Process> processes) : base(
        $"File '{path}' is already in use by {string.Join(", ", processes.Select(ProcessTitle))}",
        inner
    ) {
    }

    private static string ProcessTitle(Process p) {
        if (string.IsNullOrWhiteSpace(p.MainWindowTitle)) {
            return p.ProcessName;
        } else {
            return $"{p.MainWindowTitle} ({p.ProcessName})";
        }
    }
}
