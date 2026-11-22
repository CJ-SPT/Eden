using Eden.Data.Catalog.FactionWarfare;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.FactionWarfare;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.FactionWarfare;

[Injectable(InjectionType.Singleton)]
internal sealed partial class MilitiaComplexStore(
    WorldDatabase database,
    NpcProfileCatalog profiles,
    MilitiaFlightPresence presence,
    IMilitiaHubs? hubs = null,
    MilitiaObjectiveStore? objectives = null,
    ICooperativeFleets? fleets = null
) : IMilitiaComplexes, IMilitiaComplexSimulation
{
    private static readonly IGrouping<MilitiaComplexFamily, MilitiaComplexVariant>[] Variants = MilitiaComplexVariants
        .All.Where(variant => !variant.Retired)
        .GroupBy(variant => variant.Family)
        .ToArray();

    // Guarded by the resident owner. Continuity is deliberately absent from a saved world.
    private readonly Dictionary<long, Sample> _samples = new();

    private readonly record struct Presence(long ShipId, long ConnectionId);

    private sealed record Sample(long SiteId, long At, int Side, Presence[] Ships, long TicksHeld = 0);

    private sealed record Site(
        long Slot,
        long Id,
        int Generation,
        WarzoneSystem System,
        MilitiaComplexProfile Profile,
        SavedMilitiaComplex? Saved,
        SpacePosition Entry,
        SpacePosition Room
    )
    {
        internal SpacePosition Capture => Room + Profile.CapturePoint.Position;
        internal SpacePosition Beacon => Room + Profile.Beacon.Position;
        internal SpacePosition Gate => Entry + Profile.Gate!.Position;
        internal bool Replaced => Saved is not null && (Saved.Generation != Generation || Saved.Occupier != System.Occupier);

        internal SpacePosition GateDestination(MilitiaFaction? faction)
        {
            if (Profile.Battlefield is not { } battlefield)
            {
                return Beacon;
            }
            var rally =
                faction == System.Occupier ? battlefield.DefenderRally
                : MilitiaRules.AreEnemies(faction, System.Occupier) ? battlefield.AttackerRally
                : battlefield.NeutralRally;
            return Room + rally;
        }
    }

    private sealed record Participant(Pilot Pilot, Presence Presence, int Side, bool CanCapture)
    {
        internal long ShipId => Presence.ShipId;
    }

    private LiveWorld? Resident => LiveWorld.Find(database.Path);

    public bool IsWarzone(int systemId) => FactionWarzoneCatalog.Systems.ContainsKey(systemId);

    public IReadOnlyList<CombatAnomaly> Sites(int systemId, long now)
    {
        if (!IsWarzone(systemId) || Resident is not { } live)
        {
            return [];
        }
        hubs?.Sweep(now);
        var sites = live.Economy.Inspect(world => Read(world, systemId, now).Select(site => View(site, now)).ToArray());
        return objectives?.Sites(systemId, now) is { Count: > 0 } available ? sites.Concat(available).ToArray() : sites;
    }

    public IReadOnlyList<AnomalySystemSites> Directory(long now, bool battlefields = false)
    {
        hubs?.Sweep(now);
        return Resident?.Economy.Inspect(world =>
                FactionWarzoneCatalog
                    .Roster.Select(seed => new AnomalySystemSites(
                        seed.SystemId,
                        Read(world, seed.SystemId, now)
                            .Where(site => (site.Profile.Battlefield is not null) == battlefields)
                            .Select(s => View(s, now, false))
                            .ToArray()
                    ))
                    .Where(entry => entry.Sites.Count > 0)
                    .ToArray()
            )
            ?? [];
    }

    public SpacePosition Redirect(int systemId, SpacePosition position, long now)
    {
        if (!IsWarzone(systemId) || Resident is not { } live)
        {
            return position;
        }
        hubs?.Sweep(now);
        return live.Economy.Inspect(world =>
        {
            var site = Read(world, systemId, now)
                .FirstOrDefault(site =>
                    site.Profile.Gate is not null && site.Room.DistanceTo(position) <= NpcEncounterDefinitions.VisibilityRange
                );
            return site?.Gate ?? position;
        });
    }

    public void Advance(int systemId, IReadOnlyList<MilitiaComplexPilot> pilots, long now)
    {
        if (!IsWarzone(systemId) || Resident is not { } live)
        {
            return;
        }
        hubs?.Sweep(now);
        using var owner = live.EnterPersistence("militia complex", TransactionParticipants.None);
        AdvanceOwned(live, systemId, pilots, now);
    }

    private void AdvanceOwned(LiveWorld live, int systemId, IReadOnlyList<MilitiaComplexPilot> pilots, long now)
    {
        pilots = pilots.Where(pilot => !live.Economy.Cloaked(pilot.ShipId)).ToArray();
        objectives?.Advance(systemId, pilots, now);
        var nearby = live.Economy.Inspect(world =>
            Read(world, systemId, now)
                .Where(site =>
                    _samples.ContainsKey(site.Slot)
                    || site.Profile.Battlefield is { } layout
                        && Enumerable
                            .Range(0, layout.Points.Count)
                            .Any(index => _samples.ContainsKey(MilitiaComplexRules.PointId(site.Id, index)))
                    || site.Saved is { DefenderId: not null }
                    || pilots.Any(pilot =>
                        site.Profile.Battlefield is null
                            ? pilot.Position.DistanceTo(site.Capture) <= MilitiaComplexRules.CaptureRange
                            : pilot.Position.DistanceTo(site.Room) <= NpcEncounterDefinitions.VisibilityRange
                    )
                )
                .ToArray()
        );
        if (nearby.Length == 0)
        {
            return;
        }

        using CatalogDbContext catalog = new(database.CatalogPath);
        long? hubNpc = nearby[0].System.HubPhase == MilitiaHubPhase.Vulnerable ? live.Economy.Peek<SavedMilitiaHub>(systemId)?.NpcId : null;
        var participants = new TransactionParticipants(
            pilots: pilots.Select(p => p.PilotId),
            ships: pilots.Select(p => p.ShipId),
            resources: nearby
                .Select(s => s.Saved?.DefenderId)
                .Concat(
                    live.Economy.Inspect(world =>
                        nearby
                            .Where(s => s.Profile.Battlefield is not null)
                            .SelectMany(s => BattlefieldWings(world, s.Id))
                            .Where(npc => npc.Active)
                            .Select(npc => (long?)npc.Id)
                            .ToArray()
                    )
                )
                .Append(hubNpc)
                .OfType<long>()
                .Select(id => new SimulationResource(SharedSimulationResource.Npc, id))
        );
        using var action = live.Economy.Begin(participants);
        var samples = new Dictionary<long, Sample?>();
        foreach (var site in nearby)
        {
            if (site.Profile.Battlefield is not null)
            {
                AdvanceBattlefield(action, catalog, site, pilots, now, samples);
            }
            else
            {
                samples[site.Slot] = Step(action, catalog, site, pilots, now);
            }
        }
        action.SaveChanges();
        action.Commit();
        foreach (var (slot, sample) in samples)
        {
            if (sample is null)
            {
                _samples.Remove(slot);
            }
            else
            {
                _samples[slot] = sample;
            }
        }
    }

    private Sample? Step(WorldAction world, CatalogDbContext catalog, Site site, IReadOnlyList<MilitiaComplexPilot> pilots, long now)
    {
        var state = world.Set<SavedMilitiaComplex>().Find(site.Slot);
        var previous = SampleFor(site);
        if (previous?.At > now)
        {
            return previous;
        }
        if (site.Replaced && state is not null)
        {
            RetireDefender(world, catalog, state, now);
            state.Generation = site.Generation;
            state.Occupier = site.System.Occupier;
            state.Progress = 0;
            state.ReplaceAt = null;
            previous = null;
        }

        var participants = Participants(world, catalog, site, pilots, site.Capture);
        var attackers = participants.Where(p => p.Side == 1).ToArray();
        var defenders = participants.Where(p => p.Side == -1).ToArray();
        if (state is null && participants.Length == 0)
        {
            return null;
        }
        state ??= world.Add(new SavedMilitiaComplex { Slot = site.Slot, Occupier = site.System.Occupier }).Entity;
        bool blocked = HoldDefender(world, catalog, site, state, attackers, now);
        int side = (attackers.Length > 0, defenders.Length > 0, blocked) switch
        {
            (true, false, false) => 1,
            (false, true, _) => -1,
            _ => 0,
        };
        var ships = (side == 1 ? attackers : defenders).Select(p => p.Presence).ToArray();
        var sample = new Sample(site.Id, now, side, ships);
        if (side == 0 && participants.Length == 0)
        {
            return null;
        }
        if (
            side != 0
            && previous is not null
            && previous.Side == side
            && now - previous.At <= MilitiaComplexRules.MaximumSampleGap.Ticks
            && ships.Intersect(previous.Ships).Any()
        )
        {
            var policy = site.Profile.Variant.Policy;
            state.Progress = MilitiaComplexRules.Advance(state.Progress, side, now - previous.At, policy.Duration);
            if (Math.Abs(state.Progress) == policy.Duration)
            {
                Settle(world, catalog, site, side == 1 ? attackers : defenders, side == 1, policy, now);
                RetireDefender(world, catalog, state, now);
                state.ReplaceAt = checked(now + MilitiaComplexRules.Replacement(site.System.Adjacency).Ticks);
                return null;
            }
        }
        return sample;
    }

    private static Participant[] Participants(
        WorldAction world,
        CatalogDbContext catalog,
        Site site,
        IReadOnlyList<MilitiaComplexPilot> pilots,
        SpacePosition capture
    )
    {
        var candidates = new List<Participant>();
        foreach (var candidate in pilots)
        {
            if (
                candidate.Position.DistanceTo(capture) > MilitiaComplexRules.CaptureRange
                || world.Set<Pilot>().Find(candidate.PilotId) is not { } pilot
                || pilot.ActiveShipId != candidate.ShipId
                || world.Set<Ship>().Find(candidate.ShipId) is not { Position: { } at } ship
                || CrimewatchPersistence.System(catalog, ship) != site.System.SystemId
                || new SpacePosition(at.X, at.Y, at.Z).DistanceTo(capture) > MilitiaComplexRules.CaptureRange
                || MilitiaEnlistmentStore.Effective(world, pilot) is not { } faction
            )
            {
                continue;
            }
            var type = catalog.RequireItem(ship.TypeId);
            var battlefield = site.Profile.Battlefield;
            bool canCapture =
                MilitiaComplexRules.CanCapture(ship.TypeId, type.GroupId)
                && (battlefield is null || battlefield.Capturing.Contains(type.Id, type.GroupId, type.CategoryId));
            if (!canCapture && battlefield?.Blocking.Contains(type.Id, type.GroupId, type.CategoryId) != true)
            {
                continue;
            }
            if (faction == site.System.Occupier)
            {
                candidates.Add(new(pilot, new(ship.Id, candidate.ConnectionId), -1, canCapture));
            }
            else if (MilitiaRules.AreEnemies(faction, site.System.Occupier))
            {
                candidates.Add(new(pilot, new(ship.Id, candidate.ConnectionId), 1, canCapture));
            }
        }
        return candidates.DistinctBy(candidate => candidate.Pilot.Id).OrderBy(candidate => candidate.ShipId).ToArray();
    }

    private void Settle(
        WorldAction world,
        CatalogDbContext catalog,
        Site site,
        Participant[] winners,
        bool offense,
        MilitiaComplexRewardPolicy policy,
        long now
    )
    {
        var system = FactionWarzoneStore.System(world, site.System.SystemId)!;
        var faction = offense ? Opponent(system) : system.Occupier;
        int net = MilitiaAdvantageStore.Read(world, system, now).Net(faction);
        var adjusted = policy with { VictoryPoints = MilitiaAdvantageRules.AdjustedVictoryPoints(policy.VictoryPoints, net) };
        int score = MilitiaComplexRules.VictoryPoints(system.VictoryPoints, offense, adjusted);
        if (score != system.VictoryPoints)
        {
            var occupation =
                world.Set<SavedWarzoneOccupation>().Find(system.SystemId)
                ?? world.Add(new SavedWarzoneOccupation { SystemId = system.SystemId, Occupier = system.Occupier }).Entity;
            occupation.VictoryPoints = score;
            if (system.HubPhase == MilitiaHubPhase.Vulnerable && score < FactionWarzoneRules.VictoryPointThreshold)
            {
                MilitiaHubStore.Recover(world, catalog, system.SystemId, system.Occupier, now);
            }
        }
        var ordered = winners.OrderBy(winner => winner.Pilot.Id).ToArray();
        var agents = world.Set<SavedCapsuleerAgent>();
        var recipients = ordered.Where(winner => agents.Find(winner.Pilot.Id) is null).ToArray();
        long points =
            site.Profile.Battlefield is null ? MilitiaComplexRules.PointsPerPilot(system, offense, policy, recipients.Length)
            : recipients.Length == 0 ? 0
            : (long)policy.BasePoints * Math.Min(policy.Capacity, recipients.Length) / recipients.Length;
        int gained = Math.Abs(score - system.VictoryPoints);
        for (int i = 0; i < ordered.Length; i++)
        {
            var pilot = ordered[i].Pilot;
            long vp = MilitiaProgressionRules.Share(gained, i, ordered.Length);
            MilitiaProgressionStore.Record(world, pilot, faction, new(0, vp), now);
            List<MissionStandingChange> standings = [];
            MilitiaProgressionStore.AdjustStanding(
                world,
                pilot,
                faction,
                MilitiaProgressionRules.CaptureStandingFraction,
                offense ? StandingEvent.TacticalSiteConquered : StandingEvent.TacticalSiteDefended,
                system.SystemId,
                now,
                standings
            );
            foreach (var standing in standings)
            {
                world.AfterCommit(() => fleets?.NotifyStandingChanged(pilot.Id, standing.SourceId, standing.After));
            }
        }
        foreach (var recipient in recipients)
        {
            MilitiaLoyalty.Credit(
                world,
                faction,
                recipient.Pilot,
                points,
                site.Profile.Battlefield is null ? MilitiaLoyaltySource.Complex : MilitiaLoyaltySource.Battlefield,
                site.Id,
                now
            );
        }
    }

    internal static IEnumerable<long> DefenderIds(WorldAction world, int systemId) =>
        Variants
            .Select(family => world.Set<SavedMilitiaComplex>().Find(MilitiaComplexRules.Slot(systemId, family.Key))?.DefenderId)
            .OfType<long>()
            .Concat(BattlefieldDefenders(world, systemId));

    private static IEnumerable<long> BattlefieldDefenders(WorldAction world, int systemId)
    {
        long slot = MilitiaComplexRules.Slot(systemId, MilitiaComplexFamily.Battlefield);
        return world.Set<SavedMilitiaComplex>().Find(slot) is { } state
            ? BattlefieldWings(world, MilitiaComplexRules.SiteId(slot, state.Generation)).Where(npc => npc.Active).Select(npc => npc.Id)
            : [];
    }

    internal static void ResetForOccupation(WorldAction world, CatalogDbContext catalog, int systemId, MilitiaFaction occupier, long now)
    {
        foreach (var family in Variants.Concat(BattlefieldVariants))
        {
            long slot = MilitiaComplexRules.Slot(systemId, family.Key);
            var state = world.Set<SavedMilitiaComplex>().Find(slot) ?? world.Add(new SavedMilitiaComplex { Slot = slot }).Entity;
            RetireDefender(world, catalog, state, now);
            if (family.Key == MilitiaComplexFamily.Battlefield)
            {
                RetireBattlefield(world, catalog, slot, state.Generation, now);
            }
            state.Generation = checked(state.Generation + 1);
            state.Occupier = occupier;
            state.Progress = 0;
            state.ReplaceAt = null;
        }
    }

    private Sample? SampleFor(Site site) => _samples.TryGetValue(site.Slot, out var sample) && sample.SiteId == site.Id ? sample : null;

    private bool HoldDefender(
        WorldAction world,
        CatalogDbContext catalog,
        Site site,
        SavedMilitiaComplex state,
        Participant[] attackers,
        long now
    )
    {
        var npc = state.DefenderId is long id ? world.Set<WorldNpc>().Find(id) : null;
        if (npc is not { DestroyedAt: null, RetiredAt: null })
        {
            if (attackers.Length == 0 || npc?.DestroyedAt is long killedAt && now - killedAt < site.Profile.DefenderInterval.Ticks)
            {
                return false;
            }
            npc = SpawnDefender(world, catalog, site, site.Room + site.Profile.Defender, 0, now);
            state.DefenderId = npc.Id;
        }

        EngageDefender(world, catalog, npc.Id, attackers, now);
        return true;
    }

    private WorldNpc SpawnDefender(WorldAction world, CatalogDbContext catalog, Site site, SpacePosition spawn, int slot, long now)
    {
        int type = MilitiaComplexCatalog.DefenderType(site.System.Occupier, site.Profile.Variant.Family);
        var profile = profiles.Read(catalog, type);
        var npc = world
            .Add(
                new WorldNpc
                {
                    SystemId = site.System.SystemId,
                    TypeId = type,
                    Role = NpcRole.MilitiaComplexDefender,
                    AnchorKind = NpcAnchorKind.MilitiaComplex,
                    EncounterId = site.Id,
                    SpawnSlot = slot,
                    FactionId = (int)site.System.Occupier,
                    CorporationId = MilitiaRules.Corporation(site.System.Occupier),
                    X = spawn.X,
                    Y = spawn.Y,
                    Z = spawn.Z,
                    HomeX = spawn.X,
                    HomeY = spawn.Y,
                    HomeZ = spawn.Z,
                    ShieldCharge = profile.Defense.Shield,
                    UpdatedAt = now,
                }
            )
            .Entity;
        world.SaveChanges();
        return npc;
    }

    private static void EngageDefender(WorldAction world, CatalogDbContext catalog, long id, Participant[] attackers, long now)
    {
        var aggression = world.Set<NpcAggression>().Find(id);
        if (aggression is not null && !attackers.Any(p => p.ShipId == aggression.TargetShipId))
        {
            NpcCombatStore.Disengage(world, catalog, aggression.TargetShipId, id);
            aggression = null;
        }
        if (aggression is null && attackers.Length > 0)
        {
            world.Add(NpcAggression.Start(id, attackers[0].ShipId, NpcGuardRules.ReactionMilliseconds, now));
        }
    }

    private static void RetireDefender(WorldAction world, CatalogDbContext catalog, SavedMilitiaComplex state, long now)
    {
        if (state.DefenderId is long id)
        {
            RetireNpc(world, catalog, id, now);
            state.DefenderId = null;
        }
    }

    private static void RetireNpc(WorldAction world, CatalogDbContext catalog, long id, long now)
    {
        if (world.Set<NpcAggression>().Find(id) is { } aggression)
        {
            NpcCombatStore.Disengage(world, catalog, aggression.TargetShipId, id);
        }
        world.RemoveRange(world.Filter<WorldNpcEwar>(effect => effect.NpcId == id));
        if (world.Set<WorldNpc>().Find(id) is { DestroyedAt: null, RetiredAt: null } npc)
        {
            npc.RetiredAt = now;
        }
    }

    private IEnumerable<Site> Read(WorldAction world, int systemId, long now) =>
        Read(world, CatalogProvider.Open(database.CatalogPath), systemId, now);

    private static IEnumerable<Site> Read(WorldAction world, CatalogProvider catalog, int systemId, long now)
    {
        if (FactionWarzoneStore.System(world, systemId) is not { PendingTransferAt: null } system)
        {
            yield break;
        }
        var saved = world.Set<SavedMilitiaComplex>();
        var eligible = system.Adjacency == WarzoneAdjacency.Frontline ? Variants.Concat(BattlefieldVariants) : Variants;
        foreach (var family in eligible)
        {
            long slot = MilitiaComplexRules.Slot(systemId, family.Key);
            var state = saved.Find(slot);
            bool sameOccupation = state?.Occupier == system.Occupier;
            if (sameOccupation && state?.ReplaceAt > now)
            {
                continue;
            }
            int generation =
                state is null ? 0
                : (state.ReplaceAt.HasValue || !sameOccupation) ? checked(state.Generation + 1)
                : state.Generation;
            int variant = (int)((systemId + (long)generation) % family.Count());
            var profile = MilitiaComplexCatalog.Read(catalog, family.ElementAt(variant), system.Occupier);
            if (profile is null)
            {
                continue;
            }

            long id = MilitiaComplexRules.SiteId(slot, generation);
            var anchor = Anchor(systemId, family.Key, generation);
            var room = profile.Gate is null ? anchor : anchor + new SpacePosition(MissionGateRules.RoomSpacing, 0, 0);
            yield return new(slot, id, generation, system, profile, state, anchor, room);
        }
    }

    private static MilitiaFaction Opponent(WarzoneSystem system) =>
        system.Occupier == system.Warzone.FirstFaction ? system.Warzone.SecondFaction : system.Warzone.FirstFaction;

    private static SpacePosition Anchor(int system, MilitiaComplexFamily family, int generation)
    {
        var random = new Random(unchecked((system * 397) ^ ((int)family * 31) ^ generation));
        const double GridSpacing = 50_000_000_000;
        return new(
            GridSpacing * (1 + random.NextDouble() + (int)family % 2),
            GridSpacing * (random.NextDouble() - 0.5),
            GridSpacing * (1 + random.NextDouble())
        );
    }

    private CombatAnomaly View(Site site, long now, bool includeObjects = true)
    {
        var profile = site.Profile;
        var bodies = new List<StaticSpaceObject>(3);
        if (includeObjects)
        {
            var sample = site.Replaced ? null : SampleFor(site);
            bool fresh = sample is not null && now >= sample.At && now - sample.At <= MilitiaComplexRules.MaximumSampleGap.Ticks;
            int side = fresh ? sample!.Side : 0;
            long progress = site.Replaced ? 0 : site.Saved?.Progress ?? 0;
            var policy = profile.Variant.Policy;
            var occupier = site.System.Occupier;
            var attacker = Opponent(site.System);
            var bracket = new MilitiaCaptureBracket(
                (double)(side == -1 ? -progress : progress) * 100 / policy.Duration,
                policy.Minutes,
                side == 1 ? attacker
                    : side == -1 ? occupier
                    : null,
                progress > 0 ? attacker
                    : progress < 0 ? occupier
                    : null,
                side == 0 && (fresh || progress != 0)
            );
            if (profile.Battlefield is not null)
            {
                BattlefieldBodies(site, now, bodies);
            }
            else
            {
                bodies.Add(
                    profile.CapturePoint with
                    {
                        Id = MilitiaComplexRules.PointId(site.Id),
                        Position = site.Capture,
                        MilitiaCapture = bracket,
                    }
                );
            }
            bodies.Add(profile.Beacon with { Id = MilitiaComplexRules.BeaconId(site.Id), Position = site.Beacon });
            if (profile.Gate is { } gate)
            {
                bodies.Add(
                    gate with
                    {
                        Id = MilitiaComplexRules.GateId(site.Id),
                        Position = site.Gate,
                        AccelerationGateDirection = (site.Beacon - site.Gate).Unit,
                    }
                );
            }
        }
        // Local scan identifiers use a reserved FW prefix and change with the replacement.
        return new(
            site.Id,
            $"FW{(char)('A' + (int)site.Profile.Variant.Family)}-{site.Generation % 1000:000}",
            site.System.SystemId,
            profile.DungeonId,
            profile.NameId,
            profile.Difficulty,
            profile.Gate is null ? site.Capture : site.Gate,
            site.Saved is not null && !site.Replaced,
            bodies,
            (int)site.System.Occupier
        )
        {
            ArchetypeId = profile.Archetype,
        };
    }

    public SpacePosition Gate(long pilotId, long gateId, long now, SpacePosition? activate = null)
    {
        var live = Resident ?? throw new InvalidDataException("Militia complexes require the resident world.");
        hubs?.Sweep(now);
        using var owner = live.EnterPersistence("militia acceleration gate", TransactionParticipants.None);
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (activate is null)
        {
            return live.Economy.Inspect(world => ValidateGate(world, catalog, pilotId, gateId, now).Destination);
        }
        using var action = live.Economy.Begin(new(pilots: [pilotId]));
        var gate = ValidateGate(action, catalog, pilotId, gateId, now);
        if (gate.Destination != activate)
        {
            throw new InvalidDataException("This acceleration gate destination changed.");
        }
        if (gate.Neutral)
        {
            var crimewatch = CrimewatchPersistence.State(action, pilotId);
            var timers = crimewatch.Timers();
            crimewatch.SystemId = gate.Site.System.SystemId;
            crimewatch.Apply(timers with { Suspect = CrimewatchRules.Extend(timers.Suspect, now, CrimewatchRules.FlagDuration) });
        }
        action.SaveChanges();
        action.Commit();
        return gate.Destination;
    }

    private (Site Site, bool Neutral, SpacePosition Destination) ValidateGate(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long gateId,
        long now
    )
    {
        var pilot = world.Set<Pilot>().Find(pilotId) ?? throw new InvalidDataException("Unknown acceleration gate pilot.");
        var ship = world.Set<Ship>().Find(pilot.ActiveShipId);
        if (ship?.Position is not { } position)
        {
            throw new InvalidDataException("Undock before activating an acceleration gate.");
        }
        world.RequireUncloaked(ship.Id);
        int systemId = CrimewatchPersistence.System(catalog, ship);
        var site =
            Read(world, systemId, now).SingleOrDefault(s => s.Id == MilitiaComplexRules.SiteFromGate(gateId) && s.Profile.Gate is not null)
            ?? throw new InvalidDataException("This militia acceleration gate is unavailable.");
        var type = catalog.RequireItem(ship.TypeId);
        if (
            new SpacePosition(position.X, position.Y, position.Z).DistanceTo(site.Gate)
            > MilitiaComplexRules.GateRange + site.Profile.Gate!.Radius + (type.Radius ?? 0)
        )
        {
            throw new InvalidDataException("Approach within 90 kilometers of the militia acceleration gate.");
        }

        var access = site.Profile.Access!;
        if (!MissionGateAccessRules.AllowsShip(access, type.Id, type.GroupId, type.CategoryId, 0))
        {
            throw new InvalidDataException("This ship is not permitted through this acceleration gate.");
        }
        bool prohibitedEquipment = world
            .Filter<OwnedItem>(item => item.LocationId == ship.Id && item.OwnerId == pilotId)
            .Where(item => InventoryDefinitions.IsFittedSlot(item.FlagId))
            .AsEnumerable()
            .Any(item =>
            {
                return ProhibitsEquipment(catalog, access, item.TypeId);
            });
        if (prohibitedEquipment)
        {
            throw new InvalidDataException("Remove the prohibited fitted equipment before activating this acceleration gate.");
        }
        var faction = MilitiaEnlistmentStore.Effective(world, pilot);
        bool neutral = faction is null;
        if (neutral && pilot.SafetyLevel == ShipSafetyLevel.Full)
        {
            throw new InvalidDataException("Neutral pilots need Partial or Disabled safety to activate this militia acceleration gate.");
        }
        return (site, neutral, site.GateDestination(faction));
    }
}
