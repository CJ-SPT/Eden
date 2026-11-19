using Eden.Data.World.Capsuleers;
using Eden.Data.World.Economy;
using Eden.Data.World.Industry;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Operations;
using Eden.Server;
using Eden.Server.Launcher;
using Eden.World.Capsuleers;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Industry;

public sealed class FreshUniverseTests(ITestOutputHelper output)
{
    // The pinned catalog's starter system and a station with reprocessing, manufacturing and a market.
    private const int StarterSystem = 30015042;
    private const int Factory = 60015017;
    private const int Miner = (int)MiningLaserType.MinerI;
    private const string CatalogEnvironmentVariable = "EDEN_FRESH_UNIVERSE_CATALOG";

    private sealed class InstalledCatalogFactAttribute : FactAttribute
    {
        public InstalledCatalogFactAttribute()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable(CatalogEnvironmentVariable)))
            {
                Skip = $"Set {CatalogEnvironmentVariable} to the pinned build 3662168 catalog.";
            }
        }
    }

    [Fact]
    public void FreshProfileStartsEmptyAndPersistsAcrossRestart()
    {
        var directory = TestDirectory.Create("eden-fresh-profile-");
        try
        {
            WorldDatabase database = new(Path.Combine(directory.FullName, "world.sqlite"));
            TestCatalog.Create(database.OriginalCatalogPath);
            TestClock clock = new();
            WorldInitialization.Initialize(database, clock, new(FreshUniverse: true));
            WorldInitialization.Initialize(database, clock);
            using var host = ServerServices.Build(database, clock, population: new TestNpcPopulationSeeder(), liveWorld: true);
            var live = host.GetRequiredService<LiveWorld>();
            Assert.Equal(0, live.Economy.Inspect(world => world.Pilots.Count()));
            Assert.Equal(0, live.Economy.Inspect(world => world.Set<NpcMarketOrder>().Count()));
            Assert.Equal(0, live.Economy.Inspect(world => world.Set<TestingWalletGrant>().Count()));
            Assert.True(database.Settings.FreshUniverse);
            Assert.True(database.Settings.Economy.ChargeServiceCosts);
            Assert.False(database.Settings.DevelopmentSupply.TestingGrants);
            var configuration = host.GetRequiredService<IWorldConfiguration>();
            var current = configuration.Read();
            var ordinary = current.Active.Draft with { Settings = current.Active.Draft.Settings with { FreshUniverse = false } };
            Assert.Throws<InvalidDataException>(() => configuration.Save(current.Generation, ordinary, "Test"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [InstalledCatalogFact]
    public void NewCorvettesEarnAndTradeTheFirstManufacturedMinerThroughFundedEscrow()
    {
        var directory = TestDirectory.Create("eden-fresh-universe-");
        try
        {
            RunFreshWorld(directory.FullName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private void RunFreshWorld(string directory)
    {
        WorldDatabase database = new(Path.Combine(directory, "world.sqlite"));
        File.Copy(Environment.GetEnvironmentVariable(CatalogEnvironmentVariable)!, database.OriginalCatalogPath);
        TestClock clock = new(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero));
        WorldInitialization.Initialize(database, clock, new(FreshUniverse: true));
        WorldInitialization.PrepareCapsuleers(database, maximumCommunities: 0);
        long producer;
        long buyer;
        long orderId;
        long price;
        using (var host = Open())
        {
            var live = host.GetRequiredService<LiveWorld>();
            Assert.Equal(0, live.Economy.Inspect(world => world.Pilots.Count()));
            var presence = host.GetRequiredService<IHumanPresence>();
            presence.Update(Guid.NewGuid(), StarterSystem);
            var provisioner = host.GetRequiredService<CapsuleerProvisioner>();
            var synchronization = host.GetRequiredService<IWorldSynchronization>();
            for (int slot = 0; slot < StarterIndustryRules.CrewSize; slot++)
            {
                var turn = provisioner.PrepareBackgroundSystem(
                    StarterSystem,
                    synchronization,
                    () => presence.Read().ActiveSystems.Contains(StarterSystem),
                    CancellationToken.None
                );
                Assert.NotNull(turn?.CreatedPilotId);
            }
            var founders = live.Economy.Inspect(world => world.Set<SavedCapsuleerAgent>().ToArray());
            Assert.Equal(2, founders.Length);
            producer = founders.Single(row => row.Slot == StarterIndustryRules.ProducerSlot).PilotId;
            buyer = founders.Single(row => row.Slot == StarterIndustryRules.BuyerSlot).PilotId;
            CompareWithNewCharacters(host, live, founders);
            var market = host.GetRequiredService<IPilotMarket>();
            var points = host.GetRequiredService<IPilotFreeSkillPoints>();
            Assert.Empty(market.Orders(buyer, Miner));
            Assert.Throws<InvalidDataException>(() =>
                host.GetRequiredService<IPersonalMarket>().PlaceBuy(buyer, new(Factory, Miner, 1000, 1, 90))
            );
            Assert.Empty(market.Transactions(buyer));
            Assert.Equal(0, market.Balance(buyer));
            Assert.Equal(0, points.Balance(buyer));

            Advance(
                host,
                clock,
                () => host.GetRequiredService<IPersonalMarket>().PersonalOrders(buyer).Any(order => order.TypeId == Miner)
            );
            var order = host.GetRequiredService<IPersonalMarket>().PersonalOrders(buyer).Single(order => order.TypeId == Miner);
            orderId = order.Id;
            price = order.PriceCents;
            Assert.Equal(price, order.EscrowCents);
            Assert.DoesNotContain(host.GetRequiredService<IPilotInventory>().List(buyer, Factory), item => item.TypeId == Miner);
            Assert.Contains(market.Transactions(buyer), trade => !trade.IsBuy && trade.CounterpartyId != producer && trade.PriceCents > 0);
            Assert.True(points.Balance(buyer) > 0);
            output.WriteLine($"First funded order after {TimeSpan.FromSeconds(clock.Seconds)}, escrow {price} cents.");
        }

        WorldInitialization.Initialize(database, clock);
        using (var host = Open())
        {
            var personal = host.GetRequiredService<IPersonalMarket>();
            Assert.Contains(personal.PersonalOrders(buyer), order => order.Id == orderId && order.EscrowCents == price);
            var market = host.GetRequiredService<IPilotMarket>();
            Advance(
                host,
                clock,
                () => market.Transactions(buyer).Any(trade => trade.OrderId == orderId && trade.CounterpartyId == producer)
            );
            AssertSale(host);
            output.WriteLine($"First manufactured item sold after {TimeSpan.FromSeconds(clock.Seconds)}.");
        }

        WorldInitialization.Initialize(database, clock);
        using (var host = Open())
        {
            AssertSale(host);
        }

        ServiceProvider Open() => ServerServices.Build(database, clock, population: new TestNpcPopulationSeeder(), liveWorld: true);

        void AssertSale(ServiceProvider host)
        {
            var live = host.GetRequiredService<LiveWorld>();
            var market = host.GetRequiredService<IPilotMarket>();
            var inventory = host.GetRequiredService<IPilotInventory>();
            var personal = host.GetRequiredService<IPersonalMarket>();
            var fill = Assert.Single(market.Transactions(buyer), trade => trade.TypeId == Miner && trade.IsBuy);
            Assert.Equal((orderId, producer, price, 1), (fill.OrderId, fill.CounterpartyId, fill.PriceCents, fill.Quantity));
            var sale = Assert.Single(market.Transactions(producer), trade => trade.TypeId == Miner && !trade.IsBuy);
            Assert.Equal((buyer, price, 1), (sale.CounterpartyId, sale.PriceCents, sale.Quantity));
            Assert.True(sale.TaxCents > 0);
            Assert.Empty(personal.PersonalOrders(buyer));
            Assert.Equal(0, personal.Escrow(buyer).IskCents);
            Assert.Equal(1, Assert.Single(inventory.List(buyer, Factory), item => item.TypeId == Miner).Quantity);
            Assert.DoesNotContain(inventory.List(producer, Factory), item => item.TypeId == Miner);
            Assert.DoesNotContain(market.Orders(buyer, Miner), order => !order.Bid);
            var job = live.Economy.Inspect(world => world.Set<IndustryJob>().Single(row => row.PilotId == producer));
            Assert.Equal(
                (IndustryJobStatus.Delivered, StarterIndustryRules.MinerBlueprint, Miner, 1),
                (job.Status, job.BlueprintTypeId, job.ProductTypeId, job.OutputQuantity)
            );
            Assert.True(job.CostCents > 0);
            Assert.Contains(market.Transactions(producer), trade => trade.TypeId == StarterIndustryRules.MinerBlueprint && trade.IsBuy);
            var servicePayments = live.Economy.Inspect(world =>
                world.Filter<SavedServicePayment>(row => row.PilotId == producer).ToArray()
            );
            Assert.Contains(servicePayments, payment => payment.Kind == WalletEntryType.ReprocessingTax && payment.AmountCents < 0);
            Assert.Contains(
                servicePayments,
                payment =>
                    payment.Kind == WalletEntryType.Manufacturing && payment.ContextId == job.Id && payment.AmountCents == -job.CostCents
            );
            var points = host.GetRequiredService<IPilotFreeSkillPoints>();
            Assert.True(points.Balance(producer) > 0);
            Assert.True(points.Balance(buyer) > 0);
        }
    }

    private static void CompareWithNewCharacters(ServiceProvider host, LiveWorld live, IReadOnlyList<SavedCapsuleerAgent> founders)
    {
        var launcher = host.GetRequiredService<LauncherStore>();
        var creator = host.GetRequiredService<ICharacterCreation>();
        var inventory = host.GetRequiredService<IPilotInventory>();
        var market = host.GetRequiredService<IPilotMarket>();
        foreach (var founder in founders)
        {
            var bot = live.Economy.Inspect(world => world.Pilots.Find(founder.PilotId)!);
            var account = launcher.Register(new($"Rookie Account {founder.Slot}", "Isolated Test Password 1"));
            long human = creator.Create(
                account.AccountId,
                new(
                    $"Rookie Pilot {founder.Slot}",
                    bot.RaceId,
                    bot.BloodlineId,
                    bot.GenderId,
                    CharacterRules.UnspecifiedAncestry,
                    [],
                    [],
                    bot.SchoolId
                )
            );
            var player = live.Economy.Inspect(world => world.Pilots.Find(human)!);
            Assert.Equal(player.ActiveShip.TypeId, bot.ActiveShip.TypeId);
            Assert.Equal(0, market.Balance(human));
            Assert.Equal(0, market.Balance(bot.Id));
            Assert.Equal(Factory, bot.ActiveShip.StationId);
            var kit = inventory.List(bot.Id, Factory);
            Assert.Equal(2, kit.Count);
            Assert.Equal(
                inventory.List(human, player.ActiveShip.StationId).Select(i => i.TypeId).Order(),
                kit.Select(i => i.TypeId).Order()
            );
            Assert.Equal(Skills(human), Skills(bot.Id));
        }

        (int, long)[] Skills(long pilot) =>
            live.Economy.Inspect(world =>
                world
                    .Filter<PilotSkill>(skill => skill.PilotId == pilot)
                    .OrderBy(skill => skill.TypeId)
                    .Select(skill => new ValueTuple<int, long>(skill.TypeId, skill.Points))
                    .ToArray()
            );
    }

    private void Advance(ServiceProvider host, TestClock clock, Func<bool> completed)
    {
        var presence = host.GetRequiredService<IHumanPresence>();
        if (!presence.Read().ActiveSystems.Contains(StarterSystem))
        {
            presence.Update(Guid.NewGuid(), StarterSystem);
        }
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        scheduler.PopulationPrepared(StarterSystem);
        for (int turn = 0; turn < 100_000; turn++)
        {
            if (turn % 10 == 0 && completed())
            {
                return;
            }
            long due = scheduler.Jobs().Min(job => job.DueTicks);
            long delay = Math.Max(TimeSpan.TicksPerMillisecond, due - presence.Read().ElapsedTicks);
            clock.Milliseconds += (delay + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond;
            scheduler.Run((int)clock.Seconds);
        }
        foreach (var job in scheduler.Jobs())
        {
            var pilot = scheduler.Miner(job.PilotId);
            output.WriteLine(
                $"{job.PilotId} {pilot?.Work.Activity} {pilot?.Work.Trips} trips {pilot?.Work.WaitingReason} at {clock.Seconds}s."
            );
        }
        Assert.Fail("The starter industry loop exceeded its simulated work budget.");
    }
}
