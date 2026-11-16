using System.Collections.Immutable;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Exploration;

internal sealed partial class CombatAnomalyStore
{
    internal static long ResidentVersion(string path, int system) => s_versions.GetValueOrDefault((path, system));

    private static SavedSleeperSite SleeperState(WorldAction world, WorldCombatAnomaly site)
    {
        if (world is not EconomyTransaction)
        {
            throw new InvalidOperationException("Sleeper sites require the resident world.");
        }
        return world.Set<SavedSleeperSite>().Find(site.Id)
            ?? world.Add(new SavedSleeperSite { Id = site.Id, SystemId = site.SystemId }).Entity;
    }

    internal static WorldCombatAnomaly EnsureGuarded(
        WorldAction world,
        CatalogDbContext catalog,
        long parent,
        int system,
        int dungeon,
        SpacePosition position
    )
    {
        if (world is not EconomyTransaction)
        {
            throw new InvalidOperationException("Guarded wormhole sites require the resident world.");
        }
        var existing = world.Filter<SavedSleeperSite>(s => s.ParentSiteId == parent).SingleOrDefault();
        if (existing is not null)
        {
            return world.Set<WorldCombatAnomaly>().Find(existing.Id)!;
        }
        var definition = CombatAnomalyCatalog.Find(catalog, dungeon);
        if (definition is not { WormholeClass: > 0 })
        {
            throw new InvalidDataException("A guarded site has no pinned Sleeper encounter.");
        }
        var site = new WorldCombatAnomaly
        {
            Id = NextSiteId(world),
            SystemId = system,
            DungeonId = dungeon,
            Slot = SleeperRules.GuardedSiteSlot,
            IsSignature = true,
            DefinitionVersion = AnomalyEncounterRules.CurrentDefinitionVersion,
            X = position.X,
            Y = position.Y,
            Z = position.Z,
        };
        world.Add(site);
        SleeperState(world, site).ParentSiteId = parent;
        return site;
    }

    internal static WorldCombatAnomaly? Guarded(WorldAction world, long parent) =>
        world is EconomyTransaction && world.Filter<SavedSleeperSite>(s => s.ParentSiteId == parent).SingleOrDefault() is { } state
            ? world.Set<WorldCombatAnomaly>().Find(state.Id)
            : null;

    internal static long AdvanceGuarded(WorldAction world, CatalogDbContext catalog, long parent, long now)
    {
        var site = Guarded(world, parent);
        return site is null ? long.MaxValue : AdvanceSleeper(world, catalog, site, now);
    }

    internal static IReadOnlyList<StaticSpaceObject> GuardedObjects(WorldCombatAnomaly site, CombatAnomalyDefinition definition) =>
        Contact(site, definition).Objects;

    private static long AdvanceSleeper(WorldAction world, CatalogDbContext catalog, WorldCombatAnomaly site, long now)
    {
        var state = SleeperState(world, site);
        if (site.CompletedAt.HasValue)
        {
            return long.MaxValue;
        }
        if (state.ExpiresAt is long expiry && expiry <= now)
        {
            RetireSleeper(world, site, expiry);
            return long.MaxValue;
        }
        while (site.NextWaveAt is long due && due <= now)
        {
            site.NextWaveAt = null;
            NextWave(world, catalog, site, due);
            Invalidate(world, site.SystemId);
        }
        return Math.Min(state.ExpiresAt ?? long.MaxValue, site.NextWaveAt ?? long.MaxValue);
    }

    private static void ActivateSleeper(WorldAction world, CatalogDbContext catalog, WorldCombatAnomaly site, long now)
    {
        var state = SleeperState(world, site);
        if (site.CompletedAt.HasValue || state.ActivatedAt.HasValue)
        {
            return;
        }
        state.ActivatedAt = now;
        state.ExpiresAt = checked(now + SleeperRules.SiteLifetime);
        var definition = CombatAnomalyCatalog.Find(catalog, site.DungeonId)!;
        if (definition.WormholeKind == WormholeSiteKind.Gas && site.Wave < 0)
        {
            if (definition.ArrivalDelayMinimum < 0 || definition.ArrivalDelayMaximum < definition.ArrivalDelayMinimum)
            {
                throw new InvalidDataException("Invalid native gas defender delay range.");
            }
            int delay =
                definition.ArrivalDelayMinimum
                + (int)(
                    NpcSpawnRules.Seed(site.SystemId, site.Id) % (uint)(definition.ArrivalDelayMaximum - definition.ArrivalDelayMinimum + 1)
                );
            site.NextWaveAt = checked(now + delay * TimeSpan.TicksPerSecond);
        }
        Invalidate(world, site.SystemId);
    }

    private static void ArriveSleeper(WorldAction world, CatalogDbContext catalog, WorldCombatAnomaly site, long now)
    {
        ActivateSleeper(world, catalog, site, now);
        AdvanceSleeper(world, catalog, site, now);
        if (site.Wave < 0 && site.NextWaveAt is null && site.CompletedAt is null)
        {
            NextWave(world, catalog, site, now);
        }
    }

