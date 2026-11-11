using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class NpcSceneReadTests
{
    [Fact]
    public void SpatialReadsPrepareOnlyLocalNativeContactsAndReuseThemAcrossObservers()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true, useTestClock: true);
        AddDispersedNpcs(f);
        f.Db.Store.Advance(f.Npc.SystemId, 1, f.Clock.Now, [], []);
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        var first = encounters.ReadScene(f.Npc.SystemId, f.Npc.Position, f.Clock.Now);
        var runtime = LiveWorld.Find(f.Db.Path)!.CombatForSystem(f.Npc.SystemId)!;
        Assert.InRange(first.Visible.Count, 1, 34);
        Assert.Equal(first.Visible.Count, runtime.ObserverContactCount(f.Npc.SystemId));
        using var metrics = SessionPerformance.Begin("local native contact preparation", _ => { }, force: true)!;
        for (int repeat = 0; repeat < 10; repeat++)
        {
            var next = encounters.ReadScene(f.Npc.SystemId, f.Npc.Position + new SpacePosition(1, 0, 0), f.Clock.Now);
            Assert.Equal(first.Visible.Count, next.Visible.Count);
            for (int index = 0; index < first.Visible.Count; index++)
            {
                Assert.Same(first.Visible[index], next.Visible[index]);
            }
            Assert.Same(first.Warping, next.Warping);
        }
        Assert.Equal(first.Visible.Count, runtime.ObserverContactCount(f.Npc.SystemId));
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceneQueriesMatchFullContactsAcrossObserversWithoutWarmSql(bool simulationObjects)
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true, simulationObjects: simulationObjects, useTestClock: true);
        AddDispersedNpcs(f);
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        var positions = new[] { f.Npc.Position, f.Npc.Position + new SpacePosition(2_000_000, 0, 0) };
        foreach (var position in positions)
        {
            AssertScene(encounters, f, position);
        }
        using var metrics = SessionPerformance.Begin("spatial scene observers", _ => { }, force: true)!;
        for (int repeat = 0; repeat < 8; repeat++)
        {
            foreach (var position in positions)
            {
                AssertScene(encounters, f, position);
            }
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void GeometryIsSharedWhileSameTickMembershipAndRollbackRemainCurrent()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true, useTestClock: true);
        AddDispersedNpcs(f);
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        var first = encounters.ReadScene(f.Npc.SystemId, f.Npc.Position, f.Clock.Now);
        var other = encounters.ReadScene(f.Npc.SystemId, f.Npc.Position + new SpacePosition(2_000_000, 0, 0), f.Clock.Now);
        Assert.Same(first.Warping, other.Warping);
        var original = first.Visible.Single(n => n.Id == f.Npc.Id);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var row = world.Set<WorldNpc>().Find(f.Npc.Id)!;
            row.X += 2_000_000;
            row.HomeX = row.X;
            world.SaveChanges();
        }
        Assert.DoesNotContain(encounters.ReadScene(f.Npc.SystemId, f.Npc.Position, f.Clock.Now).Visible, n => n.Id == f.Npc.Id);
        Assert.Contains(encounters.ReadScene(f.Npc.SystemId, other.Visible[0].Position, f.Clock.Now).Visible, n => n.Id == f.Npc.Id);
        using (WorldDbContext world = new(f.Db.Path))
        {
            using var transaction = world.Database.BeginTransaction();
            world.Set<WorldNpc>().Find(f.Npc.Id)!.DestroyedAt = f.Clock.Now;
            world.SaveChanges();
            transaction.Rollback();
        }
        AssertScene(encounters, f, other.Visible[0].Position);
        Assert.Equal(f.Npc.Position, original.Position);
        AssertScene(encounters, f, f.Npc.Position);
    }

    [Fact]
    public void SceneHealthAndWeaponsRetainSubsecondDeadlinesAndCommittedDamage()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true, useTestClock: true);
        long started = f.Clock.Now;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var npc = world.Set<WorldNpc>().Find(f.Npc.Id)!;
            npc.ShieldCharge /= 2;
            world.Add(
                new NpcAggression
                {
                    NpcId = npc.Id,
                    TargetShipId = f.Pilot.ShipId,
                    LastShotAt = started,
                }
            );
            world.SaveChanges();
        }
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        var before = encounters.ReadScene(f.Npc.SystemId, f.Npc.Position, started).Visible.Single(n => n.Id == f.Npc.Id);
        var weapon = Assert.IsType<NpcWeaponContact>(before.Weapon);
        f.Clock.Milliseconds = 250;
        var after = encounters.ReadScene(f.Npc.SystemId, f.Npc.Position, f.Clock.Now).Visible.Single(n => n.Id == f.Npc.Id);
        Assert.True(after.Health.Remaining.Shield > before.Health.Remaining.Shield);
        long expires = started + (long)Math.Ceiling(weapon.Duration * TimeSpan.TicksPerMillisecond);
        Assert.NotNull(encounters.ReadScene(f.Npc.SystemId, f.Npc.Position, expires - 1).Visible.Single(n => n.Id == f.Npc.Id).Weapon);
        Assert.Null(encounters.ReadScene(f.Npc.SystemId, f.Npc.Position, expires).Visible.Single(n => n.Id == f.Npc.Id).Weapon);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldNpc>().Find(f.Npc.Id)!.ArmorDamage = 1;
            world.SaveChanges();
        }
        AssertScene(encounters, f, f.Npc.Position);
    }

    private static void AddDispersedNpcs(NpcCombatTests.Fixture f)
    {
        using WorldDbContext world = new(f.Db.Path);
        for (int index = 1; index <= 64; index++)
        {
            double x = f.Npc.Position.X + (index % 2 == 0 ? 2_000_000 : 0);
            double y = f.Npc.Position.Y + index * 100;
            world.Add(
                new WorldNpc
                {
                    SystemId = f.Npc.SystemId,
                    TypeId = f.Npc.TypeId,
                    X = x,
                    HomeX = x,
                    Y = y,
                    HomeY = y,
                    Z = f.Npc.Position.Z,
                    HomeZ = f.Npc.Position.Z,
                    ShieldCharge = f.Npc.Health.Defense.Shield,
                    UpdatedAt = f.Clock.Now,
                }
            );
        }
        world.SaveChanges();
    }

    private static void AssertScene(INpcEncounters encounters, NpcCombatTests.Fixture f, SpacePosition position)
    {
        var expected = NpcSceneContacts.From(encounters.List(f.Npc.SystemId, f.Clock.Now), f.Npc.SystemId, position);
        var actual = encounters.ReadScene(f.Npc.SystemId, position, f.Clock.Now);
        Assert.Equal(expected.Visible, actual.Visible);
        Assert.True(expected.Warping.SetEquals(actual.Warping));
    }
}
