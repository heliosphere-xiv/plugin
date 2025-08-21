using System.Text;
using Dalamud.Interface.ImGuiNotification;
using Heliosphere.Exceptions;
using Heliosphere.Model.Api;
using Heliosphere.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Semver;
using Sqids;

namespace Heliosphere.Model;

[Serializable]
internal class HeliosphereMeta {
    internal const uint LatestVersion = 4;
    private const string SqidsAlphabet = "PB02vEmaJ7WqrSFZ5ILoQf8tUu9GNhkKRw1H34slVcOdjT6CyeDYAgpiXMxnbz";
    private static readonly SqidsEncoder<uint> SqidsEncoder = new(new SqidsOptions {
        Alphabet = SqidsAlphabet,
    });

    public uint MetaVersion { get; set; } = LatestVersion;

    /// <summary>
    /// The package ID.
    /// </summary>
    public required Guid Id { get; set; }

    public required string Name { get; set; }
    public required string Tagline { get; set; }
    public required string Description { get; set; }

    public required string Author { get; set; }
    public required Guid AuthorId { get; set; }

    public required string Version { get; set; }
    public required Guid VersionId { get; set; }

    public required string Variant { get; set; }
    public required Guid VariantId { get; set; }
    public required uint ShortVariantId { get; set; }

    public required bool IncludeTags { get; set; }

    public FileStorageMethod FileStorageMethod { get; set; } = FileStorageMethod.Original;

    internal string ErrorName => $"{this.Name} v{this.Version} (P:{this.Id.ToCrockford()} Va:{this.VariantId.ToCrockford()} Ve:{this.VersionId.ToCrockford()})";

    internal static async Task<HeliosphereMeta?> Load(string path, CancellationToken token = default) {
        var text = await FileHelper.ReadAllTextAsync(path, token);
        var obj = JsonConvert.DeserializeObject<JObject>(text);
        if (obj == null) {
            return null;
        }

        var (meta, changed) = await Convert(obj, token);
        if (changed) {
            var json = JsonConvert.SerializeObject(meta, Formatting.Indented);
            await using var file = FileHelper.Create(path);
            await file.WriteAsync(Encoding.UTF8.GetBytes(json), token);
        }

        return meta;
    }

    private static async Task<(HeliosphereMeta, bool)> Convert(JObject config, CancellationToken token = default) {
        var changed = await RunMigrations(config, token);
        return (config.ToObject<HeliosphereMeta>()!, changed);
    }

    private static async Task<bool> RunMigrations(JObject config, CancellationToken token = default) {
        var version = GetVersion();
        var changed = false;
        while (version < LatestVersion) {
            switch (version) {
                case 1:
                    await MigrateV1(config, token);
                    break;
                case 2:
                    MigrateV2(config);
                    break;
                case 3:
                    await MigrateV3(config, token);
                    break;
                default:
                    throw new Exception("Invalid Heliosphere meta - unknown version");
            }

            changed = true;
            version = GetVersion();
        }

        if (version == LatestVersion) {
            return changed;
        }

        throw new Exception("Could not migrate Heliosphere meta version");

        uint GetVersion() {
            uint version;
            if (config.TryGetValue(nameof(MetaVersion), out var versionToken)) {
                if (versionToken.Type == JTokenType.Integer) {
                    version = versionToken.Value<uint>();
                } else {
                    throw new Exception("Invalid Heliosphere meta");
                }
            } else {
                version = 1;
            }

            return version;
        }
    }

    private static async Task MigrateV1(JObject config, CancellationToken token = default) {
        var hasMetaVersion = config.Properties().Any(prop => prop.Name == nameof(MetaVersion));
        var hasAuthorUuid = config.Properties().Any(prop => prop.Name == "AuthorUuid");

        if (!hasMetaVersion && !hasAuthorUuid) {
            // this is an invalid heliosphere.json created by the website pmp
            // download (only existed for a few days)

            config[nameof(MetaVersion)] = 2u;
            config[nameof(IncludeTags)] = true;
            return;
        }

        // rename AuthorUuid to AuthorId
        config[nameof(AuthorId)] = config["AuthorUuid"];
        config.Remove("AuthorUuid");

        // get new value for VersionId
        var versionId = config[nameof(VersionId)]!.Value<int>();
        var newVersionId = (await Plugin.GraphQl.ConvertVersionId.ExecuteAsync(versionId, token)).Data?.ConvertVersionId;
        if (newVersionId == null) {
            throw new MetaMigrationException(1, 2, "Invalid version id while migrating Heliosphere meta");
        }

        config[nameof(VersionId)] = newVersionId.Value;

        // get new value for VariantId
        var variantId = config[nameof(VariantId)]!.Value<int>();
        var newVariantId = (await Plugin.GraphQl.ConvertVariantId.ExecuteAsync(variantId, token)).Data?.ConvertVariantId;
        if (newVariantId == null) {
            throw new MetaMigrationException(1, 2, "Invalid variant id while migrating Heliosphere meta");
        }

        config[nameof(VariantId)] = newVariantId.Value;

        // set version number
        config[nameof(MetaVersion)] = 2u;
    }

