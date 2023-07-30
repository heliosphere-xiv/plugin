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

    /// <summary>
    /// This is a unique ID only sent to Sentry. It can be accessed and sent via
    /// support channels to make finding error reports easier.
    /// </summary>
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

    private void Redact() {
        if (this.OneClickSalt != null) {
            this.OneClickSalt = new byte[] { 1 };
        }

        if (this.OneClickHash != null) {
            this.OneClickHash = "[redacted]";
        }
    }

    internal static Configuration CloneAndRedact(Configuration other) {
        var redacted = new Configuration(other);
        redacted.Redact();

        return redacted;
    }
}
