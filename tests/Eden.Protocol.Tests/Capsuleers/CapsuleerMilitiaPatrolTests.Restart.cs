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
    [Fact]
    public async Task DisablingMilitiaPatrolsOnReopenLetsAnInFlightPilotReturnHome()
    {
        var directory = TestDirectory.Create("militia-patrol-return-");
        try
        {
            WorldDatabase database = new(Path.Combine(directory.FullName, "world.sqlite"));
            TestClock clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
            PrepareCatalog(database.CatalogPath);
            Configure(database, clock);
            using (var host = ServerServices.Build(database, clock, liveWorld: true))
            {
                var live = host.GetRequiredService<LiveWorld>();
                StagePilots(live, database, clock);
                using var human = host.GetRequiredService<IHumanPresence>().Select(Human, Jita);
                var scheduler = host.GetRequiredService<CapsuleerScheduler>();
                scheduler.PopulationPrepared(Nennamaila);
                scheduler.PopulationPrepared(Aldranette);
                var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
                for (int turn = 0; turn < MaximumSeconds && scheduler.Hauler(CaldariPilot)?.Work.Navigation.Warp is null; turn++)
                {
                    clock.Seconds++;
                    maintenance.Tick();
                }
                var departing = Assert.IsType<HaulerObservation>(scheduler.Hauler(CaldariPilot));
                Assert.Equal(Nennamaila, departing.Pilot.SystemId);
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
            reopenedScheduler.PopulationPrepared(Nennamaila);
            reopenedScheduler.PopulationPrepared(Aldranette);
            var resumed = Assert.IsType<HaulerObservation>(reopenedScheduler.Hauler(CaldariPilot));
            Assert.NotNull(resumed.Work.Navigation.Warp);
            Assert.NotNull(resumed.Work.MilitiaPatrol);
            var worker = reopened.GetRequiredService<CapsuleerMaintenance>();
            bool returning = false;
            HaulerObservation? docked = null;
            for (int turn = 0; turn < MaximumSeconds; turn++)
            {
                clock.Seconds++;
                worker.Tick();
                var pilot = reopenedScheduler.Hauler(CaldariPilot);
                returning |= pilot?.Work.MilitiaPatrol?.Phase == MilitiaPatrolPhase.Returning;
                if (pilot is { Pilot: { StationId: NennamailaStation, Position: null }, Work.MilitiaPatrol: null })
                {
                    docked = pilot;
                    break;
                }
            }
            Assert.True(returning, "The saved patrol never transitioned into its return phase.");
            Assert.NotNull(docked);
            Assert.Equal(CaldariShip, docked.Pilot.ShipId);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
