using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Heliosphere.Model.Penumbra;

[Serializable]
internal class ImcModGroup : ModGroup {
    public string Name { get; set; }
    public string? Description { get; set; }
    public int Priority { get; set; }
    public uint DefaultSettings { get; set; }
    public string Type { get; set; } = "Imc";
    public JToken Identifier { get; set; }
    public bool AllVariants { get; set; }
    public bool OnlyAttributes { get; set; }
    public JToken DefaultEntry { get; set; }
    public List<ImcOption> Options { get; set; } = [];

    [JsonIgnore]
    public (uint, uint) OriginalIndex { get; set; }

    [JsonConstructor]
    #pragma warning disable CS8618
    public ImcModGroup() {
    }
    #pragma warning restore CS8618

    internal ImcModGroup(string name, string? description, JToken identifier, bool allVariants, bool onlyAttributes, JToken defaultEntry) {
        this.Name = name;
        this.Description = description;
        this.Identifier = identifier;
        this.AllVariants = allVariants;
        this.OnlyAttributes = onlyAttributes;
        this.DefaultEntry = defaultEntry;
    }
}
