using System.Collections.Concurrent;
using System.Text.Json;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class ServerPerformanceReplayTests
{
    [Fact]
    public async Task ReplaySavedFramesAndFactoryTradesWithoutSqlAfterStartup()
    {
        if (Environment.GetEnvironmentVariable("EDEN_GAMEPLAY_ZERO_SQL_SOURCE") is not { } source)
        {
            output.WriteLine("Set EDEN_GAMEPLAY_ZERO_SQL_SOURCE to the frozen factory-courier incident checkpoint.");
            return;
        }

        const long observerPilot = 90000001;
        const long observerAccount = 1000002;
        const long courier = 90003982;
        TestClock clock = new(new DateTimeOffset(2026, 11, 23, 5, 14, 0, TimeSpan.Zero));
        List<ReplaySample> samples = [];
        List<TrafficActionTiming> trades = [];
        ConcurrentQueue<string> trace = new();
        ConcurrentDictionary<string, int> commands = new(StringComparer.Ordinal);
        string directory = CopySnapshot(source);
        try
        {
            CopyThroughputSidecars(source, directory, null);
            WorldDatabase database = new(Path.Combine(directory, "world.sqlite"));
            WorldInitialization.Initialize(database, clock);
            using (WorldDbContext rehearsal = new(database.Path))
            {
                Assert.Equal(CapsuleerAction.Trade, rehearsal.Set<SavedCapsuleerJob>().Find(courier)?.Action);
                // Keep the courier's saved work, deadlines, and economy. Move unrelated jobs off the measured turn.
                long defer = TimeSpan.FromDays(45).Ticks;
                rehearsal.Database.ExecuteSql($"UPDATE WorldCapsuleerJob SET DueTicks=DueTicks+{defer} WHERE PilotId<>{courier}");
            }

            using var host = ServerServices.Build(database, clock, liveWorld: true);
            ServerStartup.WarmCatalogs(host, database);
            var live = host.GetRequiredService<LiveWorld>();
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            var presence = host.GetRequiredService<IHumanPresence>();
            var coldCourier = Assert.IsType<HaulerObservation>(scheduler.Hauler(courier));

            // Include both foreground requests and traffic preparation in the SQL audit. Checkpointing follows separately.
            using (SessionPerformance.Begin("gameplay after catalog startup", trace.Enqueue, force: true, command: RecordCommand))
            using (GameplaySqlGuard.Begin())
            {
                live.TrafficActions.ReportedTradePilots = new HashSet<long> { courier };
                using (var observed = presence.Select(observerPilot, coldCourier.Pilot.SystemId))
                {
                    for (int turn = 0; turn < 200 && trades.Count < 2; turn++)
                    {
                        var job = live.CapsuleerJobs[courier];
                        long ticks = job.DueTicks - presence.Read().ElapsedTicks;
                        if (ticks > 0)
                        {
                            clock.Milliseconds += (ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond;
                        }
                        using var scope = new CatalogReadScope(database).Begin();
                        _ = scheduler.RunMaintenance(turn, 1, CancellationToken.None);
                        var next = live.CapsuleerJobs[courier];
                        if (next != job)
                        {
                            trace.Enqueue($"Courier turn {turn}: {job} -> {next}");
                            if (live.TrafficActions.ReportedTrades.LastOrDefault(timing => timing.Job == job) is { } trade)
                            {
                                trades.Add(trade);
                            }
                        }
                        else
                        {
                            clock.Milliseconds++;
                        }
                    }
                    trace.Enqueue($"Courier settled: {live.CapsuleerJobs[courier]}; queue head {scheduler.Jobs().FirstOrDefault()}");
                    Assert.Equal(2, trades.Count);
                    var settled = Assert.IsType<HaulerObservation>(scheduler.Hauler(courier));
                    Assert.Equal(CapsuleerAction.Arrival, live.CapsuleerJobs[courier].Action);
                    Assert.Equal(60006511, settled.Work.CorporationSupply?.FactorySupply?.SupplierStationId);
                    Assert.Equal(new[] { 30000903, 30000905, 30002510, 30002187, 30002988, 30002986 }, settled.Work.Journey?.Systems);
                }

                using var player = new ReplayPlayer(host, clock, observerPilot, observerAccount, trace);
                for (int sample = 0; sample < 6; sample++)
                {
                    samples.Add(await player.Frame());
                    samples.Add(await player.Call("marketProxy", "GetRegionBest"));
                    clock.Milliseconds += 1000;
                }
            }

            Assert.All(samples, sample => Assert.Equal((0, 0), (sample.Sql, sample.Connections)));
            Assert.All(trades, trade => Assert.Equal((0, 0), (trade.Total.Commands, trade.Total.Connections)));
            Assert.Contains(trades[1].Phases, phase => phase.Name == "traffic.findPlanetarySite");
            Assert.Empty(commands);
            await live.FlushAsync();
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("EDEN_GAMEPLAY_ZERO_SQL_OUTPUT") is { } destination)
            {
                Directory.CreateDirectory(destination);
                await File.WriteAllTextAsync(
                    Path.Combine(destination, "zero-sql-gameplay.json"),
                    JsonSerializer.Serialize(
                        new
                        {
                            Samples = samples,
                            Trades = trades,
                            Commands = commands,
                        },
                        new JsonSerializerOptions { WriteIndented = true }
                    )
                );
                await File.WriteAllLinesAsync(Path.Combine(destination, "zero-sql-gameplay.log"), trace);
            }
            Directory.Delete(directory, recursive: true);
        }

        void RecordCommand(string sql) => commands.AddOrUpdate(sql, 1, static (_, count) => count + 1);
    }
}
