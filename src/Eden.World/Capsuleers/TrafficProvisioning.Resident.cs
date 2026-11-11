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
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal static partial class TrafficProvisioning
{
    internal static TrafficProfile? Select(
        WorldDatabase database,
        WorldAction world,
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
        bool ReservedCharacter(long character) => reservations?.Characters.Contains(character) ?? world.TrafficCharacterTaken(character);
        bool ReservedName(string name) => reservations?.Names.Contains(name) ?? world.HasPilotName(name);
        var roster =
            rosterTypes
            ?? world
                .Set<SavedCapsuleerAgent>()
                .Where(agent => agent.CommunitySystemId == system && agent.Career == CapsuleerCareer.Hauling)
                .AsEnumerable()
                .Select(agent => world.Pilots.Where(p => p.Id == agent.PilotId).Select(p => p.ActiveShipId).Single())
                .Select(ship => world.Ships.Where(s => s.Id == ship).Select(s => s.TypeId).Single())
                .ToArray();
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
            return candidates.FirstOrDefault(profile => !ReservedCharacter(profile.CharacterId) && !ReservedName(profile.Name));
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
            .FirstOrDefault(profile => !ReservedCharacter(profile.CharacterId) && !ReservedName(profile.Name));
        // Reservations only grow during stopped-world preparation; an exhausted pool stays exhausted.
        prepared.Exhausted = selected is null;
        return selected;
    }

    internal static void Record(WorldDatabase database, WorldAction world, long pilot, TrafficProfile profile)
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
        WorldAction world,
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
        var pilot = world.Pilots.Single(value => value.Id == pilotId);
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
        int attempts = world.PilotCount + 1;
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
