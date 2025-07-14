using System.Diagnostics.CodeAnalysis;
using Dalamud.Configuration;

namespace Heliosphere;

internal class Configuration : IPluginConfiguration {
    internal const int LatestVersion = 3;

    public int Version { get; set; } = LatestVersion;

    /// <summary>
    /// This is a unique ID only sent to Sentry. It can be accessed and sent via
    /// support channels to make finding error reports easier.
    /// </summary>
    public Guid UserId = Guid.NewGuid();

    public bool FirstTimeSetupComplete;
    public LoginUpdateMode LoginUpdateMode = LoginUpdateMode.Update;
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
    public Dictionary<Guid, PackageSettings> PackageSettings = [];
    /// <summary>
    /// The migration number of the latest migration the user has run. These are
    /// prompts that the user must approve, so this is only set after approval
    /// and success.
    /// </summary>
    public uint LatestMigration;

    [Obsolete("Use LoginUpdateMode")]
    public bool AutoUpdate = true;
    [Obsolete("Use LoginUpdateMode")]
    public bool CheckForUpdates = true;

    public Configuration() {
    }

    internal Configuration(Configuration other) {
        this.Version = other.Version;
        this.UserId = other.UserId;
        this.FirstTimeSetupComplete = other.FirstTimeSetupComplete;
        this.LoginUpdateMode = other.LoginUpdateMode;
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
        this.PackageSettings = other.PackageSettings.ToDictionary(
            entry => entry.Key,
            entry => new PackageSettings {
                LoginUpdateMode = entry.Value.LoginUpdateMode,
                Update = entry.Value.Update,
            }
        );
    }

    internal bool TryGetPackageSettings(
        Guid packageId,
        [NotNullWhen(true)]
        out PackageSettings? settings
    ) {
        return this.PackageSettings.TryGetValue(packageId, out settings);
    }

    internal PackageSettings GetPackageSettingsOrDefault(Guid packageId) {
        return this.TryGetPackageSettings(packageId, out var settings)
            ? settings
            : Heliosphere.PackageSettings.NewDefault;
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

    internal void CleanUp() {
        // remove any default package settings
        var defaultSettings = Heliosphere.PackageSettings.NewDefault;
        var toRemove = this.PackageSettings.Keys.Where(key => this.PackageSettings[key] == defaultSettings);
        foreach (var remove in toRemove) {
            this.PackageSettings.Remove(remove);
        }
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

[Serializable]
internal class PackageSettings {
    public required LoginUpdateMode? LoginUpdateMode = null;
    public required UpdateSetting Update = UpdateSetting.Default;

    internal static PackageSettings NewDefault { get; } = new() {
        LoginUpdateMode = null,
        Update = UpdateSetting.Default,
    };

    public override bool Equals(object? obj) {
        if (obj == null || this.GetType() != obj.GetType() || obj is not PackageSettings other) {
            return false;
        }

        return this.LoginUpdateMode == other.LoginUpdateMode
            && this.Update == other.Update;
    }

    public override int GetHashCode() {
        return this.LoginUpdateMode.GetHashCode()
            ^ this.Update.GetHashCode();
    }

    internal enum UpdateSetting {
        Default,
        Never,
    }
}

internal static class UpdateSettingExt {
    internal static string Description(this PackageSettings.UpdateSetting setting) {
        return setting switch {
            PackageSettings.UpdateSetting.Default => "No special behaviour",
            PackageSettings.UpdateSetting.Never => "Never update",
            _ => "Unknown",
        };
    }
}

internal enum LoginUpdateMode {
    None,
    Check,
    Update,
}

internal static class LoginUpdateModeExt {
    internal static string Name(this LoginUpdateMode mode) {
        return mode switch {
            LoginUpdateMode.None => "Disabled",
            LoginUpdateMode.Check => "Check only",
            LoginUpdateMode.Update => "Automatically update",
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(mode)),
        };
    }

    internal static string Name(this LoginUpdateMode? mode) {
        if (mode == null) {
            return "Use global setting";
        }

        return mode.Value.Name();
    }

    internal static string ToJsName(this LoginUpdateMode mode) {
        return mode switch {
            LoginUpdateMode.None => "none",
            LoginUpdateMode.Check => "check",
            LoginUpdateMode.Update => "update",
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(mode)),
        };
    }

    internal static bool TryFromJsName(string name, out LoginUpdateMode mode) {
        switch (name) {
            case "none":
                mode = LoginUpdateMode.None;
                return true;
            case "check":
                mode = LoginUpdateMode.Check;
                return true;
            case "update":
                mode = LoginUpdateMode.Update;
                return true;
            default:
                mode = 0;
                return false;
        }
    }

    internal static string Help(this LoginUpdateMode mode) {
        return mode switch {
            LoginUpdateMode.None => "Do not check for or automatically apply mod updates on login",
            LoginUpdateMode.Check => "Check for but do not automatically apply mod updates on login",
            LoginUpdateMode.Update => "Check for and automatically apply mod updates on login",
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(mode)),
        };
    }

    internal static string Help(this LoginUpdateMode? mode) {
        if (mode == null) {
            return "Use the global login update behaviour setting for this mod";
        }

        return mode.Value.Help();
    }
}
