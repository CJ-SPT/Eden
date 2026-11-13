using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Mining;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class ServerPerformanceReplayTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task ReplayPopulatedWorldAtPlayerScale(int playerCount)
    {
        if (Environment.GetEnvironmentVariable("EDEN_SCALE_SOURCE") is not { } source)
        {
            output.WriteLine("Set EDEN_SCALE_SOURCE to a frozen world/catalog directory to run the populated player replay.");
            return;
        }
        int samples = int.Parse(Environment.GetEnvironmentVariable("EDEN_SCALE_SAMPLES") ?? "24");
        long template = long.Parse(Environment.GetEnvironmentVariable("EDEN_SCALE_TEMPLATE_PILOT") ?? "90001835");
        string destination = Environment.GetEnvironmentVariable("EDEN_SCALE_OUTPUT") ?? "artifacts/player-scale";
        Directory.CreateDirectory(destination);
        string directory = CopySnapshot(source);
        ConcurrentQueue<string> reports = new();
        List<ReplayPlayer> players = [];
        ServiceProvider? host = null;
        Exception? failure = null;
        try
        {
            foreach (string file in new[] { "traffic.sqlite", "world.sqlite.skills.sqlite", "world.sqlite.skills-active" })
            {
                if (File.Exists(Path.Combine(source, file)))
                {
                    File.Copy(Path.Combine(source, file), Path.Combine(directory, file));
                }
            }
            string packages = Path.Combine(source, "world.sqlite.skill-packages");
            if (Directory.Exists(packages))
            {
                foreach (string file in Directory.EnumerateFiles(packages, "*", SearchOption.AllDirectories))
                {
                    string target = Path.Combine(directory, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }
            }
            WorldDatabase database = new(Path.Combine(directory, "world.sqlite"));
            ScaleReplayClock clock = new();
            WorldInitialization.Initialize(database, clock);
            var seeded = PopulatedScaleFixture.Seed(database, template, playerCount);
            host = ServerServices.Build(database, clock, liveWorld: true);
            var live = host.GetRequiredService<LiveWorld>();
            live.Economy.Warm();
            database.Catalog.PreloadCore();
            host.GetRequiredService<Eden.World.Market.PilotMarketStore>().WarmMarketCatalog();
            host.GetRequiredService<Eden.World.Combat.Npcs.NpcPopulationSeeder>().WarmCatalog(database);
            host.GetRequiredService<Eden.Protocol.Industry.EconomicPriceCache>().Warm();
            long admissionStarted = Stopwatch.GetTimestamp();
            foreach (var pilot in seeded)
            {
                var player = new ReplayPlayer(host, clock, pilot.Pilot, pilot.Account, reports, selected: false);
                player.MeasureCounters = false;
                players.Add(player);
                await player.Call("charUnboundMgr", "SelectCharacterID", pilot.Pilot);
                if (!pilot.Docked)
                {
                    await player.Call("beyonce", "MachoBindObject", (long)pilot.System, null);
                    await player.Call("beyonce", "CmdStop");
                }
                if (pilot.Mining)
                {
                    player.LockScaleAsteroid(pilot.Asteroid);
                }
            }
            clock.Advance(TimeSpan.FromSeconds(10));
            for (int index = 0; index < players.Count; index++)
            {
                if (seeded[index].Mining)
                {
                    await players[index].StartScaleMining(seeded[index].Asteroid);
                }
            }
            double admission = Stopwatch.GetElapsedTime(admissionStarted).TotalMilliseconds;
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            var population = host.GetRequiredService<ICapsuleerBackgroundPreparation>();
            var diagnostics = host.GetRequiredService<ICapsuleerDiagnostics>();
            List<ScaleFrame> frames = [];
            List<ScaleFrame> counted = [];
            List<ScaleTurn> turns = [];
            List<string> slow = [];
            ScaleOwnership ownership = new();
            using var observe = live.ObserveOwnership(ownership.Record);
            long measuredStarted = 0;
            long allocated = 0;
            const int Warmup = 3;
            for (int turn = -Warmup; turn < samples; turn++)
            {
                clock.Advance(TimeSpan.FromSeconds(5));
                if (turn == 0)
                {
                    ownership.Clear();
                    measuredStarted = Stopwatch.GetTimestamp();
                    allocated = GC.GetTotalAllocatedBytes(precise: true);
                }
                long started = Stopwatch.GetTimestamp();
                foreach (var player in players)
                {
                    player.MeasureCounters = turn == -1;
                }
                var tasks = players
                    .Select(
                        (player, index) =>
                            Task.Run(async () =>
                            {
                                var pilot = seeded[index];
                                ReplaySample? call = null;
                                if (turn == -1 || turn % 4 == 0)
                                {
                                    call =
                                        pilot.Mining ? await player.ApplyScalePoints()
                                        : pilot.Docked ? await player.Call("station", "GetGuests")
                                        : await player.Call("beyonce", "CmdGotoDirection", 1d, turn % 8 == 0 ? 0d : 1d, 0d);
                                }
                                var frame = await player.Frame();
                                player.AssertScaleMining();
                                return new ScaleFrame(
                                    turn,
                                    pilot.Mining ? "mining"
                                        : pilot.Docked ? "docked"
                                        : "movement",
                                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                                    frame.Frame,
                                    call?.Reply ?? 0,
                                    frame.Sql + (call?.Sql ?? 0),
                                    frame.Connections + (call?.Connections ?? 0),
                                    frame.Fits + (call?.Fits ?? 0)
                                );
                            })
                    )
                    .ToArray();
                var background = Task.Run(() =>
                {
                    using var metrics = SessionPerformance.Begin("scale background", reports.Enqueue, force: turn == -1);
                    var prepared = population.PrepareNext();
                    Assert.Null(prepared.Failure);
                    maintenance.Tick();
                    return new ScaleBackground(metrics?.Commands ?? 0, metrics?.Connections ?? 0, metrics?.FittingCalculations ?? 0);
                });
                Task checkpoint = turn >= 0 && turn % 8 == 0 ? Task.Run(live.FlushAsync) : Task.CompletedTask;
                await Task.WhenAll(tasks.Cast<Task>().Append(background).Append(checkpoint)).WaitAsync(TimeSpan.FromMinutes(10));
                double duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var traffic = diagnostics.Read();
                if (turn == -1)
                {
                    counted.AddRange(await Task.WhenAll(tasks));
                }
                if (turn >= 0)
                {
                    frames.AddRange(await Task.WhenAll(tasks));
                    turns.Add(new(turn, duration, await background, traffic.DueJobs, traffic.CompletedActions, traffic.OldestDelayTicks));
                }
                while (reports.TryDequeue(out string? message))
                {
                    if (turn >= 0 && slow.Count < 200 && message.Contains("latency", StringComparison.OrdinalIgnoreCase))
                    {
                        slow.Add(message);
                    }
                }
                if (turn % 4 == 0)
                {
                    output.WriteLine($"players={playerCount} turn={turn} wall={duration:F2}ms due={traffic.DueJobs}");
                    await File.WriteAllTextAsync(
                        Path.Combine(destination, $"progress-{playerCount}.txt"),
                        $"turn={turn}, wallMs={duration:F2}, due={traffic.DueJobs}, completed={traffic.CompletedActions}"
                    );
                }
            }
            double elapsed = Stopwatch.GetElapsedTime(measuredStarted).TotalSeconds;
            long[] mined = players.Where(player => player.IsScaleMiner).Select(player => player.ScaleMinedOre()).ToArray();
            Assert.All(mined, quantity => Assert.True(quantity > 0, "Each simulated miner must deliver ore."));
            var completion = frames.Select(frame => frame.Completion).Order().ToArray();
            var execution = frames.Select(frame => frame.Execution).Order().ToArray();
            var summary = new
            {
                Players = playerCount,
                Systems = seeded.Select(pilot => pilot.System).Distinct().Count(),
                Mining = seeded.Count(pilot => pilot.Mining),
                Docked = seeded.Count(pilot => pilot.Docked),
                Samples = samples,
                MinedUnits = mined.Sum(),
                AdmissionMs = admission,
                ElapsedSeconds = elapsed,
                FramesPerSecond = frames.Count / elapsed,
                CompletionP50Ms = Percentile(completion, .5),
                CompletionP95Ms = Percentile(completion, .95),
                CompletionP99Ms = Percentile(completion, .99),
                CompletionMaxMs = completion.Max(),
                ExecutionP95Ms = Percentile(execution, .95),
                CountedFrames = counted.Count,
                Sql = counted.Sum(frame => frame.Sql),
                Connections = counted.Sum(frame => frame.Connections),
                Fits = counted.Sum(frame => frame.Fits),
                AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated,
                ManagedBytes = GC.GetTotalMemory(forceFullCollection: false),
                WorkingSetBytes = Environment.WorkingSet,
                Ownership = ownership.Snapshot(),
                Checkpoints = live.RecentCheckpoints,
                Traffic = diagnostics.Read(),
                Frames = frames,
                Counted = counted,
                Turns = turns,
                Slow = slow,
            };
            await File.WriteAllTextAsync(
                Path.Combine(destination, $"players-{playerCount}.json"),
                JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true })
            );
            Assert.Null(live.LastSaveFailure);
            Assert.Equal(0, diagnostics.Read().Failures);
            foreach (var player in players)
            {
                player.Dispose();
            }
            players.Clear();
        }
        catch (Exception error)
        {
            failure = error;
            await File.WriteAllTextAsync(Path.Combine(destination, $"failure-{playerCount}.txt"), error.ToString());
            if (host?.GetService<LiveWorld>() is { } live)
            {
                string diagnosis;
                using (live.EnterShared("scale failure diagnosis"))
                {
                    var systems = players
                        .Where(player => player.Context.Selected)
                        .Select(player => player.Context.Pilot.SystemId)
                        .Distinct();
                    var missing = systems
                        .SelectMany(system => live.CombatForSystem(system)?.CaptureCheckpoint().Positions ?? [])
                        .Where(position => live.Economy.Peek<Eden.Data.World.Inventory.Ship>(position.ShipId) is null)
                        .ToArray();
                    diagnosis = JsonSerializer.Serialize(missing);
                }
                await File.WriteAllTextAsync(Path.Combine(destination, $"missing-ships-{playerCount}.json"), diagnosis);
            }
            throw;
        }
        finally
        {
            if (!reports.IsEmpty)
            {
                await File.WriteAllLinesAsync(Path.Combine(destination, $"diagnostics-{playerCount}.log"), reports);
            }
            foreach (var player in players)
            {
                try
                {
                    player.Dispose();
                }
                catch (Exception error)
                {
                    output.WriteLine($"Cleanup: {error.Message}");
                    failure ??= error;
                }
            }
            try
            {
                host?.Dispose();
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception error) when (failure is not null)
            {
                output.WriteLine($"Cleanup retained {directory}: {error.Message}");
            }
        }
    }

    private sealed record ScaleBackground(int Sql, int Connections, int Fits);

    private sealed record ScaleTurn(int Turn, double Milliseconds, ScaleBackground Background, int Due, long Completed, long OldestDelay);

    private sealed record ScaleFrame(
        int Turn,
        string Kind,
        double Completion,
        double Execution,
        double Reply,
        int Sql,
        int Connections,
        int Fits
    );

    private sealed class ScaleOwnership
    {
        private readonly object _gate = new();
        private long _count;
        private double _wait;
        private OwnershipTiming? _maximumHold;
        private OwnershipTiming? _maximumWait;

        internal void Record(OwnershipTiming timing)
        {
            lock (_gate)
            {
                _count++;
                _wait += timing.Wait.TotalMilliseconds;
                if (_maximumHold is null || timing.Hold > _maximumHold.Hold)
                {
                    _maximumHold = timing;
                }
                if (_maximumWait is null || timing.Wait > _maximumWait.Wait)
                {
                    _maximumWait = timing;
                }
            }
        }

        internal void Clear()
        {
            lock (_gate)
            {
                _count = 0;
                _wait = 0;
                _maximumHold = null;
                _maximumWait = null;
            }
        }

        internal ScaleOwnershipSummary Snapshot()
        {
            lock (_gate)
            {
                return new(_count, _wait, _maximumHold, _maximumWait);
            }
        }
    }

    private sealed record ScaleOwnershipSummary(long Count, double TotalWaitMs, OwnershipTiming? MaximumHold, OwnershipTiming? MaximumWait);

    private sealed partial class ReplayPlayer
    {
        private long? _scaleMiner;
        private long _scaleAsteroid;
        internal bool IsScaleMiner => _scaleMiner is not null;

        internal long ScaleMinedOre() =>
            _scope
                .ServiceProvider.GetRequiredService<IPilotInventory>()
                .List(Context.Pilot.PilotId, Context.Pilot.ShipId)
                .Where(item => item.TypeId == 1230)
                .Sum(item => (long)item.Quantity);

        internal void LockScaleAsteroid(long asteroid)
        {
            Context.Flight.RefreshAsteroids(_scope.ServiceProvider.GetRequiredService<IAsteroidField>().List(Context.Pilot.SystemId));
            _scope.ServiceProvider.GetRequiredService<TargetingHandler>().Call(Context, "dogmaIM", "AddTarget", [asteroid]);
        }

        internal async Task StartScaleMining(long asteroid)
        {
            var targeting = _scope.ServiceProvider.GetRequiredService<TargetingHandler>();
            targeting.Advance(Context);
            Assert.True(targeting.IsLocked(asteroid));
            long module = _scope
                .ServiceProvider.GetRequiredService<IPilotInventory>()
                .List(Context.Pilot.PilotId, Context.Pilot.ShipId)
                .First(item => InventoryDefinitions.IsMiningGroup(item.GroupId) && item.Online)
                .Id;
            await Call("dogmaIM", "Activate", module, "miningLaser", asteroid, 100L);
            _scaleMiner = module;
            _scaleAsteroid = asteroid;
            AssertScaleMining();
        }

        internal void AssertScaleMining()
        {
            if (_scaleMiner is { } module)
            {
                if (!_scope.ServiceProvider.GetRequiredService<MiningHandler>().IsActive(module))
                {
                    var target = Context.Flight.FindTarget(_scaleAsteroid);
                    var diagnosis = _scope
                        .ServiceProvider.GetRequiredService<LiveWorld>()
                        .Economy.Inspect(world => new ScaleMiningStop(
                            Context.Pilot.PilotId,
                            Context.Pilot.ShipId,
                            module,
                            _scope.ServiceProvider.GetRequiredService<TargetingHandler>().IsLocked(_scaleAsteroid),
                            target is null ? null : Context.Pilot.Position?.DistanceTo(target.Position),
                            world.Set<Eden.Data.World.Mining.WorldAsteroid>().Find(_scaleAsteroid)?.Quantity,
                            world
                                .Set<Eden.Data.World.Inventory.OwnedItem>()
                                .Where(item => item.LocationId == Context.Pilot.ShipId && item.TypeId == 1230)
                                .Sum(item => item.Quantity),
                            world.Set<Eden.Data.World.Fittings.ShipEnergy>().Find(Context.Pilot.ShipId)?.Charge
                        ));
                    Assert.Fail(JsonSerializer.Serialize(diagnosis));
                }
            }
        }

        internal async Task<ReplaySample> ApplyScalePoints()
        {
            _arrival = Stopwatch.GetTimestamp();
            _reply = 0;
            var request = Request("skills", "ApplyFreeSkillPointsToQueue");
            var fields = BlueRead.Tuple(request.State, 14);
            fields[2] = new BlueInstance(
                "carbon.common.script.net.machoNetAddress.MachoAddress",
                new object?[] { 1L, (long)Eden.Protocol.PilotSessions.PilotLogin.NodeId, null, null }
            );
            fields[4] = new object?[]
            {
                new object?[]
                {
                    0L,
                    BlueMarshal.Encode(
                        new object?[]
                        {
                            Context.Reference("@skills").Id,
                            "ApplyFreeSkillPointsToQueue",
                            Array.Empty<object?>(),
                            new Dictionary<string, object?>(),
                        }
                    ),
                },
            };
            using var metrics = SessionPerformance.Begin("scale skill application", _reports.Enqueue, force: MeasureCounters);
            await _calls.ProcessAsync(new BlueInstance(request.Name, fields), _arrival);
            Assert.True(_reply > 0);
            return new(
                "skill points",
                _reply,
                Stopwatch.GetElapsedTime(_arrival).TotalMilliseconds,
                metrics?.Commands ?? 0,
                metrics?.Connections ?? 0,
                metrics?.FittingCalculations ?? 0
            );
        }
    }

    private sealed record ScaleMiningStop(
        long Pilot,
        long Ship,
        long Module,
        bool Locked,
        double? Distance,
        long? Remaining,
        int Cargo,
        double? Charge
    );
}
