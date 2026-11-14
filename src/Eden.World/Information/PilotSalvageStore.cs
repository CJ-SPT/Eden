using System.Security.Cryptography;
using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Information;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Information;

internal sealed partial class PilotInformationStore
{
    private static SalvageResult Salvage(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        long targetId,
        InformationModule module,
        long now
    )
    {
        var wreck = world.Set<WorldWreck>().Find(targetId)!;
        if (wreck.SalvagedAt is not null || wreck.SalvageJson is null)
        {
            throw new InvalidDataException("This wreck has already been salvaged or has no verified salvage material pool.");
        }
        double difficulty = catalog.FittingAttributes(wreck.TypeId).GetValueOrDefault(DogmaAttributeId.AccessDifficulty);
        double chance = InformationRules.SalvageChance(module.AccessBonus, difficulty);
        if (chance <= 0)
        {
            throw new InvalidDataException("This wreck is too difficult for the fitted salvager.");
        }
        double draw = RandomNumberGenerator.GetInt32(SalvageLootRules.AccessRollResolution) / (double)SalvageLootRules.AccessRollResolution;
        if (draw >= chance)
        {
            return new(false, []);
        }
        var drops =
            JsonSerializer.Deserialize<ScanItem[]>(wreck.SalvageJson) ?? throw new InvalidDataException("Invalid saved salvage manifest.");
        double used = InventoryPersistence.UsedVolume(world, catalog, pilot.Id, pilot.ActiveShipId, (int)InventoryFlag.Cargo);
        double volume = drops.Sum(d => PilotInventoryStore.Volume(catalog, d.TypeId) * d.Quantity);
        double capacity = PilotFittingStore.ShipAttributes(world, catalog, pilot).GetValueOrDefault(DogmaAttributeId.CargoCapacity);
        if (used + volume > capacity)
        {
            throw new CargoCapacityException(Math.Max(0, capacity - used), volume);
        }
        List<InventoryChange> changes = [];
        foreach (var drop in drops)
        {
            changes.Add(
                InventoryPersistence.MergeOutput(
                    world,
                    catalog,
                    pilot.Id,
                    pilot.ActiveShipId,
                    (int)InventoryFlag.Cargo,
                    drop.TypeId,
                    drop.Quantity
                )
            );
        }
        wreck.SalvagedAt = now;
        bool occupied = world.Set<OwnedItem>().Any(i => i.LocationId == wreck.Id) || world.Ships.Any(s => s.StoredInId == wreck.Id);
        if (occupied)
        {
            _ = catalog.RequireItem(SalvagePersistence.CargoContainerType);
            wreck.TypeId = SalvagePersistence.CargoContainerType;
            wreck.Name = "Cargo Container";
        }
        else
        {
            wreck.ExpiresAt = now;
        }
        return new(true, changes);
    }
}
