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
    private const int Jita = 30000142;
    private const int JitaStation = 60003760;
    private const long Human = 90000001;
    private const long AttackerPilot = 90150001;
    private const long DefenderPilot = 90150002;
    private const long AttackerShip = 1000000150001;
    private const long DefenderShip = 1000000150002;
    private const int Merlin = 603;
    private const int Railgun = 561;
    private const int Antimatter = 215;
    private const int MaximumSeconds = 1_200;

    [Theory]
    [InlineData(14)]
    [InlineData(25)]
    public async Task OpposingMilitiaPatrolsMeetAndFightThroughTheLiveFrontlineComplexOffscreen(int seed)
    {
        var directory = TestDirectory.Create("militia-patrol-replay-");
        try
        {
            WorldDatabase database = new(Path.Combine(directory.FullName, "world.sqlite"));
            TestClock clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
            var scenario = PatrolScenario.FromSeed(seed);
            PrepareCatalog(database.CatalogPath, scenario);
            Configure(database, clock);
            long contestedSite;
            using (var host = ServerServices.Build(database, clock, liveWorld: true))
            {
                var live = host.GetRequiredService<LiveWorld>();
                StagePilots(live, database, clock, scenario);
                var enlistment = host.GetRequiredService<IMilitiaEnlistment>();
                Assert.Null(enlistment.Read(AttackerPilot).Effective);
                Assert.Null(enlistment.Read(DefenderPilot).Effective);
                var scheduler = host.GetRequiredService<CapsuleerScheduler>();
                var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
                var concord = host.GetRequiredService<ConcordMaintenance>();
                var complexes = host.GetRequiredService<IMilitiaComplexes>();
                var presence = host.GetRequiredService<IHumanPresence>();
                using var human = presence.Select(Human, Jita);
                scheduler.PopulationPrepared(scenario.AttackerSystem);
                scheduler.PopulationPrepared(scenario.Frontline);

                var occupation = host.GetRequiredService<IFactionWarzones>().System(scenario.Frontline);
                Assert.Equal(WarzoneAdjacency.Frontline, occupation?.Adjacency);
                Assert.Equal(scenario.DefenderFaction, occupation?.Occupier);
                var site = Assert.Single(complexes.Sites(scenario.Frontline, clock.Now));
                Assert.Equal(scenario.DungeonId, site.DungeonId);
                var capture = Assert.Single(site.Objects, body => body.MilitiaCapture is not null);
                var gate = Assert.Single(site.Objects, body => body.AccelerationGateDirection is not null);
                bool attackerApproachedGate = false;
                bool defenderApproachedGate = false;
                bool attackerEntered = false;
                bool defenderEntered = false;
                bool contested = false;
                long? firstShotAt = null;
                List<PilotDamage> shots = [];
                host.GetRequiredService<PilotDamageSignal>().Applied += damage =>
                {
                    if (
                        damage.Applied > 0
                        && (damage.PilotId, damage.AttackerId) is (AttackerPilot, DefenderPilot) or (DefenderPilot, AttackerPilot)
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
                    var attacker = scheduler.Hauler(AttackerPilot);
                    var defender = scheduler.Hauler(DefenderPilot);
                    attackerApproachedGate |= AtGate(attacker);
                    defenderApproachedGate |= AtGate(defender);
                    attackerEntered |= InCapture(attacker);
                    defenderEntered |= InCapture(defender);
                    if (InCapture(attacker) && InCapture(defender))
                    {
                        var bracket = complexes
                            .Sites(scenario.Frontline, clock.Now)
                            .Single(candidate => candidate.Id == site.Id)
                            .Objects.Single(body => body.Id == capture.Id)
                            .MilitiaCapture;
                        contested |= bracket is { Paused: true, Capturing: null };
                    }
                }

                string state =
                    $"At {clock.Seconds}s: Attacker {Describe(AttackerPilot)}, Defender {Describe(DefenderPilot)}. "
                    + $"Gate: {attackerApproachedGate}/{defenderApproachedGate}, capture: {attackerEntered}/{defenderEntered}, "
                    + $"contested: {contested}, damage events: {shots.Count}.";
                Assert.Equal(new[] { Jita }, presence.Read().ActiveSystems);
                Assert.Equal(scenario.AttackerFaction, enlistment.Read(AttackerPilot).Effective);
                Assert.Equal(scenario.DefenderFaction, enlistment.Read(DefenderPilot).Effective);
                Assert.True(attackerApproachedGate && defenderApproachedGate, state);
                Assert.True(attackerEntered && defenderEntered && contested, state);
                Assert.NotEmpty(shots);
                output.WriteLine($"First opposing militia railgun damage at {firstShotAt} simulated seconds.");
                Assert.All(shots, shot => Assert.Equal(Railgun, shot.WeaponType));
                var map = host.GetRequiredService<IMapStatistics>().History(MapHistoryStatistic.Jumps, 1);
                Assert.Contains(map.Rows, row => row.SystemId == scenario.BorderSystem && row.Counts.Jumps >= 1);
                Assert.Contains(map.Rows, row => row.SystemId == scenario.Frontline && row.Counts.Jumps >= 1);
                var crimewatch = host.GetRequiredService<IPilotCrimewatch>();
                Assert.Equal(CrimewatchFlag.None, crimewatch.Read(AttackerPilot, scenario.Frontline).Timers.Flag(clock.Now));
                Assert.Equal(CrimewatchFlag.None, crimewatch.Read(DefenderPilot, scenario.Frontline).Timers.Flag(clock.Now));
                contestedSite = site.Id;
                await live.FlushAsync();

                bool AtGate(HaulerObservation? pilot) =>
                    pilot?.Pilot is { Position: { } at }
                    && pilot.Pilot.SystemId == scenario.Frontline
                    && at.DistanceTo(gate.Position) <= MilitiaComplexRules.GateRange + gate.Radius;

                bool InCapture(HaulerObservation? pilot) =>
                    pilot?.Work.MilitiaPatrol?.Site.Id == site.Id
                    && pilot.Pilot is { Position: { } at }
                    && pilot.Pilot.SystemId == scenario.Frontline
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
            var retained = saved.Economy.Inspect(world => world.Set<SavedCapsuleerHauler>().Find(scenario.AttackerSystem)!);
            Assert.Equal(contestedSite, HaulerCheckpoint.Read(retained.Progress)!.MilitiaPatrol?.Site.Id);
            Assert.Equal(scenario.AttackerFaction, reopened.GetRequiredService<IMilitiaEnlistment>().Read(AttackerPilot).Effective);
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

    private static void StagePilots(
        LiveWorld live,
        WorldDatabase database,
        TestClock clock,
        PatrolScenario scenario,
        bool includeAttacker = true,
        bool includeDefender = true,
        int? worldSeed = null,
        bool fromTradeHub = false
    )
    {
        int origin = fromTradeHub ? Jita : scenario.AttackerSystem;
        using var world = live.Economy.Begin(new(systems: [scenario.Frontline, origin]));
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
        var provisioning = world.Set<SavedCapsuleerProvisioning>().SingleOrDefault();
        provisioning ??= world.Add(new SavedCapsuleerProvisioning { Id = 1 }).Entity;
        provisioning.WorldSeed = worldSeed ?? scenario.Seed;
        int station = fromTradeHub ? JitaStation : scenario.AttackerStation;
        int[] route = fromTradeHub ? [Jita, .. scenario.Route] : scenario.Route;
        if (includeAttacker)
        {
            Stage(AttackerPilot, AttackerShip, origin, station, scenario.DefenderStation, route, "Attacker Patrol Test");
        }
        if (includeDefender)
        {
            Stage(
                DefenderPilot,
                DefenderShip,
                scenario.Frontline,
                scenario.DefenderStation,
                station,
                [.. route.Reverse()],
                "Defender Patrol Test"
            );
        }
        CapsuleerPopulationStore.ScheduleFounders(world);
        world.Commit();

        void Stage(long pilot, long ship, int system, int station, int destination, int[] systems, string name)
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
                    Seed = scenario.Seed,
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
                    RouteSystems = systems,
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
