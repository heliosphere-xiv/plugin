namespace Heliosphere.Exceptions;

internal class MetaMigrationException : Exception {
    internal uint From { get; }
    internal uint To { get; }

    internal MetaMigrationException(uint from, uint to, string message) : base(message) {
        this.From = from;
        this.To = to;
    }
}
