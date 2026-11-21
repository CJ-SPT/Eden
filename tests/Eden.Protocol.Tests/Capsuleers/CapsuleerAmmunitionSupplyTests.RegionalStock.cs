using Eden.Data.World.Capsuleers;
using Eden.Data.World.Corporations;
using Eden.Data.World.Economy;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.Protocol.Tests.Industry;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ammo = Eden.Gameplay.Capsuleers.CapsuleerAmmunitionSupplyRules;
using Replacement = Eden.Gameplay.Capsuleers.CapsuleerReplacementRules;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerAmmunitionSupplyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task BotFundsRegionalKitOrdersAndHaulsManufacturedStockAcrossRestarts(bool partialFill, bool miningFit)
    {
        const int factorySystem = 30000142;
        const long buyerFunds = 100_000_000L * MarketDefinitions.CentsPerIsk;
        const int merlin = (int)TechOneHullType.Merlin;
        const int gun = (int)CombatWeaponType.GatlingRailgun75mm;
        const int personalCharges = 7;
        const int high = (int)InventoryFlag.HighSlot0;
        int hull = miningFit ? (int)CapsuleerMiningHull.Venture : merlin;
        ReplacementPart[] reserve = miningFit
            ? [new(hull, 1), new((int)MiningLaserType.MinerI, 2), new(2046, 1)]
            : Replacement.MerlinParts.ToArray();
        var expected = reserve.Where(part => !partialFill || part.TypeId == hull).ToArray();
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        PrepareRegionalTrade(fixture);
        UseLocalOrders(fixture);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        long producer;
        long courier;
        int buyer;
        int supplier;
        long[] bidIds;
        long bidDeadline;
        long paidBalance;
        long[] cargoIds;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            Assert.NotEqual(catalog.RequireStation(Depot).RegionId, catalog.RequireStation(FactoryStation).RegionId);
            using (var setup = live.Economy.Begin(TransactionParticipants.None))
            {
                var settings = setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!;
                settings.TestingMarket = false;
                settings.TestingGrants = false;
                var agents = setup.Set<SavedCapsuleerAgent>().OrderBy(agent => agent.PilotId).ToArray();
                producer = agents.Single(agent => agent.Career == CapsuleerCareer.Mining).PilotId;
                var carriers = agents.Where(agent => agent.Career == CapsuleerCareer.Hauling).Take(2).ToArray();
                courier = carriers[0].PilotId;
                long patrol = carriers[1].PilotId;
                setup.Pilots.Find(producer)!.ActiveShip.StationId = FactoryStation;
                setup.Pilots.Find(courier)!.ActiveShip.StationId = Depot;
                setup.Pilots.Find(courier)!.ActiveShip.TypeId = (int)FreightHull.Tayra;
                var escort = setup.Pilots.Find(patrol)!;
                escort.ActiveShip.StationId = Depot;
                supplier = Charter(setup, catalog, producer, "Regional Foundry", "RFOU", fixture.Clock.Now);
                buyer = Charter(setup, catalog, courier, "Regional Patrol", "RPAT", fixture.Clock.Now);
                CorporationMembershipStore.Move(setup, escort, buyer, fixture.Clock.Now);
                setup.RemoveRange(setup.Filter<OwnedItem>(item => item.OwnerId == patrol).ToArray());
                TrafficFitting.Apply(
                    setup,
                    catalog,
                    patrol,
                    escort.ActiveShipId,
                    TrafficSnapshotTests.Profile() with
                    {
                        HullTypeId = hull,
                        Equipment = miningFit
                            ?
                            [
                                new((int)MiningLaserType.MinerI, high, 1),
                                new((int)MiningLaserType.MinerI, high + 1, 1),
                                new(2046, (int)InventoryFlag.LowSlot0, 1),
                            ]
                            :
                            [
                                new(gun, high, 1),
                                new(Ammo.ChargeType, high, 200),
                                new(gun, high + 1, 1),
                                new(Ammo.ChargeType, high + 1, 200),
                            ],
                    },
                    fixture.Clock.Now
                );
                foreach (
                    var agent in agents.Where(agent => agent.PilotId != producer && agent.PilotId != courier && agent.PilotId != patrol)
                )
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
                }
                // Drive the factory's real docked industry decisions after the bids arrive.
                foreach (long operatorId in new[] { producer, patrol })
                {
                    setup.Set<SavedCapsuleerJob>().Find(operatorId)!.DueTicks = TimeSpan.FromDays(1).Ticks;
                }
                foreach (
                    var recipe in StarterProductionTests
                        .ReadAudit()
                        .Recipes.Where(recipe => recipe.Products.Any(product => reserve.Any(part => part.TypeId == product.TypeID)))
                )
                {
                    setup.Add(
                        new OwnedItem
                        {
                            Id = setup.NextItemId(),
                            OwnerId = producer,
                            LocationId = FactoryStation,
                            TypeId = recipe.BlueprintId,
                            FlagId = (int)InventoryFlag.Hangar,
                            Singleton = true,
                            Quantity = 1,
                            BlueprintRuns = -1,
                        }
                    );
                    foreach (var material in recipe.Materials)
                    {
                        InventoryPersistence.MergeOutput(
                            setup,
                            catalog,
                            producer,
                            FactoryStation,
                            (int)InventoryFlag.Hangar,
                            material.TypeID,
                            material.Quantity * Ammo.ProductionRuns
                        );
                    }
                }
                AddStock(setup, courier, FactoryStation, Ammo.ChargeType, personalCharges);
                setup.Set<SavedCorporation>().Find(supplier)!.BalanceCents = buyerFunds;
                var corporation = setup.Set<SavedCorporation>().Find(buyer)!;
                var hauler = host.GetRequiredService<SqliteWorld>().FindPilot(setup, -1, courier)!;
                corporation.BalanceCents = 1;
                Assert.Null(CapsuleerIndustry.PlanRegionalStock(setup, catalog, live, hauler, new HashSet<int>(), fixture.Clock.Now));
                corporation.BalanceCents = buyerFunds;
                Assert.Null(
                    CapsuleerIndustry.PlanRegionalStock(setup, catalog, live, hauler, new HashSet<int> { factorySystem }, fixture.Clock.Now)
                );
                Assert.Empty(setup.Set<SavedPersonalOrder>());
                var planned = CapsuleerIndustry.PlanRegionalStock(setup, catalog, live, hauler, new HashSet<int>(), fixture.Clock.Now);
                Assert.NotNull(planned);
                Assert.Equal(
                    reserve.Select(part => part.TypeId).Order(),
                    planned.Run.RegionalStock!.Purchases.Select(part => part.TypeId).Order()
                );
                setup.Commit();
            }

            using var human = host.GetRequiredService<IHumanPresence>().Select(Customer, Home);
            var waiting = Advance(host, observation => observation.Work.CorporationSupply?.RegionalStock?.OrderIds?.Length == 3);
            var trip = waiting.Work.CorporationSupply!.RegionalStock!;
            bidIds = trip.OrderIds!;
            bidDeadline = trip.BidDeadline;
            live.Economy.Inspect(world =>
            {
                var bids = Bids(world);
                Assert.Equal(FactoryStation, waiting.Pilot.StationId);
                Assert.Null(waiting.Pilot.Position);
                Assert.Equal((Depot, FactoryStation), (trip.Headquarters, trip.Supplier));
                Assert.All(
                    bids,
                    bid =>
                    {
                        Assert.True(bid.Bid);
                        Assert.Equal(PersonalOrderStatus.Open, bid.Status);
                        Assert.Equal(courier, bid.PilotId);
                        Assert.Equal(FactoryStation, bid.StationId);
                        Assert.Equal(bid.Entered * bid.PriceCents, bid.EscrowCents);
                    }
                );
                Assert.Equal(
                    reserve.Select(part => (part.TypeId, part.Quantity)).Order(),
                    bids.Select(bid => (bid.TypeId, bid.Entered)).Order()
                );
                var fees = BuyerFees(world);
                Assert.Equal(3, fees.Length);
                Assert.Equal(
                    buyerFunds - bids.Sum(bid => bid.EscrowCents) + fees.Sum(fee => fee.AmountCents),
                    world.Set<SavedCorporation>().Find(buyer)!.BalanceCents
                );
                return true;
            });
            await live.FlushAsync();
        }

        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using var human = host.GetRequiredService<IHumanPresence>().Select(Customer, Home);
            var factory = host.GetRequiredService<CapsuleerIndustry>();
            var ships = host.GetRequiredService<SqliteWorld>();
            for (
                int turn = 0;
                turn < 12 && live.Economy.Read(world => Bids(world).Any(bid => bid.Remaining > 0 && (!partialFill || bid.TypeId == hull)));
                turn++
            )
            {
                using var work = live.Economy.Begin(TransactionParticipants.None);
                factory.Produce(work, catalog, ships.FindPilot(work, -1, producer)!, fixture.Clock.Now);
                long next = work.Set<IndustryJob>()
                    .Where(job => job.Status == IndustryJobStatus.Installed)
                    .Select(job => job.EndTime)
                    .AsEnumerable()
                    .DefaultIfEmpty(fixture.Clock.Now)
                    .Min();
                work.Commit();
                if (next > fixture.Clock.Now)
                {
                    fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(next - fixture.Clock.Now));
                }
            }
            if (partialFill)
            {
                live.Economy.Inspect(world =>
                {
                    Assert.Single(Bids(world), bid => bid.Status == PersonalOrderStatus.Filled && bid.TypeId == hull);
                    Assert.Equal(2, Bids(world).Count(bid => bid.Status == PersonalOrderStatus.Open && bid.EscrowCents > 0));
                    return true;
                });
                fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(bidDeadline - fixture.Clock.Now));
            }
            var outbound = Advance(
                host,
                observation => observation.Work.Freight?.Stacks.Length == expected.Length && observation.Pilot.Position is not null
            );
            var freight = outbound.Work.Freight!;
            cargoIds = freight.Stacks.Select(stack => stack.ItemId).ToArray();
            paidBalance = live.Economy.Read(world =>
            {
                Assert.Equal(bidIds.Order(), Bids(world).Select(bid => bid.Id).Order());
                Assert.All(
                    Bids(world),
                    bid =>
                    {
                        Assert.Equal(
                            partialFill && bid.TypeId != hull ? PersonalOrderStatus.Cancelled : PersonalOrderStatus.Filled,
                            bid.Status
                        );
                        Assert.Equal(0, bid.EscrowCents);
                    }
                );
                Assert.Equal(
                    expected.Length,
                    world.Set<IndustryJob>().Count(job => job.Status == IndustryJobStatus.Delivered && job.OutputCorporationId == supplier)
                );
                Assert.Equal(
                    expected.Select(part => (part.TypeId, part.Quantity)).Order(),
                    freight.Stacks.Select(stack => (stack.TypeId, stack.Quantity)).Order()
                );
                Assert.All(cargoIds, id => Assert.Equal(outbound.Pilot.ShipId, world.Set<OwnedItem>().Find(id)!.LocationId));
                Assert.Equal(personalCharges, Stock(world, courier, FactoryStation, Ammo.ChargeType));
                Assert.Empty(world.Filter<OwnedItem>(item => item.OwnerId == buyer));
                var balance = world.Set<SavedCorporation>().Find(buyer)!.BalanceCents;
                var gross = world
                    .Filter<MarketExecution>(entry => entry.PilotId == courier)
                    .Where(entry => entry.IsBuy && entry.CounterpartyId == supplier)
                    .AsEnumerable()
                    .Sum(entry => entry.PriceCents * entry.Quantity);
                Assert.True(gross > 0);
                Assert.Equal(buyerFunds - gross + BuyerFees(world).Sum(fee => fee.AmountCents), balance);
                return balance;
            });
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        using var occupied = reopened.GetRequiredService<IHumanPresence>().Select(Customer, Home);
        Advance(reopened, observation => observation.Work.CorporationSupply is null && observation.Pilot.StationId == Depot);
        restored.Economy.Inspect(world =>
        {
            var delivered = world.Filter<OwnedItem>(item => item.OwnerId == buyer).ToArray();
            Assert.Equal(
                expected.Select(part => (part.TypeId, part.Quantity)).Order(),
                delivered.Select(item => (item.TypeId, item.Quantity)).Order()
            );
            Assert.All(
                delivered,
                item => Assert.Equal(((long)Depot, CorporationStorageRules.MarketDeliveriesFlag), (item.LocationId, item.FlagId))
            );
            Assert.All(cargoIds, id => Assert.Null(world.Set<OwnedItem>().Find(id)));
            Assert.Equal(paidBalance, world.Set<SavedCorporation>().Find(buyer)!.BalanceCents);
            Assert.Equal(bidIds.Order(), Bids(world).Select(bid => bid.Id).Order());
            return true;
        });

        SavedPersonalOrder[] Bids(WorldAction world) => world.Filter<SavedPersonalOrder>(order => order.CorporationId == buyer).ToArray();

        SavedWalletLedger[] BuyerFees(WorldAction world) =>
            world
                .Filter<SavedWalletLedger>(entry => entry.OwnerId == buyer)
                .Where(entry => entry.EntryType == WalletEntryType.BrokersFee)
                .ToArray();

        HaulerObservation Advance(ServiceProvider host, Func<HaulerObservation, bool> ready)
        {
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            scheduler.PopulationPrepared(Home);
            scheduler.PopulationPrepared(factorySystem);
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            HaulerObservation? last = null;
            for (int turn = 0; turn < 1600; turn++)
            {
                fixture.Clock.Seconds++;
                maintenance.Tick();
                last = scheduler.Hauler(courier);
                if (last is not null && ready(last))
                {
                    return last;
                }
            }
            throw new InvalidOperationException(
                $"Courier at {last?.Pilot.SystemId}/{last?.Pilot.StationId}, hull {last?.Pilot.ShipTypeId}, {last?.Work.Activity}. "
                    + $"Route target {last?.Work.TargetId}, buy orders {last?.Work.CorporationSupply?.RegionalStock?.OrderIds?.Length}, "
                    + $"cargo stacks {last?.Work.Freight?.Stacks.Length}. {last?.Work.WaitingReason} {scheduler.Read().LastFailure}"
            );
        }
    }

    private static void PrepareRegionalTrade(CapsuleerHaulerTests.Fixture fixture)
    {
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PassiveCatalogFixture.LoadStarterProduction(fixture.Database.CatalogPath);
        PrepareCatalog(fixture);
        PassiveCatalogFixture.LoadUtilityHulls(fixture.Database.CatalogPath, new HashSet<int> { (int)FreightHull.Tayra });
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT INTO Region(Id,Name) VALUES(10000043,'Supplier region');
            INSERT INTO Constellation(Id,RegionId,Name) VALUES(20000099,10000043,'Supplier constellation');
            UPDATE SolarSystem SET ConstellationId=20000099 WHERE Id=30000142;
            """
        );
    }
}
