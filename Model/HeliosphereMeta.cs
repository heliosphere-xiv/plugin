using System.Text;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Internal.Notifications;
using Heliosphere.Exceptions;
using Heliosphere.Model.Api;
using Heliosphere.Ui;
using Heliosphere.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Semver;

namespace Heliosphere.Model;

[Serializable]
internal class HeliosphereMeta {
    internal const uint LatestVersion = 2;

    public uint MetaVersion { get; set; } = LatestVersion;

    /// <summary>
    /// The package ID.
    /// </summary>
    public Guid Id { get; set; }

    public string Name { get; set; }
    public string Tagline { get; set; }
    public string Description { get; set; }

    public string Author { get; set; }
    public Guid AuthorId { get; set; }

    public string Version { get; set; }
    public Guid VersionId { get; set; }

    public string Variant { get; set; }
    public Guid VariantId { get; set; }

    public bool FullInstall { get; set; }
    public bool IncludeTags { get; set; }
    public Dictionary<string, List<string>> SelectedOptions { get; set; }

    public string? ModHash { get; set; }

    internal string ErrorName => $"{this.Name} v{this.Version} (P:{this.Id.ToCrockford()} Va:{this.VariantId.ToCrockford()} Ve:{this.VersionId.ToCrockford()})";

    internal static async Task<HeliosphereMeta?> Load(string path) {
        var text = await FileHelper.ReadAllTextAsync(path);
        var obj = JsonConvert.DeserializeObject<JObject>(text);
        if (obj == null) {
            return null;
        }

        var (meta, changed) = await Convert(obj);
        if (changed) {
            var json = JsonConvert.SerializeObject(meta, Formatting.Indented);
            await using var file = FileHelper.Create(path);
            await file.WriteAsync(Encoding.UTF8.GetBytes(json));
        }

        return meta;
    }

    private static async Task<(HeliosphereMeta, bool)> Convert(JObject config) {
        var version = GetVersion();
        var changed = false;
        while (version < LatestVersion) {
            switch (version) {
                case 1:
                    await MigrateV1(config);
                    break;
                default:
                    throw new Exception("Invalid Heliosphere meta - unknown version");
            }

            changed = true;
            version = GetVersion();
        }

        if (version == LatestVersion) {
            return (config.ToObject<HeliosphereMeta>()!, changed);
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

    private static async Task MigrateV1(JObject config) {
        var hasMetaVersion = config.Properties().Any(prop => prop.Name == nameof(MetaVersion));
        var hasAuthorUuid = config.Properties().Any(prop => prop.Name == "AuthorUuid");

        if (!hasMetaVersion && !hasAuthorUuid) {
            // this is an invalid heliosphere.json created by the website pmp
            // download (only existed for a few days)

            config[nameof(MetaVersion)] = 2u;
            config[nameof(FullInstall)] = true;
            config[nameof(IncludeTags)] = true;
            config[nameof(SelectedOptions)] = new JObject();
            return;
        }

        // rename AuthorUuid to AuthorId
        config[nameof(AuthorId)] = config["AuthorUuid"];
        config.Remove("AuthorUuid");

        // get new value for VersionId
        var versionId = config[nameof(VersionId)]!.Value<int>();
        var newVersionId = (await Plugin.GraphQl.ConvertVersionId.ExecuteAsync(versionId)).Data?.ConvertVersionId;
        if (newVersionId == null) {
            throw new MetaMigrationException(1, 2, "Invalid version id while migrating Heliosphere meta");
        }

        config[nameof(VersionId)] = newVersionId.Value;

        // get new value for VariantId
        var variantId = config[nameof(VariantId)]!.Value<int>();
        var newVariantId = (await Plugin.GraphQl.ConvertVariantId.ExecuteAsync(variantId)).Data?.ConvertVariantId;
        if (newVariantId == null) {
            throw new MetaMigrationException(1, 2, "Invalid variant id while migrating Heliosphere meta");
        }

        config[nameof(VariantId)] = newVariantId.Value;

        // set version number
        config[nameof(MetaVersion)] = 2u;
    }

    internal bool IsSimple() {
        return this.FullInstall && this.SelectedOptions.Count == 0;
    }

    internal bool IsUpdate(string version) {
        var currentSuccess = SemVersion.TryParse(this.Version, SemVersionStyles.Strict, out var current);
        var newestSuccess = SemVersion.TryParse(version, SemVersionStyles.Strict, out var newest);

        return currentSuccess && newestSuccess && current.CompareSortOrderTo(newest) == -1;
    }

    internal string ModDirectoryName() {
        return ModDirectoryName(this.Id, this.Name, this.Version, this.VariantId);
    }

    internal static string ModDirectoryName(Guid id, string name, string version, Guid variant) {
        var invalidChars = Path.GetInvalidFileNameChars();
        var slug = name.Select(c => invalidChars.Contains(c) ? '-' : c)
            .Aggregate(new StringBuilder(), (sb, c) => sb.Append(c))
            .ToString();
        return $"hs-{slug}-{version}-{variant:N}-{id:N}";
    }

    internal static HeliosphereDirectoryInfo? ParseDirectory(string input) {
        var parts = input.Split('-');
        if (parts.Length < 3) {
            return null;
        }

        if (!Guid.TryParse(parts[^1], out var packageId)) {
            return null;
        }

        if (!Guid.TryParse(parts[^2], out var variantId)) {
            return null;
        }

        return new(packageId, variantId, parts[^3]);
    }

    /// <summary>
    /// Start a task to download updates for this version.
    /// </summary>
    /// <param name="plugin">An instance of the plugin</param>
    /// <returns>Task that completes when the download finishes</returns>
    internal Task DownloadUpdates(Plugin plugin) {
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

            var info = await GraphQl.GetNewestVersion(this.VariantId);
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

            if (this.FullInstall) {
                if (plugin.Penumbra.TryGetModDirectory(out var modDir)) {
                    plugin.DownloadCodes.TryGetCode(this.Id, out var code);
                    await plugin.AddDownloadAsync(new DownloadTask(plugin, modDir, info.Versions[0].Id, this.IncludeTags, false, null, code));
                }
            } else {
                plugin.DownloadCodes.TryGetCode(this.Id, out var key);
                await InstallerWindow.OpenAndAdd(new InstallerWindow.OpenOptions {
                    Plugin = plugin,
                    PackageId = this.Id,
                    VersionId = this.VersionId,
                    SelectedOptions = this.SelectedOptions,
                    FullInstall = this.FullInstall,
                    IncludeTags = this.IncludeTags,
                    OpenInPenumbra = false,
                    DownloadKey = key,
                });
            }
        });
    }
}

internal readonly record struct HeliosphereDirectoryInfo(
    Guid PackageId,
    Guid VariantId,
    string Version
);
