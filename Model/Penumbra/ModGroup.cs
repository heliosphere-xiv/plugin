namespace Heliosphere.Model.Penumbra;

[Serializable]
internal class ModGroup {
    public string Name { get; set; }
    public string? Description { get; set; }
    public int Priority { get; set; }
    public string Type { get; set; }
    public List<DefaultMod> Options { get; set; } = new();

    internal ModGroup(string name, string? description, string type) {
        this.Name = name;
        this.Description = description;
        this.Type = type;
    }
}
