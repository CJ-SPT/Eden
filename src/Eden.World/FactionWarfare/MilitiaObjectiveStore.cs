using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.FactionWarfare;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.World.FactionWarfare;

[Injectable(InjectionType.Singleton)]
internal sealed class MilitiaObjectiveStore(WorldDatabase database, NpcProfileCatalog profiles) : IMilitiaObjectiveDeployment
{
    private const int FirstDepot = 72850;
    private const int FirstCache = 73012;
    private const int FirstRendezvous = 9908;
    private const int SupplyDungeon = 9736;
    private const int ObjectiveArchetype = 67;
    private const int RendezvousWing = 3;

    // Order is Amarr, Minmatar, Caldari, Gallente in the component records.
    private static readonly int[] CacheLabels = [636473, 636474, 636475, 636472];
    private static readonly int[] DepotLabels = [636471, 636463, 636470, 636469];
    private static readonly TimeSpan ReinforcementInterval = TimeSpan.FromMinutes(3);
    private static readonly MilitiaObjectiveKind[] SiteKinds =
    [
        MilitiaObjectiveKind.SupplyCache,
        MilitiaObjectiveKind.SupplyDepot,
        MilitiaObjectiveKind.Rendezvous,
    ];
    private static readonly CatalogDefinition<SiteProfile?> SiteProfiles = new("militia-advantage-objectives", LoadProfile);

    private sealed record DeploymentSample(int SystemId, long ShipId, long ConnectionId, long At);

    // Kept under the resident owner. A reconnect cannot earn the unobserved interval.
    private readonly Dictionary<long, DeploymentSample> _samples = [];

    private sealed record SiteProfile(int DungeonId, int NameId, int Difficulty);

    private sealed record Site(long Slot, long Id, int Generation, MilitiaFaction Faction, MilitiaObjectiveKind Kind);

