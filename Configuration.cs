using Dalamud.Configuration;

namespace Heliosphere;

internal class Configuration : IPluginConfiguration {
    internal const int LatestVersion = 2;

    public int Version { get; set; } = LatestVersion;

    public Guid UserId = Guid.NewGuid();
    public bool AutoUpdate = true;
    public bool IncludeTags = true;
    public bool ReplaceSortName = true;
    public string TitlePrefix = "[HS] ";
    public string PenumbraFolder = "Heliosphere";
    public string? DefaultCollection;
    public bool OneClick;
    public byte[]? OneClickSalt;
    public string? OneClickHash;
    public string? OneClickCollection;
}
