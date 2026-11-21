using Eden.Data.World.Agents;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.Server.Launcher;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Configuration;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Industry;

public sealed partial class FreshUniverseTests
{
    [InstalledCatalogFact]
    public async Task FreshCouriersAndRatsEarnTheirShipsAndPayToReplaceBothAfterLoss()
    {
        var directory = TestDirectory.Create("eden-fresh-careers-");
        try
        {
            await ReplayFreshCareers(directory.FullName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private async Task ReplayFreshCareers(string directory)
    {
        WorldDatabase database = new(Path.Combine(directory, "world.sqlite"));
        File.Copy(Environment.GetEnvironmentVariable(CatalogEnvironmentVariable)!, database.OriginalCatalogPath);
        TestClock clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        WorldInitialization.Initialize(database, clock, new(FreshUniverse: true));
        var configuration = new WorldConfigurationStore(database, clock);
        var saved = configuration.Read();
        var draft = saved.Active.Draft;
        configuration.Save(
            saved.Generation,
            draft with
            {
                Settings = draft.Settings with
                {
                    Capsuleers = draft.Settings.Capsuleers with
                    {
                        Miners = false,
                        FreshBountyPilots = false,
                        PrepareUnvisitedSystems = false,
                    },
                },
            },
            "Isolate fresh delivery and bounty careers"
        );
        WorldInitialization.Initialize(database, clock);
        WorldInitialization.PrepareCapsuleers(database, maximumCommunities: 0);

        int system;
        int courierHome;
        int patrolHome;
        var goals = new[] { (int)FreightHull.Badger, (int)TechOneHullType.Merlin };
        using (CatalogDbContext catalog = new(database.CatalogPath))
        {
            system = NpcDirectory
                .Agents(catalog)
                .Where(agent => agent is { Type: AgentType.Basic, Level: 1, DivisionId: MissionRules.DistributionDivision, SystemId: > 0 })
                .Select(agent => agent.SystemId!.Value)
                .Distinct()
                .First(id => FreshCareerRoutes.Read(catalog, id) is { Courier: not null, Combat: not null });
            var routes = FreshCareerRoutes.Read(catalog, system);
            courierHome = routes.Courier!.SourceStationId;
            patrolHome = routes.Combat!.SourceStationId;
            output.WriteLine(
                $"Delivery source {courierHome}, destination {routes.Courier.DestinationStationId}, pirate patrol base {patrolHome}."
            );
        }

        long courier;
        using (var limited = Open())
        {
            var live = limited.GetRequiredService<LiveWorld>();
            using var observer = limited.GetRequiredService<IHumanPresence>().Select(90000001, system);
            var preparation = limited.GetRequiredService<ICapsuleerBackgroundPreparation>();
            var turn = preparation.PrepareNext();
            Assert.Equal(system, turn.SystemId);
            Assert.Null(turn.Failure);
            Assert.True(preparation.PrepareNext().Complete);
            var agent = Assert.Single(live.Economy.Inspect(world => world.Set<SavedCapsuleerAgent>().ToArray()));
            Assert.Equal(FreshCareerRules.CourierSlot, agent.Slot);
            courier = agent.PilotId;
            await live.FlushAsync();
        }

        saved = configuration.Read();
        draft = saved.Active.Draft;
        configuration.Save(
            saved.Generation,
            draft with
            {
                Settings = draft.Settings with { Capsuleers = draft.Settings.Capsuleers with { FreshBountyPilots = true } },
            },
            "Enable the dedicated bounty hunter"
        );
        WorldInitialization.Initialize(database, clock);

        long[] pilots;
        long[] firstShips;
        long supplier;
        long[] incomeAtLoss;
        using (var host = Open())
        {
            var live = host.GetRequiredService<LiveWorld>();
            var market = host.GetRequiredService<IPilotMarket>();
            var presence = host.GetRequiredService<IHumanPresence>();
            presence.Update(Guid.NewGuid(), system);
            var provisioner = host.GetRequiredService<CapsuleerProvisioner>();
            var turn = provisioner.PrepareBackgroundSystem(
                system,
                host.GetRequiredService<IWorldSynchronization>(),
                () => presence.Read().ActiveSystems.Contains(system),
                CancellationToken.None
            );
            Assert.NotNull(turn?.CreatedPilotId);
            pilots = live.Economy.Inspect(world =>
                world.Set<SavedCapsuleerAgent>().OrderBy(agent => agent.Slot).Select(agent => agent.PilotId).ToArray()
            );
            Assert.Equal(2, pilots.Length);
            Assert.Equal(courier, pilots[0]);

            using CatalogDbContext catalog = new(database.CatalogPath);
            foreach (long id in pilots)
            {
                var start = live.Economy.Inspect(world => world.Pilots.Find(id)!.ActiveShip);
                Assert.Equal((int)HullClass.Corvette, catalog.RequireItem(start.TypeId).GroupId);
                Assert.Equal(0, market.Balance(id));
                Assert.Empty(market.Transactions(id));
                Assert.Equal(0, host.GetRequiredService<IPilotFreeSkillPoints>().Balance(id));
                Assert.Equal(2, host.GetRequiredService<IPilotInventory>().List(id, start.StationId).Count);
            }
            supplier = CreateSupplier(host, pilots[0]);
            ListStarterStock(host, catalog, supplier, goals, [courierHome, patrolHome]);
            AdvanceCareers(host, system, clock, () => Enumerable.Range(0, 2).All(i => Hull(host, pilots[i]) == goals[i]));
            firstShips = live.Economy.Inspect(world => pilots.Select(id => world.Pilots.Find(id)!.ActiveShipId).ToArray());
            incomeAtLoss = live.Economy.Inspect<long[]>(world => [DeliveryIncome(world, pilots[0]), BountyIncome(world, pilots[1])]);
            Assert.All(incomeAtLoss, income => Assert.True(income > 0));
            Assert.Contains(Allocations(live, pilots[1]), allocation => allocation.Points > 0);
            VerifyPurchases(host, pilots, goals, supplier, 1);
            Assert.NotEqual(
                courierHome,
                live.Economy.Inspect(world =>
                    world
                        .Filter<SavedMission>(mission => mission.PilotId == pilots[0])
                        .First(mission => mission.State == MissionState.Completed)
                        .DestinationStation
                )
            );

            using (var loss = live.Economy.Begin(TransactionParticipants.None))
            {
                for (int i = 0; i < pilots.Length; i++)
                {
                    long id = pilots[i];
                    var victim = loss.Pilots.Find(id)!;
                    int station = i == 0 ? courierHome : patrolHome;
                    var point = SystemObjects.Geometry(catalog, system).Single(contact => contact.Id == station);
                    SqliteWorld.SetPosition(loss, catalog, firstShips[i], point.Position, system, clock.Now);
                    var attacker = loss.Pilots.Find(supplier)!;
                    ShipLossPersistence.Apply(
                        loss,
                        catalog,
                        victim,
                        null,
                        firstShips[i],
                        system,
                        victim.ActiveShip.Position!,
                        clock.Now,
                        host.GetRequiredService<IShipLootRoll>(),
                        PilotInteractionHistoryStore.Player(loss, attacker, attacker.ActiveShipId)
                    );
                    // Finish the escape at the station so the replay focuses on the rookie's economic recovery.
                    host.GetRequiredService<SqliteWorld>().Dock(loss, catalog, id, victim.ActiveShipId, station);
                }
                loss.Commit();
            }
            await live.FlushAsync();
        }

        using var reopened = Open();
        var restored = reopened.GetRequiredService<LiveWorld>();
        AdvanceCareers(
            reopened,
            system,
            clock,
            () =>
                restored.Economy.Inspect(world =>
                    DeliveryIncome(world, pilots[0]) > incomeAtLoss[0] && BountyIncome(world, pilots[1]) > incomeAtLoss[1]
                )
        );
        VerifyPurchases(reopened, pilots, goals, supplier, 1);
        var scheduler = reopened.GetRequiredService<CapsuleerScheduler>();
        Assert.All(pilots, id => Assert.Equal((int)HullClass.Corvette, scheduler.Hauler(id)!.Pilot.ShipGroupId));
        using (CatalogDbContext catalog = new(database.CatalogPath))
        {
            ListStarterStock(reopened, catalog, supplier, goals, [courierHome, patrolHome]);
        }
        AdvanceCareers(reopened, system, clock, () => Enumerable.Range(0, 2).All(i => Hull(reopened, pilots[i]) == goals[i]));
        VerifyPurchases(reopened, pilots, goals, supplier, 2);
        restored.Economy.Inspect(world =>
        {
            Assert.All(Enumerable.Range(0, 2), i => Assert.NotEqual(firstShips[i], world.Pilots.Find(pilots[i])!.ActiveShipId));
            Assert.All(pilots, id => Assert.Single(world.Filter<ShipLoss>(loss => loss.PilotId == id)));
            Assert.True(DeliveryIncome(world, pilots[0]) > incomeAtLoss[0]);
            Assert.True(BountyIncome(world, pilots[1]) > incomeAtLoss[1]);
            return true;
        });
        Assert.Contains(Allocations(restored, pilots[1]), allocation => allocation.Points > 0);
        output.WriteLine($"Completed both replacements at {TimeSpan.FromSeconds(clock.Seconds)} simulated time.");

        // An established employer sends ordinary invitations. Each rookie finishes any paid work before accepting.
        using (var hire = restored.Economy.Begin(TransactionParticipants.None))
        {
            hire.Set<PilotWallet>().Find(supplier)!.Cents += CorporationRules.CreationFeeCents;
            hire.Add(
                new PilotSkill
                {
                    PilotId = supplier,
                    TypeId = CorporationRules.ManagementSkill,
                    Points = 250,
                }
            );
            hire.Commit();
        }
        var corporations = reopened.GetRequiredService<IPlayerCorporations>();
        var membership = reopened.GetRequiredService<ICorporationMembership>();
        var employer = corporations.Create(supplier, PlayerCorporationTests.Details("Courier and Patrol Service", "CPS")).Corporation;
        foreach (long id in pilots)
        {
            Assert.Equal(CorporationApplicationStatus.Invited, membership.Invite(supplier, id).Status);
        }
        AdvanceCareers(reopened, system, clock, () => pilots.All(id => corporations.Identity(id).Id == employer.Id));
        Assert.All(pilots, id => Assert.Contains(membership.Applications(id, false), a => a.Status == CorporationApplicationStatus.Joined));
        VerifyPurchases(reopened, pilots, goals, supplier, 2);

        ServiceProvider Open() => ServerServices.Build(database, clock, population: new TestNpcPopulationSeeder(system), liveWorld: true);
    }

    private static long CreateSupplier(ServiceProvider host, long example)
    {
        var live = host.GetRequiredService<LiveWorld>();
        var rookie = live.Economy.Inspect(world => world.Pilots.Find(example)!);
        var account = host.GetRequiredService<LauncherStore>().Register(new("Independent Supplier", "Isolated Test Password 1"));
        return host.GetRequiredService<ICharacterCreation>()
            .Create(
                account.AccountId,
                new(
                    "Independent Supplier",
                    rookie.RaceId,
                    rookie.BloodlineId,
                    rookie.GenderId,
                    CharacterRules.UnspecifiedAncestry,
                    [],
                    [],
                    rookie.SchoolId
                )
            );
    }

    private static void ListStarterStock(ServiceProvider host, CatalogDbContext catalog, long supplier, int[] goals, int[] stations)
    {
        var live = host.GetRequiredService<LiveWorld>();
        var market = host.GetRequiredService<IPersonalMarket>();
        for (int i = 0; i < goals.Length; i++)
        {
            MarketSale[] sales;
            using (var stock = live.Economy.Begin(TransactionParticipants.None))
            {
                int station = stations[i];
                stock.Pilots.Find(supplier)!.ActiveShip.StationId = station;
                if (stock.Set<PilotWallet>().Find(supplier) is null)
                {
                    stock.Add(new PilotWallet { PilotId = supplier, Cents = 10_000_000 });
                }
                sales = CapsuleerIndustry
                    .CareerKit(catalog, goals[i])!
                    .Parts()
                    .Select(part =>
                    {
                        int quantity = part.Quantity;
                        var item = InventoryPersistence
                            .MergeOutput(stock, catalog, supplier, station, (int)InventoryFlag.Hangar, part.TypeId, quantity)
                            .Item;
                        long price = Math.Clamp(CapsuleerIndustry.ReferencePrice(catalog, part.TypeId) / 10, 10, 200_000);
                        return new MarketSale(item.Id, station, part.TypeId, price, quantity);
                    })
                    .ToArray();
                stock.Commit();
            }
            market.PlaceSell(supplier, sales, 7);
        }
    }

    private void AdvanceCareers(ServiceProvider host, int system, TestClock clock, Func<bool> complete)
    {
        var presence = host.GetRequiredService<IHumanPresence>();
        if (!presence.Read().ActiveSystems.Contains(system))
        {
            presence.Update(Guid.NewGuid(), system);
        }
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        scheduler.PopulationPrepared(system);
        long turns = 0;
        while (turns++ < 160_000)
        {
            if (turns % 8 == 0 && complete())
            {
                return;
            }
            long due = scheduler.Jobs().Min(job => job.DueTicks);
            long delay = Math.Max(TimeSpan.TicksPerMillisecond, due - presence.Read().ElapsedTicks);
            clock.Milliseconds += (delay + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond;
            scheduler.Run((int)clock.Seconds);
        }
        var live = host.GetRequiredService<LiveWorld>();
        foreach (var job in scheduler.Jobs())
        {
            var actor = scheduler.Hauler(job.PilotId);
            var report = live.Economy.Inspect(world => (DeliveryIncome(world, job.PilotId), BountyIncome(world, job.PilotId)));
            output.WriteLine(
                $"{job.PilotId} {actor?.Pilot.ShipTypeId} {actor?.Pilot.SystemId} {actor?.Work.Activity} "
                    + $"{actor?.Work.WaitingReason} {actor?.Work.Ratting?.Phase} payments {report} at {clock.Seconds}s."
            );
        }
        Assert.Fail($"Fresh careers exceeded their simulated work budget. Scheduler failure: {scheduler.Read().LastFailure}");
    }

    private static int Hull(ServiceProvider host, long pilot) =>
        host.GetRequiredService<LiveWorld>().Economy.Inspect(world => world.Pilots.Find(pilot)!.ActiveShip.TypeId);

    private static long DeliveryIncome(WorldAction world, long pilot) =>
        world
            .Filter<SavedMarketPayment>(payment => payment.PilotId == pilot)
            .Where(payment =>
                payment.Kind == WalletEntryType.AgentMissionReward || payment.Kind == WalletEntryType.AgentMissionTimeBonusReward
            )
            .Sum(payment => payment.AmountCents);

    private static long BountyIncome(WorldAction world, long pilot) =>
        world.Filter<NpcBounty>(bounty => bounty.PilotId == pilot).Sum(bounty => bounty.AmountCents);

    private static void VerifyPurchases(ServiceProvider host, long[] pilots, int[] goals, long supplier, int count)
    {
        var market = host.GetRequiredService<IPilotMarket>();
        for (int i = 0; i < pilots.Length; i++)
        {
            var trades = market.Transactions(pilots[i]).Where(trade => trade.IsBuy && trade.TypeId == goals[i]).ToArray();
            Assert.Equal(count, trades.Sum(trade => trade.Quantity));
            Assert.All(
                trades,
                trade =>
                {
                    Assert.Equal(supplier, trade.CounterpartyId);
                    Assert.True(trade.PriceCents > 0);
                }
            );
        }
    }
}
