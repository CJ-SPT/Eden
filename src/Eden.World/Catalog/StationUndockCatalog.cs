using System.Collections.Immutable;
using Eden.Data.Catalog;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;

namespace Eden.World.Catalog;

internal static class StationUndockCatalog
{
    private static readonly CatalogDefinition<ImmutableArray<StationUndockPoint>> s_points = new(
        "station-undock-points",
        Load,
        Estimate: points => 256 + points.Length * 128L
    );

    public static IReadOnlyList<StationUndockPoint> Read(CatalogDbContext catalog, int stationTypeId) =>
        catalog.Definition(s_points, stationTypeId);

    private static ImmutableArray<StationUndockPoint> Load(CatalogDbContext catalog, int stationTypeId)
    {
        var type = catalog.Read(CatalogData.Client.Types, (long)stationTypeId);
        if (type?.GraphicID is not { } graphicId)
        {
            return [];
        }
        var graphic = catalog.Read(CatalogData.Client.Graphicids, graphicId);
        if (graphic?.GraphicLocationID is not { } locationId)
        {
            return [];
        }
        var locations = catalog.Read(CatalogData.Client.Graphiclocations, locationId);
        List<StationUndockPoint> points = [];
        foreach (var locator in locations?.DirectionalLocators ?? [])
        {
            UndockCategory? category = locator.Category switch
            {
                "undockPoint" => UndockCategory.Any,
                "undockPointSmall" => UndockCategory.Small,
                "undockPointLarge" => UndockCategory.Large,
                "undockPointCapital" => UndockCategory.Capital,
                "undockPointSupercapital" => UndockCategory.Supercapital,
                _ => null,
            };
            if (category is null)
            {
                continue;
            }
            var position = Position(locator.Position?.X, locator.Position?.Y, locator.Position?.Z);
            var direction = Position(locator.Direction?.X, locator.Direction?.Y, locator.Direction?.Z);
            if (direction.Length == 0)
            {
                throw new InvalidDataException($"Station type {stationTypeId} has an invalid undock direction.");
            }
            points.Add(new(category.Value, position, direction.Unit));
        }
        return points.ToImmutableArray();
    }

    private static SpacePosition Position(double? sourceX, double? sourceY, double? sourceZ) =>
        sourceX is double x && sourceY is double y && sourceZ is double z && double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z)
            ? new(x, y, z)
            : throw new InvalidDataException("Station undock locator is missing finite coordinates.");
}
