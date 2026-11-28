using Eden.Data.World.Capsuleers;
using Eden.Data.World.Economy;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rules = Eden.Gameplay.Capsuleers.CapsuleerAmmunitionSupplyRules;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerAmmunitionSupplyTests
{
    public enum EarnedMiningLaunch
    {
        Patrol,
        FitOffline,
        RoamDeclined,
    }

    [Theory]
    [InlineData(EarnedMiningLaunch.Patrol)]
    [InlineData(EarnedMiningLaunch.FitOffline)]
    [InlineData(EarnedMiningLaunch.RoamDeclined)]
    public async Task UnattendedMinerStagesAnEarnedPatrolAndResumesWorkIfTheFitFails(EarnedMiningLaunch launch)
    {
        const int frigate = (int)TechOneHullType.Merlin;
        const int turret = (int)CombatWeaponType.GatlingRailgun75mm;
        const int roamDrawPercent = 50;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PrepareCatalog(fixture);
        UseLocalOrders(fixture, fundRefits: false, earnedPatrol: true);
        if (launch == EarnedMiningLaunch.RoamDeclined)
        {
            var configuration = new WorldConfigurationStore(fixture.Database, fixture.Clock);
            var startup = configuration.Read();
            var draft = startup.Pending!.Draft;
            configuration.Save(
                startup.Generation,
                draft with
                {
                    Settings = draft.Settings with
                    {
                        Capsuleers = draft.Settings.Capsuleers with { RoamPercent = roamDrawPercent },
                        MiningHolds = draft.Settings.MiningHolds with { GeneralMultiplier = 0.1m },
                        Resources = draft.Settings.Resources with { OreYieldMultiplier = 100m },
                    },
                },
                "Use controlled roam draws and short mining trips"
            );
        }
        using (CatalogDbContext edit = new(fixture.Database.CatalogPath, writable: true))
        {
            const string security = "{\"securityStatus\":-0.2}";
            edit.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000141',{security})");
            edit.Database.ExecuteSqlRaw(
                "UPDATE SourceRecord SET Data=json_set(Data,'$.security',-0.2) "
                    + "WHERE Dataset='client/solarsystemcontent' AND Key='30000141'"
            );
        }
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        Assert.True(fixture.Database.Settings.Capsuleers.EarnedPvpShips);
        Assert.False(fixture.Database.Settings.Capsuleers.FundRefits);

        long miner;
        long miningShip;
        long combatShip;
        long gun;
        long previousActivity = 0;
        CapsuleerJob handoff;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
            using (var setup = live.Economy.Begin(TransactionParticipants.None))
            {
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = false;
                var agents = setup.Set<SavedCapsuleerAgent>().ToArray();
                miner = agents.Single(agent => agent.Career == CapsuleerCareer.Mining).PilotId;
                setup.Filter<SavedCapsuleerCommunity>(row => row.PilotId == miner).Single().Progress = null;
                miningShip = setup.Pilots.Find(miner)!.ActiveShipId;
                foreach (var agent in agents.Where(agent => agent.PilotId != miner))
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
                }
                Charter(setup, catalog, miner, "Unattended Works", "UNWK", fixture.Clock.Now);
                setup.Set<PilotWallet>().Find(miner)!.Cents = 0;
                combatShip = setup
                    .Add(
                        new Ship
                        {
                            Id = setup.NextItemId(),
                            OwnerId = miner,
                            TypeId = frigate,
                            Name = "Earned patrol frigate",
                            StationId = Depot,
                        }
                    )
                    .Entity.Id;
                CapsuleerCommunityStore.GrantRequirements(setup, catalog, miner, frigate, []);
                PilotFleetStore.Board(setup, catalog, miner, combatShip, miningShip, fixture.Clock.Now);
                TrafficFitting.Apply(
                    setup,
                    catalog,
                    miner,
                    combatShip,
                    TrafficSnapshotTests.Profile() with
                    {
                        HullTypeId = frigate,
                        Equipment = [new(turret, (int)InventoryFlag.HighSlot0, 1), new(Rules.ChargeType, (int)InventoryFlag.HighSlot0, 50)],
                    },
                    fixture.Clock.Now
                );
                gun = Assert.Single(setup.FittedItems(miner, combatShip), item => item.TypeId == turret && item.Online).Id;
                PilotFleetStore.Board(setup, catalog, miner, miningShip, combatShip, fixture.Clock.Now);
                var organization = CapsuleerCorporations.Organization(setup, miner);
                CapsuleerCorporations.SaveOrganization(setup, miner, organization with { EarnedPvpShipId = combatShip });
                if (launch == EarnedMiningLaunch.RoamDeclined)
                {
                    previousActivity = Enumerable
                        .Range(1, 200)
                        .First(sequence =>
                            !PatrolRules.Choose(miner, sequence + 1, roamDrawPercent)
                            && PatrolRules.Choose(miner, sequence + 2, roamDrawPercent)
                        );
                    if (setup.Filter<SavedCapsuleerHauler>(row => row.PilotId == miner).SingleOrDefault() is null)
                    {
                        setup.Add(
                            new SavedCapsuleerHauler
                            {
                                Id = checked(setup.Set<SavedCapsuleerHauler>().Max(row => row.Id) + 1),
                                PilotId = miner,
                                SourceStationId = Depot,
                                DestinationStationId = Depot,
                                FounderShipId = miningShip,
                                RouteSystems = [Home],
                            }
                        );
                    }
                    var work = new HaulerProgress(HaulerActivity.Docked, 0, 0, combatShip, Depot, 0, new NavigationState().Capture())
                    {
                        ActivitySequence = previousActivity,
                    };
                    new HaulerCheckpoint(miner, work).Apply(setup);
                }
                setup.Commit();
            }
            StartUnattended(host);
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            for (int turn = 0; turn < 60 && scheduler.Hauler(miner) is null; turn++)
            {
                fixture.Clock.Seconds++;
                scheduler.Run(turn, maximumActions: 1);
            }
            Assert.True(scheduler.Read().Failures == 0, scheduler.Read().LastFailure);
            var staged = Assert.IsType<HaulerObservation>(scheduler.Hauler(miner));
            Assert.Equal(combatShip, staged.Pilot.ShipId);
            Assert.Null(staged.Pilot.Position);
            Assert.Equal(previousActivity, staged.Work.ActivitySequence);
            Assert.Null(scheduler.Miner(miner));
            handoff = Assert.Single(scheduler.Jobs(), job => job.PilotId == miner);
            Assert.Equal((CapsuleerAction.Trade, staged.Work.DueTicks), (handoff.Action, handoff.DueTicks));
            await live.FlushAsync();
        }

        long retryAt;
        using (var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var restored = reopened.GetRequiredService<LiveWorld>();
            var resumed = reopened.GetRequiredService<CapsuleerScheduler>();
            var worker = reopened.GetRequiredService<CapsuleerMaintenance>();
            var saved = restored.Economy.Read(world => world.Set<SavedCapsuleerJob>().Find(miner)!);
            Assert.Equal((handoff.Sequence, handoff.Action, handoff.DueTicks), (saved.Sequence, saved.Action, saved.DueTicks));
            if (launch == EarnedMiningLaunch.FitOffline)
            {
                using var invalidated = restored.Economy.Begin(TransactionParticipants.None);
                invalidated.Set<OwnedItem>().Find(gun)!.Online = false;
                invalidated.Commit();
            }
            StartUnattended(reopened);

            if (launch == EarnedMiningLaunch.Patrol)
            {
                for (int turn = 0; turn < 120 && resumed.Hauler(miner)?.Work.Ratting?.Patrol is null; turn++)
                {
                    fixture.Clock.Seconds++;
                    worker.Tick();
                }
                var launched = Assert.IsType<HaulerObservation>(resumed.Hauler(miner));
                Assert.Equal(combatShip, launched.Pilot.ShipId);
                Assert.NotNull(launched.Pilot.Position);
                Assert.Equal(PatrolKind.Roam, launched.Work.Ratting?.Patrol?.Kind);
                Assert.True(resumed.Read().Failures == 0, resumed.Read().LastFailure);
                Assert.Single(resumed.Jobs(), job => job.PilotId == miner);
                AssertWorkShipAndWallet(restored, CapsuleerCareer.Hauling, miningShip);
                return;
            }

            for (int turn = 0; turn < 120 && resumed.Miner(miner) is null; turn++)
            {
                fixture.Clock.Seconds++;
                worker.Tick();
                Assert.Null(resumed.Hauler(miner)?.Work.Ratting);
            }
            var returned = Assert.IsType<MinerObservation>(resumed.Miner(miner));
            Assert.Equal(miningShip, returned.Pilot.ShipId);
            retryAt = returned.Work.HuntRetryAt;
            Assert.Equal(returned.Work.ActiveTicks + CapsuleerWorkRules.EarnedHuntRetry.Ticks, retryAt);
            if (launch == EarnedMiningLaunch.FitOffline)
            {
                using var repaired = restored.Economy.Begin(TransactionParticipants.None);
                repaired.Set<OwnedItem>().Find(gun)!.Online = true;
                repaired.Commit();
            }
            Assert.True(resumed.Read().Failures == 0, resumed.Read().LastFailure);
            await restored.FlushAsync();
        }

        using var restarted = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var retryWorld = restarted.GetRequiredService<LiveWorld>();
        var retryScheduler = restarted.GetRequiredService<CapsuleerScheduler>();
        var retryMaintenance = restarted.GetRequiredService<CapsuleerMaintenance>();
        retryWorld.Economy.Read(world =>
        {
            var minerRow = world.Filter<SavedCapsuleerCommunity>(row => row.PilotId == miner).Single();
            Assert.Equal(retryAt, MinerCheckpoint.Read(minerRow.Progress)!.HuntRetryAt);
            var flightRow = world.Filter<SavedCapsuleerHauler>(row => row.PilotId == miner).Single();
            long outgoingActivity = launch == EarnedMiningLaunch.RoamDeclined ? previousActivity + 1 : previousActivity;
            Assert.Equal(outgoingActivity, HaulerCheckpoint.Read(flightRow.Progress)!.ActivitySequence);
            Assert.True(world.Set<OwnedItem>().Find(gun)!.Online);
            return 0;
        });
        StartUnattended(restarted);
        for (int turn = 0; turn < 240; turn++)
        {
            fixture.Clock.Seconds++;
            retryMaintenance.Tick();
            Assert.Null(retryScheduler.Hauler(miner));
            Assert.Equal(miningShip, Assert.IsType<MinerObservation>(retryScheduler.Miner(miner)).Pilot.ShipId);
            Assert.Single(retryScheduler.Jobs(), job => job.PilotId == miner);
            if (retryScheduler.Miner(miner)!.Work.Cycles > 0)
            {
                break;
            }
        }
        Assert.True(retryScheduler.Miner(miner)!.Work.Cycles > 0);
        Assert.True(retryScheduler.Read().Failures == 0, retryScheduler.Read().LastFailure);
        AssertWorkShipAndWallet(retryWorld, CapsuleerCareer.Mining, combatShip);

        if (launch == EarnedMiningLaunch.RoamDeclined)
        {
            for (int turn = 0; turn < 1800 && retryScheduler.Hauler(miner)?.Work.Ratting?.Patrol is null; turn++)
            {
                fixture.Clock.Seconds++;
                retryMaintenance.Tick();
                if (retryScheduler.Hauler(miner) is { } attempt && attempt.Work.Ratting is null)
                {
                    Assert.True(attempt.Work.ActiveTicks >= retryAt);
                    Assert.Equal(previousActivity + 1, attempt.Work.ActivitySequence);
                }
            }
            Assert.True(retryScheduler.Read().Failures == 0, retryScheduler.Read().LastFailure);
            var retried = Assert.IsType<HaulerObservation>(retryScheduler.Hauler(miner));
            Assert.Equal(combatShip, retried.Pilot.ShipId);
            Assert.NotNull(retried.Pilot.Position);
            Assert.Equal(PatrolKind.Roam, retried.Work.Ratting?.Patrol?.Kind);
            Assert.Equal(previousActivity + 2, retried.Work.ActivitySequence);
            Assert.Single(retryScheduler.Jobs(), job => job.PilotId == miner);
            AssertWorkShipAndWallet(retryWorld, CapsuleerCareer.Hauling, miningShip, allowOreSales: true);
        }

        void AssertWorkShipAndWallet(LiveWorld live, CapsuleerCareer activeWorker, long parkedShip, bool allowOreSales = false) =>
            live.Economy.Read(world =>
            {
                var organization = CapsuleerCorporations.Organization(world, miner);
                Assert.Equal(combatShip, organization.EarnedPvpShipId);
                Assert.Equal(miningShip, organization.PvpWorkShipId);
                Assert.Equal(activeWorker, organization.ActiveWorker);
                Assert.Equal(Depot, world.Ships.Find(parkedShip)!.StationId);
                var executions = world.Filter<MarketExecution>(entry => entry.PilotId == miner).ToArray();
                if (allowOreSales)
                {
                    Assert.All(executions, execution => Assert.False(execution.IsBuy));
                }
                else
                {
                    Assert.Empty(executions);
                }
                long proceeds = executions.Sum(execution => execution.PriceCents * execution.Quantity - execution.TaxCents);
                Assert.Equal(proceeds, world.Set<PilotWallet>().Find(miner)!.Cents);
                return 0;
            });
    }

    private static void StartUnattended(IServiceProvider host)
    {
        host.GetRequiredService<CapsuleerActivitySource>().StartUnattended();
        var activity = host.GetRequiredService<IHumanPresence>().Read();
        Assert.True(activity.Active);
        Assert.Empty(activity.ActiveSystems);
    }
}
