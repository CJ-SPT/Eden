using System.Text.Json;
using Eden.Data.Catalog;

namespace Eden.World.Catalog;

internal static class CatalogContentPolicy
{
    private static readonly Lazy<CatalogContentSupport> s_current = new(Load);
    internal static CatalogContentSupport Current => s_current.Value;

    private static CatalogContentSupport Load()
    {
        using var stream =
            typeof(CatalogContentPolicy).Assembly.GetManifestResourceStream("Eden.World.Catalog.ContentSupport.json")
            ?? throw new InvalidDataException("Missing catalog content policy.");
        var policy =
            JsonSerializer.Deserialize<CatalogContentSupport>(stream, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Missing catalog content policy.");
        if (
            policy.Build != CatalogSources.PinnedBuild
            || policy.Manufacturing?.HullTypes is null
            || policy.Manufacturing.UtilityTypes is null
            || policy.Insurance?.MaterialValuedHullTypes is null
            || policy.Manufacturing.HullTypes.Any(type => type <= 0)
            || policy.Manufacturing.UtilityTypes.Any(type => type <= 0)
            || policy.Insurance.MaterialValuedHullTypes.Any(type => type <= 0)
        )
        {
            throw new InvalidDataException("Invalid build-specific catalog content policy.");
        }
        return policy;
    }
}
