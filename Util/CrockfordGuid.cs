namespace Heliosphere.Util;

internal class CrockfordGuid {
    internal Guid Inner { get; }

    internal CrockfordGuid(Guid inner) {
        this.Inner = inner;
    }

    public static implicit operator CrockfordGuid(Guid inner) => new(inner);
    public static implicit operator Guid(CrockfordGuid crock) => crock.Inner;

    public override string ToString() {
        return this.Inner.ToCrockford();
    }
}
