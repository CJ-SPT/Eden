using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Drones;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class ShipDogmaBuilder(
    TimeProvider clock,
    IPilotInventory inventory,
    IPilotShipResources resources,
    IPilotFitting fitting,
    IPilotMining mining,
    IPilotCombat combat,
    IPilotShieldBoosting boosting,
    IPilotPropulsion propulsion,
    IPilotAmmunition ammunition,
    MagazineWire magazinesWire,
    PilotBrain brain,
    IEnumerable<IModuleEffectSource> moduleEffects,
    IPilotCharacterSheet sheet,
    IPilotJumpCloneSkills clones,
    IPilotWeaponBanks banks,
    IPilotDrones? droneStore = null
)
{
    private static InventoryRow Item(InventoryItem item) => InventoryRow.From(item);

    internal static InventoryRow Item(DockedPilot pilot, string kind) =>
        kind switch
        {
            "ship" => new(
                pilot.ShipId,
                pilot.ShipTypeId,
                pilot.PilotId,
                pilot.LocationId,
                pilot.Position is null ? (int)InventoryFlag.Hangar : (int)InventoryFlag.AutoFit,
                InventoryWireDefinitions.AssembledQuantity,
                pilot.ShipGroupId,
                pilot.ShipCategoryId
            ),
            // Build 3662168: character inventory type 1373, pilot flag 57, group/category 1.
            "pilot" => new(pilot.PilotId, 1373, pilot.PilotId, pilot.ShipId, 57, InventoryWireDefinitions.AssembledQuantity, 1, 1),
            _ => new(
                pilot.StationId,
                pilot.StationTypeId,
                pilot.StationOwnerId,
                pilot.SystemId,
                (int)InventoryFlag.AutoFit,
                InventoryWireDefinitions.AssembledQuantity,
                pilot.Station.GroupId,
                pilot.Station.CategoryId
            ),
        };

    internal DogmaItemInfo<int> PrimePilot(DockedPilot pilot)
    {
        var cloneSkills = clones.ReadCloneSkills(pilot.PilotId);
        var control = droneStore?.Control(pilot.PilotId);
        return new(
            new DogmaItemKey.Item(pilot.PilotId),
            Item(pilot, "pilot"),
            sheet
                .ReadEffectiveAttributes(pilot.PilotId)
                .ToDogma()
                .Concat(
                    new Dictionary<DogmaAttributeId, int>
                    {
                        [DogmaAttributeId.MaxJumpClones] = cloneSkills.Capacity,
                        [DogmaAttributeId.CloneJumpCooldown] = cloneSkills.CooldownHours,
                        [DogmaAttributeId.MaxActiveDrones] = control?.MaximumDrones ?? 0,
                        [DogmaAttributeId.DroneControlDistance] = control?.Range ?? (int)DroneRules.BaseControlRange,
                    }
                )
                .ToDictionary(),
            new Dictionary<DogmaEffect, DogmaActiveEffect>(),
            clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()
        );
    }

    internal DogmaItemInfo<double> PrimeShip(DockedPilot pilot)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var snapshot = resources.Read(pilot.PilotId, pilot.ShipId, now);
        return PrimeShip(pilot, snapshot, now);
    }

    private DogmaItemInfo<double> PrimeShip(DockedPilot pilot, ShipResourceSnapshot snapshot, long now)
    {
        var attributes = snapshot.Attributes.ToDictionary(a => a.Key, a => a.Value);
        var current = snapshot.Health;
        attributes[DogmaAttributeId.Damage] = current.Defense.Structure - current.Remaining.Structure;
        attributes[DogmaAttributeId.ArmorDamage] = current.Defense.Armor - current.Remaining.Armor;
        attributes[DogmaAttributeId.CapacitorCharge] = snapshot.Capacitor;
        attributes[DogmaAttributeId.MaxVelocity] = pilot.Flight.MaxSpeed;
        attributes[DogmaAttributeId.Mass] = pilot.Flight.Mass;
        attributes[DogmaAttributeId.CargoCapacity] = pilot.CargoCapacity;
        attributes[DogmaAttributeId.ShieldCharge] = current.Remaining.Shield;
        if (droneStore?.Control(pilot.PilotId) is { } control)
        {
            attributes[DogmaAttributeId.DroneBandwidthLoad] = control.UsedBandwidth;
            attributes[DogmaAttributeId.DroneIsAggressive] = control.Settings.Aggressive ? 1 : 0;
            attributes[DogmaAttributeId.DroneFocusFire] = control.Settings.FocusFire ? 1 : 0;
        }
        return new(
            new DogmaItemKey.Item(pilot.ShipId),
            Item(pilot, "ship"),
            attributes,
            new Dictionary<DogmaEffect, DogmaActiveEffect>(),
            now
        );
    }

    internal DogmaItemInfo<double> PrimeModule(DockedPilot pilot, InventoryItem module)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var attributes = (
            module.GroupId == PropulsionDefinitions.AfterburnerGroup ? propulsion.ModuleAttributes(pilot.PilotId, module)
            : module.GroupId == ShieldBoostDefinitions.Group ? boosting.ModuleAttributes(pilot.PilotId, module)
            : module.TypeId == (int)CombatWeaponType.CivilianGatlingRailgun
            || module.GroupId
                is CombatDefinitions.EnergyWeaponGroup
                    or CombatDefinitions.HybridWeaponGroup
                    or CombatDefinitions.ProjectileWeaponGroup
                ? combat.ModuleAttributes(pilot.PilotId, module)
            : InventoryDefinitions.IsOreMiningGroup(module.GroupId) ? mining.ModuleAttributes(pilot.PilotId, module)
            : fitting.ModuleAttributes(pilot.PilotId, module)
        ).ToDictionary(a => a.Key, a => a.Value);
        attributes[DogmaAttributeId.IsOnline] = module.Online ? 1d : 0d;
        attributes[DogmaAttributeId.Damage] = module.CrystalDamage;
        if (module.CategoryId == (int)InventoryCategory.Drone && droneStore?.Profile(pilot.PilotId, module.TypeId) is { } drone)
        {
            foreach (var (attribute, value) in DroneWire.Attributes(drone, attributes))
            {
                attributes[attribute] = value;
            }
        }
        if (AmmunitionDefinitions.IsCrystalGroup(module.GroupId))
        {
            attributes[DogmaAttributeId.Quantity] = module.Quantity;
        }
        Dictionary<DogmaEffect, DogmaActiveEffect> effects = [];
        if (module.Online)
        {
            effects[DogmaEffect.Online] = new(module.Id, pilot.PilotId, pilot.ShipId, null, DogmaEffect.Online, now, null, 0);
        }
        foreach (var source in moduleEffects)
        {
            if (source.ActiveEffect(pilot, module.Id) is { } active)
            {
                effects[active.Effect] = active;
            }
        }
        return new(new DogmaItemKey.Item(module.Id), Item(module), attributes, effects, now);
    }

    internal PilotCallResult DroneBayDamage(DockedPilot pilot, IReadOnlySet<long> ids)
    {
        var drones = inventory
            .List(pilot.PilotId, pilot.ShipId)
            .Where(item =>
                ids.Contains(item.Id) && item.FlagId == (int)InventoryFlag.DroneBay && item.CategoryId == (int)InventoryCategory.Drone
            )
            .ToArray();
        if (drones.Length != ids.Count)
        {
            return new PilotCallResult.Failure("Read damage only for drones in your active ship's drone bay.");
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var health = droneStore?.BayHealth(pilot.PilotId, pilot.ShipId, ids);
        return new BlueMap<long, StoredDroneDamage>(
            drones.ToDictionary(
                item => item.Id,
                item => new StoredDroneDamage(
                    ChargedResource.TimeConstant(fitting.Attributes(item.TypeId).GetValueOrDefault(DogmaAttributeId.ShieldRechargeTime)),
                    now,
                    health?.GetValueOrDefault(item.Id)
                )
            )
        );
    }

    internal DogmaAllInfo AllInfo(DockedPilot pilot)
    {
        var modules = inventory.List(pilot.PilotId, pilot.ShipId).Where(i => InventoryDefinitions.IsFittedSlot(i.FlagId)).ToArray();
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var snapshot = resources.Read(pilot.PilotId, pilot.ShipId, now);
        var shipInfo = new Dictionary<long, DogmaItemInfo<double>> { [pilot.ShipId] = PrimeShip(pilot, snapshot, now) };
        var current = snapshot.Health;
        var instances = Instances(pilot, snapshot);
        foreach (var module in modules)
        {
            shipInfo[module.Id] = PrimeModule(pilot, module);
            instances[module.Id] = new(module.Id, module.Online);
        }
        var magazines = ammunition.Read(pilot.PilotId, pilot.ShipId);
        Dictionary<long, IReadOnlyDictionary<int, MagazineQuantity>> quantities = [];
        if (magazines.Count > 0)
        {
            quantities[pilot.ShipId] = magazines.ToDictionary(m => m.FlagId, m => new MagazineQuantity(m));
        }
        return new(
            new(instances, quantities, banks.Read(pilot.PilotId, pilot.ShipId)),
            shipInfo,
            magazines.ToDictionary(m => new DogmaItemKey.Charge(m.ShipId, m.FlagId, m.TypeId), m => magazinesWire.Prime(pilot.PilotId, m)),
            PrimePilot(pilot),
            brain.Read(pilot.PilotId, pilot.ShipId)
        );
    }

    internal DogmaShipState ShipState(DockedPilot pilot)
    {
        var snapshot = resources.Read(pilot.PilotId, pilot.ShipId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        var instances = Instances(pilot, snapshot);
        foreach (var module in inventory.List(pilot.PilotId, pilot.ShipId).Where(i => InventoryDefinitions.IsFittedSlot(i.FlagId)))
        {
            instances[module.Id] = new(module.Id, module.Online);
        }
        var magazines = ammunition.Read(pilot.PilotId, pilot.ShipId);
        Dictionary<long, IReadOnlyDictionary<int, MagazineQuantity>> quantities = [];
        if (magazines.Count > 0)
        {
            quantities[pilot.ShipId] = magazines.ToDictionary(m => m.FlagId, m => new MagazineQuantity(m));
        }
        return new(instances, quantities, banks.Read(pilot.PilotId, pilot.ShipId));
    }

    private static Dictionary<long, DogmaInstanceState> Instances(DockedPilot pilot, ShipResourceSnapshot snapshot)
    {
        var current = snapshot.Health;
        return new()
        {
            [pilot.PilotId] = new(pilot.PilotId),
            [pilot.ShipId] = new(
                pilot.ShipId,
                HullDamage: current.Defense.Structure - current.Remaining.Structure,
                Capacitor: snapshot.Capacitor,
                ArmorDamage: current.Defense.Armor - current.Remaining.Armor,
                Shield: current.Remaining.Shield
            ),
        };
    }
}
