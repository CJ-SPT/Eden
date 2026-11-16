using System.Collections.Concurrent;
using System.Diagnostics;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class ServerPerformanceReplayTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(512)]
    public async Task PopulationMaintenanceAndTwoMovingSessionFramesStaySqlFree(int population)
    {
        const int npcSystem = 30000144;
        const int warmupSamples = 3;
        const int measuredSamples = 5;
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        using (var setup = live.Economy.Begin(new(systems: [npcSystem])))
        {
            for (int index = 0; index < population; index++)
            {
                setup.Add(
                    new WorldNpc
                    {
                        Id = NpcEncounterDefinitions.FirstNpcId + 1000 + index,
                        EncounterId = 70000000 + index,
                        SystemId = npcSystem,
                        TypeId = NpcEncounterDefinitions.GuristasRookieType,
                        Role = NpcRole.Navy,
                    }
                );
            }
            setup.Commit();
        }
        var first = fixture.Store.FindPilot(1000001)!;
        var second = fixture.Store.FindPilot(1000002)!;
        Assert.NotEqual(npcSystem, first.SystemId);
        Assert.NotEqual(npcSystem, second.SystemId);
        fixture.Store.SetPosition(first.ShipId, new(1_000_000_000, 0, 0), first.SystemId);
        fixture.Store.SetPosition(second.ShipId, new(1_000_000_000, 1000, 0), first.SystemId);
        ConcurrentQueue<string> reports = new();
        using ReplayPlayer owner = new(fixture.Services, clock, first.PilotId, 1000001, reports);
        using ReplayPlayer observer = new(fixture.Services, clock, second.PilotId, 1000002, reports);
        owner.Context.Flight.Snapshot();
        observer.Context.Flight.Snapshot();
        Assert.Null(owner.Context.Flight.CallWire("beyonce", "CmdGotoDirection", [1d, 0d, 0d]));
        Assert.Null(observer.Context.Flight.CallWire("beyonce", "CmdGotoDirection", [1d, 0d, 0d]));
        List<double> turns = [];
        List<double> maintenance = [];
        List<double> frames = [];
        List<long> allocations = [];
        for (int sample = 0; sample < warmupSamples + measuredSamples; sample++)
        {
            clock.Seconds++;
            using var metrics = SessionPerformance.Begin("population and moving frames", _ => { }, force: true)!;
            long allocated = GC.GetTotalAllocatedBytes();
            long started = Stopwatch.GetTimestamp();
            using (var action = live.Economy.Begin(new(systems: [npcSystem])))
            {
                Assert.Equal(population, action.Filter<WorldNpc>(npc => npc.SystemId == npcSystem).ToArray().Length);
                action.Commit();
            }
            double populationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long frameStarted = Stopwatch.GetTimestamp();
            await owner.Frame();
            await observer.Frame();
            double frameMilliseconds = Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds;
            if (sample >= warmupSamples)
            {
                turns.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                maintenance.Add(populationMilliseconds);
                frames.Add(frameMilliseconds);
                allocations.Add(GC.GetTotalAllocatedBytes() - allocated);
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
        }
        Assert.True(owner.Context.Pilot.Position!.X > 1_000_000_000);
        Assert.True(observer.Context.Pilot.Position!.X > 1_000_000_000);
        output.WriteLine(
            $"Population={population}, median turn={turns.Order().ElementAt(measuredSamples / 2):F2} ms, "
                + $"maintenance={maintenance.Order().ElementAt(measuredSamples / 2):F2} ms, "
                + $"two frames={frames.Order().ElementAt(measuredSamples / 2):F2} ms, "
                + $"allocated={allocations.Order().ElementAt(measuredSamples / 2)}."
        );
        long checkpointStarted = Stopwatch.GetTimestamp();
        await live.FlushAsync();
        output.WriteLine($"Checkpoint={Stopwatch.GetElapsedTime(checkpointStarted).TotalMilliseconds:F2} ms.");
    }
}
