using Dalamud.Configuration;

namespace Heliosphere;

internal class Configuration : IPluginConfiguration {
    public int Version { get; set; } = 1;

    public bool AutoUpdate;
    public bool IncludeTags = true;
    public bool ReplaceSortName = true;
    public string TitlePrefix = "[HS] ";
    public string PenumbraFolder = "Heliosphere";
    public bool OneClick;
    public byte[]? OneClickSalt;
    public string? OneClickHash;
    public string? OneClickCollection;
}
