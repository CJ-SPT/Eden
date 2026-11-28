using Eden.Data.World.Alliances;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.Protocol.Tests.Sovereignty;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerAllianceTests
{
    private const int RecoveredTerritoryDecoySystem = 30003991;

    private static async Task ExerciseRecoveredTerritory(
        CapsuleerHaulerTests.Fixture fixture,
        ServiceProvider campaignHost,
        IReadOnlyCollection<long> raiders,
        SovereigntyClaim home,
        long defeatedHubId,
        long victoryAt,
        Action<ServiceProvider, int, Func<bool>> advance
    )
    {
        const int target = SovereigntyCaptureFixture.SystemId;
        const int hubType = EntosisRules.HubTypeId;
        var campaignWorld = campaignHost.GetRequiredService<LiveWorld>();
        int corporation = home.CorporationId;
        int headquarters = campaignWorld.Economy.Read(world => world.Set<SavedCorporation>().Find(corporation)!.StationId);
        var campaignScheduler = campaignHost.GetRequiredService<CapsuleerScheduler>();
        HashSet<long> returning = [.. raiders];
        advance(
            campaignHost,
            1800,
            () =>
            {
                returning.RemoveWhere(pilot => DockedAtHeadquarters(campaignHost, pilot));
                return returning.Count == 0
                    && raiders.All(pilot => campaignScheduler.Hauler(pilot) is { Work.SovereigntyExpedition: null });
            }
        );
        var campaignEntosis = campaignHost.GetRequiredService<IPilotEntosis>();
        Assert.All(raiders, pilot => Assert.Empty(campaignEntosis.Cycles(pilot)));

        long package;
        long price;
        long budget;
        long reserve = AllianceBillingRules.Amount(CapsuleerAllianceRules.FoundingCorporations);
        using (var setup = campaignWorld.Economy.Begin())
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
        {
            var executor = setup.Set<SavedCorporation>().Find(corporation)!;
            var members = setup
                .Filter<Pilot>(pilot => pilot.CorporationId == corporation)
                .AsEnumerable()
                .Where(pilot => setup.Set<SavedCapsuleerAgent>().Find(pilot.Id) is not null)
                .Select(pilot => pilot.Id)
                .ToArray();
            Assert.True(members.Length >= CapsuleerCorporationRules.FoundingRoster);
            foreach (long pilot in members)
            {
                if (setup.Set<SavedCapsuleerSuspension>().Find(pilot) is { } suspended)
                {
                    setup.Remove(suspended);
                }
            }
            Assert.Equal(reserve, executor.BalanceCents);
            Assert.Equal(RecoveredTerritoryDecoySystem, CapsuleerAlliances.UnclaimedSites(setup, catalog, HeadquartersSystem).First());
            setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = false;
            var seller = setup.Pilots.Find(Observer)!.ActiveShip;
            if (setup.Positions.Find(seller.Id) is { } position)
            {
                setup.Remove(position);
            }
            seller.StationId = headquarters;
            package = InventoryPersistence
                .MergeOutput(setup, catalog, Observer, headquarters, (int)InventoryFlag.Hangar, hubType, 1)
                .Item.Id;
            price = CapsuleerIndustry.ReferencePrice(catalog, hubType);
            Assert.True(price > 0);
            long maximumFee = members.Max(pilot => PilotMarketStore.SupplyBrokerFee(setup, catalog, pilot, headquarters, price, 1));
            budget = checked(reserve + 2 * (price + maximumFee));
            setup.Commit();
        }

        Review(campaignHost);
        Assert.Empty(HubOrders(campaignWorld));
        Assert.Equal(reserve, Balance(campaignWorld));
        using (var fund = campaignWorld.Economy.Begin())
        {
            fund.Set<SavedCorporation>().Find(corporation)!.BalanceCents = budget;
            fund.Commit();
        }

        Review(campaignHost);
        var bid = Assert.Single(HubOrders(campaignWorld));
        Assert.Equal((PersonalOrderStatus.Open, headquarters, 1, price), (bid.Status, bid.StationId, bid.Remaining, bid.EscrowCents));
        Assert.Equal(CapsuleerAllianceRules.FirstHubOrderDays, bid.DurationDays);
        Assert.Equal(bid.Issued + TimeSpan.FromDays(bid.DurationDays).Ticks, bid.Expires);
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
        {
            long fee = campaignWorld.Economy.Read(world =>
                PilotMarketStore.SupplyBrokerFee(world, catalog, bid.PilotId, headquarters, price, 1)
            );
            Assert.Equal(budget - price - fee, Balance(campaignWorld));
            Assert.True(Balance(campaignWorld) >= reserve + price + fee);
        }
        Review(campaignHost);
        Assert.Equal(bid.Id, Assert.Single(HubOrders(campaignWorld)).Id);
        Assert.Null(campaignHost.GetRequiredService<ISovereignty>().Read(target));
        await campaignWorld.FlushAsync();
        campaignHost.Dispose();

        using var trading = Reopen();
        var tradingWorld = trading.GetRequiredService<LiveWorld>();
        Review(trading);
        var pending = Assert.Single(HubOrders(tradingWorld));
        Assert.Equal((bid.Id, PersonalOrderStatus.Open), (pending.Id, pending.Status));
        var credit = Assert.Single(CampaignCredits(tradingWorld, defeatedHubId));
        Assert.Equal((target, home.AllianceId, (long?)victoryAt), (credit.SystemId, credit.AllianceId, credit.VictoryAt));
        trading.GetRequiredService<IPilotMarket>().Sell(Observer, [new(package, headquarters, hubType, price, 1)]);
        var purchase = VerifyPurchase(tradingWorld);
        Review(trading);
        Assert.Equal(bid.Id, Assert.Single(HubOrders(tradingWorld)).Id);

        advance(trading, 600, () => Launches(tradingWorld).Any());
        var delivery = Assert.Single(Launches(tradingWorld));
        var expedition = Assert.IsType<SovereigntyExpedition>(delivery.Work.SovereigntyExpedition);
        Assert.Equal(target, expedition.TargetSystemId);
        Assert.Null(expedition.HubId);
        Assert.Equal(new[] { HeadquartersSystem, target }, delivery.Work.Journey!.Systems);
        int beforeFuel = tradingWorld.Economy.Read(world =>
        {
            var loaded = Assert.IsType<OwnedItem>(world.Set<OwnedItem>().Find(expedition.CargoId));
            Assert.Equal(
                (delivery.PilotId, delivery.Work.ShipId, hubType, (int)InventoryFlag.InfrastructureHold, 1),
                (loaded.OwnerId, loaded.LocationId, loaded.TypeId, loaded.FlagId, loaded.Quantity)
            );
            return CargoFuel(world, delivery.Work.ShipId);
        });
        Assert.True(beforeFuel >= 3);
        Assert.Null(trading.GetRequiredService<IPilotEntosis>().Hub(target));
        await tradingWorld.FlushAsync();
        trading.Dispose();

        using var capturing = Reopen();
        var captureWorld = capturing.GetRequiredService<LiveWorld>();
        var claims = capturing.GetRequiredService<ISovereignty>();
        var entosis = capturing.GetRequiredService<IPilotEntosis>();
        var restored = captureWorld.Economy.Read(world =>
            HaulerCheckpoint.Read(world.Filter<SavedCapsuleerHauler>(row => row.PilotId == delivery.PilotId).Single().Progress)
        );
        Assert.Equal(expedition, restored?.SovereigntyExpedition);
        Review(capturing);
        Assert.Equal(purchase.Id, VerifyPurchase(captureWorld).Id);
        advance(capturing, 600, () => entosis.Cycles(delivery.PilotId).Any(cycle => cycle.ModuleId == expedition.ModuleId));
        var paid = Assert.Single(entosis.Cycles(delivery.PilotId));
        var neutral = Assert.IsType<SovereigntyHub>(entosis.Hub(target));
        Assert.Equal(neutral.HubId, paid.HubId);
        Assert.NotEqual(defeatedHubId, neutral.HubId);
        Assert.Equal(EntosisRules.NeutralCorporationId, neutral.CorporationId);
        Assert.Null(neutral.Claim);
        Assert.Equal(beforeFuel - 1, captureWorld.Economy.Read(world => CargoFuel(world, paid.ShipId)));
        Assert.Null(captureWorld.Economy.Read(world => world.Set<OwnedItem>().Find(expedition.CargoId)));

        advance(capturing, 1400, () => claims.Read(target) is not null);
        var acquired = Assert.IsType<SovereigntyClaim>(claims.Read(target));
        Assert.Equal(
            (target, home.AllianceId, corporation, neutral.HubId),
            (acquired.SystemId, acquired.AllianceId, acquired.CorporationId, acquired.HubId)
        );
        Assert.Equal(home, claims.Read(home.SystemId));
        Assert.Equal(beforeFuel - 3, captureWorld.Economy.Read(world => CargoFuel(world, paid.ShipId)));
        Assert.Empty(CampaignCredits(captureWorld, defeatedHubId));
        advance(capturing, 1400, () => DockedAtHeadquarters(capturing, delivery.PilotId) && entosis.Cycles(delivery.PilotId).Count == 0);
        Review(capturing);
        Assert.Equal(purchase.Id, VerifyPurchase(captureWorld).Id);
        Assert.Empty(Launches(captureWorld));
        await captureWorld.FlushAsync();
        capturing.Dispose();

        using var completed = Reopen();
        var finished = completed.GetRequiredService<LiveWorld>();
        var completedClaims = completed.GetRequiredService<ISovereignty>();
        Review(completed);
        Assert.Equal(purchase.Id, VerifyPurchase(finished).Id);
        Assert.Equal(acquired, completedClaims.Read(target));
        Assert.Equal(home, completedClaims.Read(home.SystemId));
        Assert.Null(completedClaims.Read(RecoveredTerritoryDecoySystem));
        Assert.Empty(CampaignCredits(finished, defeatedHubId));
        Assert.Equal(StructureState.Destroyed, finished.Economy.Read(world => world.Set<SavedStructure>().Find(defeatedHubId)!.State));
        Assert.True(DockedAtHeadquarters(completed, delivery.PilotId));
        Assert.Empty(completed.GetRequiredService<IPilotEntosis>().Cycles(delivery.PilotId));
        var completedScheduler = completed.GetRequiredService<CapsuleerScheduler>();
        advance(completed, 180, () => completedScheduler.Hauler(delivery.PilotId) is { Work.SovereigntyExpedition: null });
        Assert.Empty(Launches(finished));
        AssertUnattended(completed);

        var bill = finished.Economy.Read(world =>
            world.Filter<SavedAllianceBill>(row => row.AllianceId == home.AllianceId).Single(row => row.PaidAt == null)
        );
        long beforeDues = Balance(finished);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(bill.DueAt - fixture.Clock.Now));
        var maintenance = completed.GetRequiredService<CorporationMaintenance>();
        maintenance.Tick();
        maintenance.Tick();
        var paidBill = finished.Economy.Read(world => world.Set<SavedAllianceBill>().Find(bill.Id)!);
        Assert.Equal(corporation, paidBill.PaidByCorporationId);
        Assert.Equal(bill.DueAt, paidBill.PaidAt);
        Assert.Equal(beforeDues - bill.AmountCents, Balance(finished));
        Assert.Equal(acquired, completedClaims.Read(target));
        Assert.Equal(home, completedClaims.Read(home.SystemId));

        ServiceProvider Reopen()
        {
            var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
            StartAllianceActivity(host, unattended: true);
            host.GetRequiredService<CapsuleerScheduler>().PopulationPrepared(HeadquartersSystem);
            return host;
        }

        void Review(ServiceProvider host)
        {
            var maintenance = host.GetRequiredService<CorporationMaintenance>();
            for (int review = 0; review < 3; review++)
            {
                fixture.Clock.Seconds += 16;
                maintenance.Tick();
            }
            AssertUnattended(host);
        }

        bool DockedAtHeadquarters(ServiceProvider host, long pilot)
        {
            var state = host.GetRequiredService<CapsuleerScheduler>().Hauler(pilot);
            return state is { Pilot.Position: null } && state.Pilot.DockedLocationId == headquarters;
        }

        SavedPersonalOrder[] HubOrders(LiveWorld live) =>
            live.Economy.Read(world =>
                world
                    .Filter<SavedPersonalOrder>(order => order.CorporationId == corporation)
                    .Where(order => order.TypeId == hubType)
                    .ToArray()
            );

        long Balance(LiveWorld live) => live.Economy.Read(world => world.Set<SavedCorporation>().Find(corporation)!.BalanceCents);

        MarketExecution VerifyPurchase(LiveWorld live)
        {
            var filled = Assert.Single(HubOrders(live));
            Assert.Equal((bid.Id, PersonalOrderStatus.Filled, 0, 0L), (filled.Id, filled.Status, filled.Remaining, filled.EscrowCents));
            var executions = live.Economy.Read(world =>
                world
                    .Filter<MarketExecution>(execution => execution.TypeId == hubType)
                    .Where(execution => execution.OrderId == bid.Id)
                    .ToArray()
            );
            Assert.Equal(2, executions.Length);
            var purchase = Assert.Single(executions, execution => execution.IsBuy);
            var sale = Assert.Single(executions, execution => !execution.IsBuy);
            Assert.Equal(
                (bid.PilotId, Observer, headquarters, price, 1),
                (purchase.PilotId, purchase.CounterpartyId, purchase.StationId, purchase.PriceCents, purchase.Quantity)
            );
            Assert.Equal(
                (Observer, (long)corporation, headquarters, price, 1),
                (sale.PilotId, sale.CounterpartyId, sale.StationId, sale.PriceCents, sale.Quantity)
            );
            Assert.Equal(0L, purchase.WalletAmountCents);
            return purchase;
        }

        static IEnumerable<HaulerCheckpoint> Launches(LiveWorld live) =>
            live.HaulerCheckpoints.Values.Where(hauler =>
                hauler.Work.SovereigntyExpedition is { Duty: SovereigntyDuty.Claim, Returning: false }
            );

        static int CargoFuel(WorldAction world, long ship) =>
            world
                .Filter<OwnedItem>(item => item.LocationId == ship)
                .Where(item => item.TypeId == SovereigntyCaptureFixture.Strontium && item.FlagId == (int)InventoryFlag.Cargo)
                .Sum(item => item.Quantity);
    }

    private static SavedSovereigntyCampaignCredit[] CampaignCredits(LiveWorld live, long defeatedHubId) =>
        live.Economy.Read(world => world.Filter<SavedSovereigntyCampaignCredit>(credit => credit.HubId == defeatedHubId).ToArray());
}
