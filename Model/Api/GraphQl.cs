using Heliosphere.Model.Generated;
using StrawberryShake;

namespace Heliosphere.Model.Api; 

internal static class GraphQl {
    internal static async Task<IGetVersions_Package_Versions> GetVersionsPage(Guid packageId, int last, string? before) {
        var resp = await Plugin.GraphQl.GetVersions.ExecuteAsync(packageId, last, before);
        resp.EnsureNoErrors();
        return resp.Data!.Package!.Versions;
    }

    internal static async Task<List<IGetVersions_Package_Versions_Nodes>> GetAllVersions(Guid packageId) {
        var list = new List<IGetVersions_Package_Versions_Nodes>(5);

        string? cursor = null;
        while (true) {
            var info = await GetVersionsPage(packageId, 100, cursor);
            foreach (var node in info.Nodes) {
                list.Add(node);
            }

            if (!info.PageInfo.HasNextPage) {
                break;
            }

            cursor = info.PageInfo.StartCursor;
        }

        return list;
    }

    internal static async Task<IGetNewestVersionInfo_Package> GetNewestVersion(Guid packageId) {
        var resp = await Plugin.GraphQl.GetNewestVersionInfo.ExecuteAsync(packageId);
        resp.EnsureNoErrors();
        return resp.Data!.Package!;
    }
}
