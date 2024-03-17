using Heliosphere.Util;
using Microsoft.Extensions.Logging;

internal static partial class LoggerExtensions {
    [LoggerMessage(LogLevel.Debug)]
    internal static partial void DownloadStarted(
        this ILogger logger,
        CrockfordGuid versionId,
        Dictionary<string, List<string>> options,
        bool full,
        bool hasDownloadKey,
        bool includeTags,
        bool openInPenumbra,
        string? penumbraModPath,
        string? penumbraCollection
    );
}
