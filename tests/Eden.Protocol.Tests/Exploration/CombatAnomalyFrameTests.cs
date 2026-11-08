using System.Diagnostics;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Combat;
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
using Eden.World.Combat.Npcs;
using Eden.World.Exploration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Exploration;

public sealed class CombatAnomalyFrameTests(ITestOutputHelper output)
{
    [Fact]
    public void DestroyedAnomalyMembersDoNotScheduleRegionalPopulationRetries()
    {
        using NpcCombatTests.Fixture fixture = new(liveWorld: true, useTestClock: true);
        var database = new WorldDatabase(fixture.Db.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        SpacePosition origin = new(1e12, 1e12, 1e12);
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
            world.SaveChanges();
        }
        new CombatAnomalyStore(database).Arrive(CombatAnomalyRules.SystemId, origin, fixture.Clock.Now);
        using (WorldDbContext world = new(database.Path))
        {
            var npc = world.Set<WorldNpc>().First(n => n.EncounterId == CombatAnomalyRules.FirstSiteId);
            npc.DestroyedAt = fixture.Clock.Now;
            world.SaveChanges();
        }
        fixture.Clock.Seconds += NpcEncounterDefinitions.RespawnSeconds + 1;
        using (WorldDbContext world = new(database.Path))
        {
            Assert.Equal(long.MaxValue, NpcPopulationSchedule.Next(world, CombatAnomalyRules.SystemId, fixture.Clock.Now));
        }
        var encounters = fixture.Db.Services.GetRequiredService<INpcEncounters>();
        var initial = encounters.List(CombatAnomalyRules.SystemId, fixture.Clock.Now);
        using var metrics = SessionPerformance.Begin("anomaly population retries", _ => { }, force: true)!;
        for (int second = 0; second < 10; second++)
        {
            fixture.Clock.Seconds++;
            Assert.Equal(initial.Select(n => n.Id), encounters.List(CombatAnomalyRules.SystemId, fixture.Clock.Now).Select(n => n.Id));
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    private sealed class EmptyAsteroids : IAsteroidField
    {
        public IReadOnlyList<StaticSpaceObject> List(int systemId) => [];
    }

    [Theory]
    [InlineData(2351)]
    [InlineData(2352)]
    [InlineData(3454)]
    public async Task CombatSiteFramesDeliverDamageWithoutRepeatedSceneLoadsOrPacketFloods(int dungeonId)
    {
        using NpcCombatTests.Fixture fixture = new(liveWorld: true, useTestClock: true);
        var database = new WorldDatabase(fixture.Db.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using (CatalogDbContext catalog = new(database.CatalogPath, writable: true))
        {
            // Keep the simulated observer alive through the full replay.
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=1000000000 WHERE ItemTypeId=601 AND AttributeId=263");
        }
        SpacePosition origin = new(1e12, 1e12, 1e12);
        using (WorldDbContext world = new(database.Path))
        {
            world.Add(
                new WorldCombatAnomaly
                {
                    Id = CombatAnomalyRules.FirstSiteId,
                    SystemId = CombatAnomalyRules.SystemId,
                    DungeonId = dungeonId,
                    DefinitionVersion = 1,
                    X = origin.X,
                    Y = origin.Y,
                    Z = origin.Z,
                }
            );
            world.SaveChanges();
        }
        CombatAnomalyStore sites = new(database);
        sites.Arrive(CombatAnomalyRules.SystemId, origin, fixture.Clock.Now);
        // An earlier wave's loss remains in the ledger after the regional
        // respawn interval. It must not force population reloads each frame.
        using (WorldDbContext world = new(database.Path))
        {
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
        fixture.Clock.Seconds += NpcEncounterDefinitions.RespawnSeconds + 1;
        using (CatalogDbContext catalog = new(database.CatalogPath))
        {
            origin += CombatAnomalyCatalog.Find(catalog, dungeonId)!.Spawner + new SpacePosition(1000, 0, 0);
        }
        fixture.Db.Store.SetPosition(NpcCombatTests.Fixture.ShipId, origin, CombatAnomalyRules.SystemId);
        using var scope = fixture.Db.Services.CreateScope();
        var services = scope.ServiceProvider;
        var pilot = fixture.Pilot;
        SharedSpace space = services.GetRequiredService<SharedSpace>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(fixture.Db.Store, p, fixture.Clock, space, [], encounters: services.GetRequiredService<INpcEncounters>()),
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        var router = services.GetRequiredService<PilotCallRouter>();
        Assert.IsType<PilotCallResult.Success>(router.Call(context, "onlineStatus", "GetInitialState", Array.Empty<object?>(), _ => { }));
        Assert.IsType<PilotCallResult.Success>(router.Call(context, "userSvc", "GetRedeemTokens", Array.Empty<object?>(), _ => { }));
        CombatAnomalyHandler anomalies = new(sites, fixture.Clock);
        PilotSessionTick tick = new(
            fixture.Clock,
            services.GetRequiredService<PilotUpdateCoordinator>(),
            services.GetRequiredService<SkillHandler>(),
            new EmptyAsteroids(),
            services.GetRequiredService<IPilotOperationScope>(),
            anomalies: anomalies
        );
        int damage = 0;
        int integratedDamage = 0;
        int additions = 0;
        int frameBytes = 0;
        int framePackets = 0;
        PilotSessionOutput transport = new(
            context,
            1000001,
            42,
            packet =>
            {
                byte[] wire = BlueMarshal.Encode(packet);
                frameBytes += wire.Length;
                framePackets++;
                var message = Assert.IsType<BlueInstance>(BlueMarshal.Decode(wire));
                if (!message.Name.EndsWith(".Notification", StringComparison.Ordinal))
                {
                    return Task.CompletedTask;
                }
                var state = BlueRead.Tuple(message.State, 14);
                var destination = Assert.IsType<BlueInstance>(state[2]);
                string name = BlueMarshal.Text(BlueRead.Tuple(destination.State, 4)[1]);
                var payload = BlueRead.Tuple(Assert.Single(BlueRead.Tuple(state[4], 1)), 2);
                var call = BlueRead.Tuple(BlueMarshal.Decode(BlueRead.Bytes(payload[1])), 2);
                var args = Assert.IsType<object?[]>(BlueRead.Tuple(call[1], 2)[1]);
                if (name == "OnDamageMessage")
                {
                    damage++;
                    var fields = Assert.IsType<Dictionary<string, object?>>(Assert.Single(args));
                    Assert.True(Assert.IsType<double>(fields["damage"]) >= 0);
                    if (fields.TryGetValue("weapon", out var weapon))
                    {
                        Assert.True(Assert.IsType<long>(weapon) > 0);
                    }
                    else
                    {
                        integratedDamage++;
                        Assert.Equal("otherPlayerWeapons", BlueMarshal.Text(fields["attackType"]));
                    }
                }
                if (name == "DoDestinyUpdate")
                {
                    var actions = Assert.IsType<List<object?>>(args[0]).Cast<object?[]>().Select(u => (object?[])u[1]!);
                    additions += actions.Count(a => BlueMarshal.Text(a[0]) is "AddBalls" or "SetState");
                }
                return Task.CompletedTask;
            },
            _ => { }
        );
        List<double> milliseconds = [];
        int maxBytes = 0;
        int maxPackets = 0;
        int commands = 0;
        int connections = 0;
        int fits = 0;
        long firstSecond = fixture.Clock.Seconds;
        for (int second = 1; second <= 35; second++)
        {
            fixture.Clock.Seconds = firstSecond + second;
            frameBytes = 0;
            framePackets = 0;
            additions = 0;
            using var metrics = SessionPerformance.Begin("combat-anomaly-frame", _ => { }, force: true)!;
            long start = Stopwatch.GetTimestamp();
            await tick.AdvanceAsync(context, transport, _ => { }, 1000001, 42);
            if (second <= 5)
            {
                continue;
            }
            milliseconds.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            maxBytes = Math.Max(maxBytes, frameBytes);
            maxPackets = Math.Max(maxPackets, framePackets);
            commands += metrics.Commands;
            connections += metrics.Connections;
            fits += metrics.FittingCalculations;
            Assert.Equal(0, additions);
        }
        Assert.True(damage > 0);
        Assert.InRange(maxBytes, 1, 64_000);
        Assert.InRange(maxPackets, 1, 64);
        output.WriteLine(
            $"Dungeon={dungeonId}, frames={milliseconds.Count}, damage={damage}, integratedDamage={integratedDamage}, "
                + $"maxBytes={maxBytes}, maxPackets={maxPackets}, "
                + $"p95Ms={milliseconds.Order().ElementAt(28):F2}, SQL={commands}, connections={connections}, fits={fits}"
        );
        var owner = LiveWorld.Find(database.Path)!;
        owner.MeasureCheckpoints = true;
        await owner.FlushAsync();
        var checkpoint = owner.LastCheckpoint!;
        output.WriteLine(
            $"Checkpoint: captureMs={checkpoint.Capture.TotalMilliseconds:F2}, "
                + $"projectionMs={checkpoint.Projection.TotalMilliseconds:F2}, writeMs={checkpoint.Write.TotalMilliseconds:F2}, "
                + $"SQL={checkpoint.Commands}, connections={checkpoint.Connections}"
        );
        if (dungeonId == 2352)
        {
            Assert.Equal(0, commands);
            Assert.Equal(0, connections);
            Assert.Equal(0, fits);
        }
    }
}
