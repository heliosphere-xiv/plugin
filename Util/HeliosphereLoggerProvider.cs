using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
namespace Heliosphere.Util;

[UnsupportedOSPlatform("browser")]
[ProviderAlias("HeliosphereLogger")]
public sealed class HeliosphereLoggerProvider : ILoggerProvider {
    private readonly ConcurrentDictionary<string, HeliosphereLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);

    public HeliosphereLoggerProvider() {
    }

    public ILogger CreateLogger(string categoryName)  {
        return this._loggers.GetOrAdd(categoryName, name => new HeliosphereLogger(name));
    }

    public void Dispose() {
        this._loggers.Clear();
    }
}
