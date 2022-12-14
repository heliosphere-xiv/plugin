using System.ComponentModel;
using System.Text;
using Semver;

namespace Heliosphere.Model;

[Serializable]
internal class HeliosphereMeta {
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string Tagline { get; set; }
    public string Description { get; set; }
    public string Author { get; set; }

    [DefaultValue("00000000-0000-0000-0000-000000000000")]
    public Guid AuthorUuid { get; set; }

    public string Version { get; set; }
    public int VersionId { get; set; }
    public bool FullInstall { get; set; }
    public bool IncludeTags { get; set; }
    public Dictionary<string, List<string>> SelectedOptions { get; set; }

    [Obsolete]
    public int AuthorId { get; set; }

    internal bool IsSimple() {
        return this.FullInstall && this.SelectedOptions.Count == 0;
    }

    internal bool IsUpdate(string version) {
        var currentSuccess = SemVersion.TryParse(this.Version, SemVersionStyles.Strict, out var current);
        var newestSuccess = SemVersion.TryParse(version, SemVersionStyles.Strict, out var newest);

        return currentSuccess && newestSuccess && current.CompareSortOrderTo(newest) == -1;
    }

    internal static string ModDirectoryName(Guid id, string name, string version) {
        var invalidChars = Path.GetInvalidFileNameChars();
        var slug = name.Select(c => invalidChars.Contains(c) ? '-' : c)
            .Aggregate(new StringBuilder(), (sb, c) => sb.Append(c))
            .ToString();
        return $"hs-{slug}-{version}-{id:N}";
    }
}
