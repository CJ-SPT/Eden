using System.Diagnostics;
using System.Text.Json;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Corporations;
using Eden.Data.World.Economy;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Protocol.Tests.Capsuleers;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Industry;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Ammo = Eden.Gameplay.Capsuleers.CapsuleerAmmunitionSupplyRules;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class ServerPerformanceReplayTests
{
    private const int DynamicReplaySystem = 30001015;
    private const int DynamicReplayStation = 60012199;
    private const long DynamicReplayBuyer = 90000001;
    private const long DynamicReplayPilot = 90000003;
    private const int DynamicReplayOrderQuantity = 100;
    private const long DynamicReplayUnitPrice = 100_000;

    [Theory]
    [InlineData(CapsuleerPatrolFit.Moa, 567, 223)]
    [InlineData(CapsuleerPatrolFit.Drake, 501, 209)]
    public async Task ReplayPopulatedFactoryHuntShipLossAndFactoryReturn(int hull, int weapon, int ammunition)
    {
        if (Environment.GetEnvironmentVariable("EDEN_DYNAMIC_REPLAY_SOURCE") is not { } source)
        {
            output.WriteLine("Set EDEN_DYNAMIC_REPLAY_SOURCE to run the populated dynamic work replay.");
            return;
        }
        if (Environment.GetEnvironmentVariable("EDEN_DYNAMIC_REPLAY_HULL") is { } selected && selected != hull.ToString())
        {
            return;
        }

        string results =
            Environment.GetEnvironmentVariable("EDEN_DYNAMIC_REPLAY_OUTPUT")
            ?? throw new InvalidOperationException("Set EDEN_DYNAMIC_REPLAY_OUTPUT to retain replay evidence.");
        Directory.CreateDirectory(results);
        string report = Path.Combine(results, $"dynamic-work-{hull}.jsonl");
        using var events = new StreamWriter(report) { AutoFlush = true };
        var elapsed = Stopwatch.StartNew();
        void Record(string stage, object state) =>
            events.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        stage,
                        elapsedMs = elapsed.ElapsedMilliseconds,
                        state,
                    }
                )
            );

        Record(
            "inputs",
            new
            {
                source,
                hull,
                weapon,
                ammunition,
                station = DynamicReplayStation,
                system = DynamicReplaySystem,
                pilot = DynamicReplayPilot,
                quantity = DynamicReplayOrderQuantity,
                priceCents = DynamicReplayUnitPrice,
            }
        );
        string directory = CopySnapshot(source);
        try
        {
            CopyThroughputSidecars(source, directory, null);
            WorldDatabase database = new(Path.Combine(directory, "world.sqlite"));
            TestClock clock = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
            WorldInitialization.Initialize(database, clock);
            Record("initialized", new { clock.UtcNow });
            using var host = ServerServices.Build(database, clock, liveWorld: true);
            var configuration = database.Configuration.Active!;
            var settings = configuration.Draft.Settings;
            database.Configuration.Install(
                configuration with
                {
                    Draft = configuration.Draft with
                    {
                        Settings = settings with
                        {
                            Capsuleers = settings.Capsuleers with
                            {
                                GateVisitors = 0,
                                PrepareUnvisitedSystems = false,
                                RoamingPvp = true,
                                RoamPercent = 100,
                                RoamingGangs = false,
                                RegionalConflicts = false,
                                AutomaticRecruitment = false,
                                AutomaticAlliances = false,
                                NewSovereigntyClaims = false,
                            },
                        },
                    },
                }
            );

            var live = host.GetRequiredService<LiveWorld>();
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            var market = host.GetRequiredService<IPersonalMarket>();
            using CatalogDbContext catalog = new(database.CatalogPath);
            var (combatShip, corporation) = PrepareDynamicOperator(live, catalog, clock, hull, weapon, ammunition);
            int population = live.Economy.AdmittedCapsuleerCount;
            Assert.True(population >= 1000, $"The source has only {population} admitted bots.");
            Record(
                "populated",
                new
                {
                    population,
                    combatShip,
                    corporation,
                }
            );

            var baseline = live.Economy.Inspect(world =>
                world.Set<SavedCapsuleerJob>().ToDictionary(job => job.PilotId, job => job.Sequence)
            );
            int ProgressedPilots() => live.CapsuleerJobs.Values.Count(job => job.Sequence > baseline.GetValueOrDefault(job.PilotId));
            var ships = host.GetRequiredService<SqliteWorld>();
            int? DockedReserve() =>
                live.Economy.Inspect<int?>(world =>
                {
                    var current = ships.FindPilot(world, CapsuleerCommunityStore.MinerAccountId, DynamicReplayPilot)!;
                    return current.Position is null && world is EconomyTransaction resident
                        ? CapsuleerIndustry.ReadyHuntReplacements(resident, catalog, current)
                        : null;
                });
            long ActiveShip() => live.Economy.Peek<Eden.Data.World.Characters.Pilot>(DynamicReplayPilot)!.ActiveShipId;

            Assert.Equal(2, DockedReserve());
            long firstOrder = FundDynamicBid(market);
            using var observer = host.GetRequiredService<IHumanPresence>().Select(DynamicReplayBuyer, DynamicReplaySystem);

            void Drive(string stage, Func<bool> completed, int maximumSeconds)
            {
                for (int turn = 0; turn < maximumSeconds && !completed(); turn++)
                {
                    clock.Seconds++;
                    maintenance.Tick();
                }
                var status = scheduler.Read();
                var job = live.CapsuleerJobs.GetValueOrDefault(DynamicReplayPilot);
                var work = live.HaulerCheckpoints.GetValueOrDefault(DynamicReplayPilot)?.Work;
                Record(
                    stage,
                    new
                    {
                        clock.Seconds,
                        status.ScheduledAgents,
                        status.CompletedActions,
                        progressedPilots = ProgressedPilots(),
                        status.Failures,
                        status.LastFailure,
                        job,
                        work?.Activity,
                        work?.WaitingReason,
                        work?.ShipId,
                        work?.Ratting?.Patrol?.Kind,
                        dockedReserve = DockedReserve(),
                    }
                );
                Assert.True(completed(), $"{stage} timed out. See {report}.");
            }

            IndustryJob[] Jobs() =>
                live.Economy.Inspect(world =>
                    world
                        .Filter<IndustryJob>(job => job.PilotId == DynamicReplayPilot)
                        .Where(job => job.OutputCorporationId == corporation && job.ProductTypeId == Ammo.ChargeType)
                        .ToArray()
                );
            Drive("first_factory_job", () => Jobs().Any(job => job.Status == IndustryJobStatus.Installed), 900);
            var firstJob = Assert.Single(Jobs());
            Assert.Equal(combatShip, live.Economy.Peek<Eden.Data.World.Characters.Pilot>(DynamicReplayPilot)!.ActiveShipId);
            long deadline = Assert.IsType<long>(firstJob.CapsuleerEndTicks);
            clock.Seconds += Math.Max(0, deadline - live.CapsuleerActivity.Read().ElapsedTicks) / TimeSpan.TicksPerSecond;
            Drive("first_market_fill", () => live.Economy.Peek<SavedPersonalOrder>(firstOrder)?.Remaining == 0, 900);
            Assert.Equal(IndustryJobStatus.Delivered, Assert.Single(Jobs()).Status);
            Record(
                "first_sale",
                new
                {
                    firstJob.Id,
                    firstJob.CostCents,
                    firstJob.OutputQuantity,
                    firstOrder,
                }
            );

            Drive("hunting", () => live.HaulerCheckpoints.GetValueOrDefault(DynamicReplayPilot)?.Work.Ratting?.Patrol is not null, 900);
            var patrol = Assert.IsType<HaulerObservation>(scheduler.Hauler(DynamicReplayPilot));
            Assert.Equal(combatShip, patrol.Pilot.ShipId);
            Assert.Equal(hull, patrol.Pilot.ShipTypeId);
            Assert.NotNull(patrol.Pilot.Position);
            Assert.Equal(PatrolKind.Roam, patrol.Work.Ratting?.Patrol?.Kind);

            long capsule = LoseDynamicReplayShip(live, catalog, clock);
            var loss = live.Economy.Inspect(world => Assert.Single(world.Filter<ShipLoss>(row => row.ShipId == combatShip)));
            Assert.Equal(capsule, loss.CapsuleId);
            Record(
                "ship_loss",
                new
                {
                    loss.ShipId,
                    loss.CapsuleId,
                    loss.TypeId,
                }
            );
            Drive("owned_replacement", () => ActiveShip() != capsule && live.Economy.Peek<Ship>(ActiveShip())?.TypeId == hull, 1800);
            var replacement = Assert.IsType<HaulerObservation>(scheduler.Hauler(DynamicReplayPilot));
            Assert.Equal(hull, replacement.Pilot.ShipTypeId);
            Assert.NotEqual(combatShip, replacement.Pilot.ShipId);
            Assert.NotEqual(capsule, replacement.Pilot.ShipId);
            Assert.Null(replacement.Pilot.Position);
            Assert.Null(replacement.Work.Ratting);
            Assert.Equal(1, DockedReserve());
            long replacementSequence = live.CapsuleerJobs[DynamicReplayPilot].Sequence;
            Drive("reserve_prevents_hunt", () => live.CapsuleerJobs[DynamicReplayPilot].Sequence > replacementSequence, 120);
            Assert.Null(live.HaulerCheckpoints[DynamicReplayPilot].Work.Ratting?.Patrol);
            Assert.Null(Assert.IsType<HaulerObservation>(scheduler.Hauler(DynamicReplayPilot)).Pilot.Position);
            Assert.Equal(1, DockedReserve());
            Assert.Single(Jobs());

            long secondOrder = FundDynamicBid(market);
            Drive(
                "post_loss_factory_job",
                () => Jobs().Any(job => job.Status == IndustryJobStatus.Installed && job.Id != firstJob.Id),
                1800
            );
            var secondJob = Assert.Single(Jobs(), job => job.Id != firstJob.Id);
            Assert.Equal(replacement.Pilot.ShipId, ActiveShip());
            Assert.Equal(1, DockedReserve());
            Record(
                "replacement_then_production",
                new
                {
                    replacement.Pilot.ShipId,
                    secondJob.Id,
                    secondOrder,
                }
            );

            clock.Seconds +=
                Math.Max(0, Assert.IsType<long>(secondJob.CapsuleerEndTicks) - live.CapsuleerActivity.Read().ElapsedTicks)
                / TimeSpan.TicksPerSecond;
            Drive("second_market_fill", () => live.Economy.Peek<SavedPersonalOrder>(secondOrder)?.Remaining == 0, 900);
            Assert.All(Jobs(), job => Assert.Equal(IndustryJobStatus.Delivered, job.Status));
            var diagnostics = scheduler.Read();
            Assert.True(diagnostics.ScheduledAgents > 1);
            Assert.True(ProgressedPilots() > 1);
            await live.FlushAsync();
            Record(
                "checkpoint",
                new
                {
                    diagnostics.ScheduledAgents,
                    diagnostics.CompletedActions,
                    progressedPilots = ProgressedPilots(),
                    diagnostics.Failures,
                    live.LastCheckpoint?.Generation,
                }
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (long Ship, int Corporation) PrepareDynamicOperator(
        LiveWorld live,
        CatalogDbContext catalog,
        TestClock clock,
        int hull,
        int weapon,
        int ammunition
    )
    {
        using var setup = live.Economy.Begin(TransactionParticipants.None);
        var economy = setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!;
        economy.TestingMarket = false;
        economy.TestingGrants = false;
        var agent = setup.Set<SavedCapsuleerAgent>().Find(DynamicReplayPilot)!;
        Assert.Equal(CapsuleerCareer.Hauling, agent.Career);
        var actor = setup.Pilots.Find(DynamicReplayPilot)!;
        var buyer = setup.Pilots.Find(DynamicReplayBuyer)!;
        Assert.Null(actor.ActiveShip.Position);
        SqliteWorld.SetPosition(setup, catalog, buyer.ActiveShipId, null, null, clock.Now);
        actor.ActiveShip.StationId = DynamicReplayStation;
        buyer.ActiveShip.StationId = DynamicReplayStation;
        setup.Set<PilotWallet>().Find(DynamicReplayPilot)!.Cents = 2_000_000_000;
        var wallet =
            setup.Set<PilotWallet>().Find(DynamicReplayBuyer) ?? setup.Add(new PilotWallet { PilotId = DynamicReplayBuyer }).Entity;
        wallet.Cents = 1_000_000_000;
        var management =
            setup.Set<PilotSkill>().Find(DynamicReplayPilot, Eden.Gameplay.Corporations.CorporationRules.ManagementSkill)
            ?? setup
                .Add(new PilotSkill { PilotId = DynamicReplayPilot, TypeId = Eden.Gameplay.Corporations.CorporationRules.ManagementSkill })
                .Entity;
        management.Points = Math.Max(250, management.Points);
        var details = PlayerCorporationTests.Details("Dynamic Replay Works", "DYRW") with { ApplicationsEnabled = false };
        int corporation = PlayerCorporationStore.Create(setup, catalog, DynamicReplayPilot, details, clock.Now).Corporation.Id;
        var employer = setup.Set<SavedCorporation>().Find(corporation)!;
        employer.CapsuleerFoundingSystemId = DynamicReplaySystem;
        employer.BalanceCents = 2_000_000_000;

        CapsuleerCommunityStore.GrantRequirements(setup, catalog, DynamicReplayPilot, hull, []);
        var ready = setup
            .Add(
                new Ship
                {
                    Id = setup.NextItemId(),
                    OwnerId = DynamicReplayPilot,
                    TypeId = hull,
                    StationId = DynamicReplayStation,
                    Name = "Replay patrol ship",
                }
            )
            .Entity;
        PilotFleetStore.Board(setup, catalog, DynamicReplayPilot, ready.Id, actor.ActiveShipId, clock.Now);
        int firstHigh = (int)InventoryFlag.HighSlot0;
        TrafficFitting.Apply(
            setup,
            catalog,
            DynamicReplayPilot,
            ready.Id,
            TrafficSnapshotTests.Profile() with
            {
                HullTypeId = hull,
                Equipment =
                [
                    new(weapon, firstHigh, 1),
                    new(ammunition, firstHigh, 20),
                    new(weapon, firstHigh + 1, 1),
                    new(ammunition, firstHigh + 1, 20),
                ],
            },
            clock.Now
        );
        var old = setup.Filter<SavedCapsuleerHauler>(row => row.PilotId == DynamicReplayPilot).Single();
        old.Progress = null;
        old.SourceStationId = DynamicReplayStation;
        old.DestinationStationId = DynamicReplayStation;
        old.RouteSystems = [DynamicReplaySystem];
        var job = setup.Set<SavedCapsuleerJob>().Find(DynamicReplayPilot)!;
        job.Action = CapsuleerAction.Trade;
        job.DueTicks = setup.ActiveTicks;

        void Stock(int type, int quantity, bool blueprint = false) =>
            setup.Add(
                new OwnedItem
                {
                    Id = setup.NextItemId(),
                    OwnerId = DynamicReplayPilot,
                    LocationId = DynamicReplayStation,
                    TypeId = type,
                    Quantity = quantity,
                    FlagId = (int)InventoryFlag.Hangar,
                    Singleton = blueprint,
                    BlueprintRuns = blueprint ? -1 : 0,
                }
            );

        Stock(hull, 2);
        Stock(weapon, 4);
        Stock(ammunition, 18_000);
        var recipe = ManufacturingCatalog.Recipes(catalog)[Ammo.BlueprintType];
        Stock(recipe.BlueprintTypeId, 1, blueprint: true);
        foreach (var input in recipe.Materials)
        {
            Stock(checked((int)input.TypeId!.Value), checked((int)input.Quantity!.Value * 8));
        }
        foreach (var skill in recipe.Skills)
        {
            var trained =
                setup.Set<PilotSkill>().Find(DynamicReplayPilot, skill.TypeId)
                ?? setup.Add(new PilotSkill { PilotId = DynamicReplayPilot, TypeId = skill.TypeId }).Entity;
            double rank = catalog.FittingAttributes(skill.TypeId).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant);
            trained.Points = Math.Max(trained.Points, TrainedSkill.PointsForLevel(rank, skill.Level));
        }
        setup.Commit();
        live.CapsuleerRoutines.Settle(DynamicReplayPilot, live.CapsuleerActivity.Read().ElapsedTicks);
        return (ready.Id, corporation);
    }

    private static long FundDynamicBid(IPersonalMarket market)
    {
        market.PlaceBuy(
            DynamicReplayBuyer,
            new(DynamicReplayStation, Ammo.ChargeType, DynamicReplayUnitPrice, DynamicReplayOrderQuantity, 7)
        );
        return Assert
            .Single(
                market.PersonalOrders(DynamicReplayBuyer),
                order => order.StationId == DynamicReplayStation && order.TypeId == Ammo.ChargeType
            )
            .Id;
    }

    private static long LoseDynamicReplayShip(LiveWorld live, CatalogDbContext catalog, TestClock clock)
    {
        using var world = live.Economy.Begin();
        var pilot = world.Pilots.Find(DynamicReplayPilot)!;
        var ship = pilot.ActiveShip;
        var position = Assert.IsType<Eden.Data.World.Docking.FlightPosition>(ship.Position);
        var buyer = world.Pilots.Find(DynamicReplayBuyer)!;
        ShipLossPersistence.Apply(
            world,
            catalog,
            pilot,
            null,
            ship.Id,
            Assert.IsType<int>(position.SystemId),
            position,
            clock.Now,
            new DynamicReplayNoLoot(),
            PilotInteractionHistoryStore.Player(world, buyer, buyer.ActiveShipId)
        );
        world.Commit();
        return pilot.ActiveShipId;
    }

    private sealed class DynamicReplayNoLoot : IShipLootRoll
    {
        public bool Survives() => false;
    }
}
