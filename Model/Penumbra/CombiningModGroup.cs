using Newtonsoft.Json;

namespace Heliosphere.Model.Penumbra;

[Serializable]
internal class CombiningModGroup : ModGroup {
    public string Name { get; set; }
    public string? Description { get; set; }
    public int Priority { get; set; }
    public ulong DefaultSettings { get; set; }
    public string Type { get; set; }
    public List<CombiningOption> Options { get; set; } = [];
    public List<CombiningContainer> Containers { get; set; } = [];

    [JsonIgnore]
    public (uint, uint) OriginalIndex { get; set; }

    [JsonConstructor]
    #pragma warning disable CS8618
    public CombiningModGroup() {
    }
    #pragma warning restore CS8618

    internal CombiningModGroup(string name, string? description, string type) {
        this.Name = name;
        this.Description = description;
        this.Type = type;
    }
}
