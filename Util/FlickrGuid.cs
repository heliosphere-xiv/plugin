using Heliosphere.Util;
using SimpleBase;

internal readonly record struct FlickrGuid(Guid Inner) {
    internal Guid Inner { get; } = Inner;

    public static implicit operator FlickrGuid(Guid inner) => new(inner);

    public override string ToString() {
        return Base58.Flickr.Encode(this.Inner.ToProperByteArray());
    }
}
