namespace Heliosphere.Exceptions;

internal class MissingVersionException : BaseMissingThingException {
    protected override string Thing => "version";

    public MissingVersionException(Guid id) : base(id) {
    }
}
