using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Maps;
using Eden.Protocol.Combat;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerMilitiaPatrolTests(ITestOutputHelper output)
{
    private const int Aldranette = MilitiaPatrolRules.Aldranette;
    private const int Nennamaila = MilitiaPatrolRules.Nennamaila;
    private const int AldranetteStation = MilitiaPatrolRules.AldranetteStation;
    private const int NennamailaStation = MilitiaPatrolRules.NennamailaStation;
    private const int Jita = 30000142;
    private const long Human = 90000001;
    private const long CaldariPilot = 90150001;
    private const long GallentePilot = 90150002;
    private const long CaldariShip = 1000000150001;
    private const long GallenteShip = 1000000150002;
    private const int Merlin = 603;
    private const int Railgun = 561;
    private const int Antimatter = 215;
    private const int GallenteScout = 9601;
    private const int MaximumSeconds = 1_200;

    [Fact]
    public async Task OpposingMilitiaPatrolsMeetAndFightThroughTheSameComplexGateOffscreen()
    {
        var directory = TestDirectory.Create("militia-patrol-replay-");
        try
        {
            WorldDatabase database = new(Path.Combine(directory.FullName, "world.sqlite"));
            TestClock clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
            PrepareCatalog(database.CatalogPath);
            Configure(database, clock);
            long contestedSite;
            using (var host = ServerServices.Build(database, clock, liveWorld: true))
            {
                var live = host.GetRequiredService<LiveWorld>();
                StagePilots(live, database, clock);
                var enlistment = host.GetRequiredService<IMilitiaEnlistment>();
                Assert.Null(enlistment.Read(CaldariPilot).Effective);
                Assert.Null(enlistment.Read(GallentePilot).Effective);
                var scheduler = host.GetRequiredService<CapsuleerScheduler>();
                var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
                var concord = host.GetRequiredService<ConcordMaintenance>();
                var complexes = host.GetRequiredService<IMilitiaComplexes>();
                var presence = host.GetRequiredService<IHumanPresence>();
                using var human = presence.Select(Human, Jita);
                scheduler.PopulationPrepared(Nennamaila);
                scheduler.PopulationPrepared(Aldranette);

                var site = Assert.Single(complexes.Sites(Aldranette, clock.Now));
                Assert.Equal(GallenteScout, site.DungeonId);
                var capture = Assert.Single(site.Objects, body => body.MilitiaCapture is not null);
                var gate = Assert.Single(site.Objects, body => body.AccelerationGateDirection is not null);
                bool caldariApproachedGate = false;
                bool gallenteApproachedGate = false;
                bool caldariEntered = false;
                bool gallenteEntered = false;
                bool contested = false;
                long? firstShotAt = null;
                List<PilotDamage> shots = [];
                host.GetRequiredService<PilotDamageSignal>().Applied += damage =>
                {
                    if (
                        damage.Applied > 0
                        && (damage.PilotId, damage.AttackerId) is (CaldariPilot, GallentePilot) or (GallentePilot, CaldariPilot)
                    )
                    {
                        firstShotAt ??= clock.Seconds;
                        shots.Add(damage);
                    }
                };

                for (int turn = 0; turn < MaximumSeconds && !(contested && shots.Count > 0); turn++)
                {
                    clock.Seconds++;
                    maintenance.Tick();
                    concord.Tick();
                    var caldari = scheduler.Hauler(CaldariPilot);
                    var gallente = scheduler.Hauler(GallentePilot);
                    caldariApproachedGate |= AtGate(caldari);
                    gallenteApproachedGate |= AtGate(gallente);
                    caldariEntered |= InCapture(caldari);
                    gallenteEntered |= InCapture(gallente);
                    if (InCapture(caldari) && InCapture(gallente))
                    {
                        var bracket = complexes
                            .Sites(Aldranette, clock.Now)
                            .Single(candidate => candidate.Id == site.Id)
                            .Objects.Single(body => body.Id == capture.Id)
                            .MilitiaCapture;
                        contested |= bracket is { Paused: true, Capturing: null };
                    }
                }

                string state =
                    $"At {clock.Seconds}s: Caldari {Describe(CaldariPilot)}, Gallente {Describe(GallentePilot)}. "
                    + $"Gate: {caldariApproachedGate}/{gallenteApproachedGate}, capture: {caldariEntered}/{gallenteEntered}, "
                    + $"contested: {contested}, damage events: {shots.Count}.";
                Assert.Equal(new[] { Jita }, presence.Read().ActiveSystems);
                Assert.Equal(MilitiaFaction.Caldari, enlistment.Read(CaldariPilot).Effective);
                Assert.Equal(MilitiaFaction.Gallente, enlistment.Read(GallentePilot).Effective);
                Assert.True(caldariApproachedGate && gallenteApproachedGate, state);
                Assert.True(caldariEntered && gallenteEntered && contested, state);
                Assert.NotEmpty(shots);
                output.WriteLine($"First opposing militia railgun damage at {firstShotAt} simulated seconds.");
                Assert.All(shots, shot => Assert.Equal(Railgun, shot.WeaponType));
                var map = host.GetRequiredService<IMapStatistics>().History(MapHistoryStatistic.Jumps, 1);
                Assert.Contains(map.Rows, row => row.SystemId == Aldranette && row.Counts.Jumps >= 1);
                var crimewatch = host.GetRequiredService<IPilotCrimewatch>();
                Assert.Equal(CrimewatchFlag.None, crimewatch.Read(CaldariPilot, Aldranette).Timers.Flag(clock.Now));
                Assert.Equal(CrimewatchFlag.None, crimewatch.Read(GallentePilot, Aldranette).Timers.Flag(clock.Now));
                contestedSite = site.Id;
                await live.FlushAsync();

                bool AtGate(HaulerObservation? pilot) =>
                    pilot?.Pilot is { SystemId: Aldranette, Position: { } at }
                    && at.DistanceTo(gate.Position) <= MilitiaComplexRules.GateRange + gate.Radius;

                bool InCapture(HaulerObservation? pilot) =>
                    pilot?.Work.MilitiaPatrol?.Site.Id == site.Id
                    && pilot.Pilot is { SystemId: Aldranette, Position: { } at }
                    && at.DistanceTo(capture.Position) <= MilitiaComplexRules.CaptureRange;

                string Describe(long pilotId) =>
                    scheduler.Hauler(pilotId) is { } state
                        ? $"{state.Pilot.SystemId} {state.Work.Activity} {state.Work.MilitiaPatrol?.Phase} {state.Work.WaitingReason} "
                            + $"(gate distance {state.Pilot.Position?.DistanceTo(gate.Position):F0}, "
                            + $"capture distance {state.Pilot.Position?.DistanceTo(capture.Position):F0}, "
                            + $"warp pending {state.Work.Navigation.WarpDestination is not null}, "
                            + $"warp stamp {state.Work.Navigation.Warp?.StartStamp}, speed {state.Work.Navigation.Velocity.Length:F0})"
                        : "no job";
            }

            using var reopened = ServerServices.Build(database, clock, liveWorld: true);
            var saved = reopened.GetRequiredService<LiveWorld>();
            var retained = saved.Economy.Inspect(world => world.Filter<SavedCapsuleerHauler>(row => row.PilotId == CaldariPilot).Single());
            Assert.Equal(contestedSite, HaulerCheckpoint.Read(retained.Progress)!.MilitiaPatrol?.Site.Id);
            Assert.Equal(MilitiaFaction.Caldari, reopened.GetRequiredService<IMilitiaEnlistment>().Read(CaldariPilot).Effective);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void Configure(WorldDatabase database, TestClock clock)
    {
        WorldInitialization.Initialize(database, clock, economy: new(false, false));
        WorldConfigurationStore configurations = new(database, clock);
        var state = configurations.Read();
        var draft = state.Active.Draft;
        configurations.Save(
            state.Generation,
            draft with
            {
                Settings = draft.Settings with
                {
                    NpcPopulation = new(false, false, false, false),
                    Capsuleers = draft.Settings.Capsuleers with
                    {
                        MilitiaPatrols = true,
                        Miners = false,
                        GateVisitors = 0,
                        RoamingPvp = false,
                        RegionalConflicts = false,
                        ActivePilotCap = 2,
                    },
                },
            },
            "militia patrol replay"
        );
        WorldInitialization.Initialize(database, clock);
    }

    private static void StagePilots(LiveWorld live, WorldDatabase database, TestClock clock)
    {
        using var world = live.Economy.Begin(new(systems: [Aldranette, Nennamaila]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (world.Set<Account>().Find(CapsuleerCommunityStore.MinerAccountId) is null)
        {
            world.Add(
                new Account
                {
                    Id = CapsuleerCommunityStore.MinerAccountId,
                    UserName = "capsuleers",
                    CreatedAt = clock.Now,
                    CloneGrade = AccountCloneGrade.Omega,
                }
            );
        }
        Stage(CaldariPilot, CaldariShip, Nennamaila, NennamailaStation, AldranetteStation, "Nennamaila Patrol Test");
        Stage(GallentePilot, GallenteShip, Aldranette, AldranetteStation, NennamailaStation, "Aldranette Patrol Test");
        CapsuleerPopulationStore.ScheduleFounders(world);
        world.Commit();

        void Stage(long pilot, long ship, int system, int station, int destination, string name)
        {
            world.Add(new InventoryOwner { Id = pilot });
            world.Add(
                new Ship
                {
                    Id = ship,
                    TypeId = Merlin,
                    OwnerId = pilot,
                    StationId = station,
                    Name = name,
                }
            );
            world.Add(
                new Pilot
                {
                    Id = pilot,
                    AccountId = CapsuleerCommunityStore.MinerAccountId,
                    CorporationId = 1000044,
                    ActiveShipId = ship,
                    Name = name,
                    RaceId = 1,
                    BloodlineId = 1,
                    GenderId = 0,
                    SchoolId = 17,
                    CreatedAt = clock.Now,
                }
            );
            world.Add(new SavedHomeStation { PilotId = pilot, StationId = station });
            world.Add(new PilotWallet { PilotId = pilot, Cents = 0 });
            world.Add(
                new SavedCapsuleerPopulation
                {
                    SystemId = system,
                    HomeStationId = station,
                    Seed = 1,
                    Version = CapsuleerPopulationStore.PopulationVersion,
                }
            );
            world.Add(
                new SavedCapsuleerHauler
                {
                    Id = system,
                    PilotId = pilot,
                    FounderShipId = ship,
                    SourceStationId = station,
                    DestinationStationId = destination,
                    RouteSystems = system == Aldranette ? [Aldranette, Nennamaila] : [Nennamaila, Aldranette],
                }
            );
            CapsuleerPopulationStore.AddAgent(world, new(pilot, system, CapsuleerCareer.Hauling, ship, 0, 0));
            world.SaveChanges();
            TrafficFitting.Apply(
                world,
                catalog,
                pilot,
                ship,
                TrafficSnapshotTests.Profile() with
                {
                    HullTypeId = Merlin,
                    Equipment = [new(Railgun, (int)InventoryFlag.HighSlot0, 1), new(Antimatter, (int)InventoryFlag.HighSlot0, 50)],
                },
                clock.Now
            );
        }
    }
}
