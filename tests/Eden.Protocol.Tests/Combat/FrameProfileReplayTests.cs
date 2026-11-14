using System.Collections.Concurrent;
using System.Text.Json;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class ServerPerformanceReplayTests
{
    private sealed record FrameVector(double X, double Y, double Z);

    [Fact]
    public async Task ProfileSavedSpaceSessionFrames()
    {
        if (Environment.GetEnvironmentVariable("EDEN_FRAME_SOURCE") is not { } source)
        {
            output.WriteLine("Set EDEN_FRAME_SOURCE to a frozen world/catalog directory.");
            return;
        }
        string destination = Environment.GetEnvironmentVariable("EDEN_FRAME_OUTPUT") ?? "artifacts/frame-profile";
        long pilotId = long.Parse(Environment.GetEnvironmentVariable("EDEN_FRAME_PILOT") ?? "90001835");
        string directory = CopySnapshot(source);
        try
        {
            CopyReplaySidecars(source, directory);
            WorldDatabase database = new(Path.Combine(directory, "world.sqlite"));
            TestClock clock = new(new DateTimeOffset(2026, 11, 14, 6, 46, 0, TimeSpan.Zero));
            WorldInitialization.Initialize(database, clock);
            long account;
            using (WorldDbContext world = new(database.Path))
            {
                account = world.Pilots.Single(pilot => pilot.Id == pilotId).AccountId;
            }
            using var host = ServerServices.Build(database, clock, liveWorld: true);
            var store = host.GetRequiredService<SqliteWorld>();
            var pilot = store.FindPilot(account, pilotId)!;
            store.SetPosition(pilot.ShipId, new(pilot.X + 2_000_000, pilot.Y, pilot.Z), pilot.SystemId);
            ConcurrentQueue<string> reports = new();
            using var player = new ReplayPlayer(host, clock, pilotId, account, reports);
            player.Context.Flight.Snapshot();
            await player.Call("beyonce", "CmdGotoDirection", 1d, 0d, 0d);
            var startingPosition = player.Context.Flight.Pilot.Position!;
            List<PerformanceSample> phases = [];
            List<ReplaySample> frames = [];
            List<ReplaySample> counted = [];
            int warmup = int.Parse(Environment.GetEnvironmentVariable("EDEN_FRAME_WARMUP") ?? "20");
            int samples = int.Parse(Environment.GetEnvironmentVariable("EDEN_FRAME_SAMPLES") ?? "100");
            const int countedWarmup = 10;
            Assert.True(warmup >= countedWarmup);
            Assert.True(samples > 0);
            long allocated = 0;
            for (int sample = -warmup; sample < samples; sample++)
            {
                clock.Seconds++;
                if (sample < 0)
                {
                    using var captured = SessionPerformance.Capture(
                        "frame profile",
                        phase =>
                        {
                            if (sample >= -countedWarmup)
                            {
                                phases.Add(phase);
                            }
                        }
                    );
                    var frame = await player.Frame();
                    if (sample >= -countedWarmup)
                    {
                        counted.Add(frame);
                    }
                }
                else
                {
                    player.MeasureCounters = false;
                    if (sample == 0)
                    {
                        allocated = GC.GetTotalAllocatedBytes(precise: true);
                    }
                    frames.Add(await player.Frame());
                }
            }
            allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            var finalPosition = player.Context.Flight.Pilot.Position!;
            var live = host.GetRequiredService<LiveWorld>();
            live.MeasureCheckpoints = true;
            await live.FlushAsync();
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(
                Path.Combine(destination, "frames.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        WarmupFrames = warmup,
                        MeasuredFrames = samples,
                        Frames = frames,
                        CountedFrames = counted,
                        AllocatedBytesPerFrame = allocated / samples,
                        Position = new FrameVector(finalPosition.X, finalPosition.Y, finalPosition.Z),
                        Velocity = new FrameVector(
                            player.Context.Flight.Velocity.X,
                            player.Context.Flight.Velocity.Y,
                            player.Context.Flight.Velocity.Z
                        ),
                        Phases = phases
                            .GroupBy(phase => phase.Name)
                            .Select(group => new
                            {
                                Name = group.Key,
                                Count = group.Count(),
                                MeanMs = group.Average(phase => phase.Elapsed.TotalMilliseconds),
                                MaxMs = group.Max(phase => phase.Elapsed.TotalMilliseconds),
                                Bytes = group.Average(phase => phase.AllocatedBytes),
                                Sql = group.Sum(phase => phase.Commands),
                                Connections = group.Sum(phase => phase.Connections),
                                Fits = group.Sum(phase => phase.Fittings),
                            })
                            .OrderByDescending(phase => phase.MeanMs),
                        Checkpoints = live.RecentCheckpoints,
                    },
                    new JsonSerializerOptions { WriteIndented = true }
                )
            );
            await File.WriteAllLinesAsync(Path.Combine(destination, "frames.log"), reports);
            output.WriteLine($"Frames mean {frames.Average(frame => frame.Frame):F3} ms; max {frames.Max(frame => frame.Frame):F3} ms.");
            Assert.Equal(pilotId, player.Context.Pilot.PilotId);
            Assert.Equal(pilot.ShipId, player.Context.Pilot.ShipId);
            Assert.True(finalPosition.DistanceTo(startingPosition) > 0);
            Assert.Equal(finalPosition, store.FindPilot(account, pilotId)!.Position);
            Assert.All(
                counted,
                frame =>
                {
                    Assert.Equal(0, frame.Sql);
                    Assert.Equal(0, frame.Connections);
                    Assert.Equal(0, frame.Fits);
                }
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
