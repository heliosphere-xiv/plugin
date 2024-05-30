namespace Heliosphere.Model.Penumbra;

[Serializable]
internal class ImcOption {
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool? IsDisableSubMod { get; set; }
    public int? AttributeMask { get; set; }
}
