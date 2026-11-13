using System.Collections.Immutable;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.Catalog.Exploration;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;

namespace Eden.World.Exploration;

internal sealed record DedMember(DedRoomMember Binding, SpacePosition Position, double AcquisitionRange);

internal sealed record DedGate(long ObjectId, StaticSpaceObject Body, bool RequiresClear, MissionGateAccess Access);

internal sealed record DedRoom(
    SpacePosition Offset,
    ImmutableArray<StaticSpaceObject> Objects,
    ImmutableArray<DedMember> Members,
    DedGate? Gate
);

internal sealed record DedComplex(
    DedComplexBinding Binding,
    int NameId,
    int Difficulty,
    int FactionId,
    int CorporationId,
    ImmutableArray<DedRoom> Rooms
);

internal static class DedComplexCatalog
{
    private sealed record Content(int Build, IReadOnlyList<DedComplexBinding> Complexes);

    private static readonly Lazy<Content> s_content = new(() =>
    {
        using var stream = typeof(DedComplexBinding).Assembly.GetManifestResourceStream("Eden.Data.DedComplexes.json")!;
        var content = JsonSerializer.Deserialize<Content>(stream, JsonSerializerOptions.Web)!;
        if (content.Build != CatalogSources.PinnedBuild)
        {
            throw new InvalidDataException("Invalid DED complex content build.");
        }
        return content;
    });

    private static readonly CatalogDefinition<ImmutableArray<DedComplex>> s_definitions = new("ded-complexes", Load);

    internal static ImmutableArray<DedComplex> Read(CatalogDbContext catalog) => catalog.Definition(s_definitions, 0);

    internal static bool IsOverseer(int type) => s_content.Value.Complexes.Any(c => c.OverseerType == type);

    internal static DedComplex Find(CatalogDbContext catalog, int dungeon) => Read(catalog).Single(d => d.Binding.DungeonId == dungeon);