    public IReadOnlyList<CombatAnomaly> Sites(int system, long now, bool signatures = false)
    {
        if (!FactionWarzoneCatalog.Systems.ContainsKey(system) || LiveWorld.Find(database.Path) is not { } live)
        {
            return [];
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return live.Economy.Inspect(world =>
            Sites(world, system, now)
                .Where(site => (site.Kind == MilitiaObjectiveKind.Rendezvous) == signatures)
                .Select(site => View(catalog, system, site))
                .OfType<CombatAnomaly>()
                .ToArray()
        );
    }

    public void Advance(int system, IReadOnlyList<MilitiaComplexPilot> pilots, long now)
    {
        if (!FactionWarzoneCatalog.Systems.ContainsKey(system) || LiveWorld.Find(database.Path) is not { } live)
        {
            return;
        }
        using var owner = live.EnterPersistence("militia objectives", TransactionParticipants.None);
        var sites = live.Economy.Inspect(world =>
            Sites(world, system, now)
                .Where(site =>
                    pilots.Any(p => p.Position.DistanceTo(MilitiaObjectiveRules.Grid(site.Id)) <= MilitiaObjectiveRules.ObjectiveRange)
                )
                .ToArray()
        );
        var deployments = live.Economy.Inspect(world =>
            world.Filter<SavedMilitiaObjective>(row => row.SystemId == system && row.PilotId != null).Select(row => row.Id).ToArray()
        );
        foreach (
            long id in _samples
                .Where(entry => entry.Value.SystemId == system && !deployments.Contains(entry.Key))
                .Select(entry => entry.Key)
                .ToArray()
        )
        {
            _samples.Remove(id);
        }
        if (sites.Length == 0 && deployments.Length == 0)
        {
            return;
        }
        long[] targets = live.Economy.Inspect(world =>
            sites.Select(s => s.Slot).Concat(deployments).SelectMany(id => Npcs(world, id)).Where(n => n.Active).Select(n => n.Id).ToArray()
        );
        using CatalogDbContext catalog = new(database.CatalogPath);
        using var action = live.Economy.Begin(
            new(
                pilots: pilots.Select(p => p.PilotId),
                ships: pilots.Select(p => p.ShipId),
                resources: targets.Select(id => new SimulationResource(SharedSimulationResource.Npc, id))
            )
        );
        foreach (var site in sites)
        {
            if (View(catalog, system, site) is not null)
            {
                OpenSite(action, catalog, system, site, pilots, now);
            }
        }
        foreach (long id in deployments)
        {
            AdvanceDeployment(action, catalog, action.Set<SavedMilitiaObjective>().Find(id)!, pilots, now);
        }
        action.SaveChanges();
        action.Commit();
    }

    private static IEnumerable<Site> Sites(WorldAction world, int system, long now)
    {
        if (
            FactionWarzoneStore.System(world, system) is not { PendingTransferAt: null } zone
            || zone.Adjacency == WarzoneAdjacency.Rearguard
        )
        {
            yield break;
        }
        foreach (var kind in SiteKinds)
        {
            var first = zone.Warzone.FirstFaction;
            var second = zone.Warzone.SecondFaction;
            foreach (var faction in new[] { first, second })
            {
                if (kind == MilitiaObjectiveKind.Rendezvous && faction != zone.Occupier)
                {
                    continue;
                }
                long slot = MilitiaObjectiveRules.Slot(system, kind, faction);
                var state = world.Set<SavedMilitiaObjective>().Find(slot);
                if (state?.ReplaceAt > now)
                {
                    continue;
                }
                int generation = (state?.Generation ?? 0) + (state?.ReplaceAt is null ? 0 : 1);
                yield return new(slot, MilitiaObjectiveRules.Site(slot, generation), generation, faction, kind);
            }
        }
    }

    private static SiteProfile? LoadProfile(CatalogDbContext catalog, int type)
    {
        var dungeon = catalog.Read(CatalogData.Client.Dungeons, (long)type);
        return dungeon is null ? null : new(type, (int)dungeon.DungeonNameID!.Value, (int)(dungeon.Difficulty ?? 1));
    }

    private static CombatAnomaly? View(CatalogDbContext catalog, int system, Site site)
    {
        int empire = MilitiaAdvantageRules.EmpireIndex(site.Faction);
        int dungeon = site.Kind == MilitiaObjectiveKind.Rendezvous ? FirstRendezvous + empire : SupplyDungeon;
        if (catalog.Definition(SiteProfiles, dungeon) is not { } profile)
        {
            return null;
        }
        string target = $"F{(char)('S' + (int)site.Kind)}{(char)('A' + empire)}-{site.Generation % 1000:000}";
        int name = site.Kind switch
        {
            MilitiaObjectiveKind.SupplyCache => CacheLabels[empire],
            MilitiaObjectiveKind.SupplyDepot => DepotLabels[empire],
            _ => profile.NameId,
        };
        return new(
            site.Id,
            target,
            system,
            profile.DungeonId,
            name,
            profile.Difficulty,
            MilitiaObjectiveRules.Grid(site.Id),
            false,
            [],
            (int)site.Faction,
            IsSignature: site.Kind == MilitiaObjectiveKind.Rendezvous
        )
        {
            ArchetypeId = ObjectiveArchetype,
        };
    }

    private void OpenSite(
        WorldAction world,
        CatalogDbContext catalog,
        int system,
        Site site,
        IReadOnlyList<MilitiaComplexPilot> pilots,
        long now
    )
    {
        var state = world.Set<SavedMilitiaObjective>().Find(site.Slot);
        var position = MilitiaObjectiveRules.Grid(site.Id);
        var target = Enemy(world, system, site.Faction, position, pilots);
        if (state is null || state.ReplaceAt is not null)
        {
            state ??= world
                .Add(
                    new SavedMilitiaObjective
                    {
                        Id = site.Slot,
                        SystemId = system,
                        Kind = site.Kind,
                        Faction = site.Faction,
                    }
                )
                .Entity;
            state.Generation = site.Generation;
            state.ReplaceAt = null;
            int index = MilitiaAdvantageRules.EmpireIndex(state.Faction);
            if (site.Kind != MilitiaObjectiveKind.Rendezvous)
            {
                int type = (site.Kind == MilitiaObjectiveKind.SupplyCache ? FirstCache : FirstDepot) + index;
                state.TargetId = Spawn(world, catalog, state, position, type, NpcRole.MilitiaObjective, 0, now).Id;
            }
            for (int i = 0; i < RendezvousWing; i++)
            {
                int type = MilitiaComplexCatalog.DefenderType(site.Faction, (MilitiaComplexFamily)Math.Min(i + 1, 3));
                var npc = Spawn(
                    world,
                    catalog,
                    state,
                    position + new SpacePosition(0, 0, (i + 1) * NpcSpawnRules.MemberSpacing),
                    type,
                    NpcRole.MilitiaComplexDefender,
                    i + 1,
                    now
                );
                Target(world, catalog, npc.Id, target, now);
            }
        }
        else if (target is not null)
        {
            foreach (var npc in Npcs(world, state.Id).Where(n => n.Active && n.Role == NpcRole.MilitiaComplexDefender))
            {
                Target(world, catalog, npc.Id, target, now);
            }
        }
    }

    public IReadOnlyList<MilitiaDeployment> Deploy(long pilotId, long shipId, IReadOnlyList<long> items, long now)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Militia deployment requires a resident world.");
        using var owner = live.EnterPersistence("militia deployable", new(pilots: [pilotId], ships: [shipId]));
        using var action = live.Economy.Begin();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = action.Pilots.Find(pilotId);
        if (
            pilot?.ActiveShipId != shipId
            || pilot.ActiveShip.Position is not { SystemId: int system } at
            || FactionWarzoneStore.System(action, system) is not { PendingTransferAt: null } zone
            || MilitiaEnlistmentStore.Effective(action, pilot) is not { } faction
            || faction != zone.Warzone.FirstFaction && faction != zone.Warzone.SecondFaction
        )
        {
            throw new InvalidDataException("Deploy from your active ship while enlisted in this warzone.");
        }
        if (
            catalog.Read(CatalogData.Client.Systems, (long)system)?.SecurityStatus > MilitiaObjectiveRules.MaximumSecurity
            || PilotSkillReader.Level(action, catalog, pilotId, MilitiaObjectiveRules.AnchoringSkill)
                < MilitiaObjectiveRules.RequiredAnchoringLevel
        )
        {
            throw new InvalidDataException("This deployment requires Anchoring III and sufficiently low system security.");
        }
        action.RequireUncloaked(shipId);
        List<MilitiaDeployment> result = [];
        foreach (long itemId in items.Distinct())
        {
            var item = action.Set<OwnedItem>().Find(itemId);
            if (
                item is not { Quantity: > 0, FlagId: (int)InventoryFlag.Cargo }
                || item.OwnerId != pilotId
                || item.LocationId != shipId
                || MilitiaObjectiveRules.Deployment(item.TypeId, faction) is not { } kind
            )
            {
                throw new InvalidDataException("Choose a matching militia deployable from your active ship's cargo.");
            }
            var position = new SpacePosition(at.X + 1_250, at.Y, at.Z);
            if (
                catalog
                    .CelestialsInSystem(system)
                    .Any(row =>
                        row.GroupId is 10 or 15 && position.DistanceTo(new(row.X, row.Y, row.Z)) < MilitiaObjectiveRules.DeploymentSpacing
                    )
            )
            {
                throw new InvalidDataException("Move farther from stations and stargates before deploying.");
            }
            var active = action
                .Filter<SavedMilitiaObjective>(row =>
                    row.SystemId == system && row.PilotId != null && row.Kind == kind && row.ExpiresAt > now
                )
                .ToArray();
            if (
                active.Length >= MilitiaObjectiveRules.MaximumDeployments
                || active.Any(row =>
                    row.TargetId is long target
                    && action.Set<WorldNpc>().Find(target) is { DestroyedAt: null, RetiredAt: null } npc
                    && position.DistanceTo(Position(npc)) < MilitiaObjectiveRules.DeploymentSpacing
                )
            )
            {
                throw new InvalidDataException("This system or grid is already at its limit for this militia deployable.");
            }
            var state = new SavedMilitiaObjective
            {
                SystemId = system,
                Faction = faction,
                Kind = kind,
                PilotId = pilotId,
                ReadyAt = now + MilitiaObjectiveRules.DeploymentActivation.Ticks,
                ExpiresAt = now + MilitiaObjectiveRules.DeploymentExpiry.Ticks,
            };
            var body = Spawn(action, catalog, state, position, item.TypeId, NpcRole.MilitiaObjective, 0, now);
            state.Id = body.Id;
            state.TargetId = body.Id;
            body.EncounterId = body.Id;
            action.Add(state);
            var previous = PilotInventoryStore.Read(catalog, item);
            if (--item.Quantity == 0)
            {
                action.Remove(item);
            }
            result.Add(new(itemId, body.Id, new(previous with { Quantity = item.Quantity }, previous)));
            action.SaveChanges();
        }
        action.Commit();
        return result;
    }

