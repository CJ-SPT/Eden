using System.Diagnostics;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Combat;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Skills;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Combat;

public sealed class ResidentFitFleetTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public async Task SharedGridVolleysReuseEachPilotsFitAndMidFightChangesRefresh(int count)
    {
        TestClock clock = new();
        using PilotCombatTests.Fixture f = new(clock);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                UPDATE TypeAttribute SET Value=1000000000 WHERE ItemTypeId IN(601,30380) AND AttributeId=263;
                INSERT INTO SourceRecord VALUES ('client/asteroidcountbytypeidbeltid','40009232','{{}}'),
                    ('client/asteroidcountbytypeidbeltid','40009245','{{}}');
                """
            );
        }
        using (WorldDbContext world = new(f.Db.Path))
        using (var tx = world.Database.BeginTransaction())
        {
            world.Database.ExecuteSqlRaw("PRAGMA defer_foreign_keys=ON");
            world.Set<WorldNpc>().ExecuteUpdate(s => s.SetProperty(n => n.ShieldCharge, 1000000000d));
            for (int i = 0; i < count; i++)
            {
                long pilot = 91000000 + i;
                long account = 1100000 + i;
                long ship = 7000000000000 + i;
                long module = 7100000000000 + i;
                world.Database.ExecuteSql($"INSERT INTO WorldAccount(Id,UserName) VALUES ({account},{"Fleet" + i})");
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldShip(Id,OwnerId,TypeId,StationId,Name)
                        VALUES({ship},{pilot},601,60003760,{'F' + i.ToString()});
                    """
                );
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldPilot(Id,AccountId,Name,CorporationId,ActiveShipId)
                        VALUES({pilot},{account},{"Fleet " + i},1000044,{ship});
                    """
                );
                world.Database.ExecuteSql(
                    $"INSERT INTO WorldPilotSkill SELECT {pilot},TypeId,Points FROM WorldPilotSkill WHERE PilotId=90000001"
                );
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldItem(Id,OwnerId,TypeId,LocationId,FlagId,Quantity,Singleton,Online)
                        VALUES({module},{pilot},3638,{ship},27,1,1,1);
                    """
                );
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldPosition(ShipId,SystemId,X,Y,Z)
                        VALUES({ship},{f.Npc.SystemId},{f.Npc.Position.X + 1000 + i * 200},{f.Npc.Position.Y},{f.Npc.Position.Z});
                    """
                );
            }
            tx.Commit();
        }
        using ResidentFitCache cache = new(new(f.Db.Path));
        List<(IServiceScope Scope, PilotCallContext Context, PilotSessionTick Tick, PilotSessionOutput Output, long Module)> clients = [];
        try
        {
            for (int i = 0; i < count; i++)
            {
                long account = 1100000 + i;
                var scope = f.Db.Services.CreateScope();
                var services = scope.ServiceProvider;
                var pilots = services.GetRequiredService<IPilotLookup>();
                PilotCallContext context = new(
                    account,
                    pilots.ReadAccount(account),
                    pilots.ListPilots(account),
                    services.GetRequiredService<FlightSessionFactory>().Create,
                    account,
                    SignedSettings.Empty
                );
                context.Select(f.Db.Store.FindPilot(account)!);
                context.Flight.Snapshot();
                services.GetRequiredService<PilotUpdateCoordinator>().Enter(context);
                PilotSessionOutput sink = new(context, account, account, _ => Task.CompletedTask, _ => { });
                PilotSessionTick tick = new(
                    clock,
                    services.GetRequiredService<PilotUpdateCoordinator>(),
                    services.GetRequiredService<SkillHandler>(),
                    services.GetRequiredService<IAsteroidField>(),
                    services.GetRequiredService<IPilotOperationScope>()
                );
                clients.Add((scope, context, tick, sink, 7100000000000 + i));
            }
            clock.Seconds += 30;
            foreach (var client in clients)
            {
                var router = client.Scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
                Assert.IsType<PilotCallResult.Success>(
                    router.Call(client.Context, "dogmaIM", "AddTarget", new object?[] { f.Npc.Id }, _ => { })
                );
            }
            clock.Seconds += 30;
            foreach (var client in clients)
            {
                client.Scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>().Advance(client.Context);
                var router = client.Scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
                Assert.IsType<PilotCallResult.Success>(
                    router.Call(
                        client.Context,
                        "dogmaIM",
                        "Activate",
                        new object?[] { client.Module, "targetAttack", f.Npc.Id, 100L },
                        _ => { }
                    )
                );
            }
            List<double> samples = [];
            for (int cycle = 0; cycle < 6; cycle++)
            {
                clock.Seconds += 2;
                long start = Stopwatch.GetTimestamp();
                using var metrics = SessionPerformance.Begin("fleet", _ => { }, force: true)!;
                foreach (var client in clients)
                {
                    await client.Tick.AdvanceAsync(
                        client.Context,
                        client.Output,
                        _ => { },
                        client.Context.AccountId,
                        client.Context.AccountId
                    );
                    Assert.NotNull(
                        client.Scope.ServiceProvider.GetRequiredService<CombatHandler>().ActiveEffect(client.Context.Pilot, client.Module)
                    );
                }
                samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            using (WorldDbContext changed = new(f.Db.Path))
            {
                changed.Set<PilotSkill>().Single(s => s.PilotId == 91000000 && s.TypeId == 3300).Points = 1415;
                changed.SaveChanges();
            }
            using (var metrics = SessionPerformance.Begin("skill change", _ => { }, force: true)!)
            {
                _ = f.Fitting.ShipAttributes(91000000);
                Assert.Equal(1, metrics.FittingCalculations);
            }
            var firstClient = clients[0];
            double before = firstClient
                .Scope.ServiceProvider.GetRequiredService<CombatHandler>()
                .ActiveEffect(firstClient.Context.Pilot, firstClient.Module)!
                .DurationMilliseconds!.Value;
            clock.Seconds += 2;
            using (var metrics = SessionPerformance.Begin("new skill volley", _ => { }, force: true)!)
            {
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
                Assert.Equal(1, metrics.FittingCalculations);
            }
            Assert.True(
                firstClient
                    .Scope.ServiceProvider.GetRequiredService<CombatHandler>()
                    .ActiveEffect(firstClient.Context.Pilot, firstClient.Module)!
                    .DurationMilliseconds!.Value < before
            );
            output.WriteLine(
                $"{count} pilots, serialized shared-grid tick totals ms: {string.Join(", ", samples.Select(v => v.ToString("F1")))}"
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
}
