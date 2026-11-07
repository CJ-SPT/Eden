using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Inventory;
using Eden.World.Storage;

namespace Eden.World.Drones;

internal static class DroneNpcCombat
{
    internal static NpcShot? TryFire(
        WorldDbContext world,
        CatalogDbContext catalog,
        WorldNpc npc,
        NpcAggression aggression,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        long shipId,
        long now,
        double roll,
        out bool droneTargetAvailable
    )
    {
        droneTargetAvailable = false;
        if (now < aggression.ReadyAt || !NpcPopulationRules.HasDirectWeapon(attributes))
        {
            return null;
        }
        var drone = world
            .Set<WorldDrone>()
            .Where(d =>
                d.ControllerShipId == shipId && d.TargetId == npc.Id && d.Activity == DroneActivity.Fighting && d.SystemId == npc.SystemId
            )
            .AsEnumerable()
            .OrderBy(d => d.ItemId)
            .Select(d => (Saved: d, Contact: PilotDroneStore.Contact(world, catalog, d)))
            .FirstOrDefault(d =>
                d.Contact.Position.DistanceTo(NpcMotion.Position(npc))
                    - d.Contact.Profile.Radius
                    - (catalog.FittingType(npc.TypeId)?.Radius ?? 0)
                <= attributes.GetValueOrDefault(DogmaAttributeId.EntityAttackRange)
            );
        if (drone.Saved is null)
        {
            return null;
        }
        droneTargetAvailable = true;
        if (roll >= DroneRules.NpcRetaliationChance)
        {
            return null;
        }
        var contact = drone.Contact;
        var targetAttributes = catalog.FittingAttributes(contact.Profile.TypeId);
        var damage = new DamageVector(
            attributes.GetValueOrDefault(DogmaAttributeId.EmDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.ExplosiveDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.KineticDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.ThermalDamage)
        ).Scale(attributes.GetValueOrDefault(DogmaAttributeId.DamageMultiplier));
        var weaponProfile = Weapon(attributes);
        var retaliation = DroneRules.Retaliation(
            contact,
            NpcMotion.Position(npc),
            NpcMotion.Velocity(npc),
            targetAttributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
            weaponProfile,
            roll
        );
        var result = retaliation.Result;
        drone.Saved.Shield = result.Remaining.Shield;
        drone.Saved.ArmorDamage = contact.Profile.Defense.Armor - result.Remaining.Armor;
        drone.Saved.StructureDamage = contact.Profile.Defense.Structure - result.Remaining.Structure;
        double duration = weaponProfile.CycleMilliseconds;
        if (!double.IsFinite(duration) || duration <= 0)
        {
            throw new InvalidDataException("Invalid NPC weapon cycle attributes.");
        }
        aggression.ReadyAt = checked(now + (long)Math.Ceiling(duration * TimeSpan.TicksPerMillisecond));
        aggression.LastShotAt = now;
        aggression.LastTargetId = contact.Id;
        NpcWeaponContact weapon = new(contact.Id, (int)attributes.GetValueOrDefault(DogmaAttributeId.EntityTurretType), now, duration);
        if (result.Remaining.Destroyed)
        {
            drone.Saved.SystemId = null;
            drone.Saved.ControllerShipId = 0;
            drone.Saved.TargetId = 0;
            var item = world.Set<OwnedItem>().Find(contact.Id)!;
            item.LocationId = (int)InventoryLocation.Graveyard;
            item.FlagId = (int)InventoryFlag.AutoFit;
        }
        return new(
            npc.Id,
            npc.TypeId,
            weapon,
            damage,
            result.Applied,
            retaliation.Quality,
            new(contact.Id, contact.Profile.Defense, result.Remaining),
            null
        );
    }

    internal static DroneNpcWeapon Weapon(IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        var damage = new DamageVector(
            attributes.GetValueOrDefault(DogmaAttributeId.EmDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.ExplosiveDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.KineticDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.ThermalDamage)
        ).Scale(attributes.GetValueOrDefault(DogmaAttributeId.DamageMultiplier));
        return new DroneNpcWeapon(
            damage,
            attributes.GetValueOrDefault(DogmaAttributeId.MaxRange),
            attributes.GetValueOrDefault(DogmaAttributeId.AccuracyFalloff),
            attributes.GetValueOrDefault(DogmaAttributeId.TrackingSpeed),
            attributes.GetValueOrDefault(DogmaAttributeId.WeaponSignatureResolution),
            attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime)
        );
    }
}
