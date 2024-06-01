using Newtonsoft.Json;

namespace Heliosphere.Model.Penumbra;

[Serializable]
internal class ImcOption {
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? IsDisableSubMod { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? AttributeMask { get; set; }
}
