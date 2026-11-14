using Eden.Data.Catalog.Exploration;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Docking;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Exploration;

internal sealed partial class PirateExplorationStore
{
    // Site IDs and member slots survive scene unload. The NPC row allocates its
    // own ordinary global identity, as mission NPCs do.
    public void ArriveUncloaked(int systemId, SpacePosition position, long now)
    {
        var contact = Signatures(systemId, now)
            .FirstOrDefault(s =>
                s.Exploration == ExplorationKind.Ghost && !s.Started && s.Position.DistanceTo(position) <= CombatAnomalyRules.ArrivalRange
            );
        if (contact is null)
        {
            return;
        }
        LiveWorld.Execute(
            database.Path,
            systemId,
            () =>
            {
                using var ownership = LiveWorld.Find(database.Path)?.Enter();
                using var movement = database.EnterMovement();
                using WorldActionScope transaction = new(database, new(systems: [systemId]));
                var world = transaction.World;
                var site = world.Set<SavedExplorationSite>().Find(contact.Id);
                if (site is { ResponseAt: null, ReplaceAt: null } && site.ExpiresAt > now)
                {
                    site.ResponseAt = GhostSiteRules.ResponseAt(now, site.Id);
                    Invalidate(world, systemId);
                    transaction.Commit();
                }
            },
            nameof(ArriveUncloaked)
        );
    }

    private static IEnumerable<Pilot> PosedPilots(WorldAction world, int system) =>
        world
            .Filter<FlightPosition>(p => p.SystemId == system || p.SystemId == null)
            .AsEnumerable()
            .Select(p => world.Filter<Pilot>(pilot => pilot.ActiveShipId == p.ShipId).SingleOrDefault())
            .OfType<Pilot>();

    private TransactionParticipants GhostParticipants(WorldAction world, int system, long now)
    {
        bool blastDue = world
            .Filter<SavedExplorationSite>(s =>
                s.SystemId == system
                && s.Kind == (int)ExplorationKind.Ghost
                && s.ReplaceAt == null
                && !s.Detonated
                && s.ExpiresAt > now
                && s.ResponseAt.HasValue
            )
            .AsEnumerable()
            .Any(s => s.ResponseAt + GhostSiteRules.VisibleCountdown <= now);
        return blastDue ? BlastParticipants(world, system) : new(systems: [system]);
    }

    private TransactionParticipants HackingParticipants(WorldAction world, long pilot)
    {
        if (
            world.Set<SavedHackingAttempt>().Find(pilot) is { } attempt
            && world.Set<SavedHackingContainer>().Find(attempt.ContainerId) is { } container
            && world.Set<SavedExplorationSite>().Find(container.SiteId) is { Kind: (int)ExplorationKind.Ghost } site
        )
        {
            return BlastParticipants(world, site.SystemId, pilot);
        }
        return new(pilots: [pilot]);
    }