    private static void MigrateV2(JObject config) {
        if (!config.ContainsKey(nameof(FileStorageMethod))) {
            config[nameof(FileStorageMethod)] = (int) FileStorageMethod.Hash;
        }

        config[nameof(MetaVersion)] = 3u;
    }

    private static async Task MigrateV3(JObject config, CancellationToken token = default) {
        var variantIdString = config[nameof(VariantId)]!.Value<string>();
        if (!Guid.TryParse(variantIdString, out var variantId)) {
            throw new MetaMigrationException(3, 4, "variant id was not a uuid");
        }

        var result = await Plugin.GraphQl.GetShortVariantId.ExecuteAsync(variantId, token);
        var shortVariantId = (uint) (result.Data?.Variant?.ShortId ?? 0);

        config[nameof(ShortVariantId)] = shortVariantId;

        // set version number
        config[nameof(MetaVersion)] = 4u;
    }

    internal bool IsUpdate(string version) {
        var currentSuccess = SemVersion.TryParse(this.Version, SemVersionStyles.Strict, out var current);
        var newestSuccess = SemVersion.TryParse(version, SemVersionStyles.Strict, out var newest);

        return currentSuccess && newestSuccess && current != null && newest != null && current.CompareSortOrderTo(newest) == -1;
    }

    internal string ModDirectoryName() {
        return ModDirectoryName(this.Name, this.Version, this.ShortVariantId);
    }

    internal static string ModDirectoryName(string name, string version, uint shortVariantId) {
        var invalidChars = Path.GetInvalidFileNameChars();
        var slug = name.Select(c => invalidChars.Contains(c) ? '-' : c)
            .Aggregate(new StringBuilder(), (sb, c) => sb.Append(c))
            .ToString();

        var shortVariant = SqidsEncoder.Encode(shortVariantId);

        return $"hs-{slug}-{version}-{shortVariant}";
    }

    internal static HeliosphereDirectoryInfo? ParseDirectory(string input) {
        var parts = input.Split('-');
        if (parts.Length < 2) {
            return null;
        }

        if (parts[0] != "hs") {
            return null;
        }

        var decoded = SqidsEncoder.Decode(parts[^1]);
        if (decoded == null || decoded.Count == 0) {
            return null;
        }

        var shortVariantId = decoded[0];

        // FIXME: how does this handle a version like 1.0.0-alpha.1
        return new HeliosphereDirectoryInfo(shortVariantId, parts[^2]);
    }

    /// <summary>
    /// Start a task to download updates for this version.
    /// </summary>
    /// <param name="plugin">An instance of the plugin</param>
    /// <returns>Task that completes when the download finishes</returns>
    internal Task DownloadUpdates(Plugin plugin, CancellationToken token = default) {
        var settings = plugin.Config.GetPackageSettingsOrDefault(this.Id);

        return Task.Run(async () => {
            var name = new StringBuilder();
            name.Append(this.Name);
            if (this.Variant != Consts.DefaultVariant || !plugin.Config.HideDefaultVariant) {
                name.Append(" (");
                name.Append(this.Variant);
                name.Append(')');
            }

            var notif = plugin.NotificationManager.AddNotification(new Notification {
                Type = NotificationType.Info,
                Title = "Update installer",
                Content = $"Checking {name} for updates...",
                InitialDuration = TimeSpan.MaxValue,
                Minimized = false,
            });

            var info = await GraphQl.GetNewestVersion(this.VariantId, token);
            if (info == null) {
                return;
            }

            // these come from the server already-sorted
            if (info.Versions.Count == 0 || info.Versions[0].Version == this.Version) {
                notif.Content = $"{name} is already up-to-date.";
                notif.InitialDuration = TimeSpan.FromSeconds(3);
                return;
            }

            notif.Type = NotificationType.Success;
            notif.Content = $"Update for {name} found - starting download.";
            notif.InitialDuration = TimeSpan.FromSeconds(3);

            if (plugin.Penumbra.TryGetModDirectory(out var modDir)) {
                await plugin.AddDownloadAsync(new DownloadTask {
                    Plugin = plugin,
                    PenumbraRoot = modDir,
                    PackageId = this.Id,
                    VariantId = this.VariantId,
                    VersionId = info.Versions[0].Id,
                    IncludeTags = this.IncludeTags,
                    OpenInPenumbra = false,
                    PenumbraCollection = null,
                    PenumbraFolderOverride = null,
                    Notification = null,
                    LoginUpdateMode = settings.LoginUpdateMode,
                    ManualUpdateMode = settings.Update,
                }, token);
            }
        }, token);
    }
}

internal readonly record struct HeliosphereDirectoryInfo(
    uint ShortVariantId,
    string Version
);

internal enum FileStorageMethod {
    Hash,
    Original,
}