    private static ImmutableArray<DedComplex> Load(CatalogDbContext catalog, int unused)
    {
        List<DedComplex> complexes = [];
        foreach (var binding in s_content.Value.Complexes)
        {
            var dungeon = catalog.Read(CatalogData.Client.Dungeons, (long)binding.DungeonId);
            if (dungeon is null)
            {
                continue;
            }
            if (
                dungeon.ArchetypeID != CombatAnomalyRules.CombatSiteArchetypeId
                || dungeon.EntryTypeID != ProbeScanRules.SignatureType
                || dungeon.EntryObjectID != binding.Rooms[0].EntryObjectId
                || dungeon.Rooms?.Count != binding.Rooms.Count
                || dungeon.Connections?.Length != binding.Rooms.Count - 1
                || dungeon.Difficulty != binding.Rating
            )
            {
                throw new InvalidDataException($"Pinned DED complex metadata differs for {binding.DungeonId}.");
            }
            int faction = checked((int)dungeon.FactionID!.Value);
            int corporation = checked((int)catalog.Require(CatalogData.Client.Factions, (long)faction).CorporationID!.Value);
            foreach (
                int loot in binding
                    .GuaranteedLoot.Select(l => l.TypeId)
                    .Concat(binding.OverseerLoot.Select(l => l.TypeId))
                    .Concat(binding.DeadspaceModules)
                    .Concat(binding.FactionModules)
                    .Append(binding.Blueprint)
            )
            {
                if (catalog.Require(CatalogData.Client.Types, (long)loot).Published != 1)
                {
                    throw new InvalidDataException($"DED loot type {loot} is unavailable in the pinned build.");
                }
            }
            _ = catalog.Require(
                CatalogData.Client.Blueprints,
                binding.Blueprint.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
            var rooms = binding.Rooms.Select((room, index) => LoadRoom(catalog, dungeon, room, index, corporation)).ToImmutableArray();
            complexes.Add(new(binding, checked((int)dungeon.DungeonNameID!.Value), binding.Rating, faction, corporation, rooms));
        }
        return complexes.ToImmutableArray();
    }

    private static DedRoom LoadRoom(CatalogDbContext catalog, ClientDungeons dungeon, DedRoomBinding binding, int index, int corporation)
    {
        var items = dungeon
            .Rooms![binding.RoomId.ToString(System.Globalization.CultureInfo.InvariantCulture)]
            .Objects!.Values.ToDictionary(o => o.ObjectID!.Value);
        var origin = Position(items[binding.EntryObjectId]);
        var offset = new SpacePosition(index * MissionGateRules.RoomSpacing, 0, 0);
        var structures = binding.Members.Where(m => m.Structure).Select(m => m.AnchorId).ToHashSet();
        var members = binding
            .Members.Select(m =>
            {
                var anchor = items[m.AnchorId];
                var type = catalog.RequireItem(m.TypeId);
                if (
                    m.Count <= 0
                    || (
                        m.Structure
                            ? anchor.TypeID != type.Id || anchor.TypeID is null
                            : anchor.Entities is not { Count: > 0 } && anchor.TypeID != type.Id
                    )
                )
                {
                    throw new InvalidDataException($"Invalid DED NPC anchor {m.AnchorId}.");
                }
                return new DedMember(
                    m,
                    offset + Position(anchor) - origin,
                    anchor.GuardCommand?.AggressionRange is > 0
                        ? anchor.GuardCommand.AggressionRange.Value
                        : NpcEncounterDefinitions.VisibilityRange
                );
            })
            .ToImmutableArray();
        var objects = items
            .Values.OrderBy(o => o.ObjectID)
            .Where(o =>
                o.TypeID is not null
                && o.Position is not null
                && o.ObjectID != binding.EntryObjectId
                && !structures.Contains(o.ObjectID!.Value)
            )
            .Select(o => (Object: o, Type: catalog.RequireItem(checked((int)o.TypeID!.Value))))
            .Where(pair => pair.Type.CategoryId is (int)InventoryCategory.Celestial or (int)InventoryCategory.Asteroid)
            .Select(pair =>
            {
                var (o, type) = pair;
                return new StaticSpaceObject(
                    o.ObjectID!.Value,
                    type.Id,
                    type.GroupId,
                    type.CategoryId,
                    corporation,
                    offset + Position(o) - origin,
                    DungeonObjectGeometry.Radius(
                        type.CategoryId,
                        type.Radius,
                        o.Radius,
                        catalog.Require(CatalogData.Client.Types, (long)type.Id).Volume ?? 0
                    )
                )
                {
                    IsGlobal = false,
                    Solid = DungeonObjectGeometry.IsSolid(type.GroupId) && type.GroupId != MissionGateRules.AccelerationGateGroup,
                    Name = o.ObjectNameID is long name ? catalog.Read(CatalogData.Client.LocalizationFsdEnUs, name)?.Text : null,
                };
            })
            .ToImmutableArray();
        DedGate? gate = null;
        if (binding.Gate is { } authored)
        {
            var connection = dungeon.Connections!.Value.Single(g => g.FromObjectID == authored.ObjectId);
            var body = objects.Single(o => o.Id == authored.ObjectId && o.GroupId == MissionGateRules.AccelerationGateGroup);
            var access = new MissionGateAccess(
                connection.AllowedShipsList is long shipList ? CatalogTypeLists.Read(catalog, shipList) : null,
                (connection.AllowedShipRaces ?? []).ToHashSet(),
                connection.DisallowedFittedTypesList is long forbidden ? CatalogTypeLists.Read(catalog, forbidden) : null,
                authored.Key
            );
            gate = new(authored.ObjectId, body, authored.RequiresClear, access);
        }
        return new(offset, objects, members, gate);
    }

    private static SpacePosition Position(ClientDungeonsRoomsValueObjectsValue value) =>
        new(value.Position!.X!.Value, value.Position.Y!.Value, value.Position.Z!.Value);
}
