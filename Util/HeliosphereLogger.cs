using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Heliosphere.Util;

internal sealed class HeliosphereLogger(string name) : ILogger {
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
        if (string.IsNullOrEmpty(formatted) && state is LoggerMessageState lms) {
            var sb = new StringBuilder();
            sb.Append("{\n");
            foreach (var (key, value) in lms.TagArray) {
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

                sb.Append('>');
                sb.Append('\n');
            }

            sb.Append('}');

            formatted = sb.ToString();
        }

        log(exception, $"{name} - {formatted}", []);
    }
}

internal static class HeliosphereLoggerExtensions {
    internal static ILoggingBuilder AddHeliosphereLogger(this ILoggingBuilder builder) {
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, HeliosphereLoggerProvider>());

        return builder;
    }
}