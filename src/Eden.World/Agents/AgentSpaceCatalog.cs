using System.Collections.Immutable;
using Eden.Data.Catalog;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;

namespace Eden.World.Agents;

internal sealed record AgentSpaceSite(long AgentId, AgentSpaceLocation Location, StaticSpaceObject Contact, StaticSpaceObject Beacon);

internal static class AgentSpaceCatalog
{
    // Public agent beacons have a separate identity range from private mission containers and structures.
    private const long FirstBeaconId = 7_900_000_000_000;
    private static readonly CatalogDefinition<ImmutableArray<AgentSpaceSite>> s_sites = new(
        "agents/public-sites",
        (catalog, _) => Load(catalog),
        Estimate: sites => 256 + sites.Length * 1024L
    );

    internal static AgentSpaceLocation? Location(CatalogDbContext catalog, long agentId) =>
        catalog.Definition(s_sites, 0).FirstOrDefault(site => site.AgentId == agentId)?.Location;

    internal static IEnumerable<StaticSpaceObject> Objects(CatalogDbContext catalog, int systemId) =>
        catalog
            .Definition(s_sites, 0)
            .Where(site => site.Location.SystemId == systemId)
            .SelectMany(site => new[] { site.Beacon, site.Contact })
            .DistinctBy(body => body.Id);

    private static ImmutableArray<AgentSpaceSite> Load(CatalogDbContext catalog)
    {
        List<AgentSpaceSite> result = [];
        foreach (var (id, source) in catalog.ReadMany(CatalogData.Client.Agentsinspace, MissionCatalog.FixedAgentIds))
        {
            if (!MissionCatalog.ForFixedAgent(catalog, id).Any())
            {
                continue;
            }
            int system = checked((int)source.SolarSystemID!.Value);
            int dungeonId = checked((int)source.DungeonID!.Value);
            var dungeon = catalog.Require(CatalogData.Client.Dungeons, (long)dungeonId);
            var objects = dungeon.Rooms!.Values.SelectMany(room => room.Objects!.Values).ToArray();
            var point = objects.Single(body => body.TypeID == source.TypeID);
            var entry = objects.Single(body => body.ObjectID == dungeon.EntryObjectID);
            // The client publishes local room coordinates, not the hidden server's system placement.
            // Eden gives each native public dungeon one stable, shared origin; room offsets remain exact.
            SpacePosition origin = new(MissionRules.PlacementOffset * dungeonId, MissionRules.PlacementOffset, 0);
            SpacePosition position = origin + Position(point.Position!) - Position(entry.Position!);
            var type = catalog.RequireItem(checked((int)source.TypeID!.Value));
            var npc = catalog.Require(CatalogData.Client.Npccharacters, id);
            string name = catalog.Require(CatalogData.Client.LocalizationFsdEnUs, npc.NameID!.Value).Text!;
            AgentSpaceLocation location = new(system, type.Id, position, (float)(type.Radius ?? 0));
            var contact = new StaticSpaceObject(
                id,
                type.Id,
                type.GroupId,
                type.CategoryId,
                checked((int)npc.CorporationID!.Value),
                position,
                location.Radius
            )
            {
                AgentId = id,
                Name = name,
                Solid = false,
                IsGlobal = false,
            };
            var beaconType = catalog.RequireItem(checked((int)entry.TypeID!.Value));
            string beaconName = catalog.Require(CatalogData.Client.LocalizationFsdEnUs, dungeon.DungeonNameID!.Value).Text!;
            var beacon = new StaticSpaceObject(
                FirstBeaconId + dungeonId,
                beaconType.Id,
                beaconType.GroupId,
                beaconType.CategoryId,
                1,
                origin,
                (float)(beaconType.Radius ?? 0)
            )
            {
                Name = beaconName,
                Solid = false,
            };
            result.Add(new(id, location, contact, beacon));
        }
        return result.ToImmutableArray();
    }

    private static SpacePosition Position(ClientAtomsParametersInputsItemDefaultValueObject point) =>
        new(point.X!.Value, point.Y!.Value, point.Z!.Value);
}
