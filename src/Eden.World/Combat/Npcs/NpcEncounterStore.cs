using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Mining;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Mining;
using Eden.World.Propulsion;
using Eden.World.Runtime;
using Eden.World.Runtime.Combat;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Combat.Npcs;

[Injectable]
internal sealed class NpcEncounterStore(
    WorldDatabase database,
    INpcPopulationSeeder? population = null,
    NpcProfileCatalog? hullProfiles = null
) : INpcEncounters, INpcSimulation
{
    public NpcSceneContacts ReadScene(int systemId, SpacePosition position, long now)
    {
        return LiveWorld.Execute(database.Path, systemId, () => ReadSceneOwned(systemId, position, now), nameof(ReadScene));
    }

    private NpcSceneContacts ReadSceneOwned(int systemId, SpacePosition position, long now)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            return LiveCombatRuntime.Find(database.Path)?.ReadScene(systemId, position, now, () => RefreshPopulation(systemId, now))
                ?? NpcSceneContacts.From(List(systemId, now), systemId, position);
        }
    }

    public IReadOnlyList<NpcContact> List(int systemId, long now)
    {
        return LiveWorld.Execute(database.Path, systemId, () => ListOwned(systemId, now), nameof(List));
    }

    private IReadOnlyList<NpcContact> ListOwned(int systemId, long now)
    {
        // Take the resident owner before the shared movement gate. Bot execution
        // already owns the world when it requests these same views.
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            if (
                LiveCombatRuntime
                    .Find(database.Path)
                    ?.Contacts(systemId, now, () => Read(systemId, now), () => RefreshPopulation(systemId, now)) is
                { } resident
            )
            {
                return resident;
            }
            return Read(systemId, now);
        }
    }

    public IReadOnlyList<NpcSimulationContact> Advance(int systemId, long now)
    {
        return LiveWorld.Execute(database.Path, systemId, () => AdvanceOwned(systemId, now), nameof(Advance));
    }

    private IReadOnlyList<NpcSimulationContact> AdvanceOwned(int systemId, long now)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            if (LiveCombatRuntime.Find(database.Path)?.Simulate(systemId, now, () => RefreshPopulation(systemId, now)) is { } resident)
            {
                return resident;
            }
            Read(systemId, now, false);
            return WorldActionScope.Inspect(
                database,
                world =>
                    world
                        .Set<WorldNpc>()
                        .Where(n => n.SystemId == systemId && n.DestroyedAt == null && n.RetiredAt == null)
                        .OrderBy(n => n.Id)
                        .AsEnumerable()
                        .Select(n => new NpcSimulationContact(n.Id, new(n.X, n.Y, n.Z), n.Navigation == NpcNavigationState.Warping))
                        .ToArray()
            );
        }
    }

    private long RefreshPopulation(int systemId, long now)
    {
        if (population is null)
        {
            return long.MaxValue;
        }
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            Read(systemId, now, false);
            return database.PopulationDeadlines[systemId];
        }
        using var phase = SessionPerformance.Phase("resident.npcPopulation");
        long stamp = now / TimeSpan.TicksPerSecond;
        bool seed =
            database.PopulationStamps.GetValueOrDefault(systemId, -1) != stamp
            || database.PopulationDeadlines.GetValueOrDefault(systemId, long.MaxValue) <= now;
        long due;
        if (seed)
        {
            using WorldActionScope transaction = new(database, select: world => PopulationParticipants(world, systemId));
            using CatalogDbContext catalog = new(database.CatalogPath);
            population.Seed(transaction.World, catalog, systemId, now);
            due = population.NextUpdate(transaction.World, systemId, now);
            transaction.Commit();
        }
        else
        {
            var participants = live.Economy.Inspect(world => PopulationParticipants(world, systemId));
            due = live.Economy.Inspect(world => population.NextUpdate(world, systemId, now), participants);
        }
        database.PopulationStamps[systemId] = stamp;
        database.PopulationDeadlines[systemId] = due;
        return due;
    }

    private static TransactionParticipants PopulationParticipants(WorldAction world, int system)
    {
        var npcs = world
            .Set<WorldNpc>()
            .Where(n => n.SystemId == system && n.DestroyedAt == null && n.RetiredAt == null)
            .ToArray()
            .Where(n =>
                NpcPopulationRules.IsMining(n.Role)
                || n.Role == NpcRole.Convoy
                || n.TypeId == NpcEncounterDefinitions.GuristasRookieType
                || n.Role == NpcRole.Pirate && NpcSpecialTypes.Excluded.Contains(n.TypeId)
            )
            .Select(n => new SimulationResource(SharedSimulationResource.Npc, n.Id));
        return new(systems: [system], resources: npcs);
    }

    private IReadOnlyList<NpcContact> Read(int systemId, long now, bool buildContacts = true)
    {
        using WorldActionScope transaction = new(database, select: world => PopulationParticipants(world, systemId));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        long populationStamp = now / TimeSpan.TicksPerSecond;
        if (
            population is not null
            && (
                database.PopulationStamps.GetValueOrDefault(systemId, -1) != populationStamp
                || database.PopulationDeadlines.GetValueOrDefault(systemId, long.MaxValue) <= now
            )
        )
        {
            population.Seed(world, catalog, systemId, now);
        }
        world.SaveChanges();
        var npcs = world
            .Set<WorldNpc>()
            .Where(n => n.SystemId == systemId && n.DestroyedAt == null && n.RetiredAt == null)
            .OrderBy(n => n.Id)
            .ToArray();
        var ids = npcs.Select(n => n.Id).ToArray();
        var mining = world.Set<WorldNpcMiningMember>().Where(m => Enumerable.Contains(ids, m.NpcId)).ToDictionary(m => m.NpcId);
        var ewar = world
            .Set<WorldNpcEwar>()
            .Where(e => Enumerable.Contains(ids, e.NpcId) && e.AppliedJson != null)
            .AsEnumerable()
            .Select(e => e.Contact()!)
            .ToLookup(e => e.SourceId);
        var aggression = world.Set<NpcAggression>().Where(a => Enumerable.Contains(ids, a.NpcId)).ToDictionary(a => a.NpcId);
        var profiles = npcs.Select(n => n.TypeId).Distinct().ToDictionary(id => id, id => ReadProfile(catalog, id));
        NpcWeaponContact? Weapon(WorldNpc npc)
        {
            var attributes = profiles[npc.TypeId].Attributes;
            double duration = attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime);
            return
                aggression.TryGetValue(npc.Id, out var active)
                && active.LastShotAt is long started
                && now >= started
                && now - started < duration * TimeSpan.TicksPerMillisecond
                ? new(
                    active.LastTargetId ?? active.TargetShipId,
                    checked((int)attributes.GetValueOrDefault(DogmaAttributeId.EntityTurretType)),
                    started,
                    duration
                )
                : null;
        }
        var tackle = PilotTackleStore.NpcTackles(world, ids);
        var effective = npcs.ToDictionary(
            n => n.Id,
            n => TackleDefinitions.NpcAttributes(profiles[n.TypeId].Attributes, tackle[n.Id].Select(t => t.Module()).ToArray())
        );
        foreach (var npc in npcs)
        {
            NpcRepairPersistence.Advance(npc, profiles[npc.TypeId].Attributes, now);
            if (NpcMotion.Engaged(npc) && !aggression.ContainsKey(npc.Id))
            {
                NpcMotion.Return(npc, profiles[npc.TypeId].Type.GroupId);
            }
            if (npc.Navigation == NpcNavigationState.Returning && !database.MotionStamps.ContainsKey(systemId))
            {
                NpcMotion.Advance(npc, NpcMotion.Home(npc, catalog), profiles[npc.TypeId].Type, effective[npc.Id], now, returning: true);
            }
        }
        world.SaveChanges();
        decimal bountyMultiplier = database.Settings.Rewards.BountyMultiplier;
        NpcContact[] contacts = buildContacts
            ? npcs.Select(n =>
                {
                    var (type, attributes, defense) = profiles[n.TypeId];
                    return new NpcContact(
                        n.Id,
                        n.TypeId,
                        type.GroupId,
                        type.CategoryId,
                        n.CorporationId,
                        n.Name ?? type.Name,
                        systemId,
                        new(n.X, n.Y, n.Z),
                        type.Mass ?? 0,
                        (float)(type.Radius ?? 0),
                        (float)effective[n.Id].GetValueOrDefault(DogmaAttributeId.MaxVelocity),
                        NpcMotion.Agility(attributes),
                        new(
                            n.Id,
                            defense,
                            new(
                                ChargedResource.Recharged(
                                    n.ShieldCharge,
                                    defense.Shield,
                                    defense.ShieldRechargeMilliseconds,
                                    Math.Max(0, (now - n.UpdatedAt) / (double)TimeSpan.TicksPerMillisecond)
                                ),
                                Math.Max(0, defense.Armor - n.ArmorDamage),
                                Math.Max(0, defense.Structure - n.StructureDamage)
                            )
                        ),
                        Weapon(n),
                        NpcMotion.Contact(
                            n,
                            type.GroupId,
                            effective[n.Id],
                            aggression.GetValueOrDefault(n.Id)?.TargetShipId ?? 0,
                            NpcMotion.Home(n, catalog),
                            now
                        ),
                        database.MotionStamps.GetValueOrDefault(systemId),
                        database.CollisionStamps.GetValueOrDefault(n.Id, -1) == database.MotionStamps.GetValueOrDefault(systemId),
                        n.Role,
                        n.FactionId,
                        n.RepairRevision,
                        ewar[n.Id].Any() ? ewar[n.Id].ToArray() : null,
                        mining
                            .GetValueOrDefault(n.Id)
                            ?.Activity(
                                n,
                                attributes.GetValueOrDefault(DogmaAttributeId.BehaviorMiningDuration),
                                now,
                                checked((int)attributes.GetValueOrDefault(DogmaAttributeId.EntityTurretType))
                            ),
                        mining
                            .GetValueOrDefault(n.Id)
                            ?.Transit(n)
                            ?.Course(
                                database.MotionStamps.GetValueOrDefault(systemId),
                                now,
                                new(
                                    0,
                                    (float)(type.Radius ?? 0),
                                    type.Mass ?? 0,
                                    NpcMotion.Agility(attributes),
                                    (float)effective[n.Id].GetValueOrDefault(DogmaAttributeId.MaxVelocity),
                                    0
                                )
                            )
                    )
                    {
                        AnchorKind = n.AnchorKind,
                        AnchorId = n.BeltId,
                        StandingThresholds = profiles[n.TypeId].StandingThresholds,
                        BountyCents = WorldTuningRules.Reward(NpcBountyRules.BaseCents(n.Role, attributes), bountyMultiplier),
                    };
                })
                .ToArray()
            : [];
        // A route deadline can fall between two reads in the same wall-clock
        // second. Keep the exact due time in addition to the coarse seed stamp.
        long nextPopulation = population?.NextUpdate(world, systemId, now) ?? long.MaxValue;
        transaction.Commit();
        if (population is not null)
        {
            database.PopulationStamps[systemId] = populationStamp;
            database.PopulationDeadlines[systemId] = nextPopulation;
        }
        return contacts;
    }

    private sealed record Profile(CatalogItemDefinition Type, IReadOnlyDictionary<DogmaAttributeId, double> Attributes, HullDefense Defense)
    {
        public NpcStandingThresholds? StandingThresholds { get; init; }
    }

    private Profile ReadProfile(CatalogDbContext catalog, int typeId)
    {
        var profile = (hullProfiles ?? new NpcProfileCatalog(cache: false)).Read(catalog, typeId);
        return new(profile.Type, profile.Attributes, profile.Defense) { StandingThresholds = profile.StandingThresholds };
    }

    public bool WasDestroyed(long npcId) =>
        WorldActionScope.Inspect(database, world => world.Set<WorldNpc>().Find(npcId)?.DestroyedAt is not null);
}
