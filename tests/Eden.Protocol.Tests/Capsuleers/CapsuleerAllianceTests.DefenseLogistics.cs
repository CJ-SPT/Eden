using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Corporations;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Sovereignty;
using Eden.Protocol.Tests.Sovereignty;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Sovereignty;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerAllianceTests
{
    private const int DefenseFuelSupplier = 60003760;
    private const int DefenseFuelType = SovereigntyCaptureFixture.Strontium;
    private const long DefenseFuelPrice = 100;

    [Fact]
    public async Task DryGuardStocksFuelWhileClaimsAreQuietAndRespondsAcrossRestart()
    {
        const int shipment = 4;
        long reserve = AllianceBillingRules.Amount(1);
        long funded = reserve + 100_000 * MarketDefinitions.CentsPerIsk;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        PrepareHeldClaims(fixture, regionalSupplier: true);

        int corporation;
        int headquarters;
        long pilot;
        long ship;
        SovereigntyClaim oldest;
        SovereigntyClaim later;
        using (var seeded = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = seeded.GetRequiredService<LiveWorld>();
            var claims = seeded.GetRequiredService<ISovereignty>();
            (corporation, headquarters, pilot, _) = SeedHeldClaims(seeded, fixture, primaryFuel: 0, backupFuel: 0, treasury: funded);
            oldest = Assert.IsType<SovereigntyClaim>(claims.Read(OldestDefenseSystem));
            later = Assert.IsType<SovereigntyClaim>(claims.Read(LaterDefenseSystem));
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            long sellerStock;
            using (var setup = live.Economy.Begin(TransactionParticipants.None))
            {
                var economy = setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!;
                economy.TestingMarket = false;
                economy.TestingGrants = false;
                ship = setup.Pilots.Find(pilot)!.ActiveShipId;
                var legacy = new HaulerProgress(HaulerActivity.Docked, 0, 0, ship, headquarters, 0, new NavigationState().Capture())
                {
                    HomeDefenseAllianceId = later.AllianceId,
                };
                new HaulerCheckpoint(pilot, legacy).Apply(setup);
                sellerStock = StockDefenseFuel(setup, catalog, funded, shipment);
                setup.Commit();
            }
            ListDefenseFuel(seeded, sellerStock, shipment);
            await live.FlushAsync();
        }

        long purchase;
        long chargedBalance;
        int readyFuel;
        int fuelPerCycle;
        using (var resupplying = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = resupplying.GetRequiredService<LiveWorld>();
            var entosis = resupplying.GetRequiredService<IPilotEntosis>();
            var scheduler = resupplying.GetRequiredService<CapsuleerScheduler>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            var profile = EntosisCatalog.Module(catalog.FittingAttributes(CapsuleerAllianceRules.ExpeditionLink), 1);
            long duration = EntosisCatalog.Defense(catalog, false).Duration;
            fuelPerCycle = profile.FuelQuantity;
            int minimumFuel = CapsuleerHauler.RequiredExpeditionCycles(duration, profile) * fuelPerCycle;
            live.Economy.Read(world =>
            {
                var legacy = Assert.IsType<HaulerProgress>(HaulerCheckpoint.Read(world, live, pilot));
                Assert.Equal((ship, (int?)later.AllianceId, 0), (legacy.ShipId, legacy.HomeDefenseAllianceId, FuelInHold(world, ship)));
                Assert.Null(legacy.SovereigntyExpedition);
                Assert.Empty(DefenseFuelFills(world, pilot));
                return true;
            });
            Assert.Equal(1d, entosis.Hub(oldest.SystemId)!.Conflict!.Capture.Value);
            Assert.Equal(1d, entosis.Hub(later.SystemId)!.Conflict!.Capture.Value);

            StartDefenseReplay(resupplying);
            AdvanceDefenseReplay(
                resupplying,
                fixture,
                pilot,
                750,
                () => scheduler.Hauler(pilot)?.Work.CorporationSupply?.RegionalStock is not null
            );
            var supply = scheduler.Hauler(pilot)!.Work;
            Assert.Equal(DefenseFuelSupplier, supply.CorporationSupply!.RegionalStock!.Supplier);
            Assert.Null(supply.HomeDefenseAllianceId);
            Assert.Null(supply.SovereigntyExpedition);
            Assert.Empty(entosis.Cycles(pilot));

            AdvanceDefenseReplay(
                resupplying,
                fixture,
                pilot,
                1700,
                () =>
                    scheduler.Hauler(pilot)
                        is { Pilot.Position: null, Work: { CorporationSupply: null, SovereigntyExpedition: null } } guard
                    && guard.Pilot.DockedLocationId == headquarters
                    && guard.Work.HomeDefenseAllianceId == later.AllianceId
                    && live.Economy.Read(world => FuelInHold(world, ship)) >= minimumFuel
            );
            Assert.Empty(entosis.Cycles(pilot));
            Assert.Equal(1d, entosis.Hub(oldest.SystemId)!.Conflict!.Capture.Value);
            Assert.Equal(1d, entosis.Hub(later.SystemId)!.Conflict!.Capture.Value);
            (purchase, chargedBalance, readyFuel) = live.Economy.Read(world =>
            {
                var execution = Assert.Single(DefenseFuelFills(world, pilot));
                Assert.Equal((DefenseFuelPrice, shipment), (execution.PriceCents, execution.Quantity));
                var order = Assert.Single(
                    world.Filter<SavedPersonalOrder>(entry => entry.CorporationId == corporation),
                    entry => entry.Bid && entry.StationId == DefenseFuelSupplier && entry.TypeId == DefenseFuelType
                );
                Assert.Equal((PersonalOrderStatus.Filled, 0), (order.Status, order.Remaining));
                long fee = PilotMarketStore.SupplyBrokerFee(world, catalog, pilot, DefenseFuelSupplier, order.PriceCents, order.Entered);
                long balance = world.Set<SavedCorporation>().Find(corporation)!.BalanceCents;
                Assert.Equal(funded - DefenseFuelPrice * shipment - fee, balance);
                Assert.True(balance >= reserve);
                int fuel = FuelInHold(world, ship);
                Assert.Equal(shipment, DefenseDepotFuel(world, corporation, headquarters) + fuel);
                return (execution.Id, balance, fuel);
            });
            await live.FlushAsync();
        }

        using var defending = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = defending.GetRequiredService<LiveWorld>();
        var restoredEntosis = defending.GetRequiredService<IPilotEntosis>();
        var restoredClaims = defending.GetRequiredService<ISovereignty>();
        restored.Economy.Read(world =>
        {
            var guard = Assert.IsType<HaulerProgress>(HaulerCheckpoint.Read(world, restored, pilot));
            Assert.Equal((ship, (int?)later.AllianceId, readyFuel), (guard.ShipId, guard.HomeDefenseAllianceId, FuelInHold(world, ship)));
            Assert.Null(guard.SovereigntyExpedition);
            Assert.Equal(purchase, Assert.Single(DefenseFuelFills(world, pilot)).Id);
            return true;
        });

        Assert.Equal(1d, restoredEntosis.Hub(later.SystemId)!.Conflict!.Capture.Value);
        using (var disruption = restored.Economy.Begin(TransactionParticipants.None))
        {
            var conflict = disruption.Set<SavedSovereigntyConflict>().Find(later.SystemId)!;
            conflict.Value = 0.05;
            conflict.UpdatedAt = fixture.Clock.Now;
            disruption.Commit();
        }
        StartDefenseReplay(defending);
        AdvanceDefenseReplay(defending, fixture, pilot, 900, () => restoredEntosis.Cycles(pilot).Any(cycle => cycle.HubId == later.HubId));
        var cycle = Assert.Single(restoredEntosis.Cycles(pilot));
        Assert.Equal((ship, later.HubId), (cycle.ShipId, cycle.HubId));
        Assert.Equal(readyFuel - fuelPerCycle, restored.Economy.Read(world => FuelInHold(world, ship)));
        AdvanceDefenseReplay(defending, fixture, pilot, 1300, () => restoredEntosis.Hub(later.SystemId)!.Conflict!.Capture.Value == 1);
        Assert.Equal(oldest, restoredClaims.Read(oldest.SystemId));
        Assert.Equal(later, restoredClaims.Read(later.SystemId));
        restored.Economy.Read(world =>
        {
            Assert.Equal(chargedBalance, world.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
            Assert.Equal(purchase, Assert.Single(DefenseFuelFills(world, pilot)).Id);
            return true;
        });
        AssertUnattended(defending);
    }

    [Fact]
    public async Task LostDefenderReplenishesAllianceFromRegionalFuelAcrossRestart()
    {
        const int supplier = DefenseFuelSupplier;
        const int strontium = DefenseFuelType;
        const int shipment = 3;
        const long unitPrice = DefenseFuelPrice;
        long reserve = AllianceBillingRules.Amount(1);
        long funded = reserve + 100_000 * MarketDefinitions.CentsPerIsk;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        PrepareHeldClaims(fixture, regionalSupplier: true);

        int corporation;
        int headquarters;
        long primary;
        long backup;
        long lostShip;
        long backupShip;
        long chargedBalance;
        SovereigntyClaim oldest;
        SovereigntyClaim later;
        FreightStack loaded;
        long purchase;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            var entosis = host.GetRequiredService<IPilotEntosis>();
            var claims = host.GetRequiredService<ISovereignty>();
            (corporation, headquarters, primary, backup) = SeedHeldClaims(host, fixture, primaryFuel: 16, backupFuel: 0, treasury: reserve);
            oldest = Assert.IsType<SovereigntyClaim>(claims.Read(OldestDefenseSystem));
            later = Assert.IsType<SovereigntyClaim>(claims.Read(LaterDefenseSystem));
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            Assert.NotEqual(catalog.RequireStation(headquarters).RegionId, catalog.RequireStation(supplier).RegionId);
            _ = entosis.Hub(later.SystemId);
            using (var disruption = live.Economy.Begin(TransactionParticipants.None))
            {
                disruption.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = false;
                disruption.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingGrants = false;
                var conflict = disruption.Set<SavedSovereigntyConflict>().Find(later.SystemId)!;
                conflict.Value = 0.05;
                conflict.UpdatedAt = fixture.Clock.Now;
                disruption.Commit();
            }
            StartDefenseReplay(host);
            Advance(host, 600, () => entosis.Cycles(primary).Any(cycle => cycle.HubId == later.HubId));
            var lostCycle = Assert.Single(entosis.Cycles(primary));
            lostShip = lostCycle.ShipId;
            Assert.Equal(later.HubId, lostCycle.HubId);
            Assert.Equal(15, live.Economy.Read(world => FuelInHold(world, lostShip)));

            long sellerStock;
            using (var loss = live.Economy.Begin(TransactionParticipants.None))
            {
                var victim = loss.Pilots.Find(primary)!;
                var seller = loss.Pilots.Find(Observer)!;
                var position = Assert.IsType<FlightPosition>(victim.ActiveShip.Position);
                Assert.Equal(later.SystemId, position.SystemId);
                ShipLossPersistence.Apply(
                    loss,
                    catalog,
                    victim,
                    null,
                    lostShip,
                    later.SystemId,
                    position,
                    fixture.Clock.Now,
                    new LostDefenseCargo(),
                    PilotInteractionHistoryStore.Player(loss, seller, seller.ActiveShipId)
                );
                loss.Add(new SavedCapsuleerSuspension { PilotId = primary });
                sellerStock = StockDefenseFuel(loss, catalog, funded, shipment);
                backupShip = loss.Pilots.Find(backup)!.ActiveShipId;
                loss.Commit();
            }
            ListDefenseFuel(host, sellerStock, shipment);
            scheduler.PopulationPrepared(HeadquartersSystem);
            entosis.Advance(later.SystemId, fixture.Clock.Now);
            live.Economy.Read(world =>
            {
                var loss = Assert.IsType<ShipLoss>(world.Set<ShipLoss>().Find(lostShip));
                Assert.Equal((primary, later.SystemId, (long?)Observer), (loss.PilotId, loss.SystemId, loss.AttackerPilotId));
                Assert.Null(world.Ships.Find(lostShip));
                Assert.Equal(FleetDefinitions.CapsuleType, world.Pilots.Find(primary)!.ActiveShip.TypeId);
                Assert.Single(CapsuleerCorporations.Organization(world, primary).FleetLosses!, entry => entry.ShipId == lostShip);
                Assert.Equal(0, FuelInHold(world, backupShip));
                Assert.DoesNotContain(
                    world.Filter<SavedEntosisCycle>(cycle => cycle.PilotId == primary),
                    cycle => cycle.ShipId == lostShip && cycle.Contributing
                );
                return true;
            });
            Assert.Equal(later, claims.Read(later.SystemId));
            Assert.Null(scheduler.Hauler(backup)?.Work.CorporationSupply?.RegionalStock);
            Assert.Null(scheduler.Hauler(backup)?.Work.SovereigntyExpedition);
            Assert.Empty(entosis.Cycles(backup));
            Assert.Equal(reserve, CorporationBalance(live));
            Assert.Empty(live.Economy.Read(world => SupplierFills(world)));
            Assert.Empty(
                live.Economy.Read(world => world.Filter<SavedPersonalOrder>(entry => entry.CorporationId == corporation).ToArray())
            );
            Assert.Equal(0, live.Economy.Read(world => DepotFuel(world)));

            using (var funding = live.Economy.Begin(TransactionParticipants.None))
            {
                funding.Remove(funding.Set<SavedCapsuleerSuspension>().Find(backup)!);
                var courier = host.GetRequiredService<SqliteWorld>().FindPilot(funding, -1, backup)!;
                var avoided = new HashSet<int>();
                Assert.Null(CapsuleerIndustry.PlanRegionalStock(funding, catalog, live, courier, avoided, fixture.Clock.Now));
                funding.Set<SavedCorporation>().Find(corporation)!.BalanceCents = funded;
                Assert.NotNull(CapsuleerIndustry.PlanRegionalStock(funding, catalog, live, courier, avoided, fixture.Clock.Now));
                funding.Commit();
            }
            scheduler.PopulationPrepared(HeadquartersSystem);
            Advance(host, 1100, () => scheduler.Hauler(backup)?.Work.CorporationSupply?.RegionalStock is not null);
            var supplierTrip = scheduler.Hauler(backup)!.Work;
            var regional = supplierTrip.CorporationSupply!.RegionalStock!;
            Assert.Equal((headquarters, supplier), (regional.Headquarters, regional.Supplier));
            var requested = Assert.Single(regional.Purchases);
            Assert.Equal((strontium, shipment + 1), (requested.TypeId, requested.Quantity));
            Assert.Equal(new[] { HeadquartersSystem, ObserverSystem }, supplierTrip.Journey!.Systems);
            Advance(host, 850, () => scheduler.Hauler(backup) is { Pilot.Position: not null, Work.Freight.Stacks.Length: 1 });
            var outbound = scheduler.Hauler(backup)!;
            loaded = Assert.Single(outbound.Work.Freight!.Stacks);
            Assert.Equal((strontium, shipment, headquarters), (loaded.TypeId, loaded.Quantity, outbound.Work.Freight.Destination));
            Assert.Equal(new[] { ObserverSystem, HeadquartersSystem }, outbound.Work.Journey!.Systems);
            (purchase, chargedBalance) = live.Economy.Read(world =>
            {
                Assert.Equal(0, DepotFuel(world));
                var cargo = Assert.IsType<OwnedItem>(world.Set<OwnedItem>().Find(loaded.ItemId));
                Assert.Equal((backup, backupShip, shipment), (cargo.OwnerId, cargo.LocationId, cargo.Quantity));
                var execution = Assert.Single(SupplierFills(world));
                Assert.Equal((unitPrice, shipment, 0L), (execution.PriceCents, execution.Quantity, execution.WalletAmountCents));
                var sellerFill = Assert.Single(
                    world.Filter<MarketExecution>(entry => entry.PilotId == Observer),
                    entry => !entry.IsBuy && entry.TypeId == strontium && entry.StationId == supplier
                );
                Assert.Equal(
                    ((long)corporation, unitPrice, shipment),
                    (sellerFill.CounterpartyId, sellerFill.PriceCents, sellerFill.Quantity)
                );
                var order = Assert.Single(
                    world.Filter<SavedPersonalOrder>(entry => entry.CorporationId == corporation),
                    entry => entry.Bid && entry.StationId == supplier && entry.TypeId == strontium
                );
                Assert.Equal((PersonalOrderStatus.Filled, 0, 0L), (order.Status, order.Remaining, order.EscrowCents));
                long fee = PilotMarketStore.SupplyBrokerFee(world, catalog, backup, supplier, order.PriceCents, order.Entered);
                long balance = world.Set<SavedCorporation>().Find(corporation)!.BalanceCents;
                Assert.Equal(funded - unitPrice * shipment - fee, balance);
                Assert.True(balance >= reserve);
                return (execution.Id, balance);
            });
            Assert.Empty(entosis.Cycles(backup));
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        var restoredScheduler = reopened.GetRequiredService<CapsuleerScheduler>();
        var restoredEntosis = reopened.GetRequiredService<IPilotEntosis>();
        var restoredClaims = reopened.GetRequiredService<ISovereignty>();
        restored.Economy.Read(world =>
        {
            var work = Assert.IsType<HaulerProgress>(HaulerCheckpoint.Read(world, restored, backup));
            Assert.Equal(loaded, Assert.Single(work.Freight!.Stacks));
            Assert.Equal(backupShip, world.Set<OwnedItem>().Find(loaded.ItemId)!.LocationId);
            Assert.Equal(purchase, Assert.Single(SupplierFills(world)).Id);
            return true;
        });
        StartDefenseReplay(reopened);
        Advance(reopened, 850, () => restored.Economy.Read(world => DepotFuel(world)) == shipment);
        Assert.Equal(chargedBalance, CorporationBalance(restored));
        Assert.Empty(restoredEntosis.Cycles(backup));
        Advance(reopened, 900, () => restoredEntosis.Cycles(backup).Any(cycle => cycle.HubId == later.HubId));
        var paid = Assert.Single(restoredEntosis.Cycles(backup));
        Assert.Equal((backupShip, later.HubId), (paid.ShipId, paid.HubId));
        var restoredDefense = Assert.IsType<SovereigntyExpedition>(restoredScheduler.Hauler(backup)?.Work.SovereigntyExpedition);
        Assert.Equal((SovereigntyDuty.Defend, (int?)later.SystemId), (restoredDefense.Duty, restoredDefense.HomeSystemId));
        Assert.Equal(shipment - 1, restored.Economy.Read(world => DepotFuel(world) + FuelInHold(world, backupShip)));
        Advance(reopened, 1300, () => restoredEntosis.Hub(later.SystemId)!.Conflict!.Capture.Value == 1);
        Advance(
            reopened,
            1300,
            () =>
                restoredScheduler.Hauler(backup) is { Work.SovereigntyExpedition.Returning: true, Pilot.Position: null } worker
                && worker.Pilot.DockedLocationId == headquarters
                && restoredEntosis.Cycles(backup).Count == 0
        );
        Advance(reopened, 120, () => restoredScheduler.Hauler(backup) is { Work.SovereigntyExpedition: null });
        Assert.Null(restoredScheduler.Hauler(backup)!.Work.HomeDefenseAllianceId);
        Assert.Equal(oldest, restoredClaims.Read(oldest.SystemId));
        Assert.Equal(later, restoredClaims.Read(later.SystemId));
        Assert.InRange(restored.Economy.Read(world => DepotFuel(world) + FuelInHold(world, backupShip)), 0, 1);
        Assert.Equal(chargedBalance, CorporationBalance(restored));
        Assert.Equal(purchase, restored.Economy.Read(world => Assert.Single(SupplierFills(world)).Id));
        AssertUnattended(reopened);

        void Advance(ServiceProvider host, int seconds, Func<bool> ready) => AdvanceDefenseReplay(host, fixture, backup, seconds, ready);

        int DepotFuel(WorldAction world) => DefenseDepotFuel(world, corporation, headquarters);

        MarketExecution[] SupplierFills(WorldAction world) => DefenseFuelFills(world, backup);

        long CorporationBalance(LiveWorld live) =>
            live.Economy.Read(world => world.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
    }

    private static long StockDefenseFuel(EconomyTransaction world, CatalogDbContext catalog, long balance, int shipment)
    {
        var seller = world.Pilots.Find(Observer)!;
        if (world.Positions.Find(seller.ActiveShipId) is { } sellerPosition)
        {
            world.Remove(sellerPosition);
        }
        seller.ActiveShip.StationId = DefenseFuelSupplier;
        var wallet = world.Set<PilotWallet>().Find(Observer) ?? world.Add(new PilotWallet { PilotId = Observer }).Entity;
        wallet.Cents = balance;
        return InventoryPersistence
            .MergeOutput(world, catalog, Observer, DefenseFuelSupplier, (int)InventoryFlag.Hangar, DefenseFuelType, shipment)
            .Item.Id;
    }

    private static void ListDefenseFuel(ServiceProvider host, long stock, int shipment) =>
        host.GetRequiredService<IPersonalMarket>()
            .PlaceSell(Observer, [new(stock, DefenseFuelSupplier, DefenseFuelType, DefenseFuelPrice, shipment)], 7);

    private static int FuelInHold(WorldAction world, long ship) =>
        world
            .Filter<OwnedItem>(item => item.LocationId == ship)
            .Where(item => item.TypeId == DefenseFuelType && item.FlagId == (int)InventoryFlag.Cargo)
            .Sum(item => item.Quantity);

    private static int DefenseDepotFuel(WorldAction world, int corporation, int headquarters) =>
        world
            .Filter<OwnedItem>(item => item.OwnerId == corporation)
            .Where(item =>
                item.LocationId == headquarters
                && item.FlagId == CorporationStorageRules.MarketDeliveriesFlag
                && item.TypeId == DefenseFuelType
            )
            .Sum(item => item.Quantity);

    private static MarketExecution[] DefenseFuelFills(WorldAction world, long buyer) =>
        world
            .Filter<MarketExecution>(entry => entry.PilotId == buyer)
            .Where(entry =>
                entry.IsBuy && entry.TypeId == DefenseFuelType && entry.StationId == DefenseFuelSupplier && entry.CounterpartyId == Observer
            )
            .ToArray();

    private static void StartDefenseReplay(ServiceProvider host)
    {
        StartAllianceActivity(host, unattended: true);
        host.GetRequiredService<CapsuleerScheduler>().PopulationPrepared(HeadquartersSystem);
    }

    private static void AdvanceDefenseReplay(
        ServiceProvider host,
        CapsuleerHaulerTests.Fixture fixture,
        long pilot,
        int seconds,
        Func<bool> ready
    )
    {
        var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
        for (int step = 0; step < seconds && !ready(); step++)
        {
            fixture.Clock.Seconds++;
            maintenance.Tick();
        }
        AssertUnattended(host);
        if (!ready())
        {
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            var worker = scheduler.Hauler(pilot);
            Assert.Fail(
                $"Timed out at {fixture.Clock.UtcNow}. Pilot {pilot} in {worker?.Pilot.SystemId}, "
                    + $"work {worker?.Work.Activity} {worker?.Work.WaitingReason}, expedition {worker?.Work.SovereigntyExpedition}, "
                    + $"supply {worker?.Work.CorporationSupply}, scheduler failure {scheduler.Read().LastFailure}."
            );
        }
    }

    private sealed class LostDefenseCargo : IShipLootRoll
    {
        public bool Survives() => false;
    }
}
