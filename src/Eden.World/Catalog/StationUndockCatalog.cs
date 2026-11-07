using System.Globalization;
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
            var position = Position(locator.Position);
            var direction = Position(locator.Direction);
            if (direction.Length == 0)
            {
                throw new InvalidDataException($"Station type {stationTypeId} has an invalid undock direction.");
            }
            points.Add(new(category.Value, position, direction.Unit));
        }
        return points;
    }

    private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static SpacePosition Position(ClientDungeonsRoomsValueObjectsValuePosition? value) =>
        value is { X: double x, Y: double y, Z: double z } && double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z)
            ? new(x, y, z)
            : throw new InvalidDataException("Station undock locator is missing finite coordinates.");
}