    private void AdvanceDeployment(
        WorldAction world,
        CatalogDbContext catalog,
        SavedMilitiaObjective state,
        IReadOnlyList<MilitiaComplexPilot> pilots,
        long now
    )
    {
        if (
            now >= state.ExpiresAt
            || FactionWarzoneStore.System(world, state.SystemId) is not { PendingTransferAt: null }
            || world.Set<WorldNpc>().Find(state.Id) is not { RetiredAt: null, DestroyedAt: null } body
        )
        {
            Retire(world, catalog, state, now);
            ForgetSample(world, state.Id);
            world.Remove(state);
            return;
        }
        var previous = _samples.GetValueOrDefault(state.Id);
        if (now < state.ReadyAt || now <= previous?.At)
        {
            return;
        }
        var position = Position(body);
        var wing = Npcs(world, state.Id).FirstOrDefault(n => n.Active && n.Role == NpcRole.MilitiaComplexDefender);
        var sponsor = pilots.FirstOrDefault(p =>
            p.PilotId == state.PilotId && p.Position.DistanceTo(position) <= MilitiaObjectiveRules.ObjectiveRange
        );
        var pilot = sponsor is null ? null : world.Pilots.Find(sponsor.PilotId);
        if (
            pilot is not null
            && (
                pilot.ActiveShipId != sponsor!.ShipId
                || pilot.ActiveShip.Position?.SystemId != state.SystemId
                || MilitiaEnlistmentStore.Effective(world, pilot) != state.Faction
            )
        )
        {
            pilot = null;
        }
        if (wing.Id != 0)
        {
            Target(world, catalog, wing.Id, pilot?.ActiveShipId, now);
        }
        else if (pilot is not null)
        {
            if (
                previous is not null
                && previous.ShipId == sponsor!.ShipId
                && previous.ConnectionId == sponsor.ConnectionId
                && now - previous.At <= MilitiaComplexRules.MaximumSampleGap.Ticks
            )
            {
                state.Elapsed += now - previous.At;
            }
            if (state.Waves < 3 && state.Elapsed >= state.Waves * ReinforcementInterval.Ticks)
            {
                var foe = MilitiaRules.Opponent(state.Faction);
                var hostile = new SavedMilitiaObjective
                {
                    Id = state.Id,
                    SystemId = state.SystemId,
                    Faction = foe,
                };
                int type = MilitiaComplexCatalog.DefenderType(foe, MilitiaComplexFamily.Medium);
                var npc = Spawn(
                    world,
                    catalog,
                    hostile,
                    position + new SpacePosition(0, 0, NpcSpawnRules.MemberSpacing),
                    type,
                    NpcRole.MilitiaComplexDefender,
                    state.Waves + 1,
                    now
                );
                state.Waves++;
                Target(world, catalog, npc.Id, pilot.ActiveShipId, now);
            }
            else if (state.Elapsed >= MilitiaObjectiveRules.DeploymentDuration.Ticks)
            {
                var zone = FactionWarzoneStore.System(world, state.SystemId)!;
                bool broadcast = state.Kind == MilitiaObjectiveKind.Propaganda;
                MilitiaAdvantageStore.Apply(
                    world,
                    zone,
                    broadcast ? state.Faction : MilitiaRules.Opponent(state.Faction),
                    broadcast ? MilitiaAdvantageRules.PropagandaContribution : MilitiaAdvantageRules.ListeningLoss,
                    now,
                    pilot
                );
                MilitiaLoyalty.Credit(
                    world,
                    state.Faction,
                    pilot,
                    MilitiaObjectiveRules.DeploymentReward,
                    MilitiaLoyaltySource.AdvantageObjective,
                    state.Id,
                    now
                );
                Retire(world, catalog, state, now);
                ForgetSample(world, state.Id);
                world.Remove(state);
                return;
            }
        }
        if (pilot is null || wing.Id != 0)
        {
            ForgetSample(world, state.Id);
        }
        else
        {
            DeploymentSample sample = new(state.SystemId, sponsor!.ShipId, sponsor.ConnectionId, now);
            world.AfterCommit(() => _samples[state.Id] = sample);
        }
    }

