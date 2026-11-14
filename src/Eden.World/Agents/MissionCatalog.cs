using System.Collections.Immutable;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.Catalog.Agents;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;

namespace Eden.World.Agents;

internal sealed record MissionProfile(
    MissionDefinition Definition,
    string Name,
    int NameId,
    int BriefingId,
    string Briefing,
    int? DungeonId,
    int EnemyFaction,
    int EnemyCorporation,
    int CargoType,
    int CargoQuantity,
    double CargoVolume,
    ImmutableArray<MissionRoomProfile> Rooms
)
{
    internal int WaveRoom(int wave) => Definition.Waves[wave].RoomId == 0 ? Rooms[0].Id : Definition.Waves[wave].RoomId;

    internal MissionKind Kind =>
        Definition.Mining ? MissionKind.Mining
        : DungeonId.HasValue ? MissionKind.Combat
        : MissionKind.Courier;

    internal int SupplierFaction { get; init; }
    internal int SupplierCorporation { get; init; }
    internal ImmutableArray<MissionDeposit> Deposits { get; init; } = [];

    internal bool AvailableTo(CatalogAgent agent) =>
        (SupplierFaction == 0 || SupplierFaction == agent.FactionId)
        && (SupplierCorporation == 0 || SupplierCorporation == agent.CorporationId);

    internal bool AttacksFaction(int? faction) =>
        faction.HasValue
        && Definition.Waves.SelectMany(w => w.Groups).Any(g => (g.EnemyFaction == 0 ? EnemyFaction : g.EnemyFaction) == faction);
}

internal sealed record MissionRoomProfile(
    int Id,
    SpacePosition Position,
    ImmutableArray<StaticSpaceObject> Objects,
    ImmutableArray<MissionRoomGate> Gates
)
{
    internal ImmutableArray<MissionSpawnAnchor> SpawnAnchors { get; init; } = [];
}

internal sealed record MissionRoomGate(long ObjectId, int DestinationRoomId, bool RequiresClear, MissionGateAccess Access);

internal sealed record MissionDeposit(StaticSpaceObject Contact, int Quantity);

