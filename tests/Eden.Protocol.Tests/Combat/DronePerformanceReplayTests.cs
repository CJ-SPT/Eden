using System.Diagnostics;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.Combat.Npcs;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Skills;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using Fixture = Eden.Protocol.Tests.Combat.CombatDroneTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class DronePerformanceReplayTests(ITestOutputHelper output)
{
    private const int SystemId = 30000144;
    private const int DroneType = 2488;
    private const int HullType = 33151;
    private const int Frames = 30;
    private const long FirstAccount = 1100000;
    private const long FirstPilot = 91000000;
    private const long FirstShip = 7000000000000;
    private const long FirstStack = 7100000000000;

    private sealed record Client(IServiceScope Scope, PilotCallContext Context, PilotSessionTick Tick, PilotSessionOutput Output);

    private sealed record Sample(double Milliseconds, int Commands, int Connections, int Fits);

    [Theory]
    [InlineData(1, 0, "orbit")]
    [InlineData(1, 1, "orbit")]
    [InlineData(1, 5, "orbit")]
    [InlineData(10, 5, "orbit")]
    [InlineData(1, 5, "pursuit")]
    [InlineData(10, 5, "pursuit")]
    [InlineData(1, 5, "combat")]
    [InlineData(10, 5, "combat")]
    public async Task ReplayNormalSessions(int pilots, int drones, string scenario)
    {
        using Fixture f = new(HullType);
        using (CatalogDbContext catalog = new(f.Base.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=1000000000 WHERE AttributeId=263");
        }
        using (WorldDbContext world = new(f.Base.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            world.Database.ExecuteSqlRaw("PRAGMA defer_foreign_keys=ON");
            world.Set<WorldNpc>().ExecuteUpdate(set => set.SetProperty(n => n.ShieldCharge, 1000000000d));
            for (int i = 0; i < pilots; i++)
            {
                long account = FirstAccount + i;
                long pilot = FirstPilot + i;
                long ship = FirstShip + i;
                long stack = FirstStack + i;
                world.Database.ExecuteSql($"INSERT INTO WorldAccount(Id,UserName) VALUES ({account},{"Drone fleet " + i})");
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldShip(Id,OwnerId,TypeId,StationId,Name)
                    VALUES({ship},{pilot},{HullType},60003760,{"Drone fleet " + i});
                    """
                );
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldPilot(Id,AccountId,Name,CorporationId,ActiveShipId)
                    VALUES({pilot},{account},{"Drone pilot " + i},1000044,{ship});
                    """
                );
                world.Database.ExecuteSql(
                    $"INSERT INTO WorldPilotSkill SELECT {pilot},TypeId,Points FROM WorldPilotSkill WHERE PilotId={f.Pilot}"
                );
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldItem(Id,OwnerId,TypeId,LocationId,FlagId,Quantity,Singleton,Online)
                    VALUES({stack},{pilot},{DroneType},{ship},{(int)Eden.Gameplay.Inventory.InventoryFlag.DroneBay},5,0,0);
                    """
                );
                double distance = scenario == "pursuit" ? 40000 : 1000;
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldPosition(ShipId,SystemId,X,Y,Z)
                    VALUES({ship},{SystemId},{f.Npc.Position.X + distance + i * 200},{f.Npc.Position.Y},{f.Npc.Position.Z});
                    """
                );
            }
            transaction.Commit();
        }
        using LiveWorld live = new(f.Base.Db.Services.GetRequiredService<WorldDatabase>(), f.Base.Clock, _ => { });
        live.MeasureCheckpoints = true;
        List<Client> clients = [];
        List<Sample> samples = [];
        try
        {
            for (int i = 0; i < pilots; i++)
            {
                long account = FirstAccount + i;
                var scope = f.Base.Db.Services.CreateScope();
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
                context.Select(f.Base.Db.Store.FindPilot(account)!);
                services.GetRequiredService<PilotUpdateCoordinator>().Enter(context);
                context.Flight.Snapshot();
                PilotSessionOutput sink = new(
                    context,
                    account,
                    account,
                    packet =>
                    {
                        _ = Eden.Protocol.Serialization.BlueMarshal.Encode(packet);
                        return Task.CompletedTask;
                    },
                    _ => { }
                );
                PilotSessionTick tick = new(
                    f.Base.Clock,
                    services.GetRequiredService<PilotUpdateCoordinator>(),
                    services.GetRequiredService<SkillHandler>(),
                    services.GetRequiredService<IAsteroidField>(),
                    services.GetRequiredService<IPilotOperationScope>()
                );
                clients.Add(new(scope, context, tick, sink));
                if (drones != 0)
                {
                    var ids = f.Drones.Launch(FirstPilot + i, [new(FirstStack + i, drones)], f.Now).Launches.Single().DroneIds;
                    if (scenario != "orbit")
                    {
                        f.Drones.Order(FirstPilot + i, ids, DroneOrder.Engage, f.Npc.Id, new HashSet<long> { f.Npc.Id }, f.Now);
                    }
                }
            }
            // One observer owns no drones and uses the same normal session path.
            var observerScope = f.Base.Db.Services.CreateScope();
            var observer = DroneSynchronizationTests.Context(f, observerScope.ServiceProvider, 1000002);
            f.Base.Db.Store.SetPosition(
                observer.Pilot.ShipId,
                f.Npc.Position + new Eden.Gameplay.Flight.SpacePosition(2000, 0, 0),
                SystemId
            );
            observer.Select(f.Base.Db.Store.FindPilot(1000002)!);
            observerScope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>().Enter(observer);
            observer.Flight.Snapshot();
            clients.Add(
                new(
                    observerScope,
                    observer,
                    new(
                        f.Base.Clock,
                        observerScope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>(),
                        observerScope.ServiceProvider.GetRequiredService<SkillHandler>(),
                        observerScope.ServiceProvider.GetRequiredService<IAsteroidField>()
                    ),
                    new(observer, 1000002, 1000002, _ => Task.CompletedTask, _ => { })
                )
            );
            for (int frame = 0; frame < Frames + 2; frame++)
            {
                f.Base.Clock.Seconds++;
                using var metrics = SessionPerformance.Begin("drone fleet frame", _ => { }, force: true)!;
                var watch = Stopwatch.StartNew();
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
                watch.Stop();
                if (frame >= 2)
                {
                    samples.Add(new(watch.Elapsed.TotalMilliseconds, metrics.Commands, metrics.Connections, metrics.FittingCalculations));
                }
            }
            Assert.Equal(pilots * drones, f.Drones.List(SystemId, f.Now).Count);
            var sorted = samples.Select(s => s.Milliseconds).Order().ToArray();
            output.WriteLine(
                "DRONE_REPLAY="
                    + JsonSerializer.Serialize(
                        new
                        {
                            pilots,
                            drones,
                            scenario,
                            median = sorted[sorted.Length / 2],
                            p95 = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1],
                            commands = samples.Average(s => s.Commands),
                            connections = samples.Average(s => s.Connections),
                            fits = samples.Average(s => s.Fits),
                        }
                    )
            );
            f.Base.Clock.Seconds++;
            for (int i = 0; i < pilots; i++)
            {
                f.Drones.Advance(FirstPilot + i, f.Now, (int)f.Base.Clock.Seconds);
            }
            f.Base.Clock.Seconds++;
            using (var direct = SessionPerformance.Begin("drone operations only", _ => { }, force: true)!)
            {
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < pilots; i++)
                {
                    f.Drones.Advance(FirstPilot + i, f.Now, (int)f.Base.Clock.Seconds);
                }
                for (int observerIndex = 0; observerIndex < pilots + 1; observerIndex++)
                {
                    Assert.Equal(pilots * drones, f.Drones.List(SystemId, f.Now).Count);
                }
                output.WriteLine(
                    "DRONE_OPERATIONS="
                        + JsonSerializer.Serialize(
                            new
                            {
                                pilots,
                                drones,
                                scenario,
                                milliseconds = watch.Elapsed.TotalMilliseconds,
                                direct.Commands,
                                direct.Connections,
                                direct.FittingCalculations,
                            }
                        )
                );
                if (Environment.GetEnvironmentVariable("EDEN_DRONE_BASELINE") != "1")
                {
                    Assert.Equal(0, direct.Commands);
                    Assert.Equal(0, direct.Connections);
                    Assert.Equal(0, direct.FittingCalculations);
                }
            }
            await live.FlushAsync();
            foreach (var checkpoint in live.RecentCheckpoints)
            {
                output.WriteLine("DRONE_CHECKPOINT=" + JsonSerializer.Serialize(checkpoint));
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
