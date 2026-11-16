using System.Text.Json;
using Eden.Data.World.Economy;
using Eden.Data.World.Market;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Planetary;
using Eden.World.Configuration;
using Eden.World.Planetary;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Planetary.PlanetaryIndustryTests;

namespace Eden.Protocol.Tests.Planetary;

public sealed class CustomsConfigurationTests
{
    [Theory]
    [InlineData(false, .1)]
    [InlineData(false, 1)]
    [InlineData(false, 100)]
    [InlineData(true, .1)]
    [InlineData(true, 1)]
    [InlineData(true, 100)]
    public async Task CustomsCapacityScalesAdmissionRejectsOverflowAtomicallyAndSurvivesReopen(bool resident, decimal multiplier)
    {
        using var f = Prepare();
        WorldDatabase database = new(f.Db.Path);
        SetPolicy(database, f.Clock, new(StorageCapacityMultiplier: multiplier));
        long office = PlanetaryRules.OfficeId(PlanetId);
        int quantity;
        using (LiveWorld? live = resident ? new(database, f.Clock, combatObjects: false, simulationObjects: false) : null)
        {
            var colony = Deploy(f);
            var industry = PlanetaryIndustryTests.Industry(f);
            var contact = Assert.Single(industry.OrbitContacts(f.Db.Store.FindPilot(1000001)!.SystemId, f.Clock.Now));
            f.Db.Store.SetPosition(f.ShipId, contact.Position);
            double volume = Catalog(f).Volumes[Water];
            double capacity = 35000 * (double)multiplier;
            quantity = checked((int)Math.Floor(capacity / volume));
            long cargo = f.Grant(Water, quantity + 1, cargo: true);
            industry.MoveOrbitCargo(f.PilotId, office, true, new([cargo], f.ShipId, (int)InventoryFlag.Hangar, quantity));
            var wrecks = f.Db.Services.GetRequiredService<IWrecks>();
            Assert.Equal(capacity, wrecks.Read(f.PilotId, office, f.Clock.Now).Capacity);
            var before = Assert.Single(industry.OrbitInventory(f.PilotId, office).Items);
            Assert.Equal(quantity, before.Quantity);
            Assert.Throws<CargoCapacityException>(() =>
                industry.MoveOrbitCargo(f.PilotId, office, true, new([cargo], f.ShipId, (int)InventoryFlag.Hangar, 1))
            );
            Assert.Equal(before, Assert.Single(industry.OrbitInventory(f.PilotId, office).Items));
            long port = colony.Pins.Single(p => p.TypeId == Launchpad).Id;
            industry.ImportExport(f.PilotId, office, port, [new(before.Id, 1)], []);
            // Refill the office, then check that rejected export also rolls back its tax and colony change.
            industry.MoveOrbitCargo(f.PilotId, office, true, new([cargo], f.ShipId, (int)InventoryFlag.Hangar, 1));
            string colonyBefore = JsonSerializer.Serialize(industry.Read(f.PilotId, PlanetId).Colony);
            long balanceBefore = WorldActionScope.Inspect(database, world => world.Set<PilotWallet>().Find(f.PilotId)!.Cents);
            Assert.Throws<CargoCapacityException>(() => industry.ImportExport(f.PilotId, office, port, [], [new(Water, 1)]));
            Assert.Equal(colonyBefore, JsonSerializer.Serialize(industry.Read(f.PilotId, PlanetId).Colony));
            Assert.Equal(balanceBefore, WorldActionScope.Inspect(database, world => world.Set<PilotWallet>().Find(f.PilotId)!.Cents));
            Assert.Equal(quantity, Assert.Single(industry.OrbitInventory(f.PilotId, office).Items).Quantity);
            if (live is not null)
            {
                await live.FlushAsync();
            }
        }
        WorldDatabase reopened = new(f.Db.Path);
        Assert.Equal(multiplier, reopened.Settings.PlanetaryIndustry.StorageCapacityMultiplier);
        SetPolicy(reopened, f.Clock, reopened.Settings.PlanetaryIndustry with { StorageCapacityMultiplier = .1m });
        using LiveWorld reopenedWorld = new(reopened, f.Clock, combatObjects: false, simulationObjects: false);
        PilotPlanetaryIndustryStore store = new(reopened, f.Clock, f.Db.Services.GetRequiredService<PilotTrainingStore>());
        var preserved = Assert.Single(store.OrbitInventory(f.PilotId, office).Items);
        Assert.Equal(quantity, preserved.Quantity);
        // Lowering capacity must allow withdrawals from an already overfilled office.
        store.MoveOrbitCargo(f.PilotId, office, false, new([preserved.Id], office, (int)InventoryFlag.Cargo, 1));
        Assert.Equal(quantity - 1, Assert.Single(store.OrbitInventory(f.PilotId, office).Items).Quantity);
        long reopenedPort = store.Read(f.PilotId, PlanetId).Colony!.Pins.Single(p => p.TypeId == Launchpad).Id;
        store.ImportExport(f.PilotId, office, reopenedPort, [new(preserved.Id, 1)], []);
        Assert.Equal(quantity - 2, Assert.Single(store.OrbitInventory(f.PilotId, office).Items).Quantity);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2.5)]
    [InlineData(false, 17)]
    [InlineData(false, 100)]
    [InlineData(true, 0)]
    [InlineData(true, 2.5)]
    [InlineData(true, 17)]
    [InlineData(true, 100)]
    public async Task CustomsTaxUsesRestartActivatedPercentForQuotesAndBothSettlements(bool resident, decimal percent)
    {
        using var f = Prepare();
        WorldDatabase database = new(f.Db.Path);
        WorldConfigurationStore configuration = new(database, f.Clock);
        var active = configuration.Read();
        var policy = active.Active.Draft.Settings.PlanetaryIndustry with { CustomsTaxPercent = percent };
        configuration.Save(
            active.Generation,
            active.Active.Draft with
            {
                Settings = active.Active.Draft.Settings with { PlanetaryIndustry = policy },
            },
            "customs tax test"
        );
        Assert.Equal(17, database.Settings.PlanetaryIndustry.CustomsTaxPercent);
        WorldConfigurationStore.Activate(database);
        using (LiveWorld? live = resident ? new(database, f.Clock, combatObjects: false, simulationObjects: false) : null)
        {
            var colony = Deploy(f);
            var industry = PlanetaryIndustryTests.Industry(f);
            long office = PlanetaryRules.OfficeId(PlanetId);
            var contact = Assert.Single(industry.OrbitContacts(f.Db.Store.FindPilot(1000001)!.SystemId, f.Clock.Now));
            f.Db.Store.SetPosition(f.ShipId, contact.Position);
            Assert.Equal((double)(percent / 100), industry.CustomsTaxRate(f.PilotId, office));
            long cargo = f.Grant(Water, 3, cargo: true);
            industry.MoveOrbitCargo(f.PilotId, office, true, new([cargo], f.ShipId, (int)InventoryFlag.Hangar, null));
            var stack = Assert.Single(industry.OrbitInventory(f.PilotId, office).Items);
            long port = colony.Pins.Single(p => p.TypeId == Launchpad).Id;
            var imported = industry.ImportExport(f.PilotId, office, port, [new(stack.Id, 3)], []);
            var exported = industry.ImportExport(f.PilotId, office, port, [], [new(Water, 3)]);
            long exportCents = checked((long)decimal.Ceiling(400 * 3 * percent));
            long importCents = checked((long)decimal.Ceiling(400 * 3 * percent / 2));
            Assert.Equal(exportCents, imported.BalanceCents - exported.BalanceCents);
            Assert.Equal(3, Assert.Single(industry.OrbitInventory(f.PilotId, office).Items).Quantity);
            long totalPaid = WorldActionScope.Inspect(
                database,
                world =>
                    -world
                        .Filter<SavedServicePayment>(p =>
                            p.Kind == Eden.Gameplay.Market.WalletEntryType.PlanetaryImportTax
                            || p.Kind == Eden.Gameplay.Market.WalletEntryType.PlanetaryExportTax
                        )
                        .Sum(p => p.AmountCents)
            );
            Assert.Equal(importCents + exportCents, totalPaid);
            if (live is not null)
            {
                await live.FlushAsync();
            }
        }
        Assert.Equal(percent, new WorldDatabase(f.Db.Path).Settings.PlanetaryIndustry.CustomsTaxPercent);
    }

    private static void SetPolicy(WorldDatabase database, TimeProvider clock, PlanetaryPolicy policy)
    {
        WorldConfigurationStore configuration = new(database, clock);
        var state = configuration.Read();
        configuration.Save(
            state.Generation,
            state.Active.Draft with
            {
                Settings = state.Active.Draft.Settings with { PlanetaryIndustry = policy },
            },
            "customs capacity test"
        );
        WorldConfigurationStore.Activate(database);
    }
}
