using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Heliosphere.Model.Penumbra;

[Serializable]
internal class DefaultMod {
    public Dictionary<string, string> Files { get; set; } = new();
    public Dictionary<string, string> FileSwaps { get; set; } = new();
    public List<JToken> Manipulations { get; set; } = [];

    [JsonIgnore]
    public bool IsDefault { get; set; }
}
