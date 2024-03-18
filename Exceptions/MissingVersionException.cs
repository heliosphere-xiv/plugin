namespace Heliosphere.Exceptions;

internal class MissingVersionException(Guid id) : BaseMissingThingException(id) {
    protected override string Thing => "version";
}
