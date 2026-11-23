using Eden.Gameplay.Capsuleers;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Configuration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerMilitiaPatrolTests
{
    [Theory]
    [InlineData(32)]
    [InlineData(41)]
    public async Task DisablingMilitiaPatrolsOnReopenLetsAnInFlightPilotReturnHome(int seed)
    {
        var directory = TestDirectory.Create("militia-patrol-return-");
        try
        {
            WorldDatabase database = new(Path.Combine(directory.FullName, "world.sqlite"));
            TestClock clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
            var scenario = PatrolScenario.FromSeed(seed);
            PrepareCatalog(database.CatalogPath, scenario);
            Configure(database, clock);
            using (var host = ServerServices.Build(database, clock, liveWorld: true))
            {
                var live = host.GetRequiredService<LiveWorld>();
                StagePilots(live, database, clock, scenario);
                using var human = host.GetRequiredService<IHumanPresence>().Select(Human, Jita);
                var scheduler = host.GetRequiredService<CapsuleerScheduler>();
                scheduler.PopulationPrepared(scenario.AttackerSystem);
                scheduler.PopulationPrepared(scenario.Frontline);
                var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
                for (int turn = 0; turn < MaximumSeconds && scheduler.Hauler(AttackerPilot)?.Work.Navigation.Warp is null; turn++)
                {
                    clock.Seconds++;
                    maintenance.Tick();
                }
                var departing = Assert.IsType<HaulerObservation>(scheduler.Hauler(AttackerPilot));
                Assert.Equal(scenario.AttackerSystem, departing.Pilot.SystemId);
                Assert.Equal(MilitiaPatrolPhase.Travelling, departing.Work.MilitiaPatrol?.Phase);
                Assert.NotNull(departing.Work.Navigation.Warp);
                await live.FlushAsync();
            }

            WorldConfigurationStore configuration = new(database, clock);
            var state = configuration.Read();
            var draft = state.Active.Draft;
            configuration.Save(
                state.Generation,
                draft with
                {
                    Settings = draft.Settings with { Capsuleers = draft.Settings.Capsuleers with { MilitiaPatrols = false } },
                },
                "disable militia patrol replay"
            );

            using var reopened = ServerServices.Build(database, clock, liveWorld: true);
            using var observer = reopened.GetRequiredService<IHumanPresence>().Select(Human, Jita);
            var reopenedScheduler = reopened.GetRequiredService<CapsuleerScheduler>();
            reopenedScheduler.PopulationPrepared(scenario.AttackerSystem);
            reopenedScheduler.PopulationPrepared(scenario.Frontline);
            var resumed = Assert.IsType<HaulerObservation>(reopenedScheduler.Hauler(AttackerPilot));
            Assert.NotNull(resumed.Work.Navigation.Warp);
            Assert.NotNull(resumed.Work.MilitiaPatrol);
            var worker = reopened.GetRequiredService<CapsuleerMaintenance>();
            bool returning = false;
            HaulerObservation? docked = null;
            for (int turn = 0; turn < MaximumSeconds; turn++)
            {
                clock.Seconds++;
                worker.Tick();
                var pilot = reopenedScheduler.Hauler(AttackerPilot);
                returning |= pilot?.Work.MilitiaPatrol?.Phase == MilitiaPatrolPhase.Returning;
                if (pilot is { Pilot.Position: null, Work.MilitiaPatrol: null } && pilot.Pilot.StationId == scenario.AttackerStation)
                {
                    docked = pilot;
                    break;
                }
            }
            Assert.True(returning, "The saved patrol never transitioned into its return phase.");
            Assert.NotNull(docked);
            Assert.Equal(AttackerShip, docked.Pilot.ShipId);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
