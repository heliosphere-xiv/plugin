using Newtonsoft.Json;

namespace Heliosphere.Model.Penumbra;

[Serializable]
internal class CombiningOption {
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    [JsonIgnore]
    public bool IsDefault { get; set; }
}
