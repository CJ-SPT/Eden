using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.World.Capsuleers;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    internal CapsuleerModuleCycle? RenewDefense(long pilot, CapsuleerDefenseEquipment module, CapsuleerModuleCycle? previous, long now)
    {
        return Execute(() => RenewDefenseOwned(pilot, module, previous, now), nameof(RenewDefense));
    }

    private CapsuleerModuleCycle? RenewDefenseOwned(long pilot, CapsuleerDefenseEquipment module, CapsuleerModuleCycle? previous, long now)
    {
        using var ownership = owner.Enter();
        if (
            !CanRun
            || !module.Active
            || previous?.Effect != module.Effect
            || module.Effect
                is not (
                    DogmaEffect.ActiveShieldResistance
                    or DogmaEffect.ActiveArmorResistance
                    or DogmaEffect.Afterburner
                    or DogmaEffect.Microwarpdrive
                    or DogmaEffect.SensorBooster
                    or DogmaEffect.TrackingComputer
                    or DogmaEffect.MissileGuidanceComputer
                    or DogmaEffect.OmnidirectionalTrackingLink
                )
        )
        {
            return null;
        }
        var ship = Ship(pilot, now);
        owner.Nanites.RequireAvailable(module.Item.Id, now);
        double cost = module.Attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed);
        double capacitor = ChargedResource.Recharged(
            ship.Capacitor,
            ship.CapacitorCapacity,
            ship.CapacitorRechargeMilliseconds,
            Math.Max(0, (now - ship.CapacitorAt) / (double)TimeSpan.TicksPerMillisecond)
        );
        if (
            ship.Position is null
            || ship.Health.At(now).Destroyed
            || ship.LocalCooldowns.GetValueOrDefault(module.Item.Id) > now
            || !double.IsFinite(cost)
            || cost < 0
            || capacitor + InventoryDefinitions.ResourceTolerance < cost
        )
        {
            throw new InvalidDataException("The sustained defense cycle is unavailable or unpaid.");
        }
        long ready = checked(now + previous.ReadyAt - previous.StartedAt);
        var cooldowns = ship.LocalCooldowns.ToDictionary();
        cooldowns[module.Item.Id] = ready;
        _ships[pilot] = ship with
        {
            LocalCooldowns = cooldowns,
            Capacitor = Math.Max(0, capacitor - cost),
            CapacitorAt = Math.Max(now, ship.CapacitorAt),
        };
        _dirtyShips.Add(pilot);
        owner.ObjectsChanged();
        return previous with { StartedAt = now, ReadyAt = ready };
    }

    internal CapsuleerModuleCycle? DefenseRepair(long pilot, CapsuleerDefenseEquipment module, long now)
    {
        return Execute(() => DefenseRepairOwned(pilot, module, now), nameof(DefenseRepair));
    }

    private CapsuleerModuleCycle? DefenseRepairOwned(long pilot, CapsuleerDefenseEquipment module, long now)
    {
        using var ownership = owner.Enter();
        if (!CanRun || module.Effect is not (DogmaEffect.ShieldBoosting or DogmaEffect.ArmorRepair or DogmaEffect.StructureRepair))
        {
            return null;
        }
        var ship = Ship(pilot, now).SettleRepairs(now);
        if (ship.Position is null || ship.Health.At(now).Destroyed || ship.LocalCooldowns.GetValueOrDefault(module.Item.Id) > now)
        {
            throw new InvalidDataException("The local defense module is unavailable or still cycling.");
        }
        owner.Nanites.RequireAvailable(module.Item.Id, now);
        var values = module.Attributes;
        double cost = values.GetValueOrDefault(DogmaAttributeId.CapacitorNeed);
        double capacitor = ChargedResource.Recharged(
            ship.Capacitor,
            ship.CapacitorCapacity,
            ship.CapacitorRechargeMilliseconds,
            Math.Max(0, (now - ship.CapacitorAt) / (double)TimeSpan.TicksPerMillisecond)
        );
        if (!double.IsFinite(cost) || cost < 0 || capacitor + InventoryDefinitions.ResourceTolerance < cost)
        {
            throw new InvalidDataException("Insufficient capacitor for local defense.");
        }
        long ready;
        if (module.Effect == DogmaEffect.ShieldBoosting)
        {
            ShieldBooster booster = new(
                module.Item.Id,
                module.Item.TypeId,
                values.GetValueOrDefault(DogmaAttributeId.Duration),
                values.GetValueOrDefault(DogmaAttributeId.ShieldBonus),
                cost
            );
            var current = new ShipHealth(ship.ShipId, ship.Health.Defense, ship.Health.At(now));
            double restored = ShieldBoostRules.RestoredShield(current, booster);
            var state = ship.Health with
            {
                Remaining = current.Remaining with { Shield = restored },
                UpdatedAt = Math.Max(now, ship.Health.UpdatedAt),
                Revision = ship.Health.Revision + 1,
            };
            ready = checked(now + (long)Math.Ceiling(booster.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
            ship = ship with { Health = state };
        }
        else
        {
            var layer = module.Effect == DogmaEffect.StructureRepair ? RepairLayer.Hull : RepairLayer.Armor;
            var repair = LocalRepairRules.Define(module.Item.Id, module.Item.TypeId, values, layer);
            ready = LocalRepairRules.CompletesAt(now, repair);
            ship = ship with { Repairs = ship.Repairs.Append(new(module.Item.Id, ready, repair.Amount, layer, now)).ToArray() };
        }
        var cooldowns = ship.LocalCooldowns.ToDictionary();
        cooldowns[module.Item.Id] = ready;
        _ships[pilot] = ship with
        {
            LocalCooldowns = cooldowns,
            Capacitor = Math.Max(0, capacitor - cost),
            CapacitorAt = Math.Max(now, ship.CapacitorAt),
        };
        _dirtyShips.Add(pilot);
        owner.ObjectsChanged();
        return new(module.Item.Id, module.Item.TypeId, module.Effect, 0, now, ready);
    }
}
