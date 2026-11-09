using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;

namespace Eden.World.Exploration;

internal sealed record CombatAnomalyDefinition(
    int DungeonId,
    int NameId,
    int Difficulty,
    NpcPirateFaction Faction,
    int CorporationId,
    string Name,
    SpacePosition Spawner,
    ImmutableArray<AnomalyEncounterWave> Waves,
    ImmutableArray<StaticSpaceObject> Objects,
    bool Reconstructed = false,
    double AcquisitionRange = NpcEncounterDefinitions.VisibilityRange
)
{
    internal ImmutableArray<AnomalySpawnAnchor> SpawnAnchors { get; init; } = [];
    internal ImmutableArray<AnomalyEncounterWave> VersionOneWaves { get; init; } = [];

    internal ImmutableArray<AnomalyEncounterWave> WavesFor(int version) =>
        version == 1 && !VersionOneWaves.IsDefaultOrEmpty ? VersionOneWaves : Waves;

    internal bool RequiresMajorThreat => Name.Contains("Forsaken Sanctum", StringComparison.Ordinal) || Name == "Teeming Drone Horde";
}

internal static class CombatAnomalyCatalog
{
    private const int InvisibleBeaconGroup = 4055;

    private sealed record EncounterFile(int Build, ImmutableArray<AnomalyEncounterReport> Encounters);

    private static readonly Lazy<FrozenDictionary<string, AnomalyEncounterReport>> s_reports = new(LoadReports);
    private static readonly CatalogDefinition<ImmutableArray<CombatAnomalyDefinition>> s_definitions = new(
        "combat-anomaly-definitions",
        Load,
        Estimate: values => 1024 + values.Length * 16384L
    );

    internal static ImmutableArray<CombatAnomalyDefinition> Read(CatalogDbContext catalog) => catalog.Definition(s_definitions, 0);

    internal static CombatAnomalyDefinition? Find(CatalogDbContext catalog, int dungeonId) =>
        Read(catalog).FirstOrDefault(d => d.DungeonId == dungeonId);

    private static FrozenDictionary<string, AnomalyEncounterReport> LoadReports()
    {
        using var stream = typeof(CombatAnomalyCatalog).Assembly.GetManifestResourceStream("Eden.World.AnomalyEncounters.json")!;
        var file = JsonSerializer.Deserialize<EncounterFile>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        if (file.Build != CatalogSources.PinnedBuild)
        {
            throw new InvalidDataException("Anomaly reports target a different client build.");
        }
        return file.Encounters.ToFrozenDictionary(r => r.Name, StringComparer.Ordinal);
    }

