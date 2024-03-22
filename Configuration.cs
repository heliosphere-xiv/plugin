using Dalamud.Configuration;

namespace Heliosphere;

internal class Configuration : IPluginConfiguration {
    internal const int LatestVersion = 2;

    public int Version { get; set; } = LatestVersion;

    /// <summary>
    /// This is a unique ID only sent to Sentry. It can be accessed and sent via
    /// support channels to make finding error reports easier.
    /// </summary>
    public Guid UserId = Guid.NewGuid();

    public bool AutoUpdate = true;
    public bool IncludeTags = true;
    public bool OpenPenumbraAfterInstall = true;
    public bool WarnAboutBreakingChanges = true;
    public bool ReplaceSortName = true;
    public bool HideDefaultVariant = true;
    public bool UseNotificationProgress = true;
    public bool NotificationsStartMinimised;
    public string TitlePrefix = "[HS] ";
    public string PenumbraFolder = "Heliosphere";
    public string? DefaultCollection;
    public bool OneClick;
    public byte[]? OneClickSalt;
    public string? OneClickHash;
    public string? OneClickCollection;
    public ulong MaxKibsPerSecond;
    public ulong AltMaxKibsPerSecond;
    public SpeedLimit LimitNormal = SpeedLimit.On;
    public SpeedLimit LimitInstance = SpeedLimit.Default;
    public SpeedLimit LimitCombat = SpeedLimit.Default;
    public SpeedLimit LimitParty = SpeedLimit.Default;
    public PenumbraIntegration Penumbra = new();

    public Configuration() {
    }

    internal Configuration(Configuration other) {
        this.Version = other.Version;
        this.UserId = other.UserId;
        this.AutoUpdate = other.AutoUpdate;
        this.IncludeTags = other.IncludeTags;
        this.OpenPenumbraAfterInstall = other.OpenPenumbraAfterInstall;
        this.WarnAboutBreakingChanges = other.WarnAboutBreakingChanges;
        this.ReplaceSortName = other.ReplaceSortName;
        this.HideDefaultVariant = other.HideDefaultVariant;
        this.UseNotificationProgress = other.UseNotificationProgress;
        this.NotificationsStartMinimised = other.NotificationsStartMinimised;
        this.TitlePrefix = other.TitlePrefix;
        this.PenumbraFolder = other.PenumbraFolder;
        this.DefaultCollection = other.DefaultCollection;
        this.OneClick = other.OneClick;
        this.OneClickSalt = other.OneClickSalt;
        this.OneClickHash = other.OneClickHash;
        this.OneClickCollection = other.OneClickCollection;
        this.MaxKibsPerSecond = other.MaxKibsPerSecond;
        this.AltMaxKibsPerSecond = other.AltMaxKibsPerSecond;
        this.LimitNormal = other.LimitNormal;
        this.LimitInstance = other.LimitInstance;
        this.LimitCombat = other.LimitCombat;
        this.LimitParty = other.LimitParty;
    }

    private void Redact() {
        if (this.OneClickSalt != null) {
            this.OneClickSalt = [1];
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

    internal enum SpeedLimit {
        Default,
        On,
        Alternate,
    }
}

[Serializable]
internal class PenumbraIntegration {
    public bool ShowImages = true;
    public bool ShowButtons = true;
    public PreviewImageSize ImageSize = PreviewImageSize.Medium;
}

[Serializable]
internal enum PreviewImageSize {
    Small,
    Medium,
    Large,
}
