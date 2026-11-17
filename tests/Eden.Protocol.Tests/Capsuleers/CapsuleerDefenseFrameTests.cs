using System.Diagnostics;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Skills;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Configuration;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerDefenseFrameTests(ITestOutputHelper output)
{
    private const int SourceSystem = 30000141;
    private const long Enemy = 90000002;
    private const long EnemyShip = 1000000000002;

    private sealed record Client(IServiceScope Scope, PilotCallContext Context, PilotSessionTick Tick, PilotSessionOutput Output);

    private sealed class QuietPopulation : INpcPopulationSeeder
    {
        public long NextUpdate(Eden.World.Runtime.Economy.WorldAction world, int systemId, long now) => long.MaxValue;

        public void Seed(Eden.World.Runtime.Economy.WorldAction world, CatalogDbContext catalog, int systemId, long now) { }
    }

    private static void OpenClients(CapsuleerHaulerTests.Fixture f, ServiceProvider host, List<Client> clients, int observers = 2)
    {
        foreach (long account in Enumerable.Range(1000001, observers))
        {
            var scope = host.CreateScope();
            var services = scope.ServiceProvider;
            var ships = services.GetRequiredService<SqliteWorld>();
            var pilot = ships.FindPilot(account)!;
            PilotCallContext context = new(
                account,
                pilot.Account,
                [pilot],
                services.GetRequiredService<FlightSessionFactory>().Create,
                account,
                SignedSettings.Empty
            );
            context.Select(pilot);
            var updates = services.GetRequiredService<PilotUpdateCoordinator>();
            updates.Enter(context);
            context.Flight.Snapshot();
            clients.Add(
                new(
                    scope,
                    context,
                    new(
                        f.Clock,
                        updates,
                        services.GetRequiredService<SkillHandler>(),
                        services.GetRequiredService<IAsteroidField>(),
                        host.GetRequiredService<IPilotOperationScope>()
                    ),
                    new(
                        context,
                        account,
                        account,
                        packet =>
                        {
                            _ = Eden.Protocol.Serialization.BlueMarshal.Encode(packet);
                            return Task.CompletedTask;
                        },
                        _ => { }
                    )
                )
            );
        }
    }

    private static void AddObservers(WorldDbContext world, int observers)
    {
        var template = world.Pilots.Include(p => p.ActiveShip).Single(p => p.AccountId == 1000001);
        for (int i = 2; i < observers; i++)
        {
            var pilot = (Pilot)world.Entry(template).CurrentValues.ToObject();
            pilot.Id = 99_500_000 + i;
            pilot.AccountId = 1_000_001 + i;
            pilot.Name = $"Observer {i + 1}";
            var ship = (Ship)world.Entry(template.ActiveShip).CurrentValues.ToObject();
            ship.Id = 2_900_000_000_000 + i;
            ship.OwnerId = pilot.Id;
            pilot.ActiveShipId = ship.Id;
            world.Add(new Account { Id = pilot.AccountId, UserName = $"observer{i}" });
            world.Add(new InventoryOwner { Id = pilot.Id });
            world.Add(ship);
            world.Add(pilot);
        }
        world.SaveChanges();
    }

    [Theory]
    [InlineData(10, 2)]
    [InlineData(50, 2)]
    [InlineData(50, 4)]
    [InlineData(100, 2)]
    [InlineData(100, 4)]
    public Task ArmedPatrolsOnOneGridWithFullObservers(int pilotCount, int observers) => CombatFrames(pilotCount, observers, false);

    [Fact]
    public Task ScoutReportsAndCampPartnersTackleAndFireOnTheSameVisibleShip() => CombatFrames(3, 2, true);

    private async Task CombatFrames(int pilotCount, int observers, bool camp)
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        if (camp)
        {
            PassiveCatalogFixture.LoadTackleTiers(f.Database.CatalogPath);
            PassiveCatalogFixture.LoadInterdiction(f.Database.CatalogPath);
        }
        using (CatalogDbContext edit = new(f.Database.CatalogPath, writable: true))
        {
            edit.Database.ExecuteSqlRaw(
                "UPDATE SourceRecord SET Data=json_set(Data,'$.security',-0.2) WHERE Dataset='client/solarsystemcontent' AND Key='30000141'"
            );
            if (pilotCount > 10)
            {
                // Two station-rich communities each admit at least 50 home pilots under the normal roster cap.
                foreach (int system in new[] { SourceSystem, SourceSystem + 1 })
                {
                    var template = edit.Set<Station>().First(station => station.SolarSystemId == system);
                    for (int i = 0; i < 7; i++)
                    {
                        var station = (Station)edit.Entry(template).CurrentValues.ToObject();
                        station.Id = 61000000 + (system - SourceSystem) * 10 + i;
                        station.X += (i + 1) * 500_000;
                        edit.Add(station);
                    }
                }
                edit.SaveChanges();
            }
            string data = "{\"securityStatus\":-0.2}";
            edit.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems',{SourceSystem},{data})");
        }
        var config = new WorldConfigurationStore(f.Database, f.Clock);
        var settings = config.Read();
        var draft = settings.Active.Draft;
        config.Save(
            settings.Generation,
            draft with
            {
                Settings = draft.Settings with
                {
                    // This measures independent neutral patrols; regional allies have a separate workload.
                    Capsuleers = new(Miners: false, TrafficMultiplier: 10, GateVisitors: 0)
                    {
                        GeneratedCombatPercent = 0,
                        RegionalConflicts = false,
                    },
                },
            },
            "armed patrol measurement"
        );
        WorldInitialization.Initialize(f.Database, f.Clock);
        using var provisioner = new CapsuleerProvisioner(f.Database, f.Clock);
        using (WorldDbContext setup = new(f.Database.Path))
        using (CatalogDbContext catalog = new(f.Database.CatalogPath))
        {
            for (int filled = 0; filled < pilotCount; filled += 50)
            {
                provisioner.Provision(SourceSystem + filled / 50, catalog, setup, maximumFounders: Math.Min(50, pilotCount - filled));
                setup.ChangeTracker.Clear();
            }
        }
        long[] pilots;
        using (WorldDbContext world = new(f.Database.Path))
        using (CatalogDbContext catalog = new(f.Database.CatalogPath))
        using (var transaction = world.Database.BeginTransaction())
        {
            var ships = world.Set<SavedCapsuleerHauler>().OrderBy(h => h.PilotId).ToArray();
            Assert.Equal(pilotCount, ships.Length);
            var gate = SystemObjects.Geometry(catalog, SourceSystem).First(o => o.DestinationGateId is not null);
            var origin = gate.Position + new SpacePosition(gate.Radius + 10_000, 0, 0);
            pilots = ships.Select(h => h.PilotId).ToArray();
            for (int i = 0; i < ships.Length; i++)
            {
                var founder = ships[i];
                SqliteWorld.SetPosition(world, catalog, founder.FounderShipId, null, null, f.Clock.Now);
                foreach (
                    var cargo in world
                        .Set<OwnedItem>()
                        .Where(i => i.LocationId == founder.FounderShipId && i.FlagId == (int)InventoryFlag.Cargo)
                )
                {
                    cargo.LocationId = founder.SourceStationId;
                    cargo.FlagId = (int)InventoryFlag.Hangar;
                }
                world.SaveChanges();
                var fit = TrafficSnapshotTests.Profile() with
                {
                    HullTypeId = 603,
                    Equipment = [new(561, (int)InventoryFlag.HighSlot0, 1), new(215, (int)InventoryFlag.HighSlot0, 50)],
                };
                if (camp && i == (int)CampRole.Tackle)
                {
                    fit = fit with { Equipment = [.. fit.Equipment, new(447, (int)InventoryFlag.MediumSlot0, 1)] };
                }
                TrafficFitting.Apply(world, catalog, founder.PilotId, founder.FounderShipId, fit, f.Clock.Now);
                SqliteWorld.SetPosition(
                    world,
                    catalog,
                    founder.FounderShipId,
                    origin + new SpacePosition(0, i * 550, 0),
                    SourceSystem,
                    f.Clock.Now
                );
                var work = new HaulerProgress(
                    HaulerActivity.Flying,
                    0,
                    0,
                    founder.FounderShipId,
                    founder.SourceStationId,
                    gate.Id,
                    new NavigationState().Capture()
                )
                {
                    Ratting = new(founder.SourceStationId, [gate.Id], 0, TimeSpan.FromMinutes(5).Ticks)
                    {
                        Phase = RattingPhase.Searching,
                        Patrol = camp
                            ? new(PatrolKind.GateCamp, SourceSystem) { Camp = new(gate.Id, TimeSpan.FromMinutes(4).Ticks, (CampRole)i) }
                            : new(PatrolKind.Roam, SourceSystem),
                    },
                };
                new HaulerCheckpoint(founder.PilotId, work).Apply(world);
                world.Set<SavedCapsuleerJob>().Find(founder.PilotId)!.DueTicks = TimeSpan.TicksPerSecond;
            }
            AddObservers(world, observers);
            foreach (var pilot in world.Pilots.Where(p => p.AccountId > 0).ToArray())
            {
                SqliteWorld.SetPosition(
                    world,
                    catalog,
                    pilot.ActiveShipId,
                    origin + new SpacePosition(camp && pilot.Id == Enemy ? 1500 : 80_000, pilot.AccountId - 1000000, 0),
                    SourceSystem,
                    f.Clock.Now
                );
            }
            world.SaveChanges();
            transaction.Commit();
        }
        using var host = ServerServices.Build(f.Database, f.Clock, population: new QuietPopulation(), liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, SourceSystem);
        using var enemy = host.GetRequiredService<IHumanPresence>().Select(Enemy, SourceSystem);
        host.GetRequiredService<CapsuleerScheduler>().PopulationPrepared(SourceSystem);
        List<Client> clients = [];
        try
        {
            OpenClients(f, host, clients, observers);
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
            HashSet<long> fired = [];
            List<double> times = [];
            int maximumWakes = 0;
            bool tackled = false;
            int fittingFrames = 0;
            int fittingEvaluations = 0;
            for (int frame = 0; frame < 36; frame++)
            {
                f.Clock.Seconds++;
                using var metrics = SessionPerformance.Begin("armed patrol frame", _ => { }, force: true)!;
                long start = Stopwatch.GetTimestamp();
                int wakes = 0;
                do
                {
                    worker.Tick();
                    wakes++;
                } while (scheduler.NextWake().Delay == TimeSpan.Zero && wakes < pilotCount);
                Assert.NotEqual(TimeSpan.Zero, scheduler.NextWake().Delay);
                maximumWakes = Math.Max(maximumWakes, wakes);
                foreach (var client in clients)
                {
                    await client.Tick.AdvanceAsync(
                        client.Context,
                        client.Output,
                        _ => { },
                        client.Context.AccountId,
                        client.Context.AccountId
                    );
                }
                if (frame >= 16)
                {
                    times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    Assert.Equal(0, metrics.Commands);
                    Assert.Equal(0, metrics.Connections);
                    if (!camp)
                    {
                        Assert.Equal(0, metrics.FittingCalculations);
                    }
                    else if (metrics.FittingCalculations > 0)
                    {
                        fittingFrames++;
                        fittingEvaluations += metrics.FittingCalculations;
                    }
                }
                if (camp)
                {
                    var tackler = scheduler.Hauler(pilots[(int)CampRole.Tackle])!.Work.Defense;
                    if (tackler?.TargetId > 0)
                    {
                        Assert.Equal(EnemyShip, tackler.TargetId);
                    }
                    if (tackler?.TargetId == EnemyShip && tackler.Cycles.Any(c => c.Effect == DogmaEffect.WarpScrambler))
                    {
                        tackled = true;
                    }
                }
                foreach (long pilot in pilots)
                {
                    if (scheduler.Hauler(pilot)?.Work.Defense?.Cycles.Any(c => c.Effect == DogmaEffect.ProjectileFired) == true)
                    {
                        fired.Add(pilot);
                    }
                }
            }
            Assert.Equal(camp ? 2 : pilotCount, fired.Count);
            if (camp)
            {
                Assert.True(tackled);
                Assert.True(fittingFrames < times.Count / 2, $"Tackle fit work in {fittingFrames} of {times.Count} warmed frames.");
                output.WriteLine($"Paid tackle affected fits in {fittingFrames} warmed frames, {fittingEvaluations} evaluations.");
                Assert.DoesNotContain(pilots[(int)CampRole.Scout], fired);
                var watch = host.GetRequiredService<CapsuleerDefense>()
                    .Camps.At(SourceSystem, scheduler.Hauler(pilots[0])!.Work.Ratting!.Belts[0], TimeSpan.FromMinutes(4).Ticks, 0);
                Assert.Equal(EnemyShip, watch!.Reported(TimeSpan.FromSeconds(36).Ticks));
            }
            times.Sort();
            output.WriteLine(
                $"{pilots.Length} pilots, {observers} observers, {fired.Count} fired, max {maximumWakes} wakes. "
                    + $"Median {times[10]:F2} ms, p95 {times[18]:F2} ms, max {times[19]:F2} ms."
            );
            var live = host.GetRequiredService<LiveWorld>();
            live.MeasureCheckpoints = true;
            await live.FlushAsync();
            var checkpoint = live.LastCheckpoint!;
            output.WriteLine(
                $"Checkpoint capture {checkpoint.Capture.TotalMilliseconds:F2} ms, projection "
                    + $"{checkpoint.Projection.TotalMilliseconds:F2} ms, write {checkpoint.Write.TotalMilliseconds:F2} ms."
            );
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task WarmCompleteMaintenanceAndTwoObserverFramesKeepCombatResident(bool combat, bool ratting)
    {
        using CapsuleerHaulerTests.Fixture f = new(isolateRoute: true);
        var founder = f.Founder();
        SpacePosition origin;
        using (WorldDbContext world = new(f.Database.Path))
        using (CatalogDbContext catalog = new(f.Database.CatalogPath))
        using (var transaction = world.Database.BeginTransaction())
        {
            TrafficFitting.Apply(
                world,
                catalog,
                founder.PilotId,
                founder.FounderShipId,
                TrafficSnapshotTests.Profile() with
                {
                    HullTypeId = 603,
                    Equipment = [new(561, (int)InventoryFlag.HighSlot0, 1), new(215, (int)InventoryFlag.HighSlot0, 50)],
                },
                f.Clock.Now
            );
            var station = catalog.RequireStation(60015157);
            origin = new(station.X + 100_000, station.Y, station.Z);
            SqliteWorld.SetPosition(world, catalog, founder.FounderShipId, origin, SourceSystem, f.Clock.Now);
            foreach (var pilot in world.Pilots.Where(pilot => pilot.AccountId > 0).ToArray())
            {
                SqliteWorld.SetPosition(
                    world,
                    catalog,
                    pilot.ActiveShipId,
                    origin + new SpacePosition(pilot.Id == Enemy ? 500 : 1000, 0, 0),
                    SourceSystem,
                    f.Clock.Now
                );
            }
            if (combat)
            {
                world.Add(
                    new SavedPilotHostility
                    {
                        SourceShipId = EnemyShip,
                        TargetShipId = founder.FounderShipId,
                        LastAttackAt = f.Clock.Now,
                        ExpiresAt = f.Clock.Now + TimeSpan.FromMinutes(15).Ticks,
                    }
                );
            }
            if (ratting)
            {
                var work = new HaulerProgress(
                    HaulerActivity.Flying,
                    0,
                    0,
                    founder.FounderShipId,
                    60015157,
                    0,
                    new NavigationState().Capture(),
                    Journey: new(TrafficJourneyKind.Local, 60015157, 60015157, 60015157, [SourceSystem], 1)
                )
                {
                    Ratting = new(60015157, [40009051], 0, TimeSpan.FromMinutes(RattingRules.MaximumMinutes).Ticks)
                    {
                        Phase = RattingPhase.Returning,
                    },
                };
                new HaulerCheckpoint(founder.PilotId, work).Apply(world);
            }
            world.SaveChanges();
            transaction.Commit();
        }
        using (CatalogDbContext catalog = new(f.Database.CatalogPath, writable: true))
        {
            string data = "{\"securityStatus\":0}";
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems',{SourceSystem},{data})");
        }
        using var host = ServerServices.Build(f.Database, f.Clock, population: new QuietPopulation(), liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, SourceSystem);
        using var enemy = host.GetRequiredService<IHumanPresence>().Select(Enemy, SourceSystem);
        List<Client> clients = [];
        try
        {
            OpenClients(f, host, clients);
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
            var samples = new List<double>();
            var sql = new List<int>();
            var connections = new List<int>();
            for (int frame = 0; frame < 24; frame++)
            {
                f.Clock.Seconds++;
                List<string> commands = [];
                using var metrics = SessionPerformance.Begin(
                    "complete capsuleer and observer frame",
                    _ => { },
                    force: true,
                    command: commands.Add
                )!;
                var started = Stopwatch.GetTimestamp();
                worker.Tick();
                foreach (var client in clients)
                {
                    await client.Tick.AdvanceAsync(
                        client.Context,
                        client.Output,
                        _ => { },
                        client.Context.AccountId,
                        client.Context.AccountId
                    );
                }
                if (frame >= 12)
                {
                    samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    sql.Add(metrics.Commands);
                    connections.Add(metrics.Connections);
                    Assert.True(metrics.Commands == 0, string.Join(Environment.NewLine, commands));
                    Assert.Equal(0, metrics.Connections);
                    Assert.Equal(0, metrics.FittingCalculations);
                }
            }
            if (combat)
            {
                var observation = scheduler.Hauler(founder.PilotId)!;
                Assert.Equal(EnemyShip, observation.Work.Defense!.TargetId);
                Assert.Contains(observation.Work.Defense.Cycles, cycle => cycle.Effect == DogmaEffect.ProjectileFired);
                Assert.True(
                    host.GetRequiredService<IPilotHealth>().Read(Enemy, f.Clock.Now).Remaining.Shield
                        < host.GetRequiredService<IPilotHealth>().Read(Enemy, f.Clock.Now).Defense.Shield
                );
                var peers = clients
                    .Select(client =>
                        host.GetRequiredService<SharedSpace>()
                            .Peers(client.Context.Pilot)
                            .Single(peer => peer.Pilot.PilotId == founder.PilotId)
                    )
                    .ToArray();
                Assert.NotEmpty(peers[0].Weapons!);
                Assert.Equal(peers[0].Weapons, peers[1].Weapons);
            }
            samples.Sort();
            output.WriteLine(
                $"Combat={combat}, Ratting={ratting}, complete frames with two observers: median={samples[samples.Count / 2]:F2} ms, "
                    + $"p95={samples[(int)Math.Ceiling(samples.Count * .95) - 1]:F2} ms; "
                    + $"commands={sql.Min()}–{sql.Max()}, connections={connections.Min()}–{connections.Max()}, fits=0."
            );
            var live = host.GetRequiredService<LiveWorld>();
            live.MeasureCheckpoints = true;
            await live.FlushAsync();
            var checkpoint = live.LastCheckpoint!;
            output.WriteLine(
                $"Combat={combat}, Ratting={ratting}, checkpoint capture/projection/write: {checkpoint.Capture.TotalMilliseconds:F2}/"
                    + $"{checkpoint.Projection.TotalMilliseconds:F2}/{checkpoint.Write.TotalMilliseconds:F2} ms."
            );
            if (combat)
            {
                var current = scheduler.Hauler(founder.PilotId)!.Work.Defense!;
                using var file = WorldConnections.FileConnection(f.Database.Path);
                file.Open();
                using var command = file.CreateCommand();
                command.CommandText = "SELECT Progress FROM WorldCapsuleerHauler";
                var restored = HaulerCheckpoint.Read((string)command.ExecuteScalar()!)!.Defense!;
                Assert.Equal(current.ShipId, restored.ShipId);
                Assert.Equal(current.Locks, restored.Locks);
                Assert.Equal(JsonSerializer.Serialize(current.Cycles), JsonSerializer.Serialize(restored.Cycles));
                var before = host.GetRequiredService<IPilotHealth>().Read(Enemy, f.Clock.Now).Remaining;
                worker.Tick();
                Assert.Equal(before, host.GetRequiredService<IPilotHealth>().Read(Enemy, f.Clock.Now).Remaining);
                foreach (var client in clients)
                {
                    client.Context.Dispose();
                    client.Scope.Dispose();
                }
                clients.Clear();
                human.Dispose();
                enemy.Dispose();
                file.Dispose();
                host.Dispose();
                using var restarted = ServerServices.Build(f.Database, f.Clock, population: new QuietPopulation(), liveWorld: true);
                using var selected = restarted.GetRequiredService<IHumanPresence>().Select(Enemy, SourceSystem);
                restarted.GetRequiredService<CapsuleerMaintenance>().Tick();
                Assert.Equal(before, restarted.GetRequiredService<IPilotHealth>().Read(Enemy, f.Clock.Now).Remaining);
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
}
