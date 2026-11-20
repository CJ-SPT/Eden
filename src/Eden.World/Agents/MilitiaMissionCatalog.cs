using System.Collections.Immutable;
using Eden.Data.Catalog.Agents;
using Eden.Data.Catalog.FactionWarfare;
using Eden.Data.World.Agents;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.FactionWarfare;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Agents;

internal static class MilitiaMissionCatalog
{
    private const double EscortRange = 25_000;
    private static readonly IReadOnlyDictionary<int, MilitiaMissionBinding> Bindings = MilitiaMissionBindings.All.ToDictionary(row =>
        row.MissionId
    );

    internal static bool IsMilitia(int missionId) => Bindings.ContainsKey(missionId);

    internal static MilitiaFaction? Issuer(int missionId) =>
        Bindings.TryGetValue(missionId, out var entry) ? MilitiaRules.Opponent(entry.Enemy) : null;

    internal static bool IsMilitia(WorldAction world, WorldNpc npc) =>
        npc.AnchorKind == NpcAnchorKind.Mission
        && world.Set<SavedMission>().Find(npc.EncounterId - MissionRules.FirstSiteId) is { } mission
        && IsMilitia(mission.MissionId);

    internal static IEnumerable<int> ForAgent(CatalogAgent agent) =>
        agent.FactionId is int faction
            ? MilitiaMissionBindings
                .All.Where(row => row.Level == agent.Level && (int)MilitiaRules.Opponent(row.Enemy) == faction)
                .Select(row => row.MissionId)
            : [];

    internal static int BeaconType(int level) =>
        level switch
        {
            1 => 75918, // Small Navy Military Beacon.
            2 => 75920, // Medium Navy Military Beacon.
            3 => 75922, // Large Navy Military Beacon.
            4 => 75923, // Large Advanced Military Beacon.
            _ => throw new ArgumentOutOfRangeException(nameof(level)),
        };

    internal static MissionDefinition? Load(CatalogDbContext catalog, int missionId)
    {
        if (
            !Bindings.TryGetValue(missionId, out var binding)
            || catalog.Read(CatalogData.Client.Missions, (long)missionId)?.KillMission is not { DungeonID: long dungeonId }
            || catalog.Read(CatalogData.Client.Dungeons, dungeonId) is not { Rooms.Count: 2, Connections.Length: 1 } dungeon
        )
        {
            return null;
        }
        var gate = dungeon.Connections.Value.Single();
        if (
            dungeon.EntryObjectID is not long entry
            || gate.FromObjectID is not long source
            || dungeon.FactionID != (int)binding.Enemy
            || dungeon.Difficulty != binding.Level
        )
        {
            throw new InvalidDataException($"Invalid militia mission layout for {missionId}.");
        }
        var entrance = dungeon.Rooms.Single(room => room.Value.Objects!.Values.Any(o => o.ObjectID == entry));
        var combat = dungeon.Rooms.Single(room => room.Key != entrance.Key);
        var landing =
            combat.Value.Objects!.Values.Where(o => o.TypeID == binding.LandingBeacon).Min(o => o.ObjectID)
            ?? throw new InvalidDataException("The militia mission has no combat-room arrival beacon.");
        int roomId = int.Parse(combat.Key, System.Globalization.CultureInfo.InvariantCulture);
        int entranceId = int.Parse(entrance.Key, System.Globalization.CultureInfo.InvariantCulture);

        bool structure = binding.Level is 2 or 3;
        ImmutableArray<MissionStructureDefinition> structures = structure
            ? [new(combat.Value.Objects.Values.Single(o => o.TypeID == binding.Objective).ObjectID!.Value, true, true)]
            : [];
        List<MissionSpawnGroup> groups =
        [
            new([binding.Frigate], binding.Level + 1, EscortRange, Required: false),
            new([binding.Escort], Math.Max(1, binding.Level - 1), EscortRange, Required: false),
        ];
        if (!structure)
        {
            groups.Add(new([binding.Objective], 1, EscortRange, CompletesMission: true));
        }

        return new(
            missionId,
            binding.Level,
            binding.Level,
            false,
            "Pinned militia layouts",
            "3662168",
            [new(groups.ToImmutableArray(), roomId)]
        )
        {
            Rooms = [new(entranceId, entry), new(roomId, landing)],
            Gates = [new(source, roomId)],
            Structures = structures,
        };
    }
}
