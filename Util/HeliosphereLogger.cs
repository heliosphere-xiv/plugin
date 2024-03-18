using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Heliosphere.Util;

internal sealed class HeliosphereLogger(string name) : ILogger {
    internal SimpleGuid? OperationId { get; set; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel) {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        Action<Exception?, string, object[]>? log = logLevel switch {
            LogLevel.Trace => Plugin.Log.Verbose,
            LogLevel.Debug => Plugin.Log.Debug,
            LogLevel.Information => Plugin.Log.Information,
            LogLevel.Warning => Plugin.Log.Warning,
            LogLevel.Error => Plugin.Log.Error,
            LogLevel.Critical => Plugin.Log.Fatal,
            _ => null,
        };

        if (log == null) {
            return;
        }

        var formatted = formatter(state, exception);

        var sb = new StringBuilder();
        if (state is LoggerMessageState lms) {
            sb.Append("{\n");
            foreach (var (key, value) in lms) {
                if (key is "{OriginalFormat}" or "message") {
                    continue;
                }

                sb.Append("    ");
                sb.Append(key);
                sb.Append(" = ");
                if (value == null) {
                    sb.Append("null");
                } else {
                    sb.Append('(');
                    sb.Append(value.GetType().FullName);
                    sb.Append(") <");
                    sb.Append(value);
                    sb.Append('>');
                }

                sb.Append('\n');
            }

            sb.Append('}');
        }

        var nameWithId = this.OperationId == null
            ? name
            : $"{name} - [{this.OperationId}]";
        var stateOutput = sb.ToString();
        if (string.IsNullOrWhiteSpace(stateOutput) || stateOutput == "{\n}") {
            log(exception, $"{nameWithId} - {formatted}", []);
        } else {
            log(exception, $"{nameWithId} - {formatted}\n{stateOutput}", []);
        }
    }
}

internal static class HeliosphereLoggerExtensions {
    internal static ILoggingBuilder AddHeliosphereLogger(this ILoggingBuilder builder) {
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, HeliosphereLoggerProvider>());

        return builder;
    }
}