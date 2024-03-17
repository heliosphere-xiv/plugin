using System.Net.Http.Headers;
using Heliosphere.Util;
using Microsoft.Extensions.Logging;

#pragma warning disable LOGGEN011 // A parameter isn't referenced from the logging message

internal static partial class ILoggerExtensions {
    [LoggerMessage(LogLevel.Debug, "Download started")]
    internal static partial void DownloadStarted(
        this ILogger logger,
        SimpleGuid id,
        CrockfordGuid versionId,
        Dictionary<string, List<string>> options,
        bool full,
        bool hasDownloadKey,
        bool includeTags,
        bool openInPenumbra,
        string? penumbraModPath,
        string? penumbraCollection
    );

    [LoggerMessage(LogLevel.Debug, "Downloading files")]
    internal static partial void DownloadingFiles(
        this ILogger logger,
        SimpleGuid id
    );

    [LoggerMessage(LogLevel.Debug, "Downloading batched files")]
    internal static partial void DownloadBatchedFiles(
        this ILogger logger,
        SimpleGuid id
    );

    [LoggerMessage(LogLevel.Debug, "Downloading normal files")]
    internal static partial void DownloadNormalFiles(
        this ILogger logger,
        SimpleGuid id
    );

    [LoggerMessage(LogLevel.Debug, "Downloading batched file")]
    internal static partial void DownloadBatchedFile(
        this ILogger logger,
        SimpleGuid id,
        Uri uri,
        RangeHeaderValue? rangeHeader,
        IReadOnlyList<List<string>> chunks
    );

    [LoggerMessage(LogLevel.Debug, "Downloading file")]
    internal static partial void DownloadFile(
        this ILogger logger,
        SimpleGuid id,
        string hash
    );

    [LoggerMessage(LogLevel.Debug, "Constructing mod pack")]
    internal static partial void ConstructModPack(
        this ILogger logger,
        SimpleGuid id
    );
}
