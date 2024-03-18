namespace Heliosphere.Exceptions;

internal class MissingPackageException(Guid id) : BaseMissingThingException(id) {
    protected override string Thing => "package";
}