    private void ForgetSample(WorldAction world, long id) => world.AfterCommit(() => _samples.Remove(id));

    private WorldNpc Spawn(
        WorldAction world,
        CatalogDbContext catalog,
        SavedMilitiaObjective state,
        SpacePosition position,
        int type,
        NpcRole role,
        int slot,
        long now
    )
    {
        var profile = profiles.Read(catalog, type);
        var npc = world
            .Add(
                new WorldNpc
                {
                    SystemId = state.SystemId,
                    TypeId = type,
                    Role = role,
                    AnchorKind = NpcAnchorKind.MilitiaObjective,
                    EncounterId = state.Id,
                    SpawnSlot = slot,
                    FactionId = (int)state.Faction,
                    CorporationId = MilitiaRules.Corporation(state.Faction),
                    X = position.X,
                    Y = position.Y,
                    Z = position.Z,
                    HomeX = position.X,
                    HomeY = position.Y,
                    HomeZ = position.Z,
                    ShieldCharge = profile.Defense.Shield,
                    UpdatedAt = now,
                    MotionAt = now,
                }
            )
            .Entity;
        world.SaveChanges();
        return npc;
    }

    private static SpacePosition Position(WorldNpc npc) => new(npc.X, npc.Y, npc.Z);

    private static long? Enemy(
        WorldAction world,
        int system,
        MilitiaFaction faction,
        SpacePosition pos,
        IReadOnlyList<MilitiaComplexPilot> pilots
    ) =>
        pilots
            .Where(p => p.Position.DistanceTo(pos) <= MilitiaObjectiveRules.ObjectiveRange)
            .Where(p =>
                world.Pilots.Find(p.PilotId) is { } pilot
                && pilot.ActiveShipId == p.ShipId
                && pilot.ActiveShip.Position?.SystemId == system
                && MilitiaRules.AreEnemies(faction, MilitiaEnlistmentStore.Effective(world, pilot))
            )
            .Select(p => (long?)p.ShipId)
            .FirstOrDefault();

