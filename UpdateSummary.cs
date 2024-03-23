using Semver;

namespace Heliosphere;

internal class UpdateSummary {
    internal DateTime Started { get; } = DateTime.UtcNow;
    internal DateTime Finished { get; private set; }

    internal List<UpdatedMod> Mods { get; } = [];

    internal void Finish() {
        this.Finished = DateTime.UtcNow;
    }
}

internal class UpdatedMod {
    internal Guid PackageId { get; }

    internal string OldName { get; }
    internal string NewName { get; }
    internal List<UpdatedVariant> Variants { get; } = [];

    internal UpdatedMod(Guid packageId, string oldName, string newName) {
        this.PackageId = packageId;
        this.OldName = oldName;
        this.NewName = newName;
    }
}

internal class UpdatedVariant {
    internal Guid VariantId { get; }
    internal UpdateStatus Status { get; }

    internal string OldName { get; }
    internal string NewName { get; }

    internal List<VariantUpdateInfo> VersionHistory { get; }

    internal UpdatedVariant(Guid variantId, UpdateStatus status, string oldName, string newName, List<VariantUpdateInfo> versionHistory) {
        this.VariantId = variantId;
        this.Status = status;
        this.OldName = oldName;
        this.NewName = newName;
        this.VersionHistory = versionHistory;
    }
}

internal class VariantUpdateInfo {
    internal SemVersion Version { get; }
    internal string? Changelog { get; }

    internal VariantUpdateInfo(SemVersion version, string? changelog) {
        this.Version = version;
        this.Changelog = changelog;
    }
}

internal enum UpdateStatus {
    Success,
    Fail,
    RequiresIntervention,
}
