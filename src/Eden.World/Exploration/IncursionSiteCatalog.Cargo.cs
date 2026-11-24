using Eden.Data.World.Exploration;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;

namespace Eden.World.Exploration;

internal sealed record IncursionCargoObjective(
    int TypeId,
    int Quantity,
    int? UnlockWave,
    StaticSpaceObject Destination,
    StaticSpaceObject? Source
)
{
    internal StaticSpaceObject Place(WorldCombatAnomaly site, StaticSpaceObject body) =>
        body with
        {
            Id = IncursionObjectIds.Object(site.Id, checked((int)body.Id)),
            Position = IncursionSiteCatalog.CombatCenter(site) + body.Position,
            Container = new(body.OwnerId, body == Destination || site.IncursionCargoIssued),
        };
}

internal static partial class IncursionSiteCatalog
{
    internal const int Lyavite = 28620;
    internal const int CapturedCivilians = 3483;
    internal const int ConcordMtac = 3501;
    private const int LogisticsArray = 3261;

    internal static StaticSpaceObject ArrayBody(CatalogDbContext catalog, WorldCombatAnomaly site)
    {
        var body = Read(catalog, site.DungeonId)!.Encounter.Objects.Single(body => body.Id == IncursionObjectIds.LogisticsArray);
        return body with
        {
            Id = IncursionObjectIds.Object(site.Id, IncursionObjectIds.LogisticsArray),
            Position = new SpacePosition(site.X, site.Y, site.Z) + body.Position,
        };
    }

    private const int CargoContainerType = 23;
    private const int MtacFactory = 3498;
    private const int OreRefinery = 3502;
    private const int EvacuationFreighter = 3503;
    private const int ShieldTransferControlTower = 3495;

    private static IncursionCargoObjective? Cargo(CatalogDbContext catalog, int dungeonId)
    {
        return dungeonId switch
        {
            NationMiningColony => new(Lyavite, 255, null, Destination(OreRefinery, new(28_000, 15_000, 19_000)), null),
            OverwhelmedCivilianFacility => new(
                CapturedCivilians,
                40,
                null,
                Destination(EvacuationFreighter, new(-47_000, -11_000, 31_000)),
                Source(CargoContainerType, new(47_000, 4_000, -40_000), "Civilian Transport Cargo")
            ),
            TrueCreationsResearchCenter => new(
                ConcordMtac,
                1,
                2,
                Destination(ShieldTransferControlTower, new(-23_000, 2_000, 22_000)),
                Source(MtacFactory, new(38_000, 18_000, 9_000), "CONCORD MTAC Factory")
            ),
            _ => null,
        };

        StaticSpaceObject Destination(int type, SpacePosition position) =>
            Body(catalog, IncursionObjectIds.CargoDestination, type, position) with
            {
                Solid = false,
            };

        StaticSpaceObject Source(int type, SpacePosition position, string name) =>
            Body(catalog, IncursionObjectIds.CargoSource, type, position) with
            {
                Solid = false,
                Name = name,
            };
    }
}
