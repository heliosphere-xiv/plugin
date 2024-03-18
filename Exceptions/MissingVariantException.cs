namespace Heliosphere.Exceptions;

internal class MissingVariantException(Guid id) : BaseMissingThingException(id) {
    protected override string Thing => "variant";
}
