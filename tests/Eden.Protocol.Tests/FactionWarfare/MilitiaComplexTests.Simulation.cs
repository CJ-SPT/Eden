using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.FactionWarfare;

public sealed partial class MilitiaComplexTests
{
    [Fact]
    public async Task ServerTickKeepsSavedProgressWithoutCreditingTheDisconnectedInterval()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        WorldDatabase database = new(fixture.Path);
        LoadComplexCatalog(database);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var maintenance = fixture.Services.GetRequiredService<ConcordMaintenance>();
        var complexes = fixture.Services.GetRequiredService<IMilitiaComplexes>();
        var site = complexes.Sites(SystemId, clock.Now).Single(s => s.DungeonId == OpenDungeon);
        var pilot = fixture.Store.FindPilot(FirstAccount)!;
        fixture.Store.SetPosition(pilot.ShipId, site.Objects.Single(obj => obj.MilitiaCapture is not null).Position, SystemId);
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(new SavedMilitiaEnlistment { PilotId = pilot.PilotId, DirectFaction = MilitiaFaction.Minmatar });
            action.Commit();
        }

        var space = fixture.Services.GetRequiredService<SharedSpace>();
        using (FlightSession flight = new(fixture.Store, fixture.Store.FindPilot(FirstAccount)!, clock, space, []))
        {
            flight.Snapshot();
            maintenance.Tick();
            clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            maintenance.Tick();
            Assert.Equal(-TimeSpan.TicksPerSecond, Progress(live));
        }

        await live.FlushAsync();
        fixture.Services.Dispose();
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        live = reopened.GetRequiredService<LiveWorld>();
        maintenance = reopened.GetRequiredService<ConcordMaintenance>();
        maintenance.Tick();
        Assert.Equal(-TimeSpan.TicksPerSecond, Progress(live));

        var store = reopened.GetRequiredService<Eden.World.Docking.SqliteWorld>();
        using FlightSession reconnect = new(store, store.FindPilot(FirstAccount)!, clock, reopened.GetRequiredService<SharedSpace>(), []);
        reconnect.Snapshot();
        maintenance.Tick();
        Assert.Equal(-TimeSpan.TicksPerSecond, Progress(live));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        maintenance.Tick();
        Assert.Equal(-2 * TimeSpan.TicksPerSecond, Progress(live));
    }

    [Fact]
    public void ServerTickAdvancesAutonomousCaptureAndPausesForAligningOrOpposingPilots()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        LoadComplexCatalog(new(fixture.Path));
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var complexes = fixture.Services.GetRequiredService<IMilitiaComplexes>();
        var world = fixture.Services.GetRequiredService<IFactionWarzones>();
        var maintenance = fixture.Services.GetRequiredService<ConcordMaintenance>();
        var bot = fixture.Store.FindPilot(FirstAccount)!;
        var visitor = fixture.Store.FindPilot(SecondAccount)!;
        var open = complexes.Sites(SystemId, clock.Now).Single(s => s.DungeonId == OpenDungeon);
        var position = open.Objects.Single(obj => obj.MilitiaCapture is not null).Position;
        fixture.Store.SetPosition(bot.ShipId, position, SystemId);
        fixture.Store.SetPosition(visitor.ShipId, position, SystemId);
        var policy = MilitiaComplexRules.Policy(MilitiaComplexFamily.Open, 5);
        long progress = -policy.Duration + 3 * TimeSpan.TicksPerSecond;
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(new SavedMilitiaEnlistment { PilotId = bot.PilotId, DirectFaction = MilitiaFaction.Minmatar });
            action.Add(new SavedMilitiaEnlistment { PilotId = visitor.PilotId, DirectFaction = MilitiaFaction.Amarr });
            action.Add(
                new SavedWarzoneOccupation
                {
                    SystemId = SystemId,
                    Occupier = MilitiaFaction.Minmatar,
                    VictoryPoints = 1_000,
                }
            );
            action.Add(
                new SavedMilitiaComplex
                {
                    Slot = OpenSlot,
                    Occupier = MilitiaFaction.Minmatar,
                    Progress = progress,
                }
            );
            action.Commit();
        }

        var clear = new NavigationState().Capture();
        using (live.EnterPersistence("autonomous capture fixture", TransactionParticipants.None))
        {
            live.TrackCapsuleer(bot.PilotId, null, SystemId);
            CapsuleerJob job = new(bot.PilotId, 1, 1, 0, CapsuleerAction.Lock);
            live.CapsuleerJobs.Add(bot.PilotId, job);
            live.ProjectedCapsuleerJobs.Add(bot.PilotId, job);
        }
        SetNavigation(clear);

        Tick();
        Tick();
        Assert.Equal(progress, Progress(live));

        Guid activity = Guid.NewGuid();
        SetActivity(true);
        Tick();
        Assert.Equal(progress, Progress(live));
        Tick();
        progress -= TimeSpan.TicksPerSecond;
        Assert.Equal(progress, Progress(live));

        SetActivity(false);
        clock.AdvanceWallTime(TimeSpan.FromMilliseconds(250));
        SetActivity(true);
        Tick();
        Assert.Equal(progress, Progress(live));

        SetNavigation(clear with { WarpDestination = position + new SpacePosition(0, 0, 150_000) });
        Tick();
        Assert.Equal(progress, Progress(live));
        SetNavigation(clear);
        Tick();
        Assert.Equal(progress, Progress(live));

        var space = fixture.Services.GetRequiredService<SharedSpace>();
        using (FlightSession flight = new(fixture.Store, fixture.Store.FindPilot(SecondAccount)!, clock, space, []))
        {
            flight.Snapshot();
            Tick();
            Assert.Equal(progress, Progress(live));
        }
        Tick();
        Assert.Equal(progress, Progress(live));
        Tick();
        Assert.Equal(progress - TimeSpan.TicksPerSecond, Progress(live));
        Tick();
        int after = world.System(SystemId)!.VictoryPoints;
        Assert.InRange(after, 1, 999);
        Assert.DoesNotContain(complexes.Sites(SystemId, clock.Now), site => site.Id == open.Id);
        maintenance.Tick();
        Tick();
        Assert.Equal(after, world.System(SystemId)!.VictoryPoints);

        void SetActivity(bool online)
        {
            using var owner = live.EnterPersistence("autonomous capture activity", TransactionParticipants.None);
            live.CapsuleerActivity.Update(activity, online ? visitor.HomeSystemId : null, visitor.PilotId);
        }

        void Tick()
        {
            clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            maintenance.Tick();
        }

        void SetNavigation(NavigationFrame navigation)
        {
            using var owner = live.EnterPersistence("autonomous capture navigation", TransactionParticipants.None);
            var checkpoint = new Eden.World.Capsuleers.HaulerCheckpoint(
                bot.PilotId,
                new(HaulerActivity.Flying, 1, 1, bot.ShipId, 0, 0, navigation)
            );
            live.HaulerCheckpoints[bot.PilotId] = checkpoint;
            live.ProjectedHaulers[bot.PilotId] = checkpoint;
        }
    }
}
