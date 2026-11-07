using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class NpcOrbitTests
{
    private static NpcContact Contact(NpcCombatTests.Fixture f) =>
        f.Db.Services.GetRequiredService<INpcEncounters>().List(f.Npc.SystemId, f.Clock.Now).Single(n => n.Id == f.Npc.Id);

    private static (WorldNpc Npc, CatalogItemDefinition Type, Dictionary<DogmaAttributeId, double> Attributes) Motion(
        NpcCombatTests.Fixture f
    )
    {
        using WorldDbContext world = new(f.Db.Path);
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        return (
            world.Set<WorldNpc>().Find(f.Npc.Id)!,
            catalog.RequireItem(f.Npc.TypeId),
            catalog
                .Set<TypeAttribute>()
                .Where(a => a.ItemTypeId == f.Npc.TypeId)
                .ToDictionary(a => (DogmaAttributeId)a.AttributeId, a => a.Value)
        );
    }

    [Fact]
    public void OrbitContinuesBetweenWeaponCyclesWithoutResettingVelocityOrThePaidDeadline()
    {
        using NpcCombatTests.Fixture f = new();
        f.Aggress();
        f.Clock.Milliseconds = 7500;
        Assert.NotNull(f.Fire());
        Assert.Equal(NpcNavigationState.Orbiting, Contact(f).Navigation!.State);
        f.Clock.Milliseconds = 8500;
        Assert.Null(f.Fire());
        var moved = Contact(f);
        Assert.InRange(moved.Navigation!.Velocity.Length, 50, 120);
        Assert.True(moved.Position.DistanceTo(f.Npc.Position) > 50);
        f.Clock.Milliseconds = 8600;
        Assert.Null(f.Fire());
        Assert.Equal(moved.Position, Contact(f).Position);
        Assert.Equal(moved.Navigation, Contact(f).Navigation);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(f.Clock.Now + 1400 * TimeSpan.TicksPerMillisecond, saved.Set<NpcAggression>().Single().ReadyAt);
    }

    [Fact]
    public void CatalogCruiseAndInertiaProduceASustainedTangentialOrbit()
    {
        using NpcCombatTests.Fixture f = new();
        var (npc, type, attributes) = Motion(f);
        var target = f.Pilot.Position!;
        double radii = f.Npc.Radius + f.Pilot.Flight.ShipRadius;
        for (int tick = 0; tick <= 300; tick++)
        {
            f.Clock.Milliseconds = tick * 1000;
            NpcMotion.Pursue(npc, target, f.Pilot.Flight.ShipRadius, type, attributes, f.Clock.Now, tick + 1);
            if (tick < 200)
            {
                continue;
            }
            var radial = NpcMotion.Position(npc) - target;
            Assert.Equal(NpcNavigationState.Orbiting, npc.Navigation);
            Assert.InRange(radial.Length - radii, 1100, 1600);
            Assert.InRange(NpcMotion.Velocity(npc).Length, 200, 220.001);
            Assert.InRange(Math.Abs(radial.Unit.Dot(NpcMotion.Velocity(npc).Unit)), 0, 0.3);
        }
    }

    [Fact]
    public void HysteresisTracksMovingTargetsAndPreservesMomentumWhenPursuitResumes()
    {
        using NpcCombatTests.Fixture f = new();
        var (npc, type, attributes) = Motion(f);
        NpcMotion.Pursue(npc, f.Pilot.Position!, f.Pilot.Flight.ShipRadius, type, attributes, f.Clock.Now, 1);
        f.Clock.Milliseconds = 1000;
        NpcMotion.Pursue(npc, f.Pilot.Position!, f.Pilot.Flight.ShipRadius, type, attributes, f.Clock.Now, 2);
        var velocity = NpcMotion.Velocity(npc);
        double radii = f.Npc.Radius + f.Pilot.Flight.ShipRadius;
        var target = NpcMotion.Position(npc) + new SpacePosition(2100 + radii, 0, 0);
        NpcMotion.Pursue(npc, target, f.Pilot.Flight.ShipRadius, type, attributes, f.Clock.Now, 2);
        Assert.Equal(NpcNavigationState.Orbiting, npc.Navigation);
        Assert.Equal(velocity, NpcMotion.Velocity(npc));
        target = NpcMotion.Position(npc) + new SpacePosition(attributes[DogmaAttributeId.EntityAttackRange] + radii + 1, 0, 0);
        NpcMotion.Pursue(npc, target, f.Pilot.Flight.ShipRadius, type, attributes, f.Clock.Now, 2);
        Assert.Equal(NpcNavigationState.Pursuing, npc.Navigation);
        Assert.Equal(velocity, NpcMotion.Velocity(npc));
        f.Clock.Milliseconds = 2000;
        NpcMotion.Pursue(npc, target, f.Pilot.Flight.ShipRadius, type, attributes, f.Clock.Now, 3);
        Assert.NotEqual(velocity, NpcMotion.Velocity(npc));
        Assert.InRange(NpcMotion.Velocity(npc).Length, 0, 220);
    }

    [Fact]
    public void ObserversAndJoiningSnapshotsCarryNativeOrbitAndSwitchBackToFollow()
    {
        using NpcCombatTests.Fixture f = new();
        using FlightSession observer = new(
            f.Db.Store,
            f.Pilot,
            f.Clock,
            new(),
            [],
            encounters: f.Db.Services.GetRequiredService<INpcEncounters>()
        );
        observer.Snapshot();
        f.Aggress();
        f.Clock.Milliseconds = 7500;
        f.Fire();
        observer.Updates.Clear();
        observer.RefreshNpcs();
        var actions = observer.WireUpdates().Cast<object?[]>().Select(u => (object?[])u[1]!).ToArray();
        Assert.Contains(actions, a => Equals(a[0], "Orbit"));
        Assert.DoesNotContain(actions, a => Equals(a[0], "SetState"));
        var npc = Contact(f);
        var chunk = Assert.IsType<object?[]>(WireAssertions.Materialize(Ballpark.AddNpcs([npc], 42)));
        using BinaryReader reader = new(new MemoryStream((byte[])chunk[0]!));
        reader.ReadByte();
        reader.ReadInt32();
        Assert.Equal(npc.Id, reader.ReadInt64());
        Assert.Equal((byte)NavigationMode.Orbit, reader.ReadByte());
        reader.ReadBytes(
            sizeof(float)
                + 3 * sizeof(double)
                + sizeof(byte)
                + sizeof(double)
                + sizeof(byte)
                + sizeof(long)
                + 2 * sizeof(int)
                + sizeof(float)
                + 3 * sizeof(double)
                + 2 * sizeof(float)
                + sizeof(sbyte)
        );
        Assert.Equal(f.Pilot.ShipId, reader.ReadInt64());
        Assert.Equal(1250, reader.ReadSingle());
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
        Assert.NotEmpty(BlueMarshal.Encode(chunk));
        f.Db.Store.SetPosition(f.Pilot.ShipId, npc.Position + new SpacePosition(8000, 0, 0), f.Npc.SystemId);
        Assert.Null(f.Fire());
        observer.Updates.Clear();
        observer.RefreshNpcs();
        Assert.Contains(observer.WireUpdates().Cast<object?[]>(), u => Equals(((object?[])u[1]!)[0], "FollowBall"));
        Assert.Equal(NpcNavigationState.Pursuing, Contact(f).Navigation!.State);
    }

    [Fact]
    public async Task ConcurrentAndLateOrbitTicksNeverMoveTwiceOrBurstOverdueShots()
    {
        using NpcCombatTests.Fixture f = new();
        f.Aggress();
        f.Clock.Milliseconds = 7500;
        f.Fire();
        f.Clock.Milliseconds = 8500;
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => f.Fire())));
        var moved = Contact(f);
        Assert.InRange(moved.Position.DistanceTo(f.Npc.Position), 50, 70);
        f.Clock.Milliseconds += 86_400_000;
        Assert.NotNull(f.Fire());
        var late = Contact(f);
        Assert.InRange(late.Position.DistanceTo(moved.Position), 0, 220);
        Assert.Null(f.Fire());
        Assert.Equal(late.Position, Contact(f).Position);
    }

    [Fact]
    public void SavedOrbitRetainsGeometryAndLosingAggressionStartsReturnWithoutHealing()
    {
        using NpcCombatTests.Fixture f = new();
        f.Aggress();
        f.Clock.Milliseconds = 7500;
        f.Fire();
        f.Clock.Milliseconds = 8500;
        f.Fire();
        var before = Contact(f);
        // Reopening persistence retains the orbit; a server restart now clears
        // live aggression and is covered separately by NPC combat tests.
        Assert.Equal(before, new NpcEncounterStore(new(f.Db.Path)).List(f.Npc.SystemId, f.Clock.Now).Single(n => n.Id == f.Npc.Id));
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(NpcNavigationState.Orbiting, world.Set<WorldNpc>().Find(f.Npc.Id)!.Navigation);
            world.RemoveRange(world.Set<NpcAggression>());
            world.SaveChanges();
        }
        var returned = Contact(f);
        Assert.Equal(NpcNavigationState.Returning, returned.Navigation!.State);
        Assert.Equal(f.Npc.Position, returned.Navigation.Destination);
        Assert.Equal(before.Health, returned.Health);
        Assert.Equal(before.Position, returned.Position);
        Assert.Equal(before.Navigation!.Velocity, returned.Navigation.Velocity);
    }

    [Fact]
    public void CoincidentGeometryRemainsFiniteAndInvalidThresholdsAreRejected()
    {
        using NpcCombatTests.Fixture f = new();
        var (npc, type, attributes) = Motion(f);
        var center = NpcMotion.Position(npc);
        for (int tick = 0; tick < 20; tick++)
        {
            f.Clock.Milliseconds = tick * 1000;
            NpcMotion.Pursue(npc, center, 0, type, attributes, f.Clock.Now, tick + 1);
            Assert.True(double.IsFinite(NpcMotion.Position(npc).Length));
            Assert.True(double.IsFinite(NpcMotion.Velocity(npc).Length));
        }
        attributes[DogmaAttributeId.EntityChaseMaxDistance] = double.NaN;
        Assert.Throws<InvalidDataException>(() => NpcMotion.Pursue(npc, center, 0, type, attributes, f.Clock.Now, 20));
    }
}
