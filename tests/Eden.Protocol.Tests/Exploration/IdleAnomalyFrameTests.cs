using System.Diagnostics;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Exploration;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Combat;
using Eden.World.Catalog;
using Eden.World.Exploration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Exploration;

public sealed class IdleAnomalyFrameTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IdlePodAndRemoteObserverHaveNoRecurringSqlOrSceneReloads(bool populated, bool nearScenery)
    {
        using NpcCombatTests.Fixture fixture = new(liveWorld: true, useTestClock: true);
        fixture.Aggress();
        fixture.Clock.Milliseconds = 7500;
        fixture.AlmostLost();
        Assert.NotNull(fixture.Fire()!.Capsule);
        var database = new WorldDatabase(fixture.Db.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using (CatalogDbContext catalog = new(database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO SourceRecord(Dataset,Key,Data)
                SELECT 'client/asteroidcountbytypeidbeltid', CAST(Id AS TEXT), '{{}}' FROM Celestial WHERE GroupId=9;
                """
            );
        }
        SpacePosition origin = new(1e12, 1e12, 1e12);
        CombatAnomalyStore sites = new(database);
        if (populated)
        {
            using (WorldDbContext world = new(database.Path))
            {
                world.Add(
                    new WorldCombatAnomaly
                    {
                        Id = CombatAnomalyRules.FirstSiteId,
                        SystemId = CombatAnomalyRules.SystemId,
                        DungeonId = 3454,
                        DefinitionVersion = 1,
                        X = origin.X,
                        Y = origin.Y,
                        Z = origin.Z,
                    }
                );
                world.Add(
                    new WorldNpc
                    {
                        TypeId = fixture.Npc.TypeId,
                        SystemId = CombatAnomalyRules.SystemId,
                        EncounterId = CombatAnomalyRules.FirstSiteId,
                        AnchorKind = NpcAnchorKind.Anomaly,
                        SpawnSlot = 900,
                        DestroyedAt = fixture.Clock.Now,
                    }
                );
                world.SaveChanges();
            }
            sites.Arrive(CombatAnomalyRules.SystemId, origin, fixture.Clock.Now);
            using CatalogDbContext catalog = new(database.CatalogPath);
            origin +=
                CombatAnomalyCatalog.Find(catalog, 3454)!.Spawner
                + (nearScenery ? new SpacePosition(1000, 0, 0) : new SpacePosition(0, 100_000, 0));
        }
        fixture.Clock.Seconds += NpcEncounterDefinitions.RespawnSeconds + 1;
        fixture.Db.Store.SetPosition(fixture.Pilot.ShipId, origin, CombatAnomalyRules.SystemId);
        var observer = fixture.Db.Store.FindPilot(1000002)!;
        fixture.Db.Store.SetPosition(observer.ShipId, origin + new SpacePosition(1e10, 0, 0), CombatAnomalyRules.SystemId);
        fixture.Db.Services.GetRequiredService<IPilotContacts>().Add(90000001, observer.PilotId, 0, true);
        using ReplayClient pod = new(fixture, sites, 1000001);
        using ReplayClient remote = new(fixture, sites, 1000002);
        Assert.Equal(FleetDefinitions.CapsuleGroup, pod.Context.Pilot.ShipGroupId);
        List<double> milliseconds = [];
        int commands = 0;
        int connections = 0;
        int fits = 0;
        int maxBytes = 0;
        for (int frame = 0; frame < 35; frame++)
        {
            fixture.Clock.Seconds++;
            pod.Bytes = 0;
            remote.Bytes = 0;
            using var metrics = SessionPerformance.Begin("idle pod frame", _ => { }, force: true)!;
            long started = Stopwatch.GetTimestamp();
            await pod.Advance();
            await remote.Advance();
            if (frame < 5)
            {
                continue;
            }
            milliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            commands += metrics.Commands;
            connections += metrics.Connections;
            fits += metrics.FittingCalculations;
            maxBytes = Math.Max(maxBytes, pod.Bytes + remote.Bytes);
            if (!nearScenery)
            {
                Assert.Equal(origin, pod.Context.Pilot.Position);
            }
            else
            {
                Assert.InRange(pod.Context.Pilot.Position!.DistanceTo(origin), 0, 0.01);
            }
            Assert.Equal(populated, pod.Context.Flight.VisibleNpcs.Count > 0);
        }
        output.WriteLine(
            $"Populated={populated}, nearScenery={nearScenery}, two-session frames={milliseconds.Count}, "
                + $"p95Ms={milliseconds.Order().ElementAt(28):F2}, SQL={commands}, connections={connections}, fits={fits}, "
                + $"maxBytes={maxBytes}, drift={pod.Context.Pilot.Position!.DistanceTo(origin):F2}, "
                + $"speed={pod.Context.Flight.Velocity.Length:F2}"
        );
        Assert.Equal(0, commands);
        Assert.Equal(0, connections);
        Assert.Equal(0, fits);
        Assert.Equal(0, maxBytes);
        var owner = LiveWorld.Find(database.Path)!;
        owner.MeasureCheckpoints = true;
        await owner.FlushAsync();
        var checkpoint = owner.LastCheckpoint!;
        output.WriteLine(
            $"Checkpoint: captureMs={checkpoint.Capture.TotalMilliseconds:F2}, projectionMs={checkpoint.Projection.TotalMilliseconds:F2}, "
                + $"writeMs={checkpoint.Write.TotalMilliseconds:F2}, SQL={checkpoint.Commands}"
        );
    }

    private sealed class ReplayClient : IDisposable
    {
        private readonly IServiceScope _scope;
        private readonly PilotSessionTick _tick;
        private readonly PilotSessionOutput _transport;
        private readonly long _account;
        public PilotCallContext Context { get; }
        public int Bytes { get; set; }

        internal ReplayClient(NpcCombatTests.Fixture fixture, CombatAnomalyStore sites, long account)
        {
            _account = account;
            _scope = fixture.Db.Services.CreateScope();
            var services = _scope.ServiceProvider;
            var pilot = fixture.Db.Store.FindPilot(account)!;
            var factory = services.GetRequiredService<FlightSessionFactory>();
            Context = new(account, pilot.Account, [pilot], factory.Create, account, SignedSettings.Empty);
            Context.Select(pilot);
            Context.Flight.Snapshot();
            var updates = services.GetRequiredService<PilotUpdateCoordinator>();
            updates.Enter(Context);
            var router = services.GetRequiredService<PilotCallRouter>();
            Assert.IsType<PilotCallResult.Success>(
                router.Call(Context, "onlineStatus", "GetInitialState", Array.Empty<object?>(), _ => { })
            );
            Assert.IsType<PilotCallResult.Success>(router.Call(Context, "userSvc", "GetRedeemTokens", Array.Empty<object?>(), _ => { }));
            _tick = new(
                fixture.Clock,
                updates,
                services.GetRequiredService<SkillHandler>(),
                services.GetRequiredService<IAsteroidField>(),
                services.GetRequiredService<IPilotOperationScope>(),
                anomalies: new(sites, fixture.Clock)
            );
            _transport = new(
                Context,
                account,
                account,
                packet =>
                {
                    Bytes += BlueMarshal.Encode(packet).Length;
                    return Task.CompletedTask;
                },
                _ => { }
            );
        }

        internal Task Advance() => _tick.AdvanceAsync(Context, _transport, _ => { }, _account, _account);

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
        }
    }
}
