using Dalamud.Configuration;

namespace Heliosphere;

internal class Configuration : IPluginConfiguration {
    internal const int LatestVersion = 2;

    public Configuration() {
    }

    internal Configuration(Configuration other) {
        this.Version = other.Version;
        this.UserId = other.UserId;
        this.AutoUpdate = other.AutoUpdate;
        this.IncludeTags = other.IncludeTags;
        this.ReplaceSortName = other.ReplaceSortName;
        this.TitlePrefix = other.TitlePrefix;
        this.PenumbraFolder = other.PenumbraFolder;
        this.DefaultCollection = other.DefaultCollection;
        this.OneClick = other.OneClick;
        this.OneClickSalt = other.OneClickSalt;
        this.OneClickHash = other.OneClickHash;
        this.OneClickCollection = other.OneClickCollection;
    }

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

    internal void Redact() {
        if (this.OneClickSalt != null) {
            this.OneClickSalt = new byte[] { 1 };
        }

        if (this.OneClickHash != null) {
            this.OneClickHash = "[redacted]";
        }
    }
}