    internal static void RetireGuarded(WorldAction world, long parent, long now)
    {
        if (Guarded(world, parent) is { } site)
        {
            RetireSleeper(world, site, now);
        }
    }

    private static void RetireSleeper(WorldAction world, WorldCombatAnomaly site, long now)
    {
        Complete(site, now);
        foreach (var npc in world.Filter<WorldNpc>(n => n.EncounterId == site.Id && n.DestroyedAt == null && n.RetiredAt == null))
        {
            npc.RetiredAt = now;
        }
        Invalidate(world, site.SystemId);
        Invalidate(world, -1);
    }

    internal static void HackSignal(WorldAction world, CatalogDbContext catalog, long parent, SleeperWaveTrigger signal, long now)
    {
        if (Guarded(world, parent) is not { CompletedAt: null } site)
        {
            return;
        }
        ArriveSleeper(world, catalog, site, now);
        if (site.CompletedAt.HasValue)
        {
            return;
        }
        var state = SleeperState(world, site);
        switch (signal)
        {
            case SleeperWaveTrigger.HackStarted:
                state.HackStarts++;
                break;
            case SleeperWaveTrigger.HackFailed:
                state.HackFailures++;
                state.HackResolutions++;
                break;
            case SleeperWaveTrigger.HackResolved:
                state.HackResolutions++;
                break;
        }
        var waves = CombatAnomalyCatalog.Find(catalog, site.DungeonId)!.Waves;
        if (site.Wave + 1 < waves.Length && waves[site.Wave + 1].Trigger != SleeperWaveTrigger.Defeat)
        {
            NextWave(world, catalog, site, now);
        }
        Invalidate(world, site.SystemId);
    }

    private static bool AllowsSleeperWave(WorldAction world, WorldCombatAnomaly site, AnomalyEncounterWave wave) =>
        wave.Trigger == SleeperWaveTrigger.Defeat
        || world.Set<SavedSleeperSite>().Find(site.Id) is { } state
            && (
                wave.Trigger switch
                {
                    SleeperWaveTrigger.HackStarted => state.HackStarts > 0,
                    SleeperWaveTrigger.HackFailed => state.HackFailures > 0,
                    SleeperWaveTrigger.HackResolved => state.HackResolutions >= SleeperRules.HackThreshold(state.ParentSiteId ?? site.Id),
                    _ => false,
                }
            );

    internal static void SleeperAggressed(WorldAction world, CatalogDbContext catalog, WorldNpc npc, long now)
    {
        if (
            npc.FactionId != SleeperRules.FactionId
            || npc.AnchorKind != NpcAnchorKind.Anomaly
            || world.Set<WorldCombatAnomaly>().Find(npc.EncounterId) is not { CompletedAt: null, Wave: >= 0 } site
        )
        {
            return;
        }
        var definition = CombatAnomalyCatalog.Find(catalog, site.DungeonId)!;
        var wave = definition.Waves[site.Wave];
        if (!wave.Groups.Any(g => g.SpawnCondition == SleeperSpawnCondition.Aggression))
        {
            return;
        }
        var state = SleeperState(world, site);
        if (state.AggressedWave == site.Wave || world.Set<WorldAnomalyMember>().Find(npc.Id)?.Wave != site.Wave)
        {
            return;
        }
        state.AggressedWave = site.Wave;
        var conditional = wave with
        {
            Groups = wave.Groups.Where(g => g.SpawnCondition == SleeperSpawnCondition.Aggression).ToImmutableArray(),
        };
        var members = AnomalyEncounterRules.Members(site.Id, site.Wave, conditional, new(false, true));
        if (members.Count > 0)
        {
            Spawn(world, catalog, site, members, now, SleeperRules.ConditionalNpcSlot);
            Invalidate(world, site.SystemId);
        }
    }

    public void Activate(int systemId, SpacePosition target, long now)
    {
        if (!WormholeRules.IsWormholeSystem(systemId))
        {
            return;
        }
        LiveWorld.Execute(
            database.Path,
            systemId,
            () =>
            {
                var owner =
                    LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Sleeper sites require the resident world.");
                using var ownership = owner.Enter();
                var nearby = List(systemId, now)
                    .Concat(Signatures(systemId, now))
                    .Where(s => s.FactionId == SleeperRules.FactionId && s.Position.DistanceTo(target) <= CombatAnomalyRules.ArrivalRange)
                    .ToArray();
                using var transaction = owner.Economy.Begin(new(systems: [systemId]));
                using CatalogDbContext catalog = new(database.CatalogPath);
                foreach (var contact in nearby)
                {
                    var site = transaction.Set<WorldCombatAnomaly>().Find(contact.Id) ?? Guarded(transaction, contact.Id);
                    if (site is not null)
                    {
                        ActivateSleeper(transaction, catalog, site, now);
                    }
                }
                transaction.Commit();
            },
            nameof(Activate)
        );
    }
}
