using System.Collections.Immutable;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;

namespace Eden.World.Agents;

internal sealed record MissionShipRestrictionDirectory(
    MissionShipRestrictions All,
    ImmutableDictionary<long, MissionShipRestrictions> Gates
);

internal static class MissionShipCatalog
{
    private static readonly CatalogDefinition<ImmutableArray<MissionShipType>> s_ships = new(
        "agent-mission-ships",
        (catalog, _) =>
            catalog
                .Definitions.Items.Values.Where(t => t.CategoryId == (int)InventoryCategory.Ship)
                .Select(t => new MissionShipType(
                    t.Id,
                    t.GroupId,
                    checked((int)(catalog.Read(CatalogData.Client.Types, (long)t.Id)?.RaceID ?? 0))
                ))
                .OrderBy(t => t.TypeId)
                .ToImmutableArray(),
        Estimate: ships => 256 + ships.Length * 32L
    );

    private static readonly CatalogDefinition<MissionShipRestrictionDirectory> s_restrictions = new(
        "agent-mission-ship-restrictions",
        Load,
        Estimate: value => 512 + (value.Gates.Count + 1L) * (value.All.AllowedShipTypes.Count + value.All.RestrictedShipTypes.Count) * 4
    );

    internal static MissionShipRestrictions Read(CatalogDbContext catalog, int missionId, long? gateId)
    {
        var directory = catalog.Definition(s_restrictions, missionId);
        return gateId is not long id ? directory.All
            : directory.Gates.TryGetValue(id, out var gate) ? gate
            : throw new InvalidDataException("Choose a gate in this mission's dungeon.");
    }

    private static MissionShipRestrictionDirectory Load(CatalogDbContext catalog, int missionId)
    {
        var profile = MissionCatalog.Find(catalog, missionId);
        var ships = catalog.Definition(s_ships, 0);
        var gates = profile.Rooms.SelectMany(r => r.Gates).ToArray();
        return new(
            MissionShipRestrictionRules.Describe(ships, gates.Select(g => g.Access).ToArray()),
            gates.ToImmutableDictionary(g => g.ObjectId, g => MissionShipRestrictionRules.Describe(ships, [g.Access]))
        );
    }
}
