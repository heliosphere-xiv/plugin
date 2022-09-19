using Dalamud.Configuration;

namespace Heliosphere;

internal class Configuration : IPluginConfiguration {
    public int Version { get; set; } = 1;

    public bool AutoUpdate;
    public string TitlePrefix = "[HS] ";
    public string PenumbraFolder = "Heliosphere";
}