    private static ImmutableArray<CombatAnomalyDefinition> Load(CatalogDbContext catalog, int unused)
    {
        List<CombatAnomalyDefinition> definitions = [];
        foreach (var (id, summary) in catalog.Records(CatalogData.Sde.Dungeons).OrderBy(p => p.Key))
        {
            string name = summary.Name?.En?.Replace("Blood Raider ", "Blood ", StringComparison.Ordinal) ?? "";
            if (
                summary.ArchetypeID != CombatAnomalyRules.CombatSiteArchetypeId
                || !s_reports.Value.TryGetValue(name, out var report)
                || report.Variants.IsDefaultOrEmpty
            )
            {
                continue;
            }
            var dungeon = catalog.Read(CatalogData.Client.Dungeons, id);
            if (dungeon?.EntryTypeID != CombatAnomalyRules.AnomalyTypeId || dungeon.Rooms is not { Count: 1 })
            {
                continue;
            }
            var room = dungeon.Rooms.Single().Value;
            var entry = room.Objects?.Values.SingleOrDefault(o => o.ObjectID == dungeon.EntryObjectID);
            if (entry?.Position is null)
            {
                throw new InvalidDataException($"Anomaly {id} has no entry position.");
            }
            var origin = Position(entry.Position);
            var spawner = room.Objects!.Values.FirstOrDefault(o => o.Entities is { Count: > 0 })?.Position;
            var anchors = room
                .Objects.Values.Where(o => o.Position is not null && o.TypeID.HasValue)
                .OrderBy(o => o.ObjectID)
                .Select(o => (Source: o, Type: catalog.FittingType(o.TypeID!.Value)))
                .Where(o => o.Source.Entities is { Count: > 0 } || o.Type?.GroupId == AnomalyEncounterRules.SentryGroup)
                .Select(o => new AnomalySpawnAnchor(
                    o.Source.ObjectID!.Value,
                    Position(o.Source.Position!) - origin,
                    o.Source.GuardCommand?.AggressionRange is > 0
                        ? o.Source.GuardCommand.AggressionRange.Value
                        : NpcEncounterDefinitions.VisibilityRange,
                    o.Source.GuardCommand?.ObjectTriggerSpawn != 1
                        && (o.Source.Entities is not { Count: > 0 } || o.Source.Entities.Values.Any(e => e.TriggerSpawn != 1)),
                    o.Source.GuardCommand?.ObjectTriggerSpawn == 1 || o.Source.Entities?.Values.Any(e => e.TriggerSpawn == 1) == true,
                    o.Type?.GroupId == AnomalyEncounterRules.SentryGroup ? o.Type.Id : null
                ))
                .ToImmutableArray();
            if (report.DungeonVariants is null || !report.DungeonVariants.TryGetValue(checked((int)id), out int variant))
            {
                throw new InvalidDataException($"Anomaly {id} has no explicit report binding.");
            }
            var waves = report.Variants[variant];
            var previousWaves =
                report.VersionOneDungeonVariants?.TryGetValue(checked((int)id), out int previousVariant) == true
                    ? report.Variants[previousVariant]
                    : waves;
            var faction =
                name.StartsWith("Drone", StringComparison.Ordinal) || name.StartsWith("Teeming", StringComparison.Ordinal)
                    ? NpcPirateFaction.RogueDrones
                    : (NpcPirateFaction)(summary.FactionID ?? 0);
            var factionData = catalog.Read(CatalogData.Client.Factions, (long)faction);
            int corporation = checked((int)(factionData?.CorporationID ?? NpcEncounterDefinitions.GuristasCorporation));
            var objects = room
                .Objects.Values.Where(o => o.TypeID.HasValue && o.Position is not null)
                .Select(o => (Source: o, Type: catalog.FittingType(o.TypeID!.Value)))
                .Where(o =>
                    o.Type is not null
                    && o.Type.CategoryId is 2 or 25
                    && o.Source.ObjectID != dungeon.EntryObjectID
                    && o.Type.GroupId != InvisibleBeaconGroup
                )
                .Select(o => new StaticSpaceObject(
                    o.Source.ObjectID!.Value,
                    o.Type!.Id,
                    o.Type.GroupId,
                    o.Type.CategoryId,
                    corporation,
                    Position(o.Source.Position!) - origin,
                    DungeonObjectGeometry.Radius(
                        o.Type.CategoryId,
                        o.Type.Radius,
                        o.Source.Radius,
                        catalog.Read(CatalogData.Client.Types, (long)o.Type.Id)?.Volume ?? 0
                    )
                )
                {
                    IsGlobal = false,
                    Solid = DungeonObjectGeometry.IsSolid(o.Type.GroupId),
                })
                .ToImmutableArray();
            definitions.Add(
                new(
                    checked((int)id),
                    checked((int)dungeon.DungeonNameID!.Value),
                    checked((int)(dungeon.Difficulty ?? 1)),
                    faction,
                    corporation,
                    name,
                    spawner is null ? SpacePosition.Zero : Position(spawner) - origin,
                    waves,
                    objects,
                    report.Reconstructed,
                    room.Objects.Values.Where(o => o.Entities is { Count: > 0 })
                        .Select(o => o.GuardCommand?.AggressionRange)
                        .FirstOrDefault(r => r is > 0)
                        ?? NpcEncounterDefinitions.VisibilityRange
                )
                {
                    SpawnAnchors = anchors,
                    VersionOneWaves = previousWaves,
                }
            );
        }
        return definitions.ToImmutableArray();
    }

    private static SpacePosition Position(ClientAtomsParametersInputsItemDefaultValueObject position) =>
        new(position.X!.Value, position.Y!.Value, position.Z!.Value);
}
