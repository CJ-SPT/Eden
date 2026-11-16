using System.Collections.Immutable;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Sovereignty;

[Injectable(InjectionType.Singleton)]
internal sealed class SovereigntyStore(WorldDatabase database) : ISovereignty
{
    private LiveWorld Resident =>
        LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Sovereignty requires an active resident world.");

    public SovereigntyClaim? Read(int systemId) =>
        Resident.Economy.Inspect(world => world.Set<SavedSovereigntyClaim>().Find(systemId) is { } row ? Snapshot(row) : null);

    public IReadOnlyList<SovereigntyClaim> List(int allianceId) =>
        Resident.Economy.Inspect(world =>
            (IReadOnlyList<SovereigntyClaim>)
                world
                    .Filter<SavedSovereigntyClaim>(row => row.AllianceId == allianceId)
                    .OrderBy(row => row.SystemId)
                    .Select(Snapshot)
                    .ToImmutableArray()
        );

    public SovereigntySystem? System(int systemId)
    {
        var row = database.Catalog.Find(CatalogData.Sde.MapSolarSystems, (long)systemId);
        if (
            row is null
            || row.RegionID is not > 0 or > int.MaxValue
            || row.ConstellationID is not > 0
            || row.SecurityStatus is not { } security
        )
        {
            return null;
        }
        var constellation = database.Catalog.Find(CatalogData.Sde.MapConstellations, row.ConstellationID.Value);
        var region = database.Catalog.Find(CatalogData.Sde.MapRegions, row.RegionID.Value);
        if (constellation is null || region is null || constellation.RegionID != row.RegionID)
        {
            return null;
        }
        bool allowed =
            row.DisallowedAnchorCategories?.Contains(SovereigntyRules.SovereigntyCategoryId) != true
            && row.DisallowedAnchorGroups?.Contains(SovereigntyRules.HubGroupId) != true;
        // NPC nullsec commonly inherits faction ownership. Serpentis Prime, for
        // example, has a faction on its constellation but neither system nor region.
        long? faction = row.FactionID ?? constellation.FactionID ?? region.FactionID;
        return new(systemId, checked((int)row.RegionID.Value), security, faction, allowed);
    }

    private static SovereigntyClaim Snapshot(SavedSovereigntyClaim row) =>
        new(row.SystemId, row.HubId, row.CorporationId, row.AllianceId, row.ClaimedAt);
}
