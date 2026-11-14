using System.Diagnostics;
using System.Text.Json;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
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
            foreach (long account in new long[] { 1000001, 1000002 })
            {
                var scope = host.CreateScope();
                var services = scope.ServiceProvider;
                var ships = services.GetRequiredService<SqliteWorld>();
                var pilot = ships.FindPilot(account)!;
                var space = services.GetRequiredService<SharedSpace>();
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
