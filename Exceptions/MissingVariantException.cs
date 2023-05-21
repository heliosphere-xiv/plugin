namespace Heliosphere.Exceptions;

internal class MissingVariantException : BaseMissingThingException {
    protected override string Thing => "variant";

    public MissingVariantException(Guid id) : base(id) {
    }
}
