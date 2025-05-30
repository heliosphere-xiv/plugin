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

    public bool FirstTimeSetupComplete;
    public bool AutoUpdate = true;
    public bool CheckForUpdates = true;
    public bool IncludeTags = true;
    public bool OpenPenumbraAfterInstall = true;
    public bool WarnAboutBreakingChanges = true;
    public bool ReplaceSortName = true;
    public bool ReplaceModName = true;
    public bool HideDefaultVariant = true;
    public bool UseNotificationProgress = true;
    public bool NotificationsStartMinimised;
    public bool AllowCommandInstalls = true;
    public bool AllowCommandOneClick;
    public bool UseRecycleBin;
    public string TitlePrefix = "[HS] ";
    public string PenumbraFolder = "Heliosphere";
    public Guid? DefaultCollectionId;
    public bool OneClick;
    public byte[]? OneClickSalt;
    public string? OneClickHash;
    public Guid? OneClickCollectionId;
    public ulong MaxKibsPerSecond;
    public ulong AltMaxKibsPerSecond;
    public SpeedLimit LimitNormal = SpeedLimit.On;
    public SpeedLimit LimitInstance = SpeedLimit.Default;
    public SpeedLimit LimitCombat = SpeedLimit.Default;
    public SpeedLimit LimitParty = SpeedLimit.Default;
    public PenumbraIntegration Penumbra = new();
    /// <summary>
    /// The migration number of the latest migration the user has run. These are
    /// prompts that the user must approve, so this is only set after approval
    /// and success.
    /// </summary>
    public uint LatestMigration;

    public Configuration() {
    }

    internal Configuration(Configuration other) {
        this.Version = other.Version;
        this.UserId = other.UserId;
        this.FirstTimeSetupComplete = other.FirstTimeSetupComplete;
        this.AutoUpdate = other.AutoUpdate;
        this.CheckForUpdates = other.CheckForUpdates;
        this.IncludeTags = other.IncludeTags;
        this.OpenPenumbraAfterInstall = other.OpenPenumbraAfterInstall;
        this.WarnAboutBreakingChanges = other.WarnAboutBreakingChanges;
        this.ReplaceSortName = other.ReplaceSortName;
        this.ReplaceModName = other.ReplaceModName;
        this.HideDefaultVariant = other.HideDefaultVariant;
        this.UseNotificationProgress = other.UseNotificationProgress;
        this.NotificationsStartMinimised = other.NotificationsStartMinimised;
        this.AllowCommandInstalls = other.AllowCommandInstalls;
        this.AllowCommandOneClick = other.AllowCommandOneClick;
        this.UseRecycleBin = other.UseRecycleBin;
        this.TitlePrefix = other.TitlePrefix;
        this.PenumbraFolder = other.PenumbraFolder;
        this.DefaultCollectionId = other.DefaultCollectionId;
        this.OneClick = other.OneClick;
        this.OneClickSalt = other.OneClickSalt;
        this.OneClickHash = other.OneClickHash;
        this.OneClickCollectionId = other.OneClickCollectionId;
        this.MaxKibsPerSecond = other.MaxKibsPerSecond;
        this.AltMaxKibsPerSecond = other.AltMaxKibsPerSecond;
        this.LimitNormal = other.LimitNormal;
        this.LimitInstance = other.LimitInstance;
        this.LimitCombat = other.LimitCombat;
        this.LimitParty = other.LimitParty;
        this.LatestMigration = other.LatestMigration;
        this.Penumbra = new PenumbraIntegration {
            ShowImages = other.Penumbra.ShowImages,
            ShowButtons = other.Penumbra.ShowButtons,
            ImageSize = other.Penumbra.ImageSize,
        };
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
    public float ImageSize = 0.375f;
}
