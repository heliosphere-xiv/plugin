using Heliosphere.Model.Generated;
using StrawberryShake;

namespace Heliosphere.Model.Api;

internal static class GraphQl {
    internal static async Task<IReadOnlyList<IGetVersions_Package_Variants>> GetAllVersions(Guid packageId) {
        var resp = await Plugin.GraphQl.GetVersions.ExecuteAsync(packageId);
        resp.EnsureNoErrors();
        return resp.Data!.Package!.Variants.Reverse().ToList();
    }

    internal static async Task<IGetNewestVersionInfo_GetVersion_Variant> GetNewestVersion(int variantId) {
        var resp = await Plugin.GraphQl.GetNewestVersionInfo.ExecuteAsync(variantId);
        resp.EnsureNoErrors();
        return resp.Data!.GetVersion!.Variant;
    }
}
