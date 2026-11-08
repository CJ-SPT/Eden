using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Propulsion;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class LiveSimulationTests
{
    [Fact]
    public void ChangingNpcPopulationKeepsOtherSystemsWarm()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true);
        const int remote = 30000142;
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        _ = encounters.List(f.Npc.SystemId, f.Clock.Now);
        _ = encounters.List(remote, f.Clock.Now);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var npc = world.Set<WorldNpc>().Find(f.Npc.Id)!;
            npc.ShieldCharge -= 1;
            world.SaveChanges();
        }
        using (var metrics = SessionPerformance.Begin("other system survives population change", _ => { }, force: true)!)
        {
            _ = encounters.List(remote, f.Clock.Now);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        Assert.Equal(
            f.Npc.Health.Remaining.Shield - 1,
            encounters.List(f.Npc.SystemId, f.Clock.Now).Single(n => n.Id == f.Npc.Id).Health.Remaining.Shield,
            6
        );
    }

    [Fact]
    public void FinishingAReturnInvalidatesAnEarlierAcquisitionInTheSameSecond()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var npc = world.Set<WorldNpc>().Find(f.Npc.Id)!;
            npc.X += 200;
            npc.Navigation = NpcNavigationState.Returning;
            npc.MotionAt = f.Clock.Now;
            world.SaveChanges();
        }
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        NpcContact Contact() => encounters.List(f.Npc.SystemId, f.Clock.Now).Single(n => n.Id == f.Npc.Id);
        Assert.Equal(NpcNavigationState.Returning, Contact().Navigation!.State);
        NpcTargetCandidate candidate = new(NpcCombatTests.Fixture.PilotId, NpcCombatTests.Fixture.ShipId);
        bool returned = false;
        for (int second = 1; second <= 100; second++)
        {
            f.Clock.Seconds = second;
            f.Combat.Acquire([candidate], f.Clock.Now);
            if (Contact().Navigation is null)
            {
                f.Combat.Acquire([candidate], f.Clock.Now);
                Assert.Contains(f.Npc.Id, f.Combat.Aggressors(candidate.ShipId));
                returned = true;
                break;
            }
        }
        Assert.True(returned);
    }

    [Fact]
    public void MotionProjectionPreservesSavedDamageAboveTheCurrentHullCapacity()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var npc = world.Set<WorldNpc>().Find(f.Npc.Id)!;
            npc.ArmorDamage = 500;
            npc.StructureDamage = 600;
            world.SaveChanges();
        }
        f.Db.Store.Advance(f.Npc.SystemId, 1, f.Clock.Now, [], []);
        LiveWorld.Find(f.Db.Path)!.Combat!.Flush();
        using WorldDbContext saved = new(f.Db.Path);
        var row = saved.Set<WorldNpc>().Find(f.Npc.Id)!;
        Assert.Equal(500, row.ArmorDamage);
        Assert.Equal(600, row.StructureDamage);
    }

    [Fact]
    public void TackleMutationsRefreshResidentContactSpeedAndRemovingThemRestoresIt()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true);
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        NpcContact Contact() => encounters.List(f.Npc.SystemId, f.Clock.Now).Single(n => n.Id == f.Npc.Id);
        double normal = Contact().MaxSpeed;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new WorldTackle
                {
                    ModuleId = f.Module,
                    PilotId = NpcCombatTests.Fixture.PilotId,
                    SourceShipId = NpcCombatTests.Fixture.ShipId,
                    TargetId = f.Npc.Id,
                    TargetKind = TackleTargetKind.Npc,
                    Effect = DogmaEffect.StasisWebifier,
                    WebPercent = -50,
                }
            );
            world.SaveChanges();
        }
        Assert.Equal(normal / 2, Contact().MaxSpeed, 6);
        using (var measure = SessionPerformance.Begin("unchanged tackle contacts", _ => { }, force: true)!)
        {
            for (int call = 0; call < 10; call++)
            {
                Assert.Equal(normal / 2, Contact().MaxSpeed, 6);
            }
            Assert.Equal(0, measure.Commands);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldTackle>().ExecuteDelete();
        }
        Assert.Equal(normal, Contact().MaxSpeed, 6);
    }

    [Fact]
    public void PopulationScheduleUsesOccupiedEncountersAndLatestLiveGuardSlots()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true);
        using WorldDbContext world = new(f.Db.Path);
        long now = f.Clock.Now;
        world.Add(
            new WorldNpcEncounter
            {
                Id = 123,
                SystemId = f.Npc.SystemId,
                Occupied = true,
                NextSpawnAt = now + 200 * TimeSpan.TicksPerSecond,
            }
        );
        foreach (
            var row in new[]
            {
                new WorldNpc
                {
                    EncounterId = 123,
                    SpawnSlot = 0,
                    Role = NpcRole.Navy,
                    DestroyedAt = now - TimeSpan.TicksPerMinute,
                },
                new WorldNpc
                {
                    EncounterId = 123,
                    SpawnSlot = 0,
                    Role = NpcRole.Navy,
                },
                new WorldNpc
                {
                    EncounterId = 123,
                    SpawnSlot = 1,
                    Role = NpcRole.Navy,
                    DestroyedAt = now,
                },
                new WorldNpc
                {
                    EncounterId = 123,
                    SpawnSlot = 2,
                    Role = NpcRole.Navy,
                    DestroyedAt = now,
                    RetiredAt = now,
                },
            }
        )
        {
            row.SystemId = f.Npc.SystemId;
            row.TypeId = f.Npc.TypeId;
            world.Add(row);
        }
        world.SaveChanges();
        Assert.Equal(now + 200 * TimeSpan.TicksPerSecond, NpcPopulationSchedule.Next(world, f.Npc.SystemId, now));
        world.Set<WorldNpcEncounter>().Find(123L)!.Occupied = false;
        world.SaveChanges();
        Assert.Equal(
            now + NpcEncounterDefinitions.RespawnSeconds * TimeSpan.TicksPerSecond,
            NpcPopulationSchedule.Next(world, f.Npc.SystemId, now)
        );
    }

    [Fact]
    public void PopulationRefreshRunsAtItsDeadlineAndRespawnsAfterTheWarmWait()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true);
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldNpc>().Find(f.Npc.Id)!.DestroyedAt = f.Clock.Now;
            world.SaveChanges();
        }
        Assert.Single(encounters.List(f.Npc.SystemId, f.Clock.Now));
        using (var measured = SessionPerformance.Begin("waiting for respawn", _ => { }, force: true)!)
        {
            for (int second = 1; second < NpcEncounterDefinitions.RespawnSeconds; second++)
            {
                f.Clock.Seconds = second;
                Assert.Single(encounters.List(f.Npc.SystemId, f.Clock.Now));
            }
            Assert.Equal(0, measured.Commands);
            Assert.Equal(0, measured.Connections);
        }
        f.Clock.Seconds = NpcEncounterDefinitions.RespawnSeconds;
        var refreshed = encounters.List(f.Npc.SystemId, f.Clock.Now);
        Assert.Equal(2, refreshed.Count);
        Assert.DoesNotContain(refreshed, n => n.Id == f.Npc.Id);
        using var sameTick = SessionPerformance.Begin("respawn call burst", _ => { }, force: true)!;
        for (int call = 0; call < 20; call++)
        {
            Assert.Equal(refreshed, encounters.List(f.Npc.SystemId, f.Clock.Now));
        }
        Assert.Equal(0, sameTick.Commands);
        Assert.Equal(0, sameTick.Connections);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(24)]
    public async Task WarmMotionContactsAndAcquisitionAreSqlFreeAndCheckpointTheirNavigation(int count)
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldNpc>().Where(n => n.Id != f.Npc.Id).ExecuteDelete();
            for (int index = 1; index < count; index++)
            {
                world.Add(
                    new WorldNpc
                    {
                        SystemId = f.Npc.SystemId,
                        TypeId = f.Npc.TypeId,
                        HomeX = f.Npc.Position.X,
                        HomeY = f.Npc.Position.Y,
                        HomeZ = f.Npc.Position.Z + index * 500,
                        X = f.Npc.Position.X,
                        Y = f.Npc.Position.Y,
                        Z = f.Npc.Position.Z + index * 500,
                        ShieldCharge = f.Npc.Health.Defense.Shield,
                        UpdatedAt = f.Clock.Now,
                    }
                );
            }
            world.SaveChanges();
        }
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        var pilot = f.Pilot;
        IReadOnlyList<ShipMotion> ships = [new(pilot.ShipId, f.Npc.SystemId, 0, pilot.Position!, pilot.Flight, new())];
        void Advance(int stamp)
        {
            ships = f.Db.Store.Advance(f.Npc.SystemId, stamp, f.Clock.Now, ships, []).Select(s => s.Ship).ToArray();
        }
        LiveWorld.Find(f.Db.Path)!.Combat!.Clear();
        using (var cold = SessionPerformance.Begin("cold system reads", _ => { }, force: true)!)
        {
            Advance(1);
            Assert.Equal(1, cold.TackleReads);
            Assert.Equal(1, cold.AggressionReads);
            // Both population sizes hydrate the same four batched NPC datasets.
            Assert.InRange(cold.Commands, 1, 20);
            Assert.InRange(cold.Connections, 1, 12);
        }
        Assert.Equal(count, encounters.List(f.Npc.SystemId, f.Clock.Now).Count);
        f.Combat.Acquire([new(pilot.PilotId, pilot.ShipId)], f.Clock.Now);
        IReadOnlyList<NpcContact> contacts = [];
        using (var measure = SessionPerformance.Begin("warm system objects", _ => { }, force: true)!)
        {
            for (int second = 1; second <= 12; second++)
            {
                f.Clock.Seconds++;
                Advance(second + 1);
                contacts = encounters.List(f.Npc.SystemId, f.Clock.Now);
                Assert.Equal(count, contacts.Count);
                for (int call = 0; call < 10; call++)
                {
                    f.Combat.Acquire([new(pilot.PilotId, pilot.ShipId)], f.Clock.Now);
                    Assert.Equal(contacts, encounters.List(f.Npc.SystemId, f.Clock.Now));
                }
            }
            Assert.Equal(0, measure.Commands);
            Assert.Equal(0, measure.Connections);
            Assert.Equal(0, measure.FittingCalculations);
            Assert.Equal(0, measure.TackleReads);
            Assert.Equal(0, measure.AggressionReads);
        }
        Assert.All(contacts, n => Assert.NotNull(n.Navigation));
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        string checkpoint = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "simulation-checkpoint.sqlite");
        File.Copy(f.Db.Path, checkpoint);
        using WorldDbContext reopened = new(checkpoint);
        foreach (var npc in contacts)
        {
            var row = reopened.Set<WorldNpc>().Find(npc.Id)!;
            Assert.Equal(npc.Position, new(row.X, row.Y, row.Z));
            Assert.Equal(npc.Navigation!.Velocity, new(row.VelocityX, row.VelocityY, row.VelocityZ));
            Assert.Equal(npc.Navigation.State, row.Navigation);
            Assert.True(row.MotionAt > 0);
        }
        var saved = reopened.Positions.Find(pilot.ShipId)!;
        Assert.Equal(ships[0].Position, new(saved.X, saved.Y, saved.Z));
        Assert.Equal(count, reopened.Set<NpcAggression>().Count());
    }

    [Theory]
    [InlineData(NpcNavigationState.Idle, NpcRole.Pirate)]
    [InlineData(NpcNavigationState.Pursuing, NpcRole.Pirate)]
    [InlineData(NpcNavigationState.Returning, NpcRole.Pirate)]
    [InlineData(NpcNavigationState.Patrolling, NpcRole.Navy)]
    public void ResidentMotionMatchesTheSqlAdapter(NpcNavigationState navigation, NpcRole role)
    {
        using NpcCombatTests.Fixture control = new(liveWorld: true, simulationObjects: false);
        using NpcCombatTests.Fixture resident = new(liveWorld: true);
        void Prepare(NpcCombatTests.Fixture f)
        {
            using WorldDbContext world = new(f.Db.Path);
            world.Set<WorldNpc>().Where(n => n.Id != f.Npc.Id).ExecuteDelete();
            var npc = world.Set<WorldNpc>().Find(f.Npc.Id)!;
            npc.Navigation = navigation;
            npc.Role = role;
            npc.X += 4000;
            npc.VelocityX = 50;
            npc.VelocityY = 20;
            npc.MotionAt = f.Clock.Now;
            if (navigation == NpcNavigationState.Pursuing)
            {
                world.Add(new NpcAggression { NpcId = npc.Id, TargetShipId = f.Pilot.ShipId });
            }
            world.SaveChanges();
        }
        Prepare(control);
        Prepare(resident);
        var pilot = control.Pilot;
        IReadOnlyList<ShipMotion> expected = [new(pilot.ShipId, control.Npc.SystemId, 0, pilot.Position!, pilot.Flight, new())];
        IReadOnlyList<ShipMotion> actual = [expected[0] with { Navigation = expected[0].Navigation.Copy() }];
        for (int second = 1; second <= 12; second++)
        {
            control.Clock.Seconds++;
            resident.Clock.Seconds++;
            expected = control
                .Db.Store.Advance(control.Npc.SystemId, second, control.Clock.Now, expected, [])
                .Select(s => s.Ship)
                .ToArray();
            actual = resident.Db.Store.Advance(resident.Npc.SystemId, second, resident.Clock.Now, actual, []).Select(s => s.Ship).ToArray();
            LiveWorld.Find(resident.Db.Path)!.Combat!.Flush();
            using WorldDbContext left = new(control.Db.Path);
            using WorldDbContext right = new(resident.Db.Path);
            var a = left.Set<WorldNpc>().Find(control.Npc.Id)!;
            var b = right.Set<WorldNpc>().Find(resident.Npc.Id)!;
            Assert.Equal(
                (a.X, a.Y, a.Z, a.VelocityX, a.VelocityY, a.VelocityZ, a.Navigation, a.MotionAt),
                (b.X, b.Y, b.Z, b.VelocityX, b.VelocityY, b.VelocityZ, b.Navigation, b.MotionAt)
            );
            Assert.Equal(expected[0].Position, actual[0].Position);
            Assert.Equal(expected[0].Navigation.Velocity, actual[0].Navigation.Velocity);
        }
    }
}
