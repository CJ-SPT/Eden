using System.Diagnostics;
using Eden.Data.Catalog;
using Eden.Data.Combat;
using Eden.Data.Combat.Npcs;
using Eden.Data.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Combat;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class ServerPerformanceReplayTests(ITestOutputHelper output)
{
    private const long Module = 8000000000001;
    private const int Samples = 20;
    private const int HeavyElectronBlasterTwoType = 3130;

    [Fact]
    public void MeasureSavedFitting()
    {
        string? source = Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_SOURCE");
        if (source is null)
        {
            return;
        }
        long pilot = long.Parse(Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_PILOT") ?? "90000001");
        string directory = CopySnapshot(source);
        try
        {
            WorldInitialization.Initialize(new(Path.Combine(directory, "world.sqlite")));
            using var services = ServerServices.Build(new(Path.Combine(directory, "world.sqlite")));
            var fitting = services.GetRequiredService<IPilotFitting>();
            List<string> reports = [];
            IReadOnlyDictionary<Eden.Gameplay.Dogma.DogmaAttributeId, double>? previous = null;
            for (int sample = 0; sample < 10; sample++)
            {
                using var metrics = SessionPerformance.Begin($"fitting {pilot} sample {sample}", reports.Add, force: true);
                var attributes = fitting.ShipAttributes(pilot);
                if (previous is not null)
                {
                    Assert.Equal(previous.OrderBy(p => p.Key), attributes.OrderBy(p => p.Key));
                }
                previous = attributes;
            }
            if (Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_OUTPUT") is { } destination)
            {
                Directory.CreateDirectory(destination);
                File.WriteAllLines(Path.Combine(destination, "fitting.log"), reports);
                File.WriteAllLines(
                    Path.Combine(destination, "fitting-values.log"),
                    previous!.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value:R}")
                );
            }
            foreach (string report in reports)
            {
                output.WriteLine(report);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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
        long pilotId = long.Parse(Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_PILOT") ?? "90000001");
        long account;
        string directory = CopySnapshot(source);
        try
        {
            TestClock clock = new(new DateTimeOffset(2026, 11, 6, 5, 0, 0, TimeSpan.Zero));
            WorldInitialization.Initialize(new(Path.Combine(directory, "world.sqlite")), clock);
            using var services = ServerServices.Build(new(Path.Combine(directory, "world.sqlite")), clock);
            var store = services.GetRequiredService<SqliteWorld>();
            WorldNpc target;
            using (WorldDbContext world = new(Path.Combine(directory, "world.sqlite")))
            {
                target = world.Set<WorldNpc>().First(n => n.Role == NpcRole.Pirate && n.DestroyedAt == null && n.RetiredAt == null);
                var pilot = world.Pilots.Single(p => p.Id == pilotId);
                account = pilot.AccountId;
                var flags = world.Set<OwnedItem>().Where(i => i.LocationId == pilot.ActiveShipId).Select(i => i.FlagId).ToArray();
                world
                    .Set<OwnedItem>()
                    .Add(
                        new OwnedItem
                        {
                            Id = Module,
                            OwnerId = pilotId,
                            TypeId = 3638,
                            LocationId = pilot.ActiveShipId,
                            FlagId = Enumerable.Range(27, 8).First(flag => !flags.Contains(flag)),
                            Quantity = 1,
                            Singleton = true,
                            Online = true,
                        }
                    );
                world.SaveChanges();
            }
            var selected = store.FindPilot(account, pilotId)!;
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
                account,
                pilots.ReadAccount(account),
                pilots.ListPilots(account),
                provider.GetRequiredService<FlightSessionFactory>().Create,
                42,
                SignedSettings.Empty
            );
            context.Select(store.FindPilot(account, pilotId)!);
            var updates = provider.GetRequiredService<PilotUpdateCoordinator>();
            var skills = provider.GetRequiredService<SkillHandler>();
            PilotSessionOutput sessionOutput = new(context, account, 42, _ => Task.CompletedTask, reports.Add);
            PilotSessionCalls calls = new(
                context,
                sessionOutput,
                provider.GetRequiredService<PilotCallRouter>(),
                updates,
                skills,
                clock,
                reports.Add,
                account,
                42,
                provider.GetRequiredService<IPilotOperationScope>()
            );
            PilotSessionTick tick = new(
                clock,
                updates,
                skills,
                provider.GetRequiredService<IAsteroidField>(),
                provider.GetRequiredService<IPilotOperationScope>()
            );
            using (SessionPerformance.Begin($"{scenario} cold", reports.Add, force: true))
            {
                updates.Enter(context);
                if (scenario != "docked")
                {
                    context.Flight.Snapshot();
                }
                await tick.AdvanceAsync(context, sessionOutput, reports.Add, account, 42);
            }
            long[] repeatedBlasters = [];
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
                using WorldDbContext saved = new(Path.Combine(directory, "world.sqlite"));
                repeatedBlasters = saved
                    .Set<OwnedItem>()
                    .Where(i => i.LocationId == selected.ShipId && i.Online && i.TypeId == HeavyElectronBlasterTwoType)
                    .Select(i => i.Id)
                    .ToArray();
                foreach (long blaster in repeatedBlasters)
                {
                    var result = router.Call(
                        context,
                        "dogmaIM",
                        "Activate",
                        new object?[] { blaster, "projectileFired", target.Id, 100L },
                        reports.Add
                    );
                    Assert.True(result is PilotCallResult.Success, result.ToString());
                }
            }
            List<double> ticks = [];
            List<double> replies = [];
            for (int sample = 0; sample < Samples; sample++)
            {
                clock.Seconds++;
                long start = Stopwatch.GetTimestamp();
                using (var measurement = SessionPerformance.Begin($"{scenario} sample {sample}", reports.Add, force: true)!)
                {
                    await tick.AdvanceAsync(context, sessionOutput, reports.Add, account, 42);
                    ticks.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    start = Stopwatch.GetTimestamp();
                    await calls.ProcessAsync(Request("config", "GetMultiLocationsEx", new List<object?> { target.Id }));
                    replies.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    if (sample >= 2 && scenario == "combat")
                    {
                        Assert.Equal(0, measurement.FittingCalculations);
                    }
                }
            }
            if (scenario == "combat")
            {
                Assert.Equal(selected.ShipId, context.Pilot.ShipId);
                Assert.NotNull(provider.GetRequiredService<CombatHandler>().ActiveEffect(context.Pilot, Module));
                foreach (long blaster in repeatedBlasters)
                {
                    Assert.NotNull(provider.GetRequiredService<CombatHandler>().ActiveEffect(context.Pilot, blaster));
                }
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

    private static string CopySnapshot(string source)
    {
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
            return directory;
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
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