    private static IEnumerable<NpcPopulationMember> Npcs(WorldAction world, long anchor) =>
        NpcPopulationMember.AtAnchor(world, anchor).Where(n => n.AnchorKind == NpcAnchorKind.MilitiaObjective);

    private static void Target(WorldAction world, CatalogDbContext catalog, long npc, long? target, long now)
    {
        var aggression = world.Set<NpcAggression>().Find(npc);
        if (aggression?.TargetShipId == target)
        {
            return;
        }
        if (aggression is not null)
        {
            NpcCombatStore.Disengage(world, catalog, aggression.TargetShipId, npc);
        }
        if (target is long ship)
        {
            world.Add(NpcAggression.Start(npc, ship, NpcGuardRules.ReactionMilliseconds, now));
        }
    }

    private static void Retire(WorldAction world, CatalogDbContext catalog, SavedMilitiaObjective state, long now)
    {
        foreach (var entry in Npcs(world, state.Id).Where(npc => npc.Active))
        {
            Target(world, catalog, entry.Id, null, now);
            world.Set<WorldNpc>().Find(entry.Id)!.RetiredAt = now;
        }
    }

    internal static IEnumerable<long> ActiveNpcs(WorldAction world, int system) =>
        world
            .Filter<SavedMilitiaObjective>(row => row.SystemId == system)
            .SelectMany(row => Npcs(world, row.Id))
            .Where(npc => npc.Active)
            .Select(npc => npc.Id);

