using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Thermal;

namespace Eden.World.Propulsion;

internal sealed partial class PilotInterdictionStore
{
    public InterdictionActivation ActivateNullifier(long pilotId, long shipId, long moduleId, long now)
    {
        using var transaction = Live.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        var world = (WorldAction)transaction;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Find(pilotId);
        var item = world.Set<OwnedItem>().Find(moduleId);
        if (
            pilot?.ActiveShipId != shipId
            || pilot.ActiveShip.Position is null
            || item is not { Online: true, Singleton: true }
            || item.OwnerId != pilotId
            || item.LocationId != shipId
            || !InventoryDefinitions.IsHighSlot(item.FlagId)
            || !InterdictionCatalog.Nullifier(catalog, item.TypeId)
        )
        {
            throw new InvalidDataException("Use an online interdiction nullifier on your active ship in space.");
        }
        // The pinned effect is warp-safe and can be activated while cloaked. It does not break the cloak.
        PilotFittingStore.CheckHull(catalog, item.TypeId, pilot.ActiveShip.TypeId);
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, moduleId, now);
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown?.ReadyAt > now)
        {
            throw new InvalidDataException("The interdiction nullifier is still cycling or reactivating.");
        }
        var calculation = FittingCalculation.Read(world, catalog, pilot);
        var module = InterdictionNullifier.Define(calculation.Graph.Attributes(moduleId));
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
        if (PilotHealthStore.Read(world, shipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot activate an interdiction nullifier.");
        }
        long expires = InterdictionRules.Deadline(now, module.Duration);
        long ready = checked(expires + (long)Math.Ceiling(module.Reactivation * TimeSpan.TicksPerMillisecond));
        double capacitor = ShipResources.SpendCapacitor(world, shipId, ship, module.Capacitor, now);
        var active = world.Set<ActiveFittedModule>().Find(moduleId) ?? world.Add(new ActiveFittedModule { ModuleId = moduleId }).Entity;
        active.ShipId = shipId;
        active.ExpiresAt = expires;
        cooldown ??= world.Add(new ModuleCooldown { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = ready;
        transaction.Commit();
        return new(moduleId, item.TypeId, DogmaEffect.InterdictionNullifier, module.Duration, expires, ready, capacitor);
    }

    private static bool Immune(WorldAction world, CatalogDbContext catalog, long shipId, long now)
    {
        var ship = world.Ships.Find(shipId);
        if (ship is null)
        {
            return false;
        }
        var equipped = world
            .Filter<OwnedItem>(item =>
                item.LocationId == shipId
                && item.Singleton
                && (
                    item.FlagId >= (int)InventoryFlag.LowSlot0 && item.FlagId <= (int)InventoryFlag.HighSlot7
                    || item.FlagId >= (int)InventoryFlag.RigSlot0 && item.FlagId <= (int)InventoryFlag.RigSlot2
                )
            )
            .ToArray();
        // The wiki's fitted-gear exclusions apply even to offline burst jammers. Higgs' pinned post-assignment also
        // overrides the active module in the fitting graph; checking membership here covers imported/offline fits.
        if (
            equipped.Any(item =>
                catalog.FittingType(item.TypeId)?.GroupId == (int)HullBonusEquipmentGroup.BurstJammer
                || catalog.FittingAttributes(item.TypeId).TryGetValue(DogmaAttributeId.WarpBubbleImmuneModifier, out double value)
                    && value == 0
            )
        )
        {
            return false;
        }
        bool innate = catalog.FittingAttributes(ship.TypeId).GetValueOrDefault(DogmaAttributeId.WarpBubbleImmune) > 0;
        return innate
            || equipped.Any(item =>
                item.Online
                && InterdictionCatalog.Nullifier(catalog, item.TypeId)
                && world.Set<ActiveFittedModule>().Find(item.Id) is { } active
                && active.ShipId == shipId
                && active.ExpiresAt > now
            );
    }
}
