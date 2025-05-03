namespace Heliosphere.Model.Penumbra;

[Serializable]
internal class ModMeta {
    public int FileVersion { get; set; } = 3;
    public string Name { get; set; }
    public string Author { get; set; }
    public string? Description { get; set; }
    public string Version { get; set; }
    public string? Website { get; set; }
    public string[]? ModTags { get; set; }
    public ulong[]? DefaultPreferredItems { get; set; }
}
