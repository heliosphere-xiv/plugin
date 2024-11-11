using Newtonsoft.Json;

namespace Heliosphere.Model.Penumbra;

internal interface ModGroup {
    public string Name { get; set; }
    public string? Description { get; set; }
    public int Priority { get; set; }
    public ulong DefaultSettings { get; set; }
    public string Type { get; set; }

    [JsonIgnore]
    public (uint, uint) OriginalIndex { get; set; }
}
