using System.Collections.Immutable;
using System.Text.Json;
using Eden.Data.Catalog.Exploration;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;

namespace Eden.World.Exploration;

internal static class SleeperSiteCatalog
{
    private const int InvisibleBeaconGroup = 4055;

    private static readonly Lazy<ImmutableArray<AnomalyEncounterReport>> s_reports = new(LoadReports);
    private static readonly CatalogDefinition<ImmutableArray<CombatAnomalyDefinition>> s_definitions = new("sleeper-sites", Load);

    internal static ImmutableArray<CombatAnomalyDefinition> Read(CatalogDbContext catalog) => catalog.Definition(s_definitions, 0);

    private static ImmutableArray<AnomalyEncounterReport> LoadReports()
    {
        using var stream = typeof(SleeperEncounterDefinitions).Assembly.GetManifestResourceStream("Eden.Data.SleeperEncounters.json")!;
        var file = JsonSerializer.Deserialize<SleeperEncounterDefinitions>(stream, JsonSerializerOptions.Web)!;
        if (file.Build != CatalogSources.PinnedBuild || file.Encounters.Any(e => e.WormholeClass is < 1 or > 6))
        {
            throw new InvalidDataException("Invalid pinned Sleeper encounter reports.");
        }
        return file.Encounters;
    }

    private static ImmutableArray<CombatAnomalyDefinition> Load(CatalogDbContext catalog, int unused)
    {
        List<CombatAnomalyDefinition> definitions = [];
        foreach (var report in s_reports.Value.Where(r => r.WormholeKind == WormholeSiteKind.Combat))
        {
            foreach (var (id, variant) in report.DungeonVariants!)
            {
                var dungeon = catalog.Read(CatalogData.Client.Dungeons, (long)id);
                if (dungeon?.EntryTypeID != CombatAnomalyRules.AnomalyTypeId || dungeon.Rooms is not { Count: 1 })
                {
                    continue;
                }
                var room = dungeon.Rooms.Single().Value;
                var entry = room.Objects!.Values.Single(o => o.ObjectID == dungeon.EntryObjectID);
                static SpacePosition Point(Eden.Data.Catalog.ClientAtomsParametersInputsItemDefaultValueObject position) =>
                    new(position.X!.Value, position.Y!.Value, position.Z!.Value);
                var origin = Point(entry.Position!);
                var anchors = room
                    .Objects.Values.Where(o => o.TypeID.HasValue && o.Position is not null)
                    .Select(o => (Source: o, Type: catalog.FittingType(o.TypeID!.Value)))
                    .Where(o => o.Source.Entities is { Count: > 0 } || o.Type?.GroupId == AnomalyEncounterRules.SentryGroup)
                    .Select(o => new AnomalySpawnAnchor(
                        o.Source.ObjectID!.Value,
                        Point(o.Source.Position!) - origin,
                        o.Source.GuardCommand?.AggressionRange is > 0
                            ? o.Source.GuardCommand.AggressionRange.Value
                            : NpcEncounterDefinitions.VisibilityRange,
                        o.Source.GuardCommand?.ObjectTriggerSpawn != 1
                            && (o.Source.Entities is not { Count: > 0 } || o.Source.Entities.Values.Any(e => e.TriggerSpawn != 1)),
                        o.Source.GuardCommand?.ObjectTriggerSpawn == 1 || o.Source.Entities?.Values.Any(e => e.TriggerSpawn == 1) == true,
                        o.Type?.GroupId == AnomalyEncounterRules.SentryGroup ? o.Type.Id : null
                    ))
                    .ToImmutableArray();
                var objects = room
                    .Objects.Values.Where(o => o.TypeID.HasValue && o.Position is not null)
                    .Select(o => (Source: o, Type: catalog.FittingType(o.TypeID!.Value)))
                    .Where(o =>
                        o.Type is { CategoryId: 2 or 25 }
                        && o.Source.ObjectID != dungeon.EntryObjectID
                        && o.Type.GroupId != InvisibleBeaconGroup
                    )
                    .Select(o => new StaticSpaceObject(
                        o.Source.ObjectID!.Value,
                        o.Type!.Id,
                        o.Type.GroupId,
                        o.Type.CategoryId,
                        SleeperRules.SystemOwner,
                        Point(o.Source.Position!) - origin,
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
                var waves = report.Variants[variant];
                if (waves.SelectMany(w => w.Groups).SelectMany(g => g.Types).Any(type => catalog.FittingType(type)?.CategoryId != 11))
                {
                    throw new InvalidDataException($"Sleeper site {id} references an unknown NPC.");
                }
                definitions.Add(
                    new(
                        id,
                        checked((int)dungeon.DungeonNameID!.Value),
                        checked((int)(dungeon.Difficulty ?? 1)),
                        (NpcPirateFaction)SleeperRules.FactionId,
                        SleeperRules.SystemOwner,
                        report.Name,
                        SpacePosition.Zero,
                        waves,
                        objects
                    )
                    {
                        SpawnAnchors = anchors,
                        WormholeClass = report.WormholeClass,
                    }
                );
            }
        }
        return definitions.ToImmutableArray();
    }
}
