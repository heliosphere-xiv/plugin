namespace Heliosphere.Util;

internal class CrockfordGuid {
    private Guid Inner { get; }

    internal CrockfordGuid(Guid inner) {
        this.Inner = inner;
    }

    public static implicit operator CrockfordGuid(Guid inner) => new(inner);

    public override string ToString() {
        return this.Inner.ToCrockford();
    }
}
