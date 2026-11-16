using System.Diagnostics;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Persistence;

public sealed class NpcPopulationValidationTests(ITestOutputHelper output)
{
    private const int System = 30000144;
    private const int Population = 512;
    private const long FirstNpc = NpcEncounterDefinitions.FirstNpcId + 1000;
    private const long FirstEncounter = 70000000;
    private const int AllocationPerNpc = 16384;

    [Fact]
    public void PopulationParticipantsProjectOnlyLiveMutableMembersWithoutCloningGuards()
    {
        using TestWorld fixture = new(liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        List<long> expected = [];
        using (var setup = live.Economy.Begin(new(systems: [System])))
        {
            for (int index = 0; index < Population; index++)
            {
                var guard = Npc(index);
                guard.TypeId = (int)NpcGuardType.CaldariCaptain;
                setup.Add(guard);
            }
            NpcRole[] roles =
            [
                NpcRole.MiningMiner,
                NpcRole.MiningHauler,
                NpcRole.MiningDefender,
                NpcRole.MiningLogistics,
                NpcRole.Convoy,
                NpcRole.Pirate,
            ];
            foreach (var role in roles)
            {
                var npc = Npc(Population + expected.Count);
                npc.Role = role;
                expected.Add(npc.Id);
                setup.Add(npc);
            }
            var special = Npc(Population + expected.Count);
            special.TypeId = NpcSpecialTypes.Excluded.First();
            special.Role = NpcRole.Pirate;
            expected.Add(special.Id);
            setup.Add(special);
            var destroyed = Npc(Population + expected.Count);
            destroyed.DestroyedAt = 1;
            setup.Add(destroyed);
            var retired = Npc(Population + expected.Count + 1);
            retired.RetiredAt = 1;
            setup.Add(retired);
            var remote = Npc(Population + expected.Count + 2);
            remote.SystemId = System + 1;
            setup.Add(remote);
            setup.Commit();
        }
        using var metrics = SessionPerformance.Begin("population participant discovery", output.WriteLine, force: true)!;
        live.Economy.Inspect(world =>
        {
            var participants = NpcEncounterStore.PopulationParticipants(world, System);
            Assert.Equal([System], participants.Systems);
            Assert.Equal(expected.Order(), participants.Resources.Select(resource => resource.Id).Order());
            Assert.All(participants.Resources, resource => Assert.Equal(SharedSimulationResource.Npc, resource.Kind));
            Assert.Equal(0, ((EconomyTransaction)world).Materializations);
            return true;
        });
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void NewDuplicateSlotsRejectTheWholePopulation()
    {
        using TestWorld fixture = new(liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        using (var rejected = live.Economy.Begin(new(systems: [System])))
        {
            rejected.Add(Npc(0));
            var other = Npc(1);
            other.EncounterId = FirstEncounter;
            rejected.Add(other);
            Assert.Throws<InvalidDataException>(rejected.Commit);
        }
        Assert.Equal(0, live.Economy.Inspect(world => world.Filter<WorldNpc>(_ => true).Count()));
    }

    [Theory]
    [InlineData("destroyed")]
    [InlineData("retired")]
    [InlineData("deleted")]
    [InlineData("legacy")]
    [InlineData("moved")]
    public async Task ReleasedSlotsCanBeReusedInTheSameCommitAndReopened(string release)
    {
        using TestWorld fixture = new(liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        using (var setup = live.Economy.Begin(new(systems: [System])))
        {
            setup.Add(Npc(0));
            setup.Commit();
        }
        using (var action = live.Economy.Begin(new(systems: [System])))
        {
            var previous = action.Set<WorldNpc>().Find(FirstNpc)!;
            switch (release)
            {
                case "destroyed":
                    previous.DestroyedAt = 1;
                    break;
                case "retired":
                    previous.RetiredAt = 1;
                    break;
                case "deleted":
                    action.Remove(previous);
                    break;
                case "legacy":
                    previous.Role = NpcRole.Pirate;
                    break;
                case "moved":
                    previous.SpawnSlot = 1;
                    break;
            }
            var replacement = Npc(1);
            replacement.EncounterId = FirstEncounter;
            action.Add(replacement);
            action.Commit();
        }
        await live.FlushAsync();
        using var disk = WorldConnections.FileConnection(fixture.Path);
        using WorldDbContext reopened = new(disk);
        var occupant = Assert.Single(
            reopened
                .Set<WorldNpc>()
                .Where(npc => npc.Role != NpcRole.Pirate && npc.DestroyedAt == null && npc.RetiredAt == null && npc.SpawnSlot == 0)
        );
        Assert.Equal(FirstNpc + 1, occupant.Id);
        Assert.Equal(FirstEncounter, occupant.EncounterId);
    }

    [Fact]
    public void PopulationCommitAllocationGrowsLinearlyWithTheLoadedPopulation()
    {
        long small = Measure(1);
        long populated = Measure(Population);
        Assert.InRange(populated, 0, small + Population * AllocationPerNpc);

        long Measure(int count)
        {
            using TestWorld fixture = new(liveWorld: true);
            var live = fixture.Services.GetRequiredService<LiveWorld>();
            using (var setup = live.Economy.Begin(new(systems: [System])))
            {
                for (int index = 0; index < count; index++)
                {
                    setup.Add(Npc(index));
                }
                setup.Commit();
            }
            // Warm both the read and commit on the identical resident population.
            for (int warmup = 0; warmup < 2; warmup++)
            {
                using var action = live.Economy.Begin(new(systems: [System]));
                Assert.Equal(count, action.Filter<WorldNpc>(npc => npc.SystemId == System).ToArray().Length);
                action.Commit();
            }
            using var metrics = SessionPerformance.Begin("population validation", output.WriteLine, force: true)!;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            using (var action = live.Economy.Begin(new(systems: [System])))
            {
                Assert.Equal(count, action.Filter<WorldNpc>(npc => npc.SystemId == System).ToArray().Length);
                action.Commit();
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            output.WriteLine(
                $"Population={count}, allocated={allocated}, elapsed={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms."
            );
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
            return allocated;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateSlotsRejectBothNewAndPreviouslyPublishedMembersAndRollback(bool published)
    {
        using TestWorld fixture = new(liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        using (var setup = live.Economy.Begin(new(systems: [System])))
        {
            setup.Add(Npc(0));
            if (published)
            {
                setup.Add(Npc(1));
            }
            setup.Commit();
        }
        using (var rejected = live.Economy.Begin(new(systems: [System])))
        {
            var duplicate = published ? rejected.Set<WorldNpc>().Find(FirstNpc + 1)! : rejected.Add(Npc(1)).Entity;
            duplicate.EncounterId = FirstEncounter;
            Assert.Throws<InvalidDataException>(rejected.Commit);
        }
        live.Economy.Inspect(world =>
        {
            Assert.Equal(FirstEncounter, world.Set<WorldNpc>().Find(FirstNpc)!.EncounterId);
            var other = world.Set<WorldNpc>().Find(FirstNpc + 1);
            if (published)
            {
                Assert.Equal(FirstEncounter + 1, other!.EncounterId);
            }
            else
            {
                Assert.Null(other);
            }
            return true;
        });
        await live.FlushAsync();
        using var disk = WorldConnections.FileConnection(fixture.Path);
        using WorldDbContext reopened = new(disk);
        Assert.Equal(published ? 2 : 1, reopened.Set<WorldNpc>().Count());
        Assert.Equal(FirstEncounter, reopened.Set<WorldNpc>().Find(FirstNpc)!.EncounterId);
    }

    private static WorldNpc Npc(int index) =>
        new()
        {
            Id = FirstNpc + index,
            EncounterId = FirstEncounter + index,
            SystemId = System,
            TypeId = NpcEncounterDefinitions.GuristasRookieType,
            Role = NpcRole.Navy,
        };
}
