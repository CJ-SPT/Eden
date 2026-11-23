using System.Collections.Frozen;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Storage;

namespace Eden.World.Exploration;

internal sealed record WormholeType(
    int Id,
    int TargetClass,
    double LifetimeMinutes,
    double TotalMass,
    double Regeneration,
    double JumpMass
);

internal sealed record WormholeSystem(int Id, int Class, SpacePosition Anchor);

internal sealed record WormholeSlot(long Id, int Source, int Type, int InitialDestination = 0);

internal sealed record WormholeAuthority(
    FrozenDictionary<int, WormholeType> Types,
    FrozenDictionary<int, WormholeSystem> Systems,
    FrozenDictionary<int, WormholeSystem[]> Classes,
    FrozenDictionary<int, WormholeSlot[]> Slots
);

internal static class WormholeCatalog
{
    private static readonly CatalogDefinition<WormholeAuthority> s_authority = new("wormhole-authority", Load);

    internal static WormholeAuthority Read(CatalogDbContext catalog) => catalog.GlobalDefinition(s_authority);

    private static WormholeAuthority Load(CatalogDbContext catalog, int unused)
    {
        Dictionary<int, WormholeType> types = [];
        foreach (
            var type in catalog.Definitions.Items.Values.Where(t => t.GroupId == WormholeRules.Group && t.Id != WormholeRules.ExitType)
        )
        {
            var attributes = catalog.FittingAttributes(type.Id);
            double Attribute(int id) => attributes.GetValueOrDefault((DogmaAttributeId)id);
            var profile = new WormholeType(
                type.Id,
                (int)Attribute(WormholeRules.TargetClassAttribute),
                Attribute(WormholeRules.LifetimeAttribute),
                Attribute(WormholeRules.TotalMassAttribute),
                Attribute(WormholeRules.RegenerationAttribute),
                Attribute(WormholeRules.JumpMassAttribute)
            );
            if (profile is { TargetClass: >= 1 and <= 9, LifetimeMinutes: > 0, TotalMass: > 0, JumpMass: > 0 })
            {
                types.Add(type.Id, profile);
            }
        }
        var anchors = catalog.FirstCelestialPositions();
        Dictionary<int, WormholeSystem> systems = [];
        foreach (var (id, source) in catalog.Records(CatalogData.Client.Systems))
        {
            if (source.WormholeClassID is not (>= 1 and <= 9) || catalog.FindSystem((int)id) is not { } mapped)
            {
                continue;
            }
            if (source.WormholeClassID >= 7 && !Enum.IsDefined((NpcRegion)mapped.RegionId))
            {
                continue;
            }
            if (anchors.TryGetValue((int)id, out var anchor))
            {
                systems.Add((int)id, new((int)id, (int)source.WormholeClassID.Value, anchor));
            }
        }
        var classes = systems.Values.GroupBy(s => s.Class).ToFrozenDictionary(g => g.Key, g => g.OrderBy(s => s.Id).ToArray());
        Dictionary<int, WormholeSlot[]> slots = [];
        foreach (var system in systems.Values)
        {
            // Eden population policy, not a claim about Tranquility's per-system static assignments.
            // Types are build-3662168 group 988; their target classes and all limits come from dogma.
            int[] candidates = system.Class switch
            {
                1 => [30666, 30667, 30668], // N110/J244/Z060: one known-space static.
                2 => [30677, 30678, 30679], // B274/A239/E545 plus a J-space static below.
                3 => [30686, 30687, 30688], // S047/U210/K346.
                4 => [30690, 30691, 30692, 30693, 30694], // N766/C247/X877/H900/U574.
                5 => [30702, 30703], // H296/V753.
                6 => [30711, 30712], // V911/W237.
                7 => [30579, 30583, 30584, 30644, 30645], // Z971/R943/X702/M555/B041 wanderers.
                8 => [30579, 30583, 30584, 30675, 30676],
                9 => [30579, 30583, 30584, 30643, 30646],
                _ => [],
            };
            var available = candidates
                .Where(t =>
                    types.TryGetValue(t, out var p)
                    && classes.TryGetValue(p.TargetClass, out var targets)
                    && targets.Any(target => target.Id != system.Id)
                )
                .ToArray();
            List<WormholeSlot> selected = [];
            int preferred = system.Id switch
            {
                30002813 => 30583,
                30001398 => 30584,
                30005000 => 30675,
                _ => 0,
            };
            int initial = system.Id switch
            {
                30002813 => 31000356,
                30001398 => 31000881,
                30005000 => 31001880,
                _ => 0,
            };
            if (preferred != 0 && available.Contains(preferred))
            {
                selected.Add(new((long)system.Id * 10, system.Id, preferred, initial));
            }
            else if (available.Length > 0)
            {
                selected.Add(new((long)system.Id * 10, system.Id, available[system.Id % available.Length]));
            }
            if (system.Class is 2 or 4)
            {
                int[] second = system.Class == 2 ? [30671, 30672, 30673, 30674, 30675, 30676] : [30689, 30690, 30691, 30692, 30693, 30694];
                var extra = second
                    .Where(t =>
                        types.TryGetValue(t, out var p)
                        && classes.TryGetValue(p.TargetClass, out var targets)
                        && targets.Any(target => target.Id != system.Id)
                        && selected.All(s => s.Type != t)
                    )
                    .ToArray();
                if (extra.Length > 0)
                {
                    selected.Add(new((long)system.Id * 10 + 1, system.Id, extra[system.Id % extra.Length]));
                }
            }
            slots.Add(system.Id, selected.ToArray());
        }
        return new(types.ToFrozenDictionary(), systems.ToFrozenDictionary(), classes, slots.ToFrozenDictionary());
    }
}
