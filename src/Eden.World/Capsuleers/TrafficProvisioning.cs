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
    // A preparation batch owns its reservation view. Live commits refresh it under the world owner;
    // failed communities discard it and retries reload committed identities.
    internal sealed record Reservations(HashSet<long> Characters, HashSet<string> Names)
    {
        internal readonly record struct PoolKey(int Region, TrafficSecurityBand Band, bool HighSecurity, bool LightAllowed);

        internal sealed record Pool(TrafficSnapshot.TrafficCandidatePools Candidates)
        {
            internal bool Exhausted { get; set; }
        }

        internal Dictionary<PoolKey, Pool> Pools { get; } = [];

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
        Reservations? reservations = null,
        IReadOnlyList<int>? rosterTypes = null
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
        var roster =
            rosterTypes
            ?? (
                from agent in world.Set<SavedCapsuleerAgent>()
                join pilot in world.Pilots on agent.PilotId equals pilot.Id
                join ship in world.Ships on pilot.ActiveShipId equals ship.Id
                where agent.CommunitySystemId == system && agent.Career == CapsuleerCareer.Hauling
                select ship.TypeId
            ).ToArray();
        bool lightAllowed =
            (roster.Count(type => TrafficRules.LightHull((HullClass)catalog.RequireItem(type).GroupId)) + 1) * 10 <= roster.Count + 1;
        bool Compatible(TrafficProfile profile)
        {
            var hull = catalog.FittingType(profile.HullTypeId);
            return hull is not null
                && TrafficRules.RouteAllows((HullClass)hull.GroupId, visitsHighSecurity)
                && (lightAllowed || !TrafficRules.LightHull((HullClass)hull.GroupId))
                && (cargoVolume == 0 || (hull.Capacity ?? 0) >= cargoVolume);
        }
        int Group(TrafficProfile profile) => catalog.RequireItem(profile.HullTypeId).GroupId;
        if (reservations is null || cargoVolume != 0)
        {
            var candidates = CandidatePools(database, catalog, system, route, cargoVolume, roster).Candidates(seed, system, slot);
            return candidates.FirstOrDefault(profile => !reserved.Contains(profile.CharacterId) && !names.Contains(profile.Name));
        }
        Reservations.PoolKey key = new(region, band, visitsHighSecurity, lightAllowed);
        if (!reservations.Pools.TryGetValue(key, out var prepared))
        {
            prepared = new(database.Traffic.PrepareCandidates(region, band, Compatible, Group));
            reservations.Pools.Add(key, prepared);
        }
        if (prepared.Exhausted)
        {
            return null;
        }
        var selected = prepared
            .Candidates.Candidates(seed, system, slot)
            .FirstOrDefault(profile => !reserved.Contains(profile.CharacterId) && !names.Contains(profile.Name));
        // Reservations only grow during stopped-world preparation; an exhausted pool stays exhausted.
        prepared.Exhausted = selected is null;
        return selected;
    }

    internal static TrafficSnapshot.TrafficCandidatePools CandidatePools(
        WorldDatabase database,
        CatalogDbContext catalog,
        int system,
        CapsuleerTrafficRoute route,
        double cargoVolume,
        IReadOnlyList<int> roster
    )
    {
        int region = catalog.RequireSystem(system).RegionId;
        var security = catalog.Definitions.SystemSecurity;
        var band = TrafficRules.SecurityBand(security.GetValueOrDefault(system));
        bool high = route.Systems.Any(id => TrafficRules.SecurityBand(security.GetValueOrDefault(id)) == TrafficSecurityBand.High);
        bool light =
            (roster.Count(type => TrafficRules.LightHull((HullClass)catalog.RequireItem(type).GroupId)) + 1) * 10 <= roster.Count + 1;
        return database.Traffic.RuntimeCandidates(
            catalog.Definitions.Identity.Generation,
            region,
            band,
            high,
            light,
            profile =>
                catalog.FittingType(profile.HullTypeId) is { } hull
                && TrafficRules.RouteAllows((HullClass)hull.GroupId, high)
                && (light || !TrafficRules.LightHull((HullClass)hull.GroupId))
                && (cargoVolume == 0 || (hull.Capacity ?? 0) >= cargoVolume),
            profile => catalog.RequireItem(profile.HullTypeId).GroupId,
            cargoVolume
        );
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
        bool generated = CapsuleerProvisioningRules.MatchesName(
            seed,
            agent.CommunitySystemId,
            agent.Career,
            agent.Slot,
            pilot.Name,
            Math.Min(attempts, CapsuleerProvisioningRules.MaximumNameAttempts)
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
