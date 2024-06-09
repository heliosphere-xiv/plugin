using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Heliosphere.Model.Penumbra;

[Serializable]
internal class OptionItem {
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Priority { get; set; }
    public Dictionary<string, string> Files { get; set; } = new();
    public Dictionary<string, string> FileSwaps { get; set; } = new();
    public List<JToken> Manipulations { get; set; } = [];

    [JsonIgnore]
    public bool IsDefault { get; set; }
}
