using System.Diagnostics;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Mining;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Server;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Combat;

public sealed class ServerPerformanceReplayTests(ITestOutputHelper output)
{
    private const long Account = 1000001;
    private const long PilotId = 90000001;
    private const long Module = 8000000000001;
    private const int Samples = 20;

    [Theory]
    [InlineData("docked")]
    [InlineData("space")]
    [InlineData("combat")]
    public async Task ReplayIsolatedFullCatalogSession(string scenario)
    {
        string? source = Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_SOURCE");
        if (source is null)
        {
            output.WriteLine("Set EDEN_PERFORMANCE_SOURCE to an isolated snapshot directory containing world.sqlite and catalog.sqlite.");
            return;
        }
        string directory = TestDirectory.Create("eden-performance-").FullName;
        try
        {
            foreach (string file in new[] { "world.sqlite", "catalog.sqlite" })
            {
                using SqliteConnection original = new(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = Path.Combine(source, file),
                        Mode = SqliteOpenMode.ReadOnly,
                        Pooling = false,
                    }.ToString()
                );
                using SqliteConnection copy = new($"Data Source={Path.Combine(directory, file)};Pooling=False");
                original.Open();
                copy.Open();
                original.BackupDatabase(copy);
            }
            TestClock clock = new(new DateTimeOffset(2026, 11, 5, 5, 0, 0, TimeSpan.Zero));
            using var services = ServerServices.Build(new(Path.Combine(directory, "world.sqlite")), clock);
            var store = services.GetRequiredService<SqliteWorld>();
            WorldNpc target;
            using (WorldDbContext world = new(Path.Combine(directory, "world.sqlite")))
            {
                target = world.Set<WorldNpc>().First(n => n.Role == NpcRole.Pirate && n.DestroyedAt == null && n.RetiredAt == null);
                var pilot = world.Pilots.Single(p => p.Id == PilotId);
                world
                    .Set<OwnedItem>()
                    .Add(
                        new OwnedItem
                        {
                            Id = Module,
                            OwnerId = PilotId,
                            TypeId = 3638,
                            LocationId = pilot.ActiveShipId,
                            FlagId = 27,
                            Quantity = 1,
                            Singleton = true,
                            Online = true,
                        }
                    );
                world.SaveChanges();
            }
            var selected = store.FindPilot(Account)!;
            if (scenario == "combat")
            {
                // Keep all measured samples in combat. The copied catalog only changes durability.
                using CatalogDbContext catalog = new(Path.Combine(directory, "catalog.sqlite"), writable: true);
                catalog.Database.ExecuteSql(
                    $"""
                    UPDATE TypeAttribute SET Value={1000000000d} WHERE AttributeId=263
                        AND ItemTypeId IN ({selected.ShipTypeId},{target.TypeId})
                    """
                );
                using WorldDbContext health = new(Path.Combine(directory, "world.sqlite"));
                health.Set<ShipHealthState>().Where(h => h.ShipId == selected.ShipId).ExecuteDelete();
                health.Add(
                    new ShipHealthState
                    {
                        ShipId = selected.ShipId,
                        ShieldCharge = 1000000000d,
                        UpdatedAt = clock.Now,
                    }
                );
                health
                    .Set<WorldNpc>()
                    .Where(n => n.Id == target.Id)
                    .ExecuteUpdate(set => set.SetProperty(n => n.ShieldCharge, 1000000000d));
                health.SaveChanges();
            }
            store.SetPosition(
                selected.ShipId,
                scenario == "docked" ? null : new(target.X + (scenario == "combat" ? 1500 : 2000000), target.Y, target.Z),
                target.SystemId
            );
            using var scope = services.CreateScope();
            var provider = scope.ServiceProvider;
            var pilots = provider.GetRequiredService<IPilotLookup>();
            List<string> reports = [];
            using PilotCallContext context = new(
                Account,
                pilots.ReadAccount(Account),
                pilots.ListPilots(Account),
                provider.GetRequiredService<FlightSessionFactory>().Create,
                42,
                SignedSettings.Empty
            );
            context.Select(store.FindPilot(Account)!);
            var updates = provider.GetRequiredService<PilotUpdateCoordinator>();
            var skills = provider.GetRequiredService<SkillHandler>();
            PilotSessionOutput sessionOutput = new(context, Account, 42, _ => Task.CompletedTask, reports.Add);
            PilotSessionCalls calls = new(
                context,
                sessionOutput,
                provider.GetRequiredService<PilotCallRouter>(),
                updates,
                skills,
                clock,
                reports.Add,
                Account,
                42
            );
            PilotSessionTick tick = new(clock, updates, skills, provider.GetRequiredService<IAsteroidField>());
            using (SessionPerformance.Begin($"{scenario} cold", reports.Add, force: true))
            {
                updates.Enter(context);
                if (scenario != "docked")
                {
                    context.Flight.Snapshot();
                }
                await tick.AdvanceAsync(context, sessionOutput, reports.Add, Account, 42);
            }
            clock.Seconds += 10;
            if (scenario == "combat")
            {
                var router = provider.GetRequiredService<PilotCallRouter>();
                Assert.IsType<PilotCallResult.Success>(
                    router.Call(context, "dogmaIM", "AddTarget", new object?[] { target.Id }, reports.Add)
                );
                clock.Seconds += 30;
                updates.Advance(context);
                Assert.IsType<PilotCallResult.Success>(
                    router.Call(context, "dogmaIM", "Activate", new object?[] { Module, "targetAttack", target.Id, 100L }, reports.Add)
                );
            }
            List<double> ticks = [];
            List<double> replies = [];
            for (int sample = 0; sample < Samples; sample++)
            {
                clock.Seconds++;
                long start = Stopwatch.GetTimestamp();
                using (SessionPerformance.Begin($"{scenario} sample {sample}", reports.Add, force: true))
                {
                    await tick.AdvanceAsync(context, sessionOutput, reports.Add, Account, 42);
                    ticks.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    start = Stopwatch.GetTimestamp();
                    await calls.ProcessAsync(Request("config", "GetMultiLocationsEx", new List<object?> { target.Id }));
                    replies.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
            }
            if (scenario == "combat")
            {
                Assert.Equal(selected.ShipId, context.Pilot.ShipId);
                Assert.NotNull(provider.GetRequiredService<CombatHandler>().ActiveEffect(context.Pilot, Module));
            }
            foreach (var (name, values) in new[] { ("ticks", ticks), ("replies", replies) })
            {
                var sorted = values.Order().ToArray();
                reports.Add($"{scenario} {name} median={sorted[Samples / 2]:F2}ms p95={sorted[(int)(Samples * 0.95) - 1]:F2}ms");
            }
            foreach (string report in reports)
            {
                output.WriteLine(report);
            }
            if (Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_OUTPUT") is { } destination)
            {
                Directory.CreateDirectory(destination);
                File.WriteAllLines(Path.Combine(destination, scenario + ".log"), reports);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static BlueInstance Request(string service, string method, params object?[] arguments)
    {
        const string address = "carbon.common.script.net.machoNetAddress.MachoAddress";
        return new(
            "carbon.common.script.net.machoNetPacket.CallReq",
            new object?[]
            {
                6L,
                new BlueInstance(address, new object?[] { 2L, 0L, 1L, null }),
                new BlueInstance(address, new object?[] { 8L, service, null }),
                null,
                new object?[]
                {
                    new object?[] { 0L, BlueMarshal.Encode(new object?[] { null, method, arguments, new Dictionary<string, object?>() }) },
                },
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
            }
        );
    }
}