    private TransactionParticipants BlastParticipants(WorldAction world, int system, long? actor = null)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilots = PosedPilots(world, system)
            .Where(p => (p.ActiveShip.Position?.SystemId ?? catalog.RequireStation(p.ActiveShip.StationId).SolarSystemId) == system)
            .Select(p => (p.Id, p.ActiveShipId))
            .ToArray();
        var ids = pilots.Select(p => p.Id).ToHashSet();
        if (actor.HasValue)
        {
            ids.Add(actor.Value);
        }
        var targets = pilots
            .SelectMany(p => ShipLossPersistence.LossNpcParticipants(world, database.Path, p.ActiveShipId))
            .Select(id => new SimulationResource(SharedSimulationResource.Npc, id));
        return new(systems: [system], pilots: ids, resources: targets);
    }

    private long AdvanceGhostSite(
        WorldAction world,
        CatalogDbContext catalog,
        SavedExplorationSite site,
        ExplorationDefinition profile,
        long now
    )
    {
        if (site.ResponseAt is not long responseAt)
        {
            return long.MaxValue;
        }
        long explosionAt = responseAt + GhostSiteRules.VisibleCountdown;
        long departureAt = explosionAt + GhostSiteRules.ResponseLifetime;
        if (!site.ResponseSpawned && now >= responseAt)
        {
            site.ResponseSpawned = true;
            if (now < departureAt)
            {
                SpawnResponse(world, catalog, site, profile, now);
            }
        }
        if (!site.Detonated && now >= explosionAt)
        {
            site.Detonated = true;
            var containers = world.Filter<SavedHackingContainer>(c => c.SiteId == site.Id && !c.Destroyed).ToArray();
            foreach (var container in containers.Where(c => !GhostSiteRules.IsBonusCache(profile.Containers[c.Index].TypeId)))
            {
                Detonate(world, catalog, site, profile, container, now);
            }
        }
        if (now >= departureAt)
        {
            RetireResponse(world, site, now);
            return long.MaxValue;
        }
        if (now < responseAt)
        {
            return responseAt;
        }
        return now < explosionAt ? explosionAt : departureAt;
    }

    private static void SpawnResponse(
        WorldAction world,
        CatalogDbContext catalog,
        SavedExplorationSite site,
        ExplorationDefinition profile,
        long now
    )
    {
        // Pinned entry anchor, with an Eden roster because npcGroupingID does not export member types.
        int type = ((NpcPirateFaction)profile.FactionId, profile.Band) switch
        {
            (NpcPirateFaction.Angels, 0) => 16874, // Gistii Ambusher
            (NpcPirateFaction.Angels, 1) => 16875, // Gistum Depredator
            (NpcPirateFaction.Angels, _) => 24007, // Gistatis Tribunus
            (NpcPirateFaction.BloodRaiders, 0) => 16935, // Corpii Collector
            (NpcPirateFaction.BloodRaiders, 1) => 16929, // Corpum Arch Reaver
            (NpcPirateFaction.BloodRaiders, _) => 24011, // Corpatis Fanatic
            (NpcPirateFaction.Guristas, 0) => 17001, // Pithi Plunderer
            (NpcPirateFaction.Guristas, 1) => 16991, // Pithum Inferno
            (NpcPirateFaction.Guristas, _) => 24015, // Pithatis Assaulter
            (NpcPirateFaction.Sansha, 0) => 17068, // Centii Minion
            (NpcPirateFaction.Sansha, 1) => 17075, // Centum Ravisher
            (NpcPirateFaction.Sansha, _) => 24023, // Centatis Behemoth
            (NpcPirateFaction.Serpentis, 0) => 17118, // Coreli Agent
            (NpcPirateFaction.Serpentis, 1) => 17114, // Corelum Chief Spy
            (NpcPirateFaction.Serpentis, _) => 24025, // Corelatis Platoon Leader
            _ => throw new InvalidDataException("Unsupported Covert Research Facility faction."),
        };
        var hull = new NpcProfileCatalog().Read(catalog, type);
        int corporation = checked((int)catalog.Require(CatalogData.Client.Factions, (long)profile.FactionId).CorporationID!.Value);
        const int responseShips = 3;
        for (int slot = 0; slot < responseShips; slot++)
        {
            double z = site.Z + (slot - 1) * NpcSpawnRules.MemberSpacing;
            world.Add(
                new WorldNpc
                {
                    TypeId = type,
                    FactionId = profile.FactionId,
                    CorporationId = corporation,
                    SystemId = site.SystemId,
                    AnchorKind = NpcAnchorKind.GhostSite,
                    EncounterId = site.Id,
                    SpawnSlot = slot,
                    Role = NpcRole.Pirate,
                    X = site.X,
                    Y = site.Y,
                    Z = z,
                    HomeX = site.X,
                    HomeY = site.Y,
                    HomeZ = z,
                    ShieldCharge = hull.Defense.Shield,
                    UpdatedAt = now,
                    MotionAt = now,
                }
            );
        }
    }

    private static void RetireResponse(WorldAction world, SavedExplorationSite site, long now)
    {
        if (!site.ResponseSpawned)
        {
            return;
        }
        foreach (
            var npc in world
                .Filter<WorldNpc>(n =>
                    n.EncounterId == site.Id && n.AnchorKind == NpcAnchorKind.GhostSite && n.RetiredAt == null && n.DestroyedAt == null
                )
                .ToArray()
        )
        {
            npc.RetiredAt = now;
        }
    }

    private void FailHack(WorldAction world, CatalogDbContext catalog, SavedExplorationSite site, SavedHackingContainer container, long now)
    {
        container.Failures++;
        if (site.Kind == (int)ExplorationKind.Ghost)
        {
            Detonate(world, catalog, site, Definition(catalog, site), container, now);
        }
        else
        {
            container.Destroyed = container.Failures >= HackingRules.MaximumFailures;
        }
    }

    private void Detonate(
        WorldAction world,
        CatalogDbContext catalog,
        SavedExplorationSite site,
        ExplorationDefinition profile,
        SavedHackingContainer container,
        long now
    )
    {
        var body = Object(catalog, site, container, profile: profile);
        container.Destroyed = true;
        world.RemoveRange(world.Filter<OwnedItem>(i => i.LocationId == container.Id).ToArray());
        var attempt = world.Filter<SavedHackingAttempt>(a => a.ContainerId == container.Id).SingleOrDefault();
        if (attempt is not null && world.Entry(attempt).State != EntityState.Deleted)
        {
            ReleaseModule(world, attempt);
            world.Remove(attempt);
        }
        double range = GhostSiteRules.ExplosionRange;
        var pilots = PosedPilots(world, site.SystemId).ToArray();
        var damage = GhostSiteRules.Explosion(profile.Band);
        var attacker = new CombatParty(body.Id, null, 0, profile.FactionId, body.TypeId, body.TypeId, null, 0);
        foreach (var pilot in pilots)
        {
            var position = pilot.ActiveShip.Position;
            if (
                position is null
                || (position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId) != site.SystemId
                || new SpacePosition(position.X, position.Y, position.Z).DistanceTo(body.Position) > range
            )
            {
                continue;
            }
            long ship = pilot.ActiveShipId;
            if (
                world.Set<ShipLoss>().Find(ship) is not null
                || world.Set<ShipLoss>().Local.Any(l => l.PilotId == pilot.Id && l.Time == now)
            )
            {
                continue;
            }
            var defense = ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(world, catalog, pilot));
            var result = PilotHealthStore.Apply(world, ship, defense, damage, now);
            PilotInteractionHistoryStore.Damage(world, ship, attacker, result.Applied);
            var loss = result.Remaining.Destroyed
                ? losses.Apply(world, catalog, pilot, ship, site.SystemId, position, now, attacker)
                : null;
            DockedPilot? replacement = null;
            if (loss is not null)
            {
                world.SaveChanges();
                replacement = lookup.FindPilot(world, pilot.AccountId, pilot.Id);
            }
            var notification = new PilotDamage(
                pilot.Id,
                0,
                body.Id,
                new(ship, defense, result.Remaining),
                damage,
                result.Applied,
                1,
                body.TypeId,
                false,
                replacement,
                loss?.Insurance
            )
            {
                InventoryChanges = loss?.Changes ?? [],
            };
            world.AfterCommit(() => damageEvents.Publish(notification));
        }
    }
}
