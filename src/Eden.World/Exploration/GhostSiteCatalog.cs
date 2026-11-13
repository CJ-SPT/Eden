using System.Collections.Immutable;
using Eden.Data.Catalog.Exploration;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Exploration;

internal static class GhostSiteCatalog
{
    private static readonly CatalogDefinition<ImmutableArray<ExplorationDefinition>> s_sites = new("ghost-sites", Load);

    internal static ImmutableArray<ExplorationDefinition> Read(CatalogDbContext catalog) => catalog.Definition(s_sites, 0);

    internal static ExplorationDefinition Find(CatalogDbContext catalog, int id) => Read(catalog).Single(s => s.DungeonId == id);

    private static ImmutableArray<ExplorationDefinition> Load(CatalogDbContext catalog, int unused)
    {
        List<ExplorationDefinition> sites = [];
        for (int id = GhostSiteRules.FirstDungeon; id <= GhostSiteRules.LastDungeon; id++)
        {
            var dungeon = catalog.Read(CatalogData.Client.Dungeons, (long)id);
            if (dungeon is null)
            {
                continue;
            }
            if (dungeon.ArchetypeID != GhostSiteRules.Archetype || dungeon.Rooms is not { Count: 1 } || dungeon.Connections?.Length > 0)
            {
                throw new InvalidDataException($"Unexpected Covert Research Facility layout for {id}.");
            }
            var room = dungeon.Rooms.Single();
            var objects = room.Value.Objects!.Values.Where(o => o.TypeID.HasValue).OrderBy(o => o.ObjectID).ToArray();
            var entry = objects.Single(o => o.ObjectID == dungeon.EntryObjectID);
            var origin = new SpacePosition(entry.Position!.X!.Value, entry.Position.Y!.Value, entry.Position.Z!.Value);
            List<ExplorationContainerDefinition> containers = [];
            List<StaticSpaceObject> scenery = [];
            foreach (var obj in objects.Where(o => o != entry))
            {
                var type = catalog.RequireItem(checked((int)obj.TypeID!.Value));
                if (obj.Position is not { X: double x, Y: double y, Z: double z })
                {
                    continue;
                }
                var position = new SpacePosition(x, y, z) - origin;
                if (GhostSiteRules.IsCache(type.Id))
                {
                    var attributes = catalog.FittingAttributes(type.Id);
                    if (
                        type.GroupId != GhostSiteRules.ContainerGroup
                        || attributes.GetValueOrDefault((DogmaAttributeId)GhostSiteRules.HackingEnabledAttribute) != 1
                    )
                    {
                        throw new InvalidDataException($"Unexpected Covert Research Facility cache {type.Id}.");
                    }
                    int difficulty = checked(
                        (int)attributes.GetValueOrDefault((DogmaAttributeId)GhostSiteRules.HackingDifficultyAttribute)
                    );
                    containers.Add(new(type.Id, difficulty, position));
                }
                else if (type.CategoryId == (int)InventoryCategory.Celestial && type.GroupId != ProbeScanRules.SignatureGroup)
                {
                    scenery.Add(
                        new(
                            obj.ObjectID!.Value,
                            type.Id,
                            type.GroupId,
                            type.CategoryId,
                            1,
                            position,
                            (float)(obj.Radius ?? type.Radius ?? 0)
                        )
                        {
                            IsGlobal = false,
                            Solid = DungeonObjectGeometry.IsSolid(type.GroupId),
                        }
                    );
                }
            }
            int band = (id - GhostSiteRules.FirstDungeon) % 4;
            if (
                containers.Count(c => !GhostSiteRules.IsBonusCache(c.TypeId)) != 4
                || containers.Count(c => GhostSiteRules.IsBonusCache(c.TypeId)) != (band < 2 ? 1 : 0)
            )
            {
                throw new InvalidDataException($"Unexpected Covert Research Facility cache count for {id}.");
            }
            int nameId = checked((int)dungeon.DungeonNameID!.Value);
            sites.Add(
                new(
                    id,
                    nameId,
                    catalog.Read(CatalogData.Client.LocalizationFsdEnUs, (long)nameId)?.Text ?? "Covert Research Facility",
                    checked((int)dungeon.FactionID!.Value),
                    ExplorationKind.Ghost,
                    band,
                    checked((int)dungeon.Difficulty!.Value),
                    containers
                )
                {
                    Scenery = scenery,
                    RoomId = int.Parse(room.Key, System.Globalization.CultureInfo.InvariantCulture),
                }
            );
        }
        return sites.ToImmutableArray();
    }
}
