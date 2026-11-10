using Eden.Data.Catalog;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;

namespace Eden.World.Industry;

internal sealed record IndustryStation(
    long Id,
    int TypeId,
    int OwnerId,
    int SolarSystemId,
    int OperationId,
    IReadOnlyList<StructureService>? StructureServices = null
)
{
    internal static IndustryStation From(CatalogStationDefinition station) =>
        new(station.Id, station.TypeId, station.OwnerId, station.SolarSystemId, station.OperationId);

    internal static IndustryStation From(StructureSnapshot structure) =>
        new(structure.Id, structure.Profile.TypeId, structure.CorporationId, structure.SystemId, 0, structure.Services);
}
