using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Combat;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Combat;

public sealed class NpcCapacityBenchmarkTests(ITestOutputHelper output)
{
    private const int SystemId = 30000144;
    private const int PlayerType = 601;
    private const int NpcType = 30380;
    private const long FirstAccount = 1300000;
    private const long FirstPilot = 93000000;
    private const long FirstShip = 7300000000000;
    private const long FirstGun = 7400000000000;
    private const double SustainedShield = 1_000_000_000;
    private const int Warmups = 4;
    private const int MinimumSamples = 10;
    private const double StopMilliseconds = 2000;
    private const double Spacing = 400;

    private sealed record Client(IServiceScope Scope, PilotCallContext Context, PilotSessionTick Tick, PilotSessionOutput Sink);

    private sealed record Frame(
        double Milliseconds,
        double CpuMilliseconds,
        long AllocatedBytes,
        int Commands,
        int Connections,
        int Fits,
        long Bytes,
        IReadOnlyDictionary<string, double> Phases
    );

    private sealed record Distribution(double Median, double P95, double Maximum);

    [Fact]
    public async Task MeasureNpcAndPlayerCapacityThroughNormalSessions()
    {
        int npcs = ReadNumber("EDEN_CAPACITY_NPCS", 8, 0, 8000);
        int players = ReadNumber("EDEN_CAPACITY_PLAYERS", 2, 1, 256);
        int frames = ReadNumber("EDEN_CAPACITY_FRAMES", 30, MinimumSamples, 120);
        bool combat = Environment.GetEnvironmentVariable("EDEN_CAPACITY_MODE") == "combat";
        Assert.True(!combat || npcs > 0, "Combat requires at least one NPC target.");
        long setup = Stopwatch.GetTimestamp();
        using NpcCombatTests.Fixture f = new(useTestClock: true);
        var database = f.Db.Services.GetRequiredService<WorldDatabase>();
        PrepareCatalog(database, combat);
        PrepareWorld(f, npcs, players);
        CheckpointClock checkpoints = new(f.Clock);
        using LiveWorld live = new(database, checkpoints, _ => { });
        live.MeasureCheckpoints = true;
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        List<Client> clients = [];
        List<Frame> samples = [];
        long bytes = 0;
        try
        {
            for (int index = 0; index < players; index++)
            {
                var scope = f.Db.Services.CreateScope();
                var services = scope.ServiceProvider;
                var lookup = services.GetRequiredService<IPilotLookup>();
                long account = FirstAccount + index;
                PilotCallContext context = new(
                    account,
                    lookup.ReadAccount(account),
                    lookup.ListPilots(account),
                    services.GetRequiredService<FlightSessionFactory>().Create,
                    account,
                    SignedSettings.Empty
                );
                context.Select(f.Db.Store.FindPilot(account)!);
                Assert.Equal(50, context.Pilot.Flight.ShipRadius);
                Assert.Equal(1500100, context.Pilot.Flight.Mass);
                services.GetRequiredService<PilotUpdateCoordinator>().Enter(context);
                context.Flight.Snapshot();
                PilotSessionOutput sink = new(
                    context,
                    account,
                    account,
                    packet =>
                    {
                        bytes += BlueMarshal.Encode(packet).Length;
                        return Task.CompletedTask;
                    },
                    _ => { }
                );
                PilotSessionTick tick = new(
                    f.Clock,
                    services.GetRequiredService<PilotUpdateCoordinator>(),
                    services.GetRequiredService<SkillHandler>(),
                    services.GetRequiredService<IAsteroidField>(),
                    services.GetRequiredService<IPilotOperationScope>()
                );
                clients.Add(new(scope, context, tick, sink));
            }
            if (combat)
            {
                f.Clock.Seconds += 30;
                foreach (var client in clients)
                {
                    Assert.IsType<PilotCallResult.Success>(
                        client
                            .Scope.ServiceProvider.GetRequiredService<PilotCallRouter>()
                            .Call(client.Context, "dogmaIM", "AddTarget", new object?[] { f.Npc.Id }, _ => { })
                    );
                }
                f.Clock.Seconds += 30;
                foreach (var client in clients)
                {
                    client.Scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>().Advance(client.Context);
                    long module = FirstGun + client.Context.AccountId - FirstAccount;
                    Assert.IsType<PilotCallResult.Success>(
                        client
                            .Scope.ServiceProvider.GetRequiredService<PilotCallRouter>()
                            .Call(client.Context, "dogmaIM", "Activate", new object?[] { module, "targetAttack", f.Npc.Id, 100L }, _ => { })
                    );
                }
            }
            double setupMilliseconds = Stopwatch.GetElapsedTime(setup).TotalMilliseconds;
            using var process = Process.GetCurrentProcess();
            int[] collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            for (int frame = -Warmups; frame < frames; frame++)
            {
                f.Clock.Seconds++;
                checkpoints.FireDueTimers();
                List<string> reports = [];
                long bytesBefore = bytes;
                long allocated = GC.GetTotalAllocatedBytes(precise: true);
                var cpu = process.TotalProcessorTime;
                using var metrics = SessionPerformance.Begin("capacity frame", reports.Add, force: true)!;
                var watch = Stopwatch.StartNew();
                foreach (var client in clients)
                {
                    await client.Tick.AdvanceAsync(
                        client.Context,
                        client.Sink,
                        reports.Add,
                        client.Context.AccountId,
                        client.Context.AccountId
                    );
                }
                watch.Stop();
                if (frame >= 0)
                {
                    Assert.DoesNotContain("combat.project", PhaseTimes(reports).Keys);
                    samples.Add(
                        new(
                            watch.Elapsed.TotalMilliseconds,
                            (process.TotalProcessorTime - cpu).TotalMilliseconds,
                            GC.GetTotalAllocatedBytes(precise: true) - allocated,
                            metrics.Commands,
                            metrics.Connections,
                            metrics.FittingCalculations,
                            bytes - bytesBefore,
                            PhaseTimes(reports)
                        )
                    );
                    if (samples.Count >= MinimumSamples && samples.TakeLast(3).All(s => s.Milliseconds > StopMilliseconds))
                    {
                        break;
                    }
                }
            }
            var current = encounters.List(SystemId, f.Clock.Now);
            Assert.Equal(npcs, current.Count);
            Assert.All(
                current,
                npc =>
                {
                    Assert.Equal(50, npc.Radius);
                    Assert.Equal(1500100, npc.Mass);
                }
            );
            Assert.Equal(combat ? npcs : 0, current.Count(n => n.Weapon?.StartedAt == f.Clock.Now));
            if (combat)
            {
                foreach (var client in clients)
                {
                    long module = FirstGun + client.Context.AccountId - FirstAccount;
                    Assert.NotNull(
                        client.Scope.ServiceProvider.GetRequiredService<CombatHandler>().ActiveEffect(client.Context.Pilot, module)
                    );
                }
            }
            var phaseNames = samples.SelectMany(s => s.Phases.Keys).Distinct().ToArray();
            var phases = phaseNames.ToDictionary(name => name, name => Summarize(samples.Select(s => s.Phases.GetValueOrDefault(name))));
            var isolated = MeasureIsolatedWork(f, clients, npcs, combat);
            await live.FlushAsync();
            using (WorldDbContext saved = new(database.Path))
            {
                Assert.Equal(npcs, saved.Set<WorldNpc>().Count(n => n.SystemId == SystemId && n.DestroyedAt == null));
                Assert.Equal(combat ? npcs : 0, saved.Set<NpcAggression>().Count());
                if (combat)
                {
                    Assert.Equal(npcs, saved.Set<NpcAggression>().Count(a => a.LastShotAt != null));
                }
            }
            process.Refresh();
            var report = new
            {
                revision = Environment.GetEnvironmentVariable("EDEN_CAPACITY_REVISION"),
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                npcs,
                players,
                mode = combat ? "combat" : "idle",
                measuredFrames = samples.Count,
                setupMilliseconds,
                frame = Summarize(samples.Select(s => s.Milliseconds)),
                cpu = Summarize(samples.Select(s => s.CpuMilliseconds)),
                meanCommands = samples.Average(s => s.Commands),
                meanConnections = samples.Average(s => s.Connections),
                meanFits = samples.Average(s => s.Fits),
                meanAllocatedBytes = samples.Average(s => s.AllocatedBytes),
                meanEncodedBytes = samples.Average(s => s.Bytes),
                workingSetBytes = process.WorkingSet64,
                collections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray(),
                phases,
                isolated,
                checkpoints = live.RecentCheckpoints,
                samples,
            };
            string json = JsonSerializer.Serialize(report);
            output.WriteLine("NPC_CAPACITY=" + json);
            if (Environment.GetEnvironmentVariable("EDEN_CAPACITY_OUTPUT") is { } destination)
            {
                Directory.CreateDirectory(destination);
                await File.WriteAllTextAsync(Path.Combine(destination, $"{players}p-{npcs}n-{(combat ? "combat" : "idle")}.json"), json);
            }
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Context.Dispose();
                client.Scope.Dispose();
            }
        }
    }

    private static int ReadNumber(string name, int fallback, int minimum, int maximum)
    {
        int value = int.Parse(
            Environment.GetEnvironmentVariable(name) ?? fallback.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture
        );
        Assert.InRange(value, minimum, maximum);
        return value;
    }

    private static Distribution Summarize(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return new(sorted[sorted.Length / 2], sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1], sorted[^1]);
    }

    private static Dictionary<string, double> PhaseTimes(IEnumerable<string> reports)
    {
        Dictionary<string, double> phases = [];
        foreach (string report in reports)
        {
            var match = Regex.Match(report, @"^Performance (.+): ([0-9.]+) ms");
            if (match.Success)
            {
                string name = match.Groups[1].Value;
                phases[name] = phases.GetValueOrDefault(name) + double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            }
        }
        return phases;
    }

    private static void PrepareCatalog(WorldDatabase database, bool combat)
    {
        using CatalogDbContext catalog = new(database.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            UPDATE TypeAttribute SET Value=1000000000 WHERE AttributeId=263 AND ItemTypeId IN (601,30380);
            UPDATE ItemType SET Mass=1500100,Radius=50 WHERE Id=601;
            INSERT OR REPLACE INTO TypeAttribute VALUES (3638,51,1000),(3638,54,50000),(3638,6,0),
                (601,76,100000),(30380,51,1000),(30380,54,50000),(30380,247,50000),(30380,475,0);
            """
        );
        if (!combat)
        {
            catalog.Database.ExecuteSqlRaw("DELETE FROM TypeAttribute WHERE ItemTypeId=30380 AND AttributeId=475");
        }
    }

    private static void PrepareWorld(NpcCombatTests.Fixture fixture, int npcs, int players)
    {
        using WorldDbContext world = new(fixture.Db.Path);
        using var transaction = world.Database.BeginTransaction();
        world.Database.ExecuteSqlRaw("PRAGMA defer_foreign_keys=ON");
        world.Set<WorldNpc>().Where(n => n.Id != fixture.Npc.Id).ExecuteDelete();
        var target = world.Set<WorldNpc>().Find(fixture.Npc.Id)!;
        var origin = fixture.Npc.Position;
        for (int index = 0; index < npcs; index++)
        {
            var npc = index == 0 ? target : new WorldNpc { SystemId = SystemId, TypeId = NpcType };
            // A non-overlapping lattice keeps every entity on the same visible grid.
            var position = origin + new SpacePosition(1000 + index % 20 * Spacing, index / 20 % 20 * Spacing, index / 400 * Spacing);
            npc.X = position.X;
            npc.HomeX = position.X;
            npc.Y = position.Y;
            npc.HomeY = position.Y;
            npc.Z = position.Z;
            npc.HomeZ = position.Z;
            npc.ShieldCharge = SustainedShield;
            npc.UpdatedAt = fixture.Clock.Now;
            if (index > 0)
            {
                world.Add(npc);
            }
        }
        if (npcs == 0)
        {
            world.Remove(target);
        }
        for (int index = 0; index < players; index++)
        {
            long account = FirstAccount + index;
            long pilot = FirstPilot + index;
            long ship = FirstShip + index;
            long gun = FirstGun + index;
            world.Database.ExecuteSql($"INSERT INTO WorldAccount(Id,UserName) VALUES({account},{"Capacity " + index})");
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldShip(Id,OwnerId,TypeId,StationId,Name) VALUES({ship},{pilot},{PlayerType},60003760,{"Capacity " + index});
                """
            );
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldPilot(Id,AccountId,Name,CorporationId,ActiveShipId) VALUES({pilot},{account},{"Capacity "
                    + index},1000044,{ship});
                """
            );
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldPilotSkill SELECT {pilot},TypeId,Points FROM WorldPilotSkill
                    WHERE PilotId={NpcCombatTests.Fixture.PilotId}
                """
            );
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldItem(Id,OwnerId,TypeId,LocationId,FlagId,Quantity,Singleton,Online)
                VALUES({gun},{pilot},{(int)CombatWeaponType.CivilianGatlingRailgun},{ship},{(int)InventoryFlag.HighSlot0},1,1,1);
                """
            );
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldPosition(ShipId,SystemId,X,Y,Z)
                VALUES({ship},{SystemId},{origin.X - 1000 - index % 20 * Spacing},{origin.Y + index / 20 * Spacing},{origin.Z});
                """
            );
        }
        world.SaveChanges();
        transaction.Commit();
        // The fixture seeder otherwise creates its two belt NPCs on a later contact read.
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw("DELETE FROM Celestial WHERE SolarSystemId=30000144 AND GroupId=9");
    }

    private object MeasureIsolatedWork(NpcCombatTests.Fixture fixture, IReadOnlyList<Client> clients, int npcs, bool combat)
    {
        var encounters = fixture.Db.Services.GetRequiredService<INpcEncounters>();
        var contacts = encounters.List(SystemId, fixture.Clock.Now);
        var bodies = contacts
            .Select(n => new CollisionMotion(
                n.Id,
                new(0, n.Radius, n.Mass, n.Agility, n.MaxSpeed, 0),
                n.Position,
                SpacePosition.Zero,
                n.Position,
                SpacePosition.Zero
            ))
            .ToArray();
        var collisions = new List<double>();
        var views = new List<double>();
        for (int sample = -1; sample < 10; sample++)
        {
            var watch = Stopwatch.StartNew();
            var resolved = ShipCollisions.Resolve(bodies);
            double collision = watch.Elapsed.TotalMilliseconds;
            Assert.Equal(npcs, resolved.Count);
            watch.Restart();
            foreach (var client in clients)
            {
                client.Context.Flight.RefreshNpcs();
            }
            double view = watch.Elapsed.TotalMilliseconds;
            if (sample >= 0)
            {
                collisions.Add(collision);
                views.Add(view);
            }
        }
        var npcCombat = fixture.Db.Services.GetRequiredService<INpcCombat>();
        var weapons = fixture.Db.Services.GetRequiredService<IPilotCombat>();
        var candidates = clients.Select(c => new NpcTargetCandidate(c.Context.Pilot.PilotId, c.Context.Pilot.ShipId)).ToArray();
        IReadOnlyList<ShipMotion> ships = clients
            .Select(c => new ShipMotion(c.Context.Pilot.ShipId, SystemId, 0, c.Context.Pilot.Position!, c.Context.Pilot.Flight, new()))
            .ToArray();
        List<double> simulation = [];
        int commands = 0;
        int connections = 0;
        int fits = 0;
        for (int sample = -1; sample < 10; sample++)
        {
            fixture.Clock.Seconds++;
            int stamp = checked((int)fixture.Clock.Seconds);
            using var metrics = SessionPerformance.Begin("isolated simulation", _ => { }, force: true)!;
            var watch = Stopwatch.StartNew();
            ships = fixture.Db.Store.Advance(SystemId, stamp, fixture.Clock.Now, ships, []).Select(s => s.Ship).ToArray();
            npcCombat.Acquire(candidates, fixture.Clock.Now);
            if (combat)
            {
                foreach (var client in clients)
                {
                    long module = FirstGun + client.Context.AccountId - FirstAccount;
                    weapons.Fire(client.Context.Pilot.PilotId, module, fixture.Npc.Id, SpacePosition.Zero, fixture.Clock.Now, 0.5);
                    foreach (long npc in npcCombat.Aggressors(client.Context.Pilot.ShipId))
                    {
                        npcCombat.Fire(
                            client.Context.Pilot.PilotId,
                            client.Context.Pilot.ShipId,
                            npc,
                            SpacePosition.Zero,
                            fixture.Clock.Now,
                            0.5,
                            stamp
                        );
                    }
                }
            }
            watch.Stop();
            if (sample >= 0)
            {
                simulation.Add(watch.Elapsed.TotalMilliseconds);
                commands += metrics.Commands;
                connections += metrics.Connections;
                fits += metrics.FittingCalculations;
            }
        }
        Assert.Equal(0, commands);
        Assert.Equal(0, connections);
        Assert.Equal(0, fits);
        return new
        {
            collision = Summarize(collisions),
            observerRefresh = Summarize(views),
            simulation = Summarize(simulation),
            commands,
            connections,
            fits,
        };
    }
}
