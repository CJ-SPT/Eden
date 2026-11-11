using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
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

internal static partial class TrafficCorrection
{
    internal static bool Apply(WorldDatabase database, WorldAction world, CatalogDbContext catalog, long pilotId, long now)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("traffic.correctTemplate");
        if (world.Set<SavedTrafficCorrection>().Find(pilotId) is not null)
        {
            return false;
        }
        var identity = world.Set<SavedTrafficIdentity>().Find(pilotId);
        var agent = world.Set<SavedCapsuleerAgent>().Find(pilotId);
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
        var ship = pilot.ActiveShip;
        if (
            identity is null
            || agent is null
            || ship.Id != agent.FounderShipId
            || ship.Position is not null
            || catalog.RequireItem(ship.TypeId).GroupId != (int)HullClass.Corvette
            || world.Set<ShipLoss>().Any(loss => loss.ShipId == ship.Id)
        )
        {
            return false;
        }
        var original = database.Traffic.Profiles.FirstOrDefault(profile => profile.KillId == identity.KillId);
        if (original is null || original.HullTypeId != ship.TypeId || pilot.Name != original.Name || ship.Name != original.Name)
        {
            return false;
        }
        var health = world.Set<ShipHealthState>().Find(ship.Id);
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
        if (
            health is not null
            && (
                health.ArmorDamage != 0
                || health.StructureDamage != 0
                || health.ShieldCharge + InventoryDefinitions.ResourceTolerance
                    < attributes.GetValueOrDefault(DogmaAttributeId.ShieldCapacity)
            )
        )
        {
            return false;
        }
        var fitted = world
            .Set<OwnedItem>()
            .Where(i => i.LocationId == ship.Id)
            .ToArray()
            .Where(i => InventoryDefinitions.IsFittedSlot(i.FlagId))
            .ToArray();
        var moduleIds = fitted.Select(i => i.Id).ToArray();
        var charges = world.Set<OwnedItem>().Where(i => Enumerable.Contains(moduleIds, i.LocationId)).ToArray();
        var actual = fitted
            .Select(i => $"{i.FlagId}:{i.TypeId}:1")
            .Concat(charges.Select(i => $"{fitted.Single(m => m.Id == i.LocationId).FlagId}:{i.TypeId}:{i.Quantity}"))
            .Order()
            .ToArray();
        var expected = original.Equipment.Select(i => $"{i.Flag}:{i.TypeId}:{i.Quantity}").Order().ToArray();
        if (!actual.SequenceEqual(expected) || fitted.Any(i => !i.Online))
        {
            return false;
        }
        double cargo = InventoryPersistence.UsedVolume(world, catalog, pilotId, ship.Id, (int)InventoryFlag.Cargo);
        double drones = InventoryPersistence.UsedVolume(world, catalog, pilotId, ship.Id, (int)InventoryFlag.DroneBay);
        var template = CandidatePools(database, catalog, agent.CommunitySystemId, cargo, drones)
            .Candidates(0, agent.CommunitySystemId, agent.Slot)
            .FirstOrDefault();
        if (template is null)
        {
            return false;
        }
        world.Add(
            new SavedTrafficCorrection
            {
                PilotId = pilotId,
                ShipId = ship.Id,
                TemplateKillId = template.KillId,
                OriginalHullTypeId = ship.TypeId,
            }
        );
        world.RemoveRange(charges);
        world.RemoveRange(fitted);
        world.SaveChanges();
        TrafficFitting.Apply(world, catalog, pilotId, ship.Id, template, now);
        if (health is not null)
        {
            world.Remove(health);
        }
        world.SaveChanges();
        return true;
    }
}