internal static class MissionCatalog
{
    private const string BasicMiningMission = "agent.missionTemplatizedContent_BasicMiningMission";
    private const int InvisibleBeaconGroup = 4055;
    private static readonly Lazy<MissionContent> s_content = new(() =>
    {
        using var stream = typeof(MissionCatalog).Assembly.GetManifestResourceStream("Eden.World.Missions.json")!;
        var content =
            JsonSerializer.Deserialize<MissionContent>(stream, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Invalid mission content.");
        if (content.Build != CatalogSources.PinnedBuild)
        {
            throw new InvalidDataException("Mission content targets a different client build.");
        }
        return content;
    });
    private static readonly CatalogDefinition<MissionProfile?> s_profile = new("agent-mission", Load, EstimatedBytes: 16384);
    private static readonly CatalogDefinition<ImmutableArray<MissionProfile>> s_profiles = new(
        "agent-missions",
        (catalog, _) =>
            s_content
                .Value.Missions.Select(definition => catalog.Definition(s_profile, definition.MissionId))
                .OfType<MissionProfile>()
                .ToImmutableArray(),
        // The full view retains profiles even if their individual cache entries are evicted.
        Estimate: profiles => 1024 + profiles.Length * 16384L
    );

    internal static ImmutableArray<MissionProfile> Read(CatalogDbContext catalog) => catalog.Definition(s_profiles, 0);

    internal static bool IsMining(int missionId) => s_content.Value.Missions.Any(m => m.MissionId == missionId && m.Mining);

    internal static IEnumerable<int> MiningObjectiveTypes(CatalogDbContext catalog) =>
        catalog
            .ReadMany(CatalogData.Client.Missions, s_content.Value.Missions.Where(m => m.Mining).Select(m => (long)m.MissionId))
            .Values.Select(mission => checked((int)(mission.KillMission?.ObjectiveTypeID ?? 0)));

    internal static MissionProfile Find(CatalogDbContext catalog, int missionId) =>
        catalog.Definition(s_profile, missionId) ?? throw new InvalidDataException("This mission's content is unavailable.");

    private static MissionProfile? Load(CatalogDbContext catalog, int missionId)
    {
        var definition = s_content.Value.Missions.SingleOrDefault(m => m.MissionId == missionId);
        if (definition is null)
        {
            return null;
        }
        var mission = catalog.Read(CatalogData.Client.Missions, (long)definition.MissionId);
        if (mission is null)
        {
            return null;
        }
        int nameId = checked((int)(mission.NameID ?? 0));
        int briefingId = checked((int)(mission.Messages?.GetValueOrDefault("messages.mission.briefing") ?? 0));
        string name =
            catalog.Read(CatalogData.Client.LocalizationFsdEnUs, (long)nameId)?.Text
            ?? catalog.Read(CatalogData.Sde.Missions, (long)definition.MissionId)?.Name?.En
            ?? "Mission";
        string briefing = catalog.Read(CatalogData.Client.LocalizationFsdEnUs, (long)briefingId)?.Text ?? name;
        int? dungeonId = checked((int?)mission.KillMission?.DungeonID);
        int cargoType = checked((int)(mission.CourierMission?.ObjectiveTypeID ?? mission.KillMission?.ObjectiveTypeID ?? 0));
        int quantity = checked((int)(mission.CourierMission?.ObjectiveQuantity ?? mission.KillMission?.ObjectiveQuantity ?? 0));
        double volume = cargoType == 0 ? 0 : catalog.Read(CatalogData.Client.Types, (long)cargoType)?.Volume ?? 0;
        if (definition.Mining && (mission.ContentTemplate != BasicMiningMission || dungeonId is null || !(volume > 0)))
        {
            throw new InvalidDataException("Mining mission does not have a catalog dungeon and objective cargo.");
        }
        int faction = 0;
        int corporation = 0;
        ImmutableArray<MissionRoomProfile> rooms = [];
        ImmutableArray<MissionDeposit> deposits = [];
        if (dungeonId is int id)
        {
            var dungeon = catalog.Require(CatalogData.Client.Dungeons, (long)id);
            faction = checked((int)(dungeon.FactionID ?? 0));
            corporation = checked((int)(catalog.Read(CatalogData.Client.Factions, (long)faction)?.CorporationID ?? 0));
            rooms = Rooms(catalog, definition, dungeon, corporation);
            if (definition.Mining)
            {
                deposits = Deposits(catalog, dungeon, cargoType, quantity, volume);
            }
        }

        if (dungeonId.HasValue && cargoType > 0 && (quantity <= 0 || volume <= 0))
        {
            throw new InvalidDataException("Combat retrieval objective has invalid catalog cargo.");
        }
        if (!dungeonId.HasValue && (cargoType == 0 || quantity <= 0 || volume <= 0))
        {
            return null;
        }
        return new(definition, name, nameId, briefingId, briefing, dungeonId, faction, corporation, cargoType, quantity, volume, rooms)
        {
            SupplierFaction = checked((int)(mission.FactionID ?? 0)),
            SupplierCorporation = checked((int)(mission.CorporationID ?? 0)),
            Deposits = deposits,
        };
    }

    private static ImmutableArray<MissionDeposit> Deposits(
        CatalogDbContext catalog,
        ClientDungeons dungeon,
        int typeId,
        int quantity,
        double volume
    )
    {
        var type = catalog.RequireItem(typeId);
        var objects = dungeon.Rooms!.Values.Single().Objects!.Values;
        var entry = Position(objects.Single(o => o.ObjectID == dungeon.EntryObjectID).Position!);
        var deposits = objects.Where(o => o.TypeID == typeId).OrderBy(o => o.ObjectID).ToArray();
        if (
            !MiningDefinitions.IsResource(type.CategoryId, type.GroupId)
            || deposits.Length is 0 or > MissionMiningRules.DepositsPerMission
            || deposits.Any(o =>
                o.Position is null || o.Radius is not (> 0 and <= int.MaxValue) || o.Radius != Math.Truncate(o.Radius.Value)
            )
            || deposits.Sum(o => (long)o.Radius!.Value) != quantity
        )
        {
            throw new InvalidDataException("Mining mission deposits do not match the catalog objective.");
        }
        bool cloud = type.GroupId == MiningDefinitions.GasCloudGroup;
        return deposits
            .Select(o => new MissionDeposit(
                new(
                    o.ObjectID!.Value,
                    typeId,
                    type.GroupId,
                    type.CategoryId,
                    1,
                    Position(o.Position!) - entry,
                    DungeonObjectGeometry.Radius(type.CategoryId, type.Radius, cloud ? null : o.Radius, volume)
                )
                {
                    IsGlobal = false,
                    Solid = !cloud,
                },
                (int)o.Radius!.Value
            ))
            .ToImmutableArray();
    }

    private static ImmutableArray<MissionRoomProfile> Rooms(
        CatalogDbContext catalog,
        MissionDefinition definition,
        ClientDungeons dungeon,
        int corporation
    )
    {
        var source = dungeon.Rooms ?? throw new InvalidDataException("Mission dungeon has no rooms.");
        var bindings = definition.Rooms.IsEmpty
            ? new[]
            {
                new MissionRoomDefinition(
                    int.Parse(source.Single().Key, System.Globalization.CultureInfo.InvariantCulture),
                    dungeon.EntryObjectID!.Value
                ),
            }
            : definition.Rooms.ToArray();
        if (
            bindings.Length != source.Count
            || bindings.Select(r => r.RoomId).Distinct().Count() != bindings.Length
            || bindings[0].EntryObjectId != dungeon.EntryObjectID
            || !definition.Mining && definition.Waves.IsDefaultOrEmpty
            || definition.Waves.Length * MissionRules.MembersPerWave > MissionRules.NpcsPerMission
        )
        {
            throw new InvalidDataException("Mission room bindings or NPC identity budget are invalid.");
        }
        var catalogGates = dungeon.Connections?.Select(g => g.FromObjectID).ToHashSet() ?? [];
        if (!catalogGates.SetEquals(definition.Gates.Select(g => (long?)g.FromObjectId)))
        {
            throw new InvalidDataException("Every catalog gate needs an explicit destination binding.");
        }
        List<MissionRoomProfile> rooms = [];
        for (int index = 0; index < bindings.Length; index++)
        {
            var binding = bindings[index];
            var room = source[binding.RoomId.ToString(System.Globalization.CultureInfo.InvariantCulture)];
            var entry = room.Objects!.Values.Single(o => o.ObjectID == binding.EntryObjectId);
            var origin = Position(entry.Position!);
            var offset = new SpacePosition(index * MissionGateRules.RoomSpacing, 0, 0);
            var anchors = room
                .Objects.Values.Where(o => o.TypeID.HasValue && o.Position is not null)
                .OrderBy(o => o.ObjectID)
                .Select(o => (Source: o, Type: catalog.FittingType(o.TypeID!.Value)))
                .Where(o => o.Source.Entities is { Count: > 0 } || o.Type?.GroupId == NpcEncounterDefinitions.SentryGroup)
                .Select(o => new MissionSpawnAnchor(
                    o.Source.ObjectID!.Value,
                    Position(o.Source.Position!) - origin,
                    o.Source.GuardCommand?.AggressionRange is > 0
                        ? o.Source.GuardCommand.AggressionRange.Value
                        : NpcEncounterDefinitions.VisibilityRange,
                    o.Source.GuardCommand?.ObjectTriggerSpawn != 1
                        && (o.Source.Entities is not { Count: > 0 } || o.Source.Entities.Values.Any(e => e.TriggerSpawn != 1)),
                    o.Source.GuardCommand?.ObjectTriggerSpawn == 1 || o.Source.Entities?.Values.Any(e => e.TriggerSpawn == 1) == true,
                    o.Type?.GroupId == NpcEncounterDefinitions.SentryGroup ? o.Type.Id : null
                ))
                .ToImmutableArray();
            List<StaticSpaceObject> objects = [];
            foreach (var item in room.Objects.Values.Where(o => o.TypeID.HasValue && o.Position is not null))
            {
                var type = catalog.FittingType(item.TypeID!.Value);
                bool gate = definition.Gates.Any(g => g.FromObjectId == item.ObjectID);
                bool container = definition.Containers.Any(c => c.ObjectId == item.ObjectID);
                bool structure = definition.Structures.Any(s => s.ObjectId == item.ObjectID);
                if (
                    type is null
                    || definition.Mining && MiningDefinitions.IsResource(type.CategoryId, type.GroupId)
                    || !container && !structure && type.CategoryId is not (2 or 25)
                    || type.GroupId == InvisibleBeaconGroup
                    || item.ObjectID == binding.EntryObjectId
                )
                {
                    continue;
                }
                if (type.GroupId == MissionGateRules.AccelerationGateGroup && !gate)
                {
                    throw new InvalidDataException("Unbound acceleration gate in mission room.");
                }
                objects.Add(
                    new(
                        item.ObjectID!.Value,
                        type.Id,
                        type.GroupId,
                        type.CategoryId,
                        corporation,
                        offset + Position(item.Position!) - origin,
                        DungeonObjectGeometry.Radius(
                            type.CategoryId,
                            type.Radius,
                            item.Radius,
                            catalog.Read(CatalogData.Client.Types, (long)type.Id)?.Volume ?? 0
                        )
                    )
                    {
                        IsGlobal = false,
                        Solid = !gate && DungeonObjectGeometry.IsSolid(type.GroupId),
                        Name =
                            (container || structure) && item.ObjectNameID is long name
                                ? catalog.Read(CatalogData.Client.LocalizationFsdEnUs, name)?.Text
                                : null,
                        StandingThresholds = container || structure ? NpcProfileCatalog.ReadStandingThresholds(catalog, type.Id) : null,
                    }
                );
            }
            var gates = definition
                .Gates.Where(g => room.Objects.Values.Any(o => o.ObjectID == g.FromObjectId))
                .Select(g =>
                {
                    var connection = dungeon.Connections!.Value.Single(c => c.FromObjectID == g.FromObjectId);
                    if (g.Key is { } key)
                    {
                        _ = MissionGateAccessRules.RequiresKey(key, false);
                        _ = catalog.RequireItem(key.TypeId);
                    }
                    var access = new MissionGateAccess(
                        connection.AllowedShipsList is > 0 ? CatalogTypeLists.Read(catalog, connection.AllowedShipsList.Value) : null,
                        (connection.AllowedShipRaces ?? []).ToHashSet(),
                        connection.DisallowedFittedTypesList is > 0
                            ? CatalogTypeLists.Read(catalog, connection.DisallowedFittedTypesList.Value)
                            : null,
                        g.Key
                    );
                    return new MissionRoomGate(g.FromObjectId, g.DestinationRoomId, g.RequiresClear, access);
                })
                .ToImmutableArray();
            if (
                gates.Any(g =>
                    !objects.Any(o => o.Id == g.ObjectId && o.GroupId == MissionGateRules.AccelerationGateGroup)
                    || !bindings.Any(r => r.RoomId == g.DestinationRoomId)
                )
            )
            {
                throw new InvalidDataException(
                    $"Mission {definition.MissionId}, room {binding.RoomId}: invalid gate source or destination."
                );
            }
            rooms.Add(new(binding.RoomId, offset, objects.ToImmutableArray(), gates) { SpawnAnchors = anchors });
        }
        if (definition.Waves.Any(w => w.RoomId != 0 && !bindings.Any(r => r.RoomId == w.RoomId)))
        {
            throw new InvalidDataException("Mission wave refers to an unavailable room.");
        }
        if (rooms.Sum(r => r.Objects.Length) > MissionRules.NpcsPerMission)
        {
            throw new InvalidDataException("Mission objects exceed their reserved identity range.");
        }
        if (
            definition.Containers.Length > MissionRules.NpcsPerMission
            || definition.Containers.Select(c => c.ObjectId).Distinct().Count() != definition.Containers.Length
        )
        {
            throw new InvalidDataException("Invalid mission container identities.");
        }
        foreach (var container in definition.Containers)
        {
            if (container.Quantity <= 0 || rooms.SelectMany(r => r.Objects).Count(o => o.Id == container.ObjectId) != 1)
            {
                throw new InvalidDataException("Mission container has invalid contents or geometry.");
            }
            _ = catalog.RequireItem(container.TypeId);
        }
        if (
            definition.Structures.Length > MissionRules.NpcsPerMission
            || definition.Structures.Length + definition.Containers.Length > MissionRules.NpcsPerMission
            || definition.Structures.Select(s => s.ObjectId).Distinct().Count() != definition.Structures.Length
        )
        {
            throw new InvalidDataException("Invalid mission structure identities.");
        }
        foreach (var structure in definition.Structures)
        {
            var item = rooms.SelectMany(r => r.Objects).SingleOrDefault(o => o.Id == structure.ObjectId);
            if (
                item is null
                || !MissionStructureRules.IsDestructibleGroup(item.GroupId)
                || definition.Containers.Any(c => c.ObjectId == structure.ObjectId)
                || new NpcProfileCatalog().Read(catalog, item.TypeId).Defense.Structure <= 0
            )
            {
                throw new InvalidDataException("A mission structure needs destructible catalog geometry and positive hull health.");
            }
            if (structure.Drop is { } drop)
            {
                if (drop.TypeId <= 0 || drop.Quantity <= 0)
                {
                    throw new InvalidDataException("Invalid mission structure objective drop.");
                }
                _ = catalog.RequireItem(drop.TypeId);
                _ = catalog.RequireItem(MissionStructureRules.RecoveryContainerType);
            }
        }
        foreach (var group in definition.Waves.SelectMany(w => w.Groups))
        {
            if (group.LootTypeId != 0 || group.LootQuantity != 0)
            {
                if (group.LootTypeId <= 0 || group.LootQuantity <= 0)
                {
                    throw new InvalidDataException("Invalid mission key loot definition.");
                }
                _ = catalog.RequireItem(group.LootTypeId);
            }
        }
        return rooms.ToImmutableArray();
    }

    private static SpacePosition Position(ClientAtomsParametersInputsItemDefaultValueObject position) =>
        new(position.X ?? 0, position.Y ?? 0, position.Z ?? 0);
}
