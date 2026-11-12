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
}
