namespace Heliosphere.Exceptions;

internal class MissingPackageException : BaseMissingThingException {
    protected override string Thing => "package";

    public MissingPackageException(Guid id) : base(id) {
    }
}
