namespace Heliosphere.Util;

internal class SimpleGuid {
    internal Guid Inner { get; }

    internal SimpleGuid(Guid inner) {
        this.Inner = inner;
    }

    public static implicit operator SimpleGuid(Guid inner) => new(inner);
    public static implicit operator Guid(SimpleGuid simple) => simple.Inner;

    public override string ToString() {
        return this.Inner.ToString("N");
    }
}
