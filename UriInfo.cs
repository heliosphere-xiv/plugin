using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Web;
using Heliosphere.Model;

namespace Heliosphere;

internal class UriInfo {
    // heliosphere://<uuid>/<version id>?name=<name>&author=<author>&version=<version
    // heliosphere://61035206b9714b059edf4c2e44393988/1?name=Bibo+Flats&author=bingus&version=1.0.0

    internal Guid Id { get; private init; }
    internal int VersionId { get; private init; }
    internal string? Name { get; private init; }
    internal string? Author { get; private init; }
    internal string? Version { get; private init; }
    internal bool? Open { get; set; }

    internal UriInfo(HeliosphereMeta meta) {
        this.Id = meta.Id;
        this.VersionId = meta.VersionId;
        this.Name = meta.Name;
        this.Author = meta.Author;
        this.Version = meta.Version;
    }

    private UriInfo() {
    }

    internal Uri ToUri() {
        var query = new Dictionary<string, string>(3);
        if (this.Name != null) {
            query.Add("name", this.Name);
        }

        if (this.Author != null) {
            query.Add("author", this.Author);
        }

        if (this.Version != null) {
            query.Add("version", this.Version);
        }

        if (this.Open is { } open) {
            query.Add("open", open.ToString().ToLowerInvariant());
        }

        var queryBuilder = new StringBuilder();
        foreach (var (name, value) in query) {
            var empty = queryBuilder.Length == 0;
            if (!empty) {
                queryBuilder.Append('&');
            }

            queryBuilder.Append(name);
            queryBuilder.Append('=');
            queryBuilder.Append(HttpUtility.UrlEncode(value));
        }

        var builder = new UriBuilder($"heliosphere://{this.Id:N}/{this.VersionId}") {
            Query = queryBuilder.ToString(),
        };

        return builder.Uri;
    }

    internal static bool TryParse(string input, [MaybeNullWhen(false)] out UriInfo info) {
        info = null;

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) {
            return false;
        }

        if (uri.Scheme != "heliosphere") {
            return false;
        }

        if (!Guid.TryParse(uri.Host, out var id)) {
            return false;
        }

        if (!int.TryParse(uri.AbsolutePath.Trim('/'), out var versionId)) {
            return false;
        }

        var query = HttpUtility.ParseQueryString(uri.Query);

        var name = query.Get("name");
        var author = query.Get("author");
        var version = query.Get("version");
        bool? open = null;
        if (bool.TryParse(query.Get("open"), out var openParse)) {
            open = openParse;
        }

        info = new UriInfo {
            Id = id,
            VersionId = versionId,
            Name = name,
            Author = author,
            Version = version,
            Open = open,
        };

        return true;
    }
}
