using Heliosphere.Exceptions;
using Heliosphere.Model.Generated;
using StrawberryShake;

namespace Heliosphere.Model.Api;

internal static class GraphQl {
    internal static async Task<IReadOnlyList<IGetVersions_Package_Variants>> GetAllVersions(Guid packageId, CancellationToken token = default) {
        var resp = await Plugin.GraphQl.GetVersions.ExecuteAsync(packageId, token);
        resp.EnsureNoErrors();

        var package = resp.Data?.Package ?? throw new MissingPackageException(packageId);
        return package.Variants.Reverse().ToList();
    }

    internal static async Task<IGetNewestVersionInfo_Variant?> GetNewestVersion(Guid variantId, CancellationToken token = default) {
        var resp = await Plugin.GraphQl.GetNewestVersionInfo.ExecuteAsync(variantId, token);
        resp.EnsureNoErrors();
        return resp.Data?.Variant ?? throw new MissingVariantException(variantId);
    }

    internal static async Task<IReadOnlyList<IGetNewestVersionInfoMulti_Variants>> GetNewestVersions(IReadOnlyList<Guid> variantIds, CancellationToken token = default) {
        var resp = await Plugin.GraphQl.GetNewestVersionInfoMulti.ExecuteAsync(variantIds, token);
        resp.EnsureNoErrors();
        return resp.Data?.Variants ?? Array.Empty<IGetNewestVersionInfoMulti_Variants>();
    }
}
