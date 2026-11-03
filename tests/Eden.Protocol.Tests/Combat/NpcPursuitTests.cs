using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class NpcPursuitTests
{
    private static NpcContact Contact(NpcCombatTests.Fixture f) =>
        f.Db.Services.GetRequiredService<INpcEncounters>().List(f.Npc.SystemId, f.Clock.Now).Single(n => n.Id == f.Npc.Id);

    private static void StartDistantFight(NpcCombatTests.Fixture f)
    {
        f.Db.Store.SetPosition(NpcCombatTests.Fixture.ShipId, f.Npc.Position + new SpacePosition(8000, 0, 0), f.Npc.SystemId);
        f.Aggress();
    }

    [Fact]
    public void RetaliationWaitsThenClosesFromOutsideWeaponRangeAndFiresAtCatalogStandOff()
    {
        using NpcCombatTests.Fixture f = new();
        Assert.Null(Contact(f).Navigation);
        Assert.Empty(f.Combat.Aggressors(NpcCombatTests.Fixture.ShipId));
        StartDistantFight(f);
        f.Clock.Milliseconds = 7499;
        Assert.Null(f.Fire());
        Assert.Equal(f.Npc.Position, Contact(f).Position);
        Assert.Null(Contact(f).Navigation);
        f.Clock.Milliseconds = 7500;
        Assert.Null(f.Fire());
        var course = Assert.IsType<NpcNavigationContact>(Contact(f).Navigation);
        Assert.Equal(NpcCombatTests.Fixture.ShipId, course.TargetId);
        Assert.Equal(0.5, course.SpeedFraction);
        Assert.Equal(1250, course.FollowRange);
        List<NpcShot> shots = [];
        for (int second = 1; second <= 60; second++)
        {
            f.Clock.Milliseconds += 1000;
            if (f.Fire() is { } shot)
            {
                shots.Add(shot);
            }
        }
        var npc = Contact(f);
        double surfaceRange = npc.Position.DistanceTo(f.Pilot.Position!) - npc.Radius - f.Pilot.Flight.ShipRadius;
        Assert.InRange(surfaceRange, 1100, 1600);
        Assert.Equal(NpcNavigationState.Orbiting, npc.Navigation!.State);
        Assert.InRange(npc.Navigation.Velocity.Length, 180, 220);
        Assert.NotEmpty(shots);
        Assert.InRange(npc.Navigation!.Velocity.DistanceTo(SpacePosition.Zero), 0, 220);
        Assert.True(f.Health.Read(NpcCombatTests.Fixture.PilotId, f.Clock.Now).Remaining.Shield < 200);
        Assert.Equal(new[] { f.Npc.Id }, f.Combat.Aggressors(NpcCombatTests.Fixture.ShipId));
    }

    [Fact]
    public async Task CompetingMovementTicksDoNotMoveTwiceAndClockRollbackDoesNotRewind()
    {
        using NpcCombatTests.Fixture f = new();
        StartDistantFight(f);
        f.Clock.Milliseconds = 7500;
        Assert.Null(f.Fire());
        f.Clock.Milliseconds = 8500;
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => f.Fire())));
        var first = Contact(f);
        Assert.InRange(first.Position.DistanceTo(f.Npc.Position), 50, 70);
        Assert.Null(f.Fire());
        Assert.Equal(first, Contact(f));
        f.Clock.Milliseconds += 86_400_000;
        Assert.Null(f.Fire());
        var later = Contact(f);
        Assert.InRange(later.Position.DistanceTo(first.Position), 0, 220);
        f.Clock.Milliseconds -= 3_600_000;
        Assert.Null(f.Fire());
        Assert.Equal(later.Position, Contact(f).Position);
        Assert.Equal(later.Navigation, Contact(f).Navigation);
    }

    [Fact]
    public void SchedulingJitterKeepsOneMovementStepPerSharedSecond()
    {
        using NpcCombatTests.Fixture f = new();
        StartDistantFight(f);
        f.Clock.Milliseconds = 7500;
        f.Fire();
        f.Clock.Milliseconds = 8500;
        f.Fire();
        var first = Contact(f);
        f.Clock.Milliseconds = 9499;
        f.Fire();
        var second = Contact(f);
        Assert.True(second.Position.DistanceTo(f.Npc.Position) > first.Position.DistanceTo(f.Npc.Position));
        f.Clock.Milliseconds = 9500;
        f.Fire();
        Assert.Equal(second.Position, Contact(f).Position);
        Assert.Equal(second.Navigation, Contact(f).Navigation);
    }

    [Fact]
    public void DisengagingReturnsTheSameDamagedNpcWithoutResettingHealthOrSpawningAnother()
    {
        using NpcCombatTests.Fixture f = new();
        StartDistantFight(f);
        f.Clock.Milliseconds = 7500;
        f.Fire();
        for (int second = 0; second < 20; second++)
        {
            f.Clock.Milliseconds += 1000;
            f.Fire();
        }
        long healthAt = f.Clock.Now;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var npc = world.Set<WorldNpc>().Find(f.Npc.Id)!;
            npc.ShieldCharge = 10;
            npc.ArmorDamage = 2;
            npc.StructureDamage = 3;
            npc.UpdatedAt = healthAt;
            world.SaveChanges();
        }
        f.Combat.Disengage(NpcCombatTests.Fixture.ShipId);
        Assert.Empty(f.Combat.Aggressors(NpcCombatTests.Fixture.ShipId));
        Assert.Equal(f.Npc.Position, Contact(f).Navigation!.Destination);
        for (int second = 0; second < 100; second++)
        {
            f.Clock.Milliseconds += 1000;
            _ = Contact(f);
        }
        var returned = Contact(f);
        Assert.InRange(f.Npc.Position.DistanceTo(returned.Position), 0, 25);
        Assert.Null(returned.Navigation);
        using WorldDbContext saved = new(f.Db.Path);
        var retained = saved.Set<WorldNpc>().Find(f.Npc.Id)!;
        Assert.Equal(10, retained.ShieldCharge);
        Assert.Equal(2, retained.ArmorDamage);
        Assert.Equal(3, retained.StructureDamage);
        Assert.Equal(healthAt, retained.UpdatedAt);
        Assert.Equal(2, saved.Set<WorldNpc>().Count());
        Assert.Empty(saved.Set<NpcBounty>());
        Assert.Empty(saved.Set<ShipLoss>());
        _ = new SqliteWorld(f.Db.Path);
        Assert.Equal(returned, Contact(f));
    }

    [Fact]
    public void PursuitUsesRelativeVelocityForBothNpcAndPlayerTurrets()
    {
        using NpcCombatTests.Fixture f = new();
        f.Aggress();
        f.Clock.Milliseconds = 7500;
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=0.365 WHERE ItemTypeId=3638 AND AttributeId=160");
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            var npc = world.Set<WorldNpc>().Find(f.Npc.Id)!;
            npc.Navigation = NpcNavigationState.Pursuing;
            npc.MotionAt = f.Clock.Now;
            npc.VelocityY = 300;
            world.SaveChanges();
        }
        var player = f.Weapons.Fire(NpcCombatTests.Fixture.PilotId, f.Module, f.Npc.Id, SpacePosition.Zero, f.Clock.Now, 0.9);
        Assert.Equal(0, player.Applied);
        var incoming = Assert.IsType<NpcShot>(f.Fire(0.9));
        Assert.Equal(0, incoming.Applied);
        Assert.Equal(0, incoming.Quality);
    }

    [Fact]
    public void MovingNpcSnapshotsAndObserverUpdatesCarryCourseWithoutResettingTheBallpark()
    {
        using NpcCombatTests.Fixture f = new();
        StartDistantFight(f);
        using FlightSession observer = new(
            f.Db.Store,
            f.Pilot,
            f.Clock,
            new(),
            [],
            encounters: f.Db.Services.GetRequiredService<INpcEncounters>()
        );
        observer.Snapshot();
        observer.Updates.Clear();
        f.Clock.Milliseconds = 7500;
        f.Fire();
        observer.RefreshNpcs();
        var calls = observer.WireUpdates().Cast<object?[]>().Select(u => Assert.IsType<object?[]>(u[1])).ToArray();
        Assert.Contains(calls, c => Equals(c[0], "FollowBall"));
        Assert.DoesNotContain(calls, c => Equals(c[0], "SetState"));
        var npc = Contact(f);
        var chunk = Assert.IsType<object?[]>(WireAssertions.Materialize(Ballpark.AddNpcs([npc], 42)));
        using BinaryReader reader = new(new MemoryStream(Assert.IsType<byte[]>(chunk[0])));
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(42, reader.ReadInt32());
        Assert.Equal(npc.Id, reader.ReadInt64());
        Assert.Equal((byte)NavigationMode.Follow, reader.ReadByte());
        reader.ReadBytes(
            sizeof(float)
                + 3 * sizeof(double)
                + sizeof(byte)
                + sizeof(double)
                + sizeof(byte)
                + sizeof(long)
                + 2 * sizeof(int)
                + sizeof(float)
        );
        Assert.Equal(npc.Navigation!.Velocity, new(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()));
        Assert.Equal(npc.Agility, reader.ReadSingle());
        Assert.Equal(0.5, reader.ReadSingle());
        Assert.Equal(-1, reader.ReadSByte());
        Assert.Equal(NpcCombatTests.Fixture.ShipId, reader.ReadInt64());
        Assert.Equal(1250, reader.ReadSingle());
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
        observer.Updates.Clear();
        f.Combat.Disengage(NpcCombatTests.Fixture.ShipId);
        observer.RefreshNpcs();
        Assert.Contains(observer.WireUpdates().Cast<object?[]>(), u => Equals(Assert.IsType<object?[]>(u[1])[0], "GotoPoint"));
        Assert.NotEmpty(BlueMarshal.Encode(observer.WireUpdates()));
    }

    [Fact]
    public void ReturningThroughTheExactHomePointDampsVelocityInsteadOfDividingByZero()
    {
        using NpcCombatTests.Fixture f = new();
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var type = catalog.Set<ItemType>().Single(t => t.Id == f.Npc.TypeId);
        var attributes = catalog
            .Set<TypeAttribute>()
            .Where(a => a.ItemTypeId == type.Id)
            .ToDictionary(a => (DogmaAttributeId)a.AttributeId, a => a.Value);
        WorldNpc npc = new()
        {
            X = f.Npc.Position.X,
            Y = f.Npc.Position.Y,
            Z = f.Npc.Position.Z,
            VelocityX = 100,
            Navigation = NpcNavigationState.Returning,
            MotionAt = f.Clock.Now,
        };
        f.Clock.Milliseconds += 1000;
        NpcMotion.Advance(npc, f.Npc.Position, type, attributes, f.Clock.Now, returning: true);
        Assert.InRange(NpcMotion.Velocity(npc).DistanceTo(SpacePosition.Zero), 0, 100);
        Assert.InRange(NpcMotion.Position(npc).DistanceTo(f.Npc.Position), 0, 100);
        for (int second = 0; second < 300 && npc.Navigation != NpcNavigationState.Idle; second++)
        {
            f.Clock.Milliseconds += 1000;
            NpcMotion.Advance(npc, f.Npc.Position, type, attributes, f.Clock.Now, returning: true);
        }
        Assert.Equal(NpcNavigationState.Idle, npc.Navigation);
        Assert.Equal(SpacePosition.Zero, NpcMotion.Velocity(npc));
        Assert.InRange(NpcMotion.Position(npc).DistanceTo(f.Npc.Position), 0, 25);
    }

    [Fact]
    public void VersionTwentyFiveMigrationRetainsNpcIdentityDamageAndExistingAggressionDeadline()
    {
        using NpcCombatTests.Fixture f = new();
        f.Aggress();
        long deadline;
        using (WorldDbContext world = new(f.Db.Path))
        {
            deadline = world.Set<NpcAggression>().Single().ReadyAt;
            world.Database.ExecuteSqlRaw(TestWorld.DropNpcNavigationColumns + "PRAGMA user_version=25");
        }
        _ = new SqliteWorld(f.Db.Path);
        using WorldDbContext saved = new(f.Db.Path);
        var npc = saved.Set<WorldNpc>().Find(f.Npc.Id)!;
        Assert.Equal(27, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Equal(f.Npc.Position, NpcMotion.Position(npc));
        Assert.True(npc.ShieldCharge < 20);
        Assert.Equal(NpcNavigationState.Idle, npc.Navigation);
        Assert.Equal(SpacePosition.Zero, NpcMotion.Velocity(npc));
        Assert.Equal(0, npc.MotionAt);
        Assert.Equal(deadline, saved.Set<NpcAggression>().Single().ReadyAt);
        Assert.Empty(saved.Set<NpcBounty>());
        Assert.Empty(saved.Set<ShipLoss>());
    }
}
