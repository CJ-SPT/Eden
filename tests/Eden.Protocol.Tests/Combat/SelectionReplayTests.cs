using System.Collections.Concurrent;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Exploration;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Server;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class ServerPerformanceReplayTests
{
    [Fact]
    public async Task ReplaySavedCharacterSelectionAndFirstTicks()
    {
        string? source = Environment.GetEnvironmentVariable("EDEN_SELECTION_SOURCE");
        if (source is null)
        {
            output.WriteLine("Set EDEN_SELECTION_SOURCE to a frozen world and catalog to run the selection replay.");
            return;
        }
        string directory = CopySnapshot(source);
        ConcurrentQueue<string> reports = new();
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
            if (File.Exists(Path.Combine(source, "world.sqlite.skills-active")))
            {
                packages = Path.Combine(packages, File.ReadAllText(Path.Combine(source, "world.sqlite.skills-active")).Trim());
            }
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
            TestClock clock = new(new DateTimeOffset(2026, 11, 14, 6, 46, 0, TimeSpan.Zero));
            WorldInitialization.Initialize(database, clock);
            long[] ids = (Environment.GetEnvironmentVariable("EDEN_SELECTION_PILOTS") ?? "90023327,90001835,90023328")
                .Split(',')
                .Select(long.Parse)
                .ToArray();
            Dictionary<long, long> accounts;
            using (WorldDbContext world = new(database.Path))
            {
                accounts = world.Pilots.Where(p => ids.Contains(p.Id)).ToDictionary(p => p.Id, p => p.AccountId);
            }
            Assert.Equal(ids.Length, accounts.Count);
            using var host = ServerServices.Build(database, clock, liveWorld: true);
            await Task.WhenAll(
                ids.Select(id =>
                    Task.Run(async () =>
                    {
                        using var scope = host.CreateScope();
                        var services = scope.ServiceProvider;
                        long account = accounts[id];
                        var lookup = services.GetRequiredService<IPilotLookup>();
                        using var performance = SessionPerformance.Begin($"selection replay {id}", reports.Enqueue, force: true);
                        using PilotCallContext context = new(
                            account,
                            lookup.ReadAccount(account),
                            lookup.ListPilots(account),
                            services.GetRequiredService<FlightSessionFactory>().Create,
                            account,
                            SignedSettings.Empty
                        );
                        PilotSessionOutput sink = new(
                            context,
                            account,
                            account,
                            packet =>
                            {
                                var decoded = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(packet)));
                                Assert.False(decoded.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
                                return Task.CompletedTask;
                            },
                            reports.Enqueue
                        );
                        var updates = services.GetRequiredService<PilotUpdateCoordinator>();
                        var skills = services.GetRequiredService<SkillHandler>();
                        var operations = services.GetRequiredService<IPilotOperationScope>();
                        var handoff = services.GetRequiredService<ICapsuleerHandoff>();
                        PilotSessionCalls calls = new(
                            context,
                            sink,
                            services.GetRequiredService<PilotCallRouter>(),
                            updates,
                            skills,
                            clock,
                            reports.Enqueue,
                            account,
                            account,
                            operations,
                            handoff: handoff
                        );
                        PilotSessionTick tick = new(
                            clock,
                            updates,
                            skills,
                            services.GetRequiredService<IAsteroidField>(),
                            operations,
                            anomalies: services.GetRequiredService<CombatAnomalyHandler>(),
                            handoff: handoff
                        );
                        await calls.ProcessAsync(Request("charUnboundMgr", "SelectCharacterID", id));
                        Assert.True(context.Selected);
                        for (int sample = 0; sample < 5; sample++)
                        {
                            using var frame = SessionPerformance.Begin($"pilot {id} frame {sample}", reports.Enqueue, force: true);
                            await tick.AdvanceAsync(context, sink, reports.Enqueue, account, account);
                        }
                    })
                )
            );
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("EDEN_SELECTION_OUTPUT") is { } destination)
            {
                Directory.CreateDirectory(destination);
                File.WriteAllLines(Path.Combine(destination, "selection.log"), reports);
            }
            Directory.Delete(directory, recursive: true);
        }
    }
}