    internal static void ResetForOccupation(WorldAction world, CatalogDbContext catalog, int system, long now)
    {
        foreach (var state in world.Filter<SavedMilitiaObjective>(row => row.SystemId == system).ToArray())
        {
            Retire(world, catalog, state, now);
            if (state.PilotId is null)
            {
                state.ReplaceAt = now;
            }
            else
            {
                world.Remove(state);
            }
        }
    }

    internal static bool CanAttack(WorldAction world, WorldNpc npc, Pilot pilot) =>
        npc.AnchorKind != NpcAnchorKind.MilitiaObjective || world is EconomyTransaction && Eligible(world, npc, pilot);

    private static bool Eligible(WorldAction world, WorldNpc npc, Pilot pilot) =>
        npc is { DestroyedAt: null, RetiredAt: null }
        && world.Set<SavedMilitiaObjective>().Find(npc.EncounterId) is { ReplaceAt: null } state
        && (
            npc.Role != NpcRole.MilitiaObjective && state.Kind != MilitiaObjectiveKind.Rendezvous
            || MilitiaRules.AreEnemies(state.Faction, MilitiaEnlistmentStore.Effective(world, pilot))
        );

    internal static void Destroyed(WorldAction world, CatalogDbContext catalog, WorldNpc npc, Pilot pilot, long now)
    {
        if (
            npc.AnchorKind != NpcAnchorKind.MilitiaObjective
            || world.Set<SavedMilitiaObjective>().Find(npc.EncounterId) is not { ReplaceAt: null } state
        )
        {
            return;
        }
        if (state.PilotId is not null)
        {
            if (npc.Id == state.Id)
            {
                Retire(world, catalog, state, now);
                world.Remove(state);
            }
            return;
        }
        bool rendezvous = state.Kind == MilitiaObjectiveKind.Rendezvous;
        if (rendezvous ? Npcs(world, state.Id).Any(n => n.Id != npc.Id && n.Active) : npc.Id != state.TargetId)
        {
            return;
        }
        state.ReplaceAt = now + (rendezvous ? MilitiaObjectiveRules.RendezvousRespawn.Ticks : MilitiaObjectiveRules.SupplyRespawn.Ticks);
        Retire(world, catalog, state, now);
        if (FactionWarzoneStore.System(world, state.SystemId) is not { PendingTransferAt: null } zone)
        {
            return;
        }
        var winner = MilitiaRules.Opponent(state.Faction);
        var contributors = (rendezvous ? new[] { pilot } : PilotInteractionHistoryStore.DamagePilots(world, npc.Id))
            .Where(contributor => MilitiaEnlistmentStore.Effective(world, contributor) == winner)
            .ToArray();
        if (contributors.Length > 0)
        {
            MilitiaAdvantageStore.Apply(
                world,
                zone,
                rendezvous ? winner : state.Faction,
                rendezvous ? MilitiaAdvantageRules.RendezvousContribution : MilitiaAdvantageRules.SupplyLoss,
                now,
                pilot
            );
            long points = rendezvous
                ? MilitiaObjectiveRules.RendezvousReward
                : MilitiaObjectiveRules.SupplyReward
                    * Math.Min(MilitiaObjectiveRules.SupplyRewardCapacity, contributors.Length)
                    / contributors.Length;
            foreach (var contributor in contributors)
            {
                MilitiaLoyalty.Credit(world, winner, contributor, points, MilitiaLoyaltySource.AdvantageObjective, npc.Id, now);
            }
        }
    }
}
