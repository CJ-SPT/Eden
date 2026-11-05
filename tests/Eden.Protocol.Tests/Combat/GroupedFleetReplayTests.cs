using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class ServerPerformanceReplayTests
{
    private sealed record FleetClient(
        IServiceScope Scope,
        PilotCallContext Context,
        PilotSessionTick Tick,
        PilotSessionOutput Sink,
        long Master,
        long[] Guns
    );

    private sealed record FleetSample(SessionPerformance Metrics, IReadOnlyList<string> Reports);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TenBrutixesFireSixGunBanksOnOneGrid(bool concurrent)
    {
        string? source = Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_SOURCE");
        if (source is null)
        {
            output.WriteLine("Set EDEN_PERFORMANCE_SOURCE to the full saved-fit snapshot.");
            return;
        }
        const long templatePilot = 90000006;
        const int pilots = 10;
        const int frames = 30;
        const int brutixNavyIssue = 33151;
        const double sustainedShieldCapacity = 1_000_000_000;
        string mode = concurrent ? "concurrent" : "serial";
        int shieldCapacity = (int)DogmaAttributeId.ShieldCapacity;
        long setupStarted = Stopwatch.GetTimestamp();
        string directory = CopySnapshot(source);
        List<FleetClient> clients = [];
        ConcurrentQueue<string> reports = new();
        try
        {
            WorldDatabase database = new(Path.Combine(directory, "world.sqlite"));
            TestClock clock = new(new DateTimeOffset(2026, 11, 6, 5, 0, 0, TimeSpan.Zero));
            WorldInitialization.Initialize(database, clock);
            using (CatalogDbContext catalog = new(database.CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSql(
                    $"""
                    UPDATE TypeAttribute SET Value={sustainedShieldCapacity}
                        WHERE ItemTypeId={brutixNavyIssue} AND AttributeId={shieldCapacity}
                    """
                );
            }
            WorldNpc target;
            using (WorldDbContext world = new(database.Path))
            using (var tx = world.Database.BeginTransaction())
            {
                world.Database.ExecuteSqlRaw("PRAGMA defer_foreign_keys=ON");
                var basis = world.Pilots.AsNoTracking().Include(p => p.ActiveShip).Single(p => p.Id == templatePilot);
                Assert.Equal(brutixNavyIssue, basis.ActiveShip.TypeId);
                var account = world.Set<Account>().AsNoTracking().Single(a => a.Id == basis.AccountId);
                var items = world.Set<OwnedItem>().AsNoTracking().Where(i => i.LocationId == basis.ActiveShipId).ToArray();
                var ids = items.Select(i => i.Id).ToArray();
                var charges = world.Set<OwnedItem>().AsNoTracking().Where(i => ids.Contains(i.LocationId)).ToArray();
                var skills = world.Set<PilotSkill>().AsNoTracking().Where(s => s.PilotId == templatePilot).ToArray();
                target = world.Set<WorldNpc>().First(n => n.Role == NpcRole.Pirate && n.DestroyedAt == null && n.RetiredAt == null);
                int targetType = target.TypeId;
                using (CatalogDbContext catalog = new(database.CatalogPath, writable: true))
                {
                    catalog.Database.ExecuteSql(
                        $"""
                        UPDATE TypeAttribute SET Value={sustainedShieldCapacity}
                            WHERE ItemTypeId={targetType} AND AttributeId={shieldCapacity}
                        """
                    );
                }
                target.ShieldCharge = sustainedShieldCapacity;
                target.UpdatedAt = clock.Now;
                for (int i = 0; i < pilots; i++)
                {
                    long pilot = 92000000 + i,
                        accountId = 1200000 + i,
                        ship = 7200000000000 + i;
                    string name = "Brutix benchmark " + i;
                    world.Database.ExecuteSql(
                        $"""
                        INSERT INTO WorldAccount(Id,UserName,CreatedAt,CloneGrade)
                            VALUES({accountId},{name},{account.CreatedAt},{(int)account.CloneGrade})
                        """
                    );
                    world.Database.ExecuteSql(
                        $"""
                        INSERT INTO WorldShip(Id,OwnerId,TypeId,StationId,Name)
                            VALUES({ship},{pilot},{brutixNavyIssue},{basis.ActiveShip.StationId},{name});
                        """
                    );
                    world.Database.ExecuteSql(
                        $"""
                        INSERT INTO WorldPilot(Id,AccountId,Name,CorporationId,ActiveShipId,RaceId,BloodlineId,GenderId,
                            AncestryId,SchoolId,CreatedAt,SafetyLevel)
                            VALUES({pilot},{accountId},{name},{basis.CorporationId},{ship},{basis.RaceId},{basis.BloodlineId},
                                {basis.GenderId},{basis.AncestryId},{basis.SchoolId},{basis.CreatedAt},{(int)basis.SafetyLevel});
                        """
                    );
                    var all = items.Concat(charges).ToArray();
                    var remap = all.Select((item, index) => (item.Id, NewId: 7300000000000L + i * 10000 + index))
                        .ToDictionary(p => p.Id, p => p.NewId);
                    foreach (var item in all)
                    {
                        OwnedItem copy = new();
                        world.Entry(copy).CurrentValues.SetValues(item);
                        copy.Id = remap[item.Id];
                        copy.OwnerId = pilot;
                        copy.LocationId = item.LocationId == basis.ActiveShipId ? ship : remap[item.LocationId];
                        world.Add(copy);
                    }
                    world.AddRange(
                        skills.Select(s => new PilotSkill
                        {
                            PilotId = pilot,
                            TypeId = s.TypeId,
                            Points = s.Points,
                        })
                    );
                    world.Add(
                        new FlightPosition
                        {
                            ShipId = ship,
                            SystemId = target.SystemId,
                            X = target.X + 1000 + i * 700,
                            Y = target.Y,
                            Z = target.Z,
                        }
                    );
                }
                world.SaveChanges();
                tx.Commit();
            }
            using var host = ServerServices.Build(database, clock);
            var store = host.GetRequiredService<SqliteWorld>();
            long bytes = 0;
            for (int i = 0; i < pilots; i++)
            {
                long account = 1200000 + i;
                var scope = host.CreateScope();
                var services = scope.ServiceProvider;
                var lookup = services.GetRequiredService<IPilotLookup>();
                PilotCallContext context = new(
                    account,
                    lookup.ReadAccount(account),
                    lookup.ListPilots(account),
                    services.GetRequiredService<FlightSessionFactory>().Create,
                    account,
                    SignedSettings.Empty
                );
                context.Select(store.FindPilot(account)!);
                var router = services.GetRequiredService<PilotCallRouter>();
                Assert.IsType<PilotCallResult.Success>(
                    router.Call(context, "dogmaIM", "LinkAllWeapons", new object?[] { context.Pilot.ShipId }, reports.Enqueue)
                );
                var bank = Assert.Single(
                    services.GetRequiredService<IPilotWeaponBanks>().Read(context.Pilot.PilotId, context.Pilot.ShipId)
                );
                Assert.Equal(5, bank.SlaveIds.Count);
                var guns = bank.SlaveIds.Prepend(bank.MasterId).ToArray();
                context.Flight.Snapshot();
                services.GetRequiredService<PilotUpdateCoordinator>().Enter(context);
                PilotSessionOutput sink = new(
                    context,
                    account,
                    account,
                    packet =>
                    {
                        Interlocked.Add(ref bytes, BlueMarshal.Encode(packet).Length);
                        return Task.CompletedTask;
                    },
                    reports.Enqueue
                );
                PilotSessionTick tick = new(
                    clock,
                    services.GetRequiredService<PilotUpdateCoordinator>(),
                    services.GetRequiredService<SkillHandler>(),
                    services.GetRequiredService<IAsteroidField>(),
                    services.GetRequiredService<IPilotOperationScope>()
                );
                clients.Add(new(scope, context, tick, sink, bank.MasterId, guns));
            }
            clock.Seconds += 30;
            foreach (var client in clients)
            {
                Assert.IsType<PilotCallResult.Success>(
                    client
                        .Scope.ServiceProvider.GetRequiredService<PilotCallRouter>()
                        .Call(client.Context, "dogmaIM", "AddTarget", new object?[] { target.Id }, reports.Enqueue)
                );
            }
            clock.Seconds += 30;
            foreach (var client in clients)
            {
                client.Scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>().Advance(client.Context);
                Assert.IsType<PilotCallResult.Success>(
                    client
                        .Scope.ServiceProvider.GetRequiredService<PilotCallRouter>()
                        .Call(
                            client.Context,
                            "dogmaIM",
                            "Activate",
                            new object?[] { client.Master, "projectileFired", target.Id, 100L },
                            reports.Enqueue
                        )
                );
            }
            var gunIds = clients.SelectMany(c => c.Guns).ToArray();
            Dictionary<long, int> roundsBefore;
            int npcCount;
            using (WorldDbContext world = new(database.Path))
            {
                roundsBefore = world
                    .Set<OwnedItem>()
                    .Where(i => gunIds.Contains(i.LocationId))
                    .ToDictionary(i => i.LocationId, i => i.Quantity);
                npcCount = world.Set<WorldNpc>().Count(n => n.SystemId == target.SystemId && n.DestroyedAt == null && n.RetiredAt == null);
            }
            Assert.Equal(pilots * 6, roundsBefore.Count);
            reports.Clear();
            reports.Enqueue(
                $"Setup including snapshot, migration, cloning, grouping, locking and first volleys: "
                    + $"{Stopwatch.GetElapsedTime(setupStarted).TotalMilliseconds:F2} ms. "
                    + $"System={target.SystemId}, NPCs={npcCount}, target={target.Id}, type={target.TypeId}."
            );
            bytes = 0;
            List<double> totals = [];
            async Task<FleetSample> Advance(FleetClient client)
            {
                List<string> sessionReports = [];
                // Independent roots keep concurrent workers from mutating a shared diagnostic parent.
                var measure = SessionPerformance.Begin($"pilot {client.Context.Pilot.PilotId}", sessionReports.Add, force: true)!;
                using (measure)
                {
                    await client.Tick.AdvanceAsync(
                        client.Context,
                        client.Sink,
                        sessionReports.Add,
                        client.Context.AccountId,
                        client.Context.AccountId
                    );
                    foreach (long gun in client.Guns)
                    {
                        Assert.NotNull(
                            client.Scope.ServiceProvider.GetRequiredService<CombatHandler>().ActiveEffect(client.Context.Pilot, gun)
                        );
                    }
                    Assert.Equal(0, measure.FittingCalculations);
                }
                return new(measure, sessionReports);
            }
            for (int frame = -2; frame < frames; frame++)
            {
                clock.Seconds++;
                if (frame == 0)
                {
                    bytes = 0;
                }
                long start = Stopwatch.GetTimestamp();
                FleetSample[] samples;
                if (concurrent)
                {
                    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    var workers = clients
                        .Select(client =>
                            Task.Run(async () =>
                            {
                                await release.Task;
                                return await Advance(client);
                            })
                        )
                        .ToArray();
                    release.SetResult();
                    samples = await Task.WhenAll(workers);
                }
                else
                {
                    List<FleetSample> sequential = [];
                    foreach (var client in clients)
                    {
                        sequential.Add(await Advance(client));
                    }
                    samples = sequential.ToArray();
                }
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (frame >= 0)
                {
                    totals.Add(elapsed);
                }
                foreach (string report in samples.SelectMany(s => s.Reports))
                {
                    reports.Enqueue(report);
                }
                var metrics = samples.Select(s => s.Metrics).ToArray();
                // Phase times overlap in concurrent mode. Counts sum, while the frame reports release-to-last-completion time.
                reports.Enqueue(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Performance fleet frame {frame}: {elapsed:F2} ms, "
                            + $"commands={metrics.Sum(m => m.Commands)}, connections={metrics.Sum(m => m.Connections)}, "
                            + $"tackleReads={metrics.Sum(m => m.TackleReads)}, aggressionReads={metrics.Sum(m => m.AggressionReads)}, "
                            + $"fittings={metrics.Sum(m => m.FittingCalculations)}, sql={metrics.Sum(m => m.SqlMilliseconds):F2} ms, "
                            + $"commit={metrics.Sum(m => m.CommitMilliseconds):F2} ms, "
                            + $"begin={metrics.Sum(m => m.TransactionStartMilliseconds):F2} ms."
                    )
                );
            }
            using (WorldDbContext world = new(database.Path))
            {
                var remaining = world
                    .Set<OwnedItem>()
                    .Where(i => gunIds.Contains(i.LocationId))
                    .ToDictionary(i => i.LocationId, i => i.Quantity);
                Assert.Equal(roundsBefore.Count, remaining.Count);
                var spent = gunIds.Select(gun => roundsBefore[gun] - remaining[gun]).ToArray();
                Assert.True(spent[0] > 0);
                Assert.All(spent, rounds => Assert.Equal(spent[0], rounds));
                reports.Enqueue(
                    $"Each of {gunIds.Length} guns consumed {spent[0]} rounds during two warmups and {frames} measured frames."
                );
            }
            var ordered = totals.Order().ToArray();
            double median = (ordered[(frames - 1) / 2] + ordered[frames / 2]) / 2;
            reports.Enqueue(
                $"Ten Brutix Navy Issues / sixty grouped guns, {mode} frame median={median:F2} ms "
                    + $"p95={ordered[(int)Math.Ceiling(frames * 0.95) - 1]:F2} ms, encoded bytes={bytes}"
            );
            foreach (string report in reports)
            {
                output.WriteLine(report);
            }

            if (Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_OUTPUT") is { } destination)
            {
                Directory.CreateDirectory(destination);
                File.WriteAllLines(Path.Combine(destination, $"grouped-fleet-{mode}.log"), reports);
            }
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Context.Dispose();
                client.Scope.Dispose();
            }
            Directory.Delete(directory, recursive: true);
        }
    }
}
