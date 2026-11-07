using System.Globalization;
using Eden.Data.Catalog;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;

namespace Eden.World.Catalog;

internal static class StationUndockCatalog
{
    public static IReadOnlyList<StationUndockPoint> Read(CatalogDbContext catalog, int stationTypeId)
    {
        var type = catalog.Set<SourceRecord>().Find("client/types", Key(stationTypeId))?.Read<ClientTypes>();
        if (type?.GraphicID is not { } graphicId)
        {
            return [];
        }
        var graphic = catalog.Set<SourceRecord>().Find("client/graphicids", Key(graphicId))?.Read<ClientGraphicids>();
        if (graphic?.GraphicLocationID is not { } locationId)
        {
            return [];
        }
        var locations = catalog.Set<SourceRecord>().Find("client/graphiclocations", Key(locationId))?.Read<ClientGraphiclocations>();
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
        return points;
    }

    private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static SpacePosition Position(double? sourceX, double? sourceY, double? sourceZ) =>
        sourceX is double x && sourceY is double y && sourceZ is double z && double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z)
            ? new(x, y, z)
            : throw new InvalidDataException("Station undock locator is missing finite coordinates.");
}
