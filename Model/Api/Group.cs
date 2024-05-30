using Heliosphere.Model.Generated;

namespace Heliosphere.Model.Api;

internal interface Group {
    public string Name { get; }
    public string? Description { get; }
    public IReadOnlyList<Option> Options { get; }
    public int Priority { get; }
    public int DefaultSettings { get; }
    public GroupType GroupType { get; }
    public uint OriginalIndex { get; }
}

internal class Option {
    public string Name { get; init; }
    public string? Description { get; init; }
}

internal class StandardGroup : Group {
    public IDownloadTask_GetVersion_Groups_Standard Inner { get; }

    public string Name => this.Inner.Name;
    public string? Description => this.Inner.Description;
    public int Priority => this.Inner.Priority;
    public int DefaultSettings => this.Inner.DefaultSettings;
    public GroupType GroupType => this.Inner.GroupType;
    public uint OriginalIndex => (uint) this.Inner.OriginalIndex;

    public IReadOnlyList<Option> Options => this.Inner.Options
        .Select(option => new Option {
            Name = option.Name,
            Description = option.Description,
        })
        .ToList();

    internal StandardGroup(IDownloadTask_GetVersion_Groups_Standard inner) {
        this.Inner = inner;
    }
}

internal class ImcGroup : Group {
    public IDownloadTask_GetVersion_Groups_Imc Inner { get; }

    public string Name => this.Inner.Name;
    public string? Description => this.Inner.Description;
    public int Priority => this.Inner.Priority;
    public int DefaultSettings => this.Inner.DefaultSettings;
    public GroupType GroupType => this.Inner.GroupType;
    public uint OriginalIndex => (uint) this.Inner.OriginalIndex;

    public IReadOnlyList<Option> Options => this.Inner.Options
        .Select(option => new Option {
            Name = option.Name,
            Description = option.Description,
        })
        .ToList();

    internal ImcGroup(IDownloadTask_GetVersion_Groups_Imc inner) {
        this.Inner = inner;
    }
}

internal static class GroupsUtil {
    internal static IEnumerable<Group> Convert(
        IDownloadTask_GetVersion_Groups groups
    ) {
        return groups.Standard.Select(g => new StandardGroup(g) as Group)
            .Concat(groups.Imc.Select(g => new ImcGroup(g)));
    }
}
