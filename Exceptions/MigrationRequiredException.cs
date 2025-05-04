namespace Heliosphere.Exceptions;

internal class MigrationRequiredException(uint current, uint expected) : Exception {
    internal uint Current { get; } = current;
    internal uint Expected { get; } = expected;

    public override string Message => $"A migration is required but has not been applied. Current migration id was {this.Current}, but migration id {this.Expected} is required.";
}
