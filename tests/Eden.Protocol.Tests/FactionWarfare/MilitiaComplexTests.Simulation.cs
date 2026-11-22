using Eden.Data.World.Capsuleers;
using Eden.Data.World.FactionWarfare;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.FactionWarfare;

public sealed partial class MilitiaComplexTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedCaptureSplitsVictoryPointsAndPaysOnlyHumanRecipients(bool battlefield)
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        WorldDatabase database = new(fixture.Path);
        LoadComplexCatalog(database);
        if (battlefield)
        {
            LoadBattlefield(database);
        }
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var complexes = fixture.Services.GetRequiredService<IMilitiaComplexes>();
        var warzones = fixture.Services.GetRequiredService<IFactionWarzones>();
        var human = fixture.Store.FindPilot(FirstAccount)!;
        var bot = fixture.Store.FindPilot(SecondAccount)!;
        var site = complexes.Sites(SystemId, clock.Now).Single(s => s.DungeonId == (battlefield ? BattlefieldDungeon : ScoutDungeon));
        var position = site
            .Objects.First(obj => battlefield ? obj.BattlefieldCapture is not null : obj.MilitiaCapture is not null)
            .Position;
        fixture.Store.SetPosition(human.ShipId, position, SystemId);
        fixture.Store.SetPosition(bot.ShipId, position, SystemId);
        var family = battlefield ? MilitiaComplexFamily.Battlefield : MilitiaComplexFamily.Scout;
        var policy = MilitiaComplexRules.Policy(family, capacity: battlefield ? 30 : 1);
        long slot = MilitiaComplexRules.Slot(SystemId, family);
        const int initialVictoryPoints = 30_000;
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            RegisterCapsuleer(action, bot.PilotId, bot.HomeSystemId, (int)bot.Home.Id);
            action.Add(new SavedMilitiaEnlistment { PilotId = human.PilotId, DirectFaction = MilitiaFaction.Minmatar });
            action.Add(new SavedMilitiaEnlistment { PilotId = bot.PilotId, DirectFaction = MilitiaFaction.Minmatar });
            action.Add(
                new SavedWarzoneOccupation
                {
                    SystemId = SystemId,
                    Occupier = MilitiaFaction.Minmatar,
                    VictoryPoints = initialVictoryPoints,
                }
            );
            action.Add(
                new SavedMilitiaComplex
                {
                    Slot = slot,
                    Occupier = MilitiaFaction.Minmatar,
                    Progress = battlefield ? -(MilitiaBattlefieldRules.WinningScore - 1) : -policy.Duration + TimeSpan.TicksPerSecond,
                }
            );
            action.Commit();
        }

        var before = warzones.System(SystemId)!;
        MilitiaComplexPilot[] pilots = [new(human.PilotId, human.ShipId, position), new(bot.PilotId, bot.ShipId, position)];
        complexes.Advance(SystemId, pilots, clock.Now);
        int seconds = battlefield ? (int)MilitiaBattlefieldRules.PointInterval.TotalSeconds : 1;
        for (int i = 0; i < seconds; i++)
        {
            clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            complexes.Advance(SystemId, pilots, clock.Now);
        }
        complexes.Advance(SystemId, pilots, clock.Now);

        int issuer = MilitiaRules.TribalLiberationForce;
        long expected = battlefield ? policy.BasePoints : MilitiaComplexRules.PointsPerPilot(before, false, policy, 1);
        var capture = live.Economy.Inspect(world =>
        {
            var award = Assert.Single(world.Set<SavedMilitiaLoyaltyAward>());
            Assert.Equal((human.PilotId, site.Id, expected), (award.PilotId, award.SourceId, award.Points));
            Assert.Null(world.Set<SavedLoyaltyBalance>().Find(bot.PilotId, issuer));
            return world.Set<SavedLoyaltyBalance>().Find(human.PilotId, issuer)!.Points;
        });
        Assert.Equal(expected, capture);
        int removed = initialVictoryPoints - warzones.System(SystemId)!.VictoryPoints;
        Assert.True(removed > 0);
        var stats = fixture.Services.GetRequiredService<IMilitiaProgression>();
        Assert.Equal(
            MilitiaProgressionRules.Share(removed, 0, 2),
            stats.Statistics(human.PilotId, MilitiaStatisticOwner.Pilot).Total.VictoryPoints
        );
        Assert.Equal(
            MilitiaProgressionRules.Share(removed, 1, 2),
            stats.Statistics(bot.PilotId, MilitiaStatisticOwner.Pilot).Total.VictoryPoints
        );
    }

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
            RegisterCapsuleer(action, bot.PilotId, bot.HomeSystemId, (int)bot.Home.Id);
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
        Assert.Empty(live.Economy.Inspect(action => action.Set<SavedMilitiaLoyaltyAward>().ToArray()));
        var stats = fixture.Services.GetRequiredService<IMilitiaProgression>();
        Assert.Equal(1_000 - after, stats.Statistics(bot.PilotId, MilitiaStatisticOwner.Pilot).Total.VictoryPoints);

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

    internal static void RegisterCapsuleer(WorldAction world, long pilotId, int homeSystem, int station, int slot = 20)
    {
        if (world.Set<SavedCapsuleerPopulation>().Find(homeSystem) is null)
        {
            world.Add(
                new SavedCapsuleerPopulation
                {
                    SystemId = homeSystem,
                    HomeStationId = station,
                    Version = 1,
                }
            );
        }
        world.Add(
            new SavedCapsuleerAgent
            {
                PilotId = pilotId,
                CommunitySystemId = homeSystem,
                Career = CapsuleerCareer.Hauling,
                Slot = slot,
                FounderShipId = world.Pilots.Find(pilotId)!.ActiveShipId,
            }
        );
    }
}
