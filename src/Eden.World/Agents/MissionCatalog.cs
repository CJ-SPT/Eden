using System.Collections.Immutable;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.Catalog.Agents;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;

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
    ImmutableArray<StaticSpaceObject> Objects
)
{
    internal MissionKind Kind => DungeonId.HasValue ? MissionKind.Combat : MissionKind.Courier;
}

internal static class MissionCatalog
{
    private const int InvisibleBeaconGroup = 4055;
    private const int CloudGroup = 227;
    private const int EnvironmentGroup = 1882;
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
    private static readonly CatalogDefinition<ImmutableArray<MissionProfile>> s_profiles = new(
        "agent-missions",
        Load,
        Estimate: profiles => 1024 + profiles.Length * 16384L
    );

    internal static ImmutableArray<MissionProfile> Read(CatalogDbContext catalog) => catalog.Definition(s_profiles, 0);

    internal static MissionProfile Find(CatalogDbContext catalog, int missionId) =>
        Read(catalog).SingleOrDefault(p => p.Definition.MissionId == missionId)
        ?? throw new InvalidDataException("This mission's content is unavailable.");

    private static ImmutableArray<MissionProfile> Load(CatalogDbContext catalog, int unused)
    {
        List<MissionProfile> profiles = [];
        foreach (var definition in s_content.Value.Missions)
        {
            var mission = catalog.Read(CatalogData.Client.Missions, (long)definition.MissionId);
            if (mission is null)
            {
                continue;
            }
            int nameId = checked((int)(mission.NameID ?? 0));
            int briefingId = checked((int)(mission.Messages?.GetValueOrDefault("messages.mission.briefing") ?? 0));
            string name =
                catalog.Read(CatalogData.Client.LocalizationFsdEnUs, (long)nameId)?.Text
                ?? catalog.Read(CatalogData.Sde.Missions, (long)definition.MissionId)?.Name?.En
                ?? "Mission";
            string briefing = catalog.Read(CatalogData.Client.LocalizationFsdEnUs, (long)briefingId)?.Text ?? name;
            int? dungeonId = checked((int?)mission.KillMission?.DungeonID);
            int faction = 0;
            int corporation = 0;
            List<StaticSpaceObject> objects = [];
            if (dungeonId is int id)
            {
                var dungeon = catalog.Require(CatalogData.Client.Dungeons, (long)id);
                if (dungeon.Rooms is not { Count: 1 } || definition.Waves.IsDefaultOrEmpty)
                {
                    throw new InvalidDataException($"Mission {definition.MissionId} needs unsupported room handling.");
                }
                faction = checked((int)(dungeon.FactionID ?? 0));
                corporation = checked((int)(catalog.Read(CatalogData.Client.Factions, (long)faction)?.CorporationID ?? 0));
                var room = dungeon.Rooms.Single().Value;
                var entry = room.Objects!.Values.Single(o => o.ObjectID == dungeon.EntryObjectID);
                var origin = Position(entry.Position!);
                foreach (var item in room.Objects.Values.Where(o => o.TypeID.HasValue && o.Position is not null))
                {
                    var type = catalog.FittingType(item.TypeID!.Value);
                    // NPC containers, gates and destructible structures need their own mechanics.
                    // Keep inert scenery only; authored NPC groups are spawned separately.
                    if (
                        type is null
                        || type.CategoryId is not (2 or 25)
                        || type.GroupId == InvisibleBeaconGroup
                        || item.ObjectID == dungeon.EntryObjectID
                        || item.Entities is { Count: > 0 }
                    )
                    {
                        continue;
                    }
                    objects.Add(
                        new(
                            item.ObjectID!.Value,
                            type.Id,
                            type.GroupId,
                            type.CategoryId,
                            corporation,
                            Position(item.Position!) - origin,
                            (float)(item.Radius ?? type.Radius ?? 0)
                        )
                        {
                            IsGlobal = false,
                            Solid = type.GroupId is not (CloudGroup or EnvironmentGroup),
                        }
                    );
                }
            }
            int cargoType = checked((int)(mission.CourierMission?.ObjectiveTypeID ?? 0));
            int quantity = checked((int)(mission.CourierMission?.ObjectiveQuantity ?? 0));
            double volume = cargoType == 0 ? 0 : catalog.Read(CatalogData.Client.Types, (long)cargoType)?.Volume ?? 0;
            if (!dungeonId.HasValue && (cargoType == 0 || quantity <= 0 || volume <= 0))
            {
                continue;
            }
            profiles.Add(
                new(
                    definition,
                    name,
                    nameId,
                    briefingId,
                    briefing,
                    dungeonId,
                    faction,
                    corporation,
                    cargoType,
                    quantity,
                    volume,
                    objects.ToImmutableArray()
                )
            );
        }
        return profiles.ToImmutableArray();
    }

    private static SpacePosition Position(ClientAtomsParametersInputsItemDefaultValueObject position) =>
        new(position.X ?? 0, position.Y ?? 0, position.Z ?? 0);
}
