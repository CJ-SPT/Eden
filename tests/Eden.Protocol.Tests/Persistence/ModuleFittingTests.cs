using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class ModuleFittingTests
{
    [Fact]
    public void PropulsionGroupRequiresASupportedCatalogActivation()
    {
        DogmaEffect[] rack = [DogmaEffect.MediumPower, DogmaEffect.Online];
        Assert.Null(
            ModuleFittingRules.Define(
                434,
                PropulsionDefinitions.AfterburnerGroup,
                new Dictionary<DogmaAttributeId, double>(),
                rack.Append(DogmaEffect.Microwarpdrive).ToHashSet()
            )
        );
        Assert.NotNull(
            ModuleFittingRules.Define(
                434,
                PropulsionDefinitions.AfterburnerGroup,
                new Dictionary<DogmaAttributeId, double> { [DogmaAttributeId.TechLevel] = 1 },
                rack.Append(DogmaEffect.Microwarpdrive).ToHashSet()
            )
        );
        Assert.NotNull(
            ModuleFittingRules.Define(
                440,
                PropulsionDefinitions.AfterburnerGroup,
                new Dictionary<DogmaAttributeId, double> { [DogmaAttributeId.TechLevel] = 2 },
                rack.Append(DogmaEffect.Microwarpdrive).ToHashSet()
            )
        );
        Assert.NotNull(
            ModuleFittingRules.Define(
                439,
                PropulsionDefinitions.AfterburnerGroup,
                new Dictionary<DogmaAttributeId, double>(),
                rack.Append(DogmaEffect.Afterburner).ToHashSet()
            )
        );
        Assert.Null(
            ModuleFittingRules.Define(
                439,
                PropulsionDefinitions.AfterburnerGroup,
                new Dictionary<DogmaAttributeId, double>(),
                new HashSet<DogmaEffect>()
            )
        );
    }

    [Fact]
    public void AHardpointIsIndependentOfTheHighSlotPosition()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)!;
        var fitting = db.Services.GetRequiredService<IPilotFitting>();
        var inventory = db.Services.GetRequiredService<IPilotInventory>();
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "INSERT INTO DogmaAttribute VALUES (14,'hiSlots'); INSERT INTO TypeAttribute VALUES (601,14,3); "
                    + "UPDATE TypeAttribute SET Value=1 WHERE AttributeId=102"
            );
        }
        var miner = Assert.Single(inventory.List(pilot.PilotId, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<OwnedItem>().Find(miner.Id)!.Quantity = 2;
            world.SaveChanges();
        }
        fitting.Fit(pilot.PilotId, [miner.Id], pilot.StationId, (int)InventoryFlag.HighSlot0 + 2);
        Assert.Equal((int)InventoryFlag.HighSlot0 + 2, Assert.Single(inventory.List(pilot.PilotId, pilot.ShipId)).FlagId);
        Assert.Equal(0, fitting.ShipAttributes(pilot.PilotId)[DogmaAttributeId.TurretSlotsLeft]);
        Assert.Throws<InvalidDataException>(() => fitting.Fit(pilot.PilotId, [miner.Id], pilot.StationId, (int)InventoryFlag.HighSlot0));
        Assert.Equal(1, Assert.Single(inventory.List(pilot.PilotId, pilot.StationId), i => i.Id == miner.Id).Quantity);
        Assert.Single(inventory.List(pilot.PilotId, pilot.ShipId));
    }
}
