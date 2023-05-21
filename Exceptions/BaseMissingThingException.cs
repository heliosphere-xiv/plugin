namespace Heliosphere.Exceptions;

internal abstract class BaseMissingThingException : Exception {
    internal Guid Id { get; }
    protected abstract string Thing { get; }
    public override string Message => $"The {this.Thing} with ID {this.Id:N} no longer exists";

    internal BaseMissingThingException(Guid id) {
        this.Id = id;
    }
}
