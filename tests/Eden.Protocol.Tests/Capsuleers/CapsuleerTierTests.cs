using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerTierTests
{
    private const int Home = 30000141;
    private const int Elsewhere = 30000144;

    [Fact]
    public void RepeatedEmptyDemotionDoesNotReopenSql()
    {
        using Combat.NpcCombatTests.Fixture fixture = new(liveWorld: true, useTestClock: true);
        var live = LiveWorld.Find(fixture.Db.Path)!;
        Assert.True(live.ReleaseSystemDetails(Home));
        using var metrics = SessionPerformance.Begin("repeated empty demotion", _ => { }, force: true)!;
        for (int turn = 0; turn < 20; turn++)
        {
            Assert.True(live.ReleaseSystemDetails(Home));
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void DemotionRetainsMotionAndFailedProjectionKeepsItForRetry()
    {
        using Combat.NpcCombatTests.Fixture fixture = new(liveWorld: true, useTestClock: true);
        var live = LiveWorld.Find(fixture.Db.Path)!;
        SpacePosition moved = fixture.Npc.Position + new SpacePosition(3000, 0, 0);
        var runtime = live.CombatForSystem(Elsewhere)!;
        runtime.PublishPilotPosition(Combat.NpcCombatTests.Fixture.ShipId, Elsewhere, moved);
        using SqliteConnection fault = new(live.ConnectionString);
        fault.Open();
        using (var command = fault.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER RejectDemotion BEFORE UPDATE ON WorldPosition BEGIN SELECT RAISE(ABORT,'test'); END";
            command.ExecuteNonQuery();
        }
        try
        {
            Assert.True(live.ReleaseSystemDetails(Elsewhere));
            Assert.Equal(moved, runtime.InformationPosition(Combat.NpcCombatTests.Fixture.ShipId));
            Assert.Equal(0, runtime.DetailedEntries);
            Assert.Equal(1, runtime.CompactPositions);
            using WorldDbContext rejected = new(fixture.Db.Path);
            Assert.Throws<SqliteException>(() => rejected.Positions.Single(p => p.ShipId == Combat.NpcCombatTests.Fixture.ShipId));
            Assert.Equal(moved, runtime.InformationPosition(Combat.NpcCombatTests.Fixture.ShipId));
        }
        finally
        {
            using var command = fault.CreateCommand();
            command.CommandText = "DROP TRIGGER RejectDemotion";
            command.ExecuteNonQuery();
        }
        Assert.True(live.ReleaseSystemDetails(Elsewhere));
        Assert.Equal(0, runtime.DetailedEntries);
        using WorldDbContext saved = new(fixture.Db.Path);
        var position = saved.Positions.Single(p => p.ShipId == Combat.NpcCombatTests.Fixture.ShipId);
        Assert.Equal(moved, new(position.X, position.Y, position.Z));
    }

    [Fact]
    public void DemotionRetainsCompactMotionAndLeavesAnotherSystemsContactsWarm()
    {
        using Combat.NpcCombatTests.Fixture fixture = new(liveWorld: true, useTestClock: true);
        var live = LiveWorld.Find(fixture.Db.Path)!;
        var contacts = fixture.Db.Services.GetRequiredService<INpcEncounters>();
        Assert.Empty(contacts.List(Home, fixture.Clock.Now));
        SpacePosition moved = fixture.Npc.Position + new SpacePosition(2500, 0, 0);
        live.CombatForSystem(Elsewhere)!.PublishPilotPosition(Combat.NpcCombatTests.Fixture.ShipId, Elsewhere, moved);
        Assert.True(live.ReleaseSystemDetails(Elsewhere));
        Assert.Equal(0, live.CombatForSystem(Elsewhere)!.DetailedEntries);
        Assert.Equal(1, live.CombatForSystem(Elsewhere)!.CompactPositions);
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            var position = world.Positions.Single(p => p.ShipId == Combat.NpcCombatTests.Fixture.ShipId);
            Assert.Equal(moved, new(position.X, position.Y, position.Z));
        }
        using var metrics = SessionPerformance.Begin("unrelated warm system after demotion", _ => { }, force: true)!;
        Assert.Empty(contacts.List(Home, fixture.Clock.Now));
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void CombatPaymentsPinDetailedStateUntilTheInteractionCanFinish()
    {
        using Ammunition.AmmunitionTests.Fixture fixture = new(liveWorld: true);
        fixture.Load(quantity: 400);
        fixture.Clock.Milliseconds += 5000;
        fixture.Space();
        var shot = fixture.Fire();
        var live = LiveWorld.Find(fixture.Db.Path)!;
        Assert.False(live.ReleaseSystemDetails(fixture.Npc.SystemId));
        using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        snapshot.Project();
        using WorldDbContext saved = new(snapshot.Database);
        Assert.Equal(shot.Health.Remaining.Shield, saved.Set<Eden.Data.World.Combat.Npcs.WorldNpc>().Find(fixture.Npc.Id)!.ShieldCharge);
    }

    [Fact]
    public void FinishedCombatReleasesItsDetailedWorkingSet()
    {
        using Combat.NpcCombatTests.Fixture fixture = new(liveWorld: true, useTestClock: true);
        fixture.Aggress();
        var live = LiveWorld.Find(fixture.Db.Path)!;
        Assert.False(live.ReleaseSystemDetails(fixture.Npc.SystemId));
        fixture.Combat.Disengage(Combat.NpcCombatTests.Fixture.ShipId);
        fixture.Clock.Seconds += (long)TimeSpan.FromMinutes(20).TotalSeconds;
        Assert.True(live.ReleaseSystemDetails(fixture.Npc.SystemId));
        Assert.Equal(0, live.CombatForSystem(fixture.Npc.SystemId)!.DetailedEntries);
    }

    [Fact]
    public async Task PaidMiningContinuesRemotelyAndReopensWithoutOfflineProduction()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        long pilot;
        int cycles;
        CapsuleerJob savedJob;
        MinerProgress savedWork;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
            MinerObservation? paid = null;
            for (int actions = 0; actions < 1000; actions++)
            {
                paid = CapsuleerTestDriver.Miner(host, fixture.Clock);
                if (paid.Work.Activity == MinerActivity.Mining)
                {
                    break;
                }
            }
            Assert.Equal(MinerActivity.Mining, paid!.Work.Activity);
            pilot = paid.Pilot.PilotId;
            cycles = paid.Work.Cycles;
            var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
            human.Transfer(Elsewhere);
            fixture.Clock.Seconds += 180;
            for (int turn = 0; turn < 20 && host.GetRequiredService<LiveWorld>().MinerCheckpoints[pilot].Work.Cycles == cycles; turn++)
            {
                scheduler.Run(checked((int)fixture.Clock.Seconds));
                fixture.Clock.Milliseconds += 50;
            }
            savedWork = host.GetRequiredService<LiveWorld>().MinerCheckpoints[pilot].Work;
            Assert.True(savedWork.Cycles > cycles);
            savedJob = scheduler.Jobs().Single(j => j.PilotId == pilot);
            for (int turn = 0; turn < 10; turn++)
            {
                scheduler.Run(checked((int)fixture.Clock.Seconds));
                fixture.Clock.Milliseconds += 50;
            }
            Assert.Equal(savedWork.Cycles, host.GetRequiredService<LiveWorld>().MinerCheckpoints[pilot].Work.Cycles);
            await host.GetRequiredService<LiveWorld>().FlushAsync();
        }
        fixture.Clock.Seconds += 86400;
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<ICapsuleerScheduler>();
        Assert.Contains(savedJob, restored.Jobs());
        using WorldDbContext world = new(fixture.Database.Path);
        Assert.Equal(savedWork, MinerCheckpoint.Read(world.Set<SavedCapsuleerCommunity>().Single(m => m.PilotId == pilot).Progress));
        Assert.Contains(world.Set<SavedCapsuleerSimulation>(), s => s.SystemId == Home);
        Assert.False(reopened.GetRequiredService<IHumanPresence>().Read().Active);
        Assert.Equal(0, restored.Run(0).Turn.Completed);
    }

    [Fact]
    public void TierRegistryDistinguishesUncreatedRemoteAndDetailedSystems()
    {
        SystemSimulationRegistry registry = new();
        Assert.Equal(SystemSimulationTier.Uncreated, registry.Get(Home).Tier);
        registry.Register(Home);
        Assert.Equal(SystemSimulationTier.RemoteScheduled, registry.Get(Home).Tier);
        registry.Begin([Home]);
        registry.Settle(Home, 1);
        Assert.Equal(SystemSimulationTier.Detailed, registry.Get(Home).Tier);
        registry.Begin([]);
        registry.Settle(Home, 2);
        registry.Pin(Home, true);
        Assert.Equal(SystemSimulationTier.Detailed, registry.Get(Home).Tier);
        registry.Pin(Home, false);
        Assert.Equal(SystemSimulationTier.RemoteScheduled, registry.Get(Home).Tier);
        registry.SetCoarse(Home, true);
        Assert.Equal(SystemSimulationTier.Coarse, registry.Get(Home).Tier);
    }
}
