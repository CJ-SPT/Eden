using Eden.Data.Catalog.Capsuleers;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal static class TrafficProvisioning
{
    // Startup owns the stopped world. A failed community discards this reservation view;
    // the next attempt reloads only committed identities and never reserves a lost grant.
    internal sealed record Reservations(HashSet<long> Characters, HashSet<string> Names)
    {
        internal static Reservations Load(WorldDbContext world) =>
            new(
                world.Set<SavedTrafficIdentity>().Select(value => value.CharacterId).ToHashSet(),
                world.Pilots.Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            );
    }

    internal static bool HasFallback(CatalogDbContext catalog) =>
        catalog.FittingType(HaulingRules.BadgerType) is not null && CapsuleerProvisioningCatalog.FreightOre(catalog) != 0;

    internal static TrafficProfile? Select(
        WorldDatabase database,
        WorldDbContext world,
        CatalogDbContext catalog,
        long seed,
        int system,
        int slot,
        CapsuleerTrafficRoute route,
        double cargoVolume = 0,
        Reservations? reservations = null
    )
    {
        if (database.Traffic.Profiles.Count == 0)
        {
            return null;
        }
        int region = catalog.RequireSystem(system).RegionId;
        var security = catalog.Definitions.SystemSecurity;
        var band = TrafficRules.SecurityBand(security.GetValueOrDefault(system));
        bool visitsHighSecurity = route.Systems.Any(id =>
            TrafficRules.SecurityBand(security.GetValueOrDefault(id)) == TrafficSecurityBand.High
        );
        var reserved = reservations?.Characters ?? world.Set<SavedTrafficIdentity>().Select(value => value.CharacterId).ToHashSet();
        var names = reservations?.Names ?? world.Pilots.Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roster = (
            from agent in world.Set<SavedCapsuleerAgent>()
            join pilot in world.Pilots on agent.PilotId equals pilot.Id
            join ship in world.Ships on pilot.ActiveShipId equals ship.Id
            where agent.CommunitySystemId == system && agent.Career == CapsuleerCareer.Hauling
            select ship.TypeId
        ).ToArray();
        bool lightAllowed =
            (roster.Count(type => TrafficRules.LightHull((HullClass)catalog.RequireItem(type).GroupId)) + 1) * 10 <= roster.Length + 1;
        bool Compatible(TrafficProfile profile)
        {
            var hull = catalog.FittingType(profile.HullTypeId);
            return hull is not null
                && TrafficRules.RouteAllows((HullClass)hull.GroupId, visitsHighSecurity)
                && (lightAllowed || !TrafficRules.LightHull((HullClass)hull.GroupId))
                && (cargoVolume == 0 || (hull.Capacity ?? 0) >= cargoVolume);
        }
        return database
            .Traffic.Candidates(seed, system, slot, region, band, Compatible, profile => catalog.RequireItem(profile.HullTypeId).GroupId)
            .FirstOrDefault(profile => !reserved.Contains(profile.CharacterId) && !names.Contains(profile.Name));
    }

    internal static void Record(WorldDatabase database, WorldDbContext world, long pilot, TrafficProfile profile)
    {
        world.Add(
            new SavedTrafficIdentity
            {
                PilotId = pilot,
                CharacterId = profile.CharacterId,
                KillId = profile.KillId,
                SnapshotId = database.Traffic.Manifest!.Identity,
            }
        );
        world.SaveChanges();
    }

    internal static bool Refresh(
        WorldDatabase database,
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        CapsuleerTrafficRoute route,
        long now
    )
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("traffic.refreshTemplate");
        var agent = world.Set<SavedCapsuleerAgent>().Find(pilotId);
        if (agent is null || agent.Career != CapsuleerCareer.Hauling || world.Set<SavedTrafficIdentity>().Find(pilotId) is not null)
        {
            return false;
        }
        var pilot = world
            .Pilots.Include(value => value.ActiveShip)
                .ThenInclude(value => value.Position)
            .Single(value => value.Id == pilotId);
        var ship = pilot.ActiveShip;
        if (
            ship.Id != agent.FounderShipId
            || ship.TypeId != HaulingRules.BadgerType
            || ship.Position is not null
            || ship.OwnerId != pilotId
            || ship.Name != pilot.Name
            || pilot.AccountId != CapsuleerCommunityStore.MinerAccountId
            || world.Set<OwnedItem>().Any(item => item.LocationId == ship.Id && item.FlagId != (int)InventoryFlag.Cargo)
            || world.Set<ShipLoss>().Any(loss => loss.ShipId == ship.Id)
        )
        {
            return false;
        }
        var health = world.Set<ShipHealthState>().Find(ship.Id);
        if (
            health is not null
            && (
                health.ArmorDamage != 0
                || health.StructureDamage != 0
                || health.ShieldCharge + InventoryDefinitions.ResourceTolerance
                    < PilotFittingStore.ShipAttributes(world, catalog, pilot).GetValueOrDefault(DogmaAttributeId.ShieldCapacity)
            )
        )
        {
            return false;
        }
        long seed = world.Set<SavedCapsuleerProvisioning>().Single().WorldSeed;
        // Provisioning probes names in order, so a finite occupied-name bound also
        // recognizes collision suffixes without scanning the whole name space.
        int attempts = world.Pilots.Count() + 1;
        bool generated = Enumerable
            .Range(0, Math.Min(attempts, CapsuleerProvisioningRules.MaximumNameAttempts))
            .Any(attempt =>
                CapsuleerProvisioningRules.Name(seed, agent.CommunitySystemId, agent.Career, agent.Slot, attempt) == pilot.Name
            );
        if (!generated)
        {
            return false;
        }
        double cargo = InventoryPersistence.UsedVolume(world, catalog, pilotId, ship.Id, (int)InventoryFlag.Cargo);
        var profile = Select(database, world, catalog, seed, agent.CommunitySystemId, agent.Slot, route, cargo);
        if (profile is null)
        {
            return false;
        }
        TrafficFitting.Apply(world, catalog, pilotId, ship.Id, profile, now);
        pilot.Name = profile.Name;
        ship.Name = profile.Name;
        Record(database, world, pilotId, profile);
        return true;
    }
}
