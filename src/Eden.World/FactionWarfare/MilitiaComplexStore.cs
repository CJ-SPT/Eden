using Eden.Data.Catalog.FactionWarfare;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Corporations;
using Eden.Data.World.FactionWarfare;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.FactionWarfare;
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
internal sealed class MilitiaComplexStore(WorldDatabase database, NpcProfileCatalog profiles) : IMilitiaComplexes
{
    private static readonly IGrouping<MilitiaComplexFamily, MilitiaComplexVariant>[] Variants = MilitiaComplexVariants
        .All.Where(variant => !variant.Retired)
        .GroupBy(variant => variant.Family)
        .ToArray();

    private readonly MilitiaComplexCatalog _catalog = new(database.CatalogPath);

    // Guarded by the resident owner. Continuity is deliberately absent from a saved world.
    private readonly Dictionary<long, Sample> _samples = new();

    private readonly record struct Presence(long ShipId, long ConnectionId);

    private sealed record Sample(long At, int Side, Presence[] Ships);

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
    }

    private sealed record Participant(Pilot Pilot, Presence Presence, int Side)
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
        return live.Economy.Inspect(world => Read(world, systemId, now).Select(site => View(site, now)).ToArray());
    }

    public IReadOnlyList<AnomalySystemSites> Directory(long now) =>
        Resident?.Economy.Inspect(world =>
            FactionWarzoneCatalog
                .Roster.Select(seed => new AnomalySystemSites(
                    seed.SystemId,
                    Read(world, seed.SystemId, now).Select(s => View(s, now, false)).ToArray()
                ))
                .Where(entry => entry.Sites.Count > 0)
                .ToArray()
        )
        ?? [];

    public SpacePosition Redirect(int systemId, SpacePosition position, long now)
    {
        if (!IsWarzone(systemId) || Resident is not { } live)
        {
            return position;
        }
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
        using var owner = live.EnterPersistence("militia complex", TransactionParticipants.None);
        pilots = pilots.Where(pilot => !live.Economy.Cloaked(pilot.ShipId)).ToArray();
        var nearby = live.Economy.Inspect(world =>
            Read(world, systemId, now)
                .Where(site =>
                    _samples.ContainsKey(site.Slot)
                    || site.Saved is { DefenderId: not null }
                    || pilots.Any(pilot => pilot.Position.DistanceTo(site.Capture) <= MilitiaComplexRules.CaptureRange)
                )
                .ToArray()
        );
        if (nearby.Length == 0)
        {
            return;
        }

        using CatalogDbContext catalog = new(database.CatalogPath);
        var participants = new TransactionParticipants(
            pilots: pilots.Select(p => p.PilotId),
            ships: pilots.Select(p => p.ShipId),
            resources: nearby
                .Select(s => s.Saved?.DefenderId)
                .OfType<long>()
                .Select(id => new SimulationResource(SharedSimulationResource.Npc, id))
        );
        using var action = live.Economy.Begin(participants);
        var samples = new Dictionary<long, Sample?>();
        foreach (var site in nearby)
        {
            samples[site.Slot] = Step(action, catalog, site, pilots, now);
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
        var previous = _samples.GetValueOrDefault(site.Slot);
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

        var participants = Participants(world, catalog, site, pilots);
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
        var sample = new Sample(now, side, ships);
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
                Settle(world, site, side == 1 ? attackers : defenders, side == 1, policy);
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
        IReadOnlyList<MilitiaComplexPilot> pilots
    )
    {
        var candidates = new List<Participant>();
        foreach (var candidate in pilots)
        {
            if (
                candidate.Position.DistanceTo(site.Capture) > MilitiaComplexRules.CaptureRange
                || world.Set<Pilot>().Find(candidate.PilotId) is not { } pilot
                || pilot.ActiveShipId != candidate.ShipId
                || world.Set<Ship>().Find(candidate.ShipId) is not { Position: { } at } ship
                || CrimewatchPersistence.System(catalog, ship) != site.System.SystemId
                || new SpacePosition(at.X, at.Y, at.Z).DistanceTo(site.Capture) > MilitiaComplexRules.CaptureRange
                || !MilitiaComplexRules.CanCapture(ship.TypeId, catalog.RequireItem(ship.TypeId).GroupId)
                || MilitiaEnlistmentStore.Effective(world, pilot) is not { } faction
            )
            {
                continue;
            }
            if (faction == site.System.Occupier)
            {
                candidates.Add(new(pilot, new(ship.Id, candidate.ConnectionId), -1));
            }
            else if (MilitiaRules.AreEnemies(faction, site.System.Occupier))
            {
                candidates.Add(new(pilot, new(ship.Id, candidate.ConnectionId), 1));
            }
        }
        return candidates.DistinctBy(candidate => candidate.Pilot.Id).OrderBy(candidate => candidate.ShipId).ToArray();
    }

    private static void Settle(WorldAction world, Site site, Participant[] winners, bool offense, MilitiaComplexRewardPolicy policy)
    {
        var system = FactionWarzoneStore.System(world, site.System.SystemId)!;
        int score = MilitiaComplexRules.VictoryPoints(system.VictoryPoints, offense, policy);
        if (score != system.VictoryPoints)
        {
            var occupation =
                world.Set<SavedWarzoneOccupation>().Find(system.SystemId)
                ?? world.Add(new SavedWarzoneOccupation { SystemId = system.SystemId, Occupier = system.Occupier }).Entity;
            occupation.VictoryPoints = score;
        }
        long points = MilitiaComplexRules.PointsPerPilot(system, offense, policy, winners.Length);
        if (points <= 0)
        {
            return;
        }

        var faction = offense ? Opponent(system) : system.Occupier;
        int issuer = MilitiaRules.Corporation(faction);
        foreach (var winner in winners)
        {
            long pilotId = winner.Pilot.Id;
            int corpId = winner.Pilot.CorporationId;
            var corp = world.Set<SavedCorporation>().Find(corpId);
            long tax = MilitiaComplexRules.CorporationTax(points, corp?.Snapshot(0).Details.LoyaltyPointTaxRate ?? 0);
            if (tax > 0)
            {
                var corporate =
                    world.Set<SavedCorporationLoyaltyBalance>().Find(corpId, issuer)
                    ?? world.Add(new SavedCorporationLoyaltyBalance { CorporationId = corpId, IssuerCorporationId = issuer }).Entity;
                corporate.Points = checked(corporate.Points + tax);
            }
            if (points > tax)
            {
                var personal =
                    world.Set<SavedLoyaltyBalance>().Find(pilotId, issuer)
                    ?? world.Add(new SavedLoyaltyBalance { PilotId = pilotId, IssuerCorporationId = issuer }).Entity;
                personal.Points = checked(personal.Points + points - tax);
            }
        }
    }

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
            int type = MilitiaComplexCatalog.DefenderType(site.System.Occupier, site.Profile.Variant.Family);
            var profile = profiles.Read(catalog, type);
            var spawn = site.Room + site.Profile.Defender;
            npc = world
                .Add(
                    new WorldNpc
                    {
                        SystemId = site.System.SystemId,
                        TypeId = type,
                        Role = NpcRole.MilitiaComplexDefender,
                        AnchorKind = NpcAnchorKind.MilitiaComplex,
                        EncounterId = site.Id,
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
            state.DefenderId = npc.Id;
        }

        var aggression = world.Set<NpcAggression>().Find(npc.Id);
        if (aggression is not null && !attackers.Any(p => p.ShipId == aggression.TargetShipId))
        {
            NpcCombatStore.Disengage(world, catalog, aggression.TargetShipId, npc.Id);
            aggression = null;
        }
        if (aggression is null && attackers.Length > 0)
        {
            world.Add(NpcAggression.Start(npc.Id, attackers[0].ShipId, NpcGuardRules.ReactionMilliseconds, now));
        }
        return true;
    }

    private static void RetireDefender(WorldAction world, CatalogDbContext catalog, SavedMilitiaComplex state, long now)
    {
        if (state.DefenderId is not long id)
        {
            return;
        }
        if (world.Set<NpcAggression>().Find(id) is { } aggression)
        {
            NpcCombatStore.Disengage(world, catalog, aggression.TargetShipId, id);
        }
        world.RemoveRange(world.Filter<WorldNpcEwar>(effect => effect.NpcId == id));
        if (world.Set<WorldNpc>().Find(id) is { DestroyedAt: null, RetiredAt: null } npc)
        {
            npc.RetiredAt = now;
        }
        state.DefenderId = null;
    }

    private IEnumerable<Site> Read(WorldAction world, int systemId, long now)
    {
        if (FactionWarzoneStore.System(world, systemId) is not { } system)
        {
            yield break;
        }
        var saved = world.Set<SavedMilitiaComplex>();
        foreach (var family in Variants)
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
            var profile = _catalog.Read(family.ElementAt(variant), system.Occupier);
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
            var sample = site.Replaced ? null : _samples.GetValueOrDefault(site.Slot);
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
            bodies.Add(
                profile.CapturePoint with
                {
                    Id = MilitiaComplexRules.PointId(site.Id),
                    Position = site.Capture,
                    MilitiaCapture = bracket,
                }
            );
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
        using var owner = live.EnterPersistence("militia acceleration gate", TransactionParticipants.None);
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (activate is null)
        {
            return live.Economy.Inspect(world => ValidateGate(world, catalog, pilotId, gateId, now).Site.Beacon);
        }
        using var action = live.Economy.Begin(new(pilots: [pilotId]));
        var gate = ValidateGate(action, catalog, pilotId, gateId, now);
        if (gate.Site.Beacon != activate)
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
        return gate.Site.Beacon;
    }

    private (Site Site, bool Neutral) ValidateGate(WorldAction world, CatalogDbContext catalog, long pilotId, long gateId, long now)
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
                var fitted = catalog.RequireItem(item.TypeId);
                return access.ForbiddenEquipment!.Contains(fitted.Id, fitted.GroupId, fitted.CategoryId);
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
        return (site, neutral);
    }
}
