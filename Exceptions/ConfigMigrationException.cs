namespace Heliosphere.Exceptions;

internal class ConfigMigrationException : Exception {
    internal uint From { get; }
    internal uint To { get; }

    internal ConfigMigrationException(uint from, uint to, string message) : base(message) {
        this.From = from;
        this.To = to;
    }
}
