using System.Text.Json;
using Eden.Data.Catalog.FactionWarfare;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Corporations;
using Eden.Data.World.FactionWarfare;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Exploration;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.FactionWarfare;

public sealed partial class MilitiaComplexTests
{
    private const int SystemId = 30002087;
    private const int FirstAccount = 1000001;
    private const int SecondAccount = 1000002;
    private const int OpenDungeon = 9668;
    private const int ScoutDungeon = 13351;
    private const int CapturePoint = 29423;
    private const int ScoutBeacon = 89193;
    private const int OpenBeacon = 75924;
    private const int AccelerationGate = 17831;
    private const int Stabilizer = 10998;
    private const int OpenDefender = 75369;
    private static readonly long OpenSlot = MilitiaComplexRules.Slot(SystemId, MilitiaComplexFamily.Open);

    [Fact]
    public async Task SharedCapturePausesAndReversesThenSettlesOnceAcrossCheckpoint()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        WorldDatabase database = new(fixture.Path);
        LoadComplexCatalog(database);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var complexes = fixture.Services.GetRequiredService<IMilitiaComplexes>();
        var warzones = fixture.Services.GetRequiredService<IFactionWarzones>();
        var defender = fixture.Store.FindPilot(FirstAccount)!;
        var attacker = fixture.Store.FindPilot(SecondAccount)!;
        var open = complexes.Sites(SystemId, clock.Now).Single(s => s.DungeonId == OpenDungeon);
        var point = open.Objects.Single(body => body.MilitiaCapture is not null);
        var sit = point.Position;
        fixture.Store.SetPosition(defender.ShipId, sit + new SpacePosition(MilitiaComplexRules.CaptureRange, 0, 0), SystemId);
        fixture.Store.SetPosition(attacker.ShipId, sit, SystemId);
        var defense = new MilitiaComplexPilot(defender.PilotId, defender.ShipId, sit);
        var offense = new MilitiaComplexPilot(attacker.PilotId, attacker.ShipId, sit);
        var policy = MilitiaComplexRules.Policy(MilitiaComplexFamily.Open, 5);
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(new SavedMilitiaEnlistment { PilotId = defender.PilotId, DirectFaction = MilitiaFaction.Minmatar });
            action.Add(new SavedMilitiaEnlistment { PilotId = attacker.PilotId, DirectFaction = MilitiaFaction.Amarr });
            action.Add(
                new SavedWarzoneOccupation
                {
                    SystemId = SystemId,
                    Occupier = MilitiaFaction.Minmatar,
                    VictoryPoints = FactionWarzoneRules.VictoryPointThreshold,
                }
            );
            var corporation = action
                .Add(
                    new SavedCorporation
                    {
                        Name = "Test Corporation",
                        Ticker = "TST",
                        DetailsJson = JsonSerializer.Serialize(
                            new CorporationDetails(
                                "Test Corporation",
                                "TST",
                                "",
                                "",
                                0,
                                .10,
                                new(null, null, null, null, null, null, null),
                                true,
                                false
                            )
                        ),
                        CeoId = defender.PilotId,
                        CreatorId = defender.PilotId,
                        StationId = (int)defender.Home.Id,
                        SystemId = defender.HomeSystemId,
                        CreatedAt = clock.Now,
                        MemberLimit = 10,
                    }
                )
                .Entity;
            action.SaveChanges();
            action.Set<Pilot>().Find(defender.PilotId)!.CorporationId = corporation.Id;
            action.Add(
                new SavedMilitiaComplex
                {
                    Slot = OpenSlot,
                    Occupier = MilitiaFaction.Minmatar,
                    Progress = policy.Duration - 1,
                }
            );
            action.Commit();
        }
        int corp = live.Economy.Inspect(action => action.Set<Pilot>().Find(defender.PilotId)!.CorporationId);
        var issuer = MilitiaRules.TribalLiberationForce;
        complexes.Advance(SystemId, [offense, defense], clock.Now);
        var npc = Assert.Single(
            live.Economy.Inspect(w =>
                w.Filter<WorldNpc>(n => n.EncounterId == open.Id && n.Role == NpcRole.MilitiaComplexDefender).ToArray()
            )
        );
        Assert.Equal(OpenDefender, npc.TypeId);
        Assert.Equal(attacker.ShipId, live.Economy.Inspect(w => w.Set<NpcAggression>().Find(npc.Id)!.TargetShipId));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        complexes.Advance(SystemId, [offense], clock.Now);
        Assert.Equal(policy.Duration - 1, Progress(live));
        var blocked = complexes
            .Sites(SystemId, clock.Now)
            .Single(s => s.Id == open.Id)
            .Objects.Single(body => body.MilitiaCapture is not null);
        Assert.True(blocked.MilitiaCapture!.Paused);
        using (var action = live.Economy.Begin(new(resources: [new(SharedSimulationResource.Npc, npc.Id)])))
        {
            action.Set<WorldNpc>().Find(npc.Id)!.DestroyedAt = clock.Now;
            action.Remove(action.Set<NpcAggression>().Find(npc.Id)!);
            action.Commit();
        }

        // No credit for a newly observed attacker or a newly arrived defender.
        complexes.Advance(SystemId, [offense, offense], clock.Now + 1);
        Assert.Equal(policy.Duration - 1, Progress(live));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        complexes.Advance(SystemId, [defense, offense], clock.Now);
        Assert.Equal(policy.Duration - 1, Progress(live));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        complexes.Advance(SystemId, [defense], clock.Now);
        Assert.Equal(policy.Duration - 1, Progress(live));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        complexes.Advance(SystemId, [defense], clock.Now);
        Assert.Equal(policy.Duration - 1 - TimeSpan.TicksPerSecond, Progress(live));
        var reverse = complexes
            .Sites(SystemId, clock.Now)
            .Single(s => s.Id == open.Id)
            .Objects.Single(body => body.MilitiaCapture is not null);
        Assert.Equal(MilitiaFaction.Minmatar, reverse.MilitiaCapture!.Capturing);
        Assert.True(reverse.MilitiaCapture.Points < 0);

        // Seed the final second to exercise exact settlement and checkpoint without waiting out a whole site.
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Set<SavedMilitiaComplex>().Find(OpenSlot)!.Progress = -policy.Duration + TimeSpan.TicksPerSecond;
            action.Commit();
        }

        long points = MilitiaComplexRules.PointsPerPilot(warzones.System(SystemId)!, false, policy, 1);
        long tax = MilitiaComplexRules.CorporationTax(points, .10);
        await live.FlushAsync();
        fixture.Services.Dispose();
        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        live = reopened.GetRequiredService<LiveWorld>();
        complexes = reopened.GetRequiredService<IMilitiaComplexes>();
        warzones = reopened.GetRequiredService<IFactionWarzones>();
        clock.AdvanceWallTime(TimeSpan.FromHours(1));
        complexes.Advance(SystemId, [defense], clock.Now);
        Assert.Equal(-policy.Duration + TimeSpan.TicksPerSecond, Progress(live));
        clock.AdvanceWallTime(TimeSpan.FromTicks(TimeSpan.TicksPerSecond - 1));
        complexes.Advance(SystemId, [defense], clock.Now);
        Assert.Equal(-policy.Duration + 1, Progress(live));
        Assert.Equal(FactionWarzoneRules.VictoryPointThreshold, warzones.System(SystemId)!.VictoryPoints);
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        complexes.Advance(SystemId, [defense], clock.Now);
        complexes.Advance(SystemId, [defense], clock.Now);
        Assert.DoesNotContain(complexes.Sites(SystemId, clock.Now), s => s.Id == open.Id);
        Assert.Equal(74_650, warzones.System(SystemId)!.VictoryPoints);
        CheckBalances(live);

        long due = clock.Now + MilitiaComplexRules.Replacement(warzones.System(SystemId)!.Adjacency).Ticks;
        Assert.DoesNotContain(complexes.Sites(SystemId, due - 1), s => s.DungeonId == OpenDungeon);
        var replacement = Assert.Single(complexes.Sites(SystemId, due), s => s.DungeonId == OpenDungeon);
        Assert.NotEqual(open.Id, replacement.Id);
        Assert.NotEqual(open.TargetId, replacement.TargetId);
        Assert.False(replacement.Started);
        complexes.Advance(SystemId, [defense], due);
        CheckBalances(live);

        using (var metrics = SessionPerformance.Begin("warmed militia complexes", _ => { }, force: true)!)
        {
            Assert.Equal(replacement.Id, Assert.Single(complexes.Sites(SystemId, due), s => s.DungeonId == OpenDungeon).Id);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        await live.FlushAsync();
        reopened.Dispose();
        using var completed = ServerServices.Build(database, clock, liveWorld: true);
        CheckBalances(completed.GetRequiredService<LiveWorld>());
        Assert.Equal(74_650, completed.GetRequiredService<IFactionWarzones>().System(SystemId)!.VictoryPoints);
        var restored = completed.GetRequiredService<IMilitiaComplexes>();
        Assert.DoesNotContain(restored.Sites(SystemId, due - 1), s => s.DungeonId == OpenDungeon);
        Assert.Equal(replacement.Id, Assert.Single(restored.Sites(SystemId, due), s => s.DungeonId == OpenDungeon).Id);

        void CheckBalances(LiveWorld world)
        {
            Assert.Equal(points - tax, world.Economy.Inspect(w => w.Set<SavedLoyaltyBalance>().Find(defender.PilotId, issuer)!.Points));
            Assert.Equal(tax, world.Economy.Inspect(w => w.Set<SavedCorporationLoyaltyBalance>().Find(corp, issuer)!.Points));
            Assert.Null(world.Economy.Inspect(w => w.Set<SavedLoyaltyBalance>().Find(attacker.PilotId, issuer)));
        }
    }

    [Fact]
    public void OffensiveWaveRespawnsAtItsDeadlineAndSharedCaptureAwardsEachEligiblePilot()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        LoadComplexCatalog(new(fixture.Path));
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var complexes = fixture.Services.GetRequiredService<IMilitiaComplexes>();
        var warzones = fixture.Services.GetRequiredService<IFactionWarzones>();
        var first = fixture.Store.FindPilot(FirstAccount)!;
        var second = fixture.Store.FindPilot(SecondAccount)!;
        var open = complexes.Sites(SystemId, clock.Now).Single(s => s.DungeonId == OpenDungeon);
        var position = open.Objects.Single(body => body.MilitiaCapture is not null).Position;
        var a = new MilitiaComplexPilot(first.PilotId, first.ShipId, position);
        var b = new MilitiaComplexPilot(second.PilotId, second.ShipId, position);
        fixture.Store.SetPosition(a.ShipId, position, SystemId);
        fixture.Store.SetPosition(b.ShipId, position + new SpacePosition(MilitiaComplexRules.CaptureRange + 1, 0, 0), SystemId);
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(new SavedMilitiaEnlistment { PilotId = a.PilotId, DirectFaction = MilitiaFaction.Amarr });
            action.Add(new SavedMilitiaEnlistment { PilotId = b.PilotId, DirectFaction = MilitiaFaction.Amarr });
            action.Commit();
        }
        complexes.Advance(SystemId, [a, b], clock.Now);
        var firstWave = LivingDefender();
        Kill(firstWave);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(210) - TimeSpan.FromTicks(1));
        complexes.Advance(SystemId, [a, b], clock.Now);
        Assert.Equal(0, Progress(live));
        Assert.Empty(Defenders());
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        complexes.Advance(SystemId, [a, b], clock.Now);
        var secondWave = LivingDefender();
        Assert.NotEqual(firstWave, secondWave);
        Kill(secondWave);

        var policy = MilitiaComplexRules.Policy(MilitiaComplexFamily.Open, 5);
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Set<SavedMilitiaComplex>().Find(OpenSlot)!.Progress = policy.Duration - TimeSpan.TicksPerSecond;
            action.Commit();
        }
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        complexes.Advance(SystemId, [a], clock.Now);
        Assert.Equal(policy.Duration - TimeSpan.TicksPerSecond, Progress(live));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        a = a with { ConnectionId = 2 };
        complexes.Advance(SystemId, [a], clock.Now);
        Assert.Equal(policy.Duration - TimeSpan.TicksPerSecond, Progress(live));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        fixture.Store.SetPosition(b.ShipId, position, SystemId);
        long expectedLP = MilitiaComplexRules.PointsPerPilot(warzones.System(SystemId)!, true, policy, 2);
        complexes.Advance(SystemId, [a, b, b], clock.Now);
        complexes.Advance(SystemId, [b, a], clock.Now);
        Assert.Equal(350, warzones.System(SystemId)!.VictoryPoints);
        Assert.Equal(expectedLP, Balance(a.PilotId));
        Assert.Equal(expectedLP, Balance(b.PilotId));
        Assert.DoesNotContain(complexes.Sites(SystemId, clock.Now), s => s.Id == open.Id);

        long Balance(long pilot) =>
            live.Economy.Inspect(w => w.Set<SavedLoyaltyBalance>().Find(pilot, MilitiaRules.ImperialCrusade)!.Points);

        long LivingDefender() => Assert.Single(Defenders()).Id;

        WorldNpc[] Defenders() =>
            live.Economy.Inspect(w =>
                w.Filter<WorldNpc>(n => n.EncounterId == open.Id && n.DestroyedAt == null && n.RetiredAt == null).ToArray()
            );

        void Kill(long npc)
        {
            using var action = live.Economy.Begin(new(resources: [new(SharedSimulationResource.Npc, npc)]));
            action.Set<WorldNpc>().Find(npc)!.DestroyedAt = clock.Now;
            action.Remove(action.Set<NpcAggression>().Find(npc)!);
            action.Commit();
        }
    }

    [Fact]
    public void GateValidatesHullFitRangeAndSafetyBeforeFlaggingAndRoutesDirectWarpToEntry()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        WorldDatabase database = new(fixture.Path);
        LoadComplexCatalog(database);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var pilot = fixture.Store.FindPilot(FirstAccount)!;
        var complexes = fixture.Services.GetRequiredService<IMilitiaComplexes>();
        var scout = complexes.Sites(SystemId, clock.Now).Single(s => s.DungeonId == ScoutDungeon);
        var gate = scout.Objects.Single(body => MilitiaComplexRules.IsGate(body.Id));
        var point = scout.Objects.Single(body => body.GroupId == MilitiaComplexRules.CapturePointGroup);
        double range = MilitiaComplexRules.GateRange + gate.Radius + pilot.Flight.ShipRadius;
        fixture.Store.SetPosition(pilot.ShipId, gate.Position + new SpacePosition(range + 1, 0, 0), SystemId);
        Assert.Contains(
            "90 kilometers",
            Assert.Throws<InvalidDataException>(() => complexes.Gate(pilot.PilotId, gate.Id, clock.Now)).Message
        );
        fixture.Store.SetPosition(pilot.ShipId, gate.Position, SystemId);
        Assert.Contains("Partial", Assert.Throws<InvalidDataException>(() => complexes.Gate(pilot.PilotId, gate.Id, clock.Now)).Message);
        Assert.Null(live.Economy.Inspect(w => w.Set<SavedCrimewatch>().Find(pilot.PilotId)));
        long module;
        using (var action = live.Economy.Begin(new(pilots: [pilot.PilotId])))
        {
            action.Set<Pilot>().Find(pilot.PilotId)!.SafetyLevel = ShipSafetyLevel.Partial;
            var item = action
                .Add(
                    new OwnedItem
                    {
                        Id = action.NextItemId(),
                        OwnerId = pilot.PilotId,
                        LocationId = pilot.ShipId,
                        TypeId = Stabilizer,
                        FlagId = (int)InventoryFlag.LowSlot0,
                        Singleton = true,
                        Quantity = 1,
                    }
                )
                .Entity;
            action.SaveChanges();
            module = item.Id;
            action.Commit();
        }
        Assert.Contains(
            "prohibited fitted",
            Assert.Throws<InvalidDataException>(() => complexes.Gate(pilot.PilotId, gate.Id, clock.Now)).Message
        );
        Assert.Null(live.Economy.Inspect(w => w.Set<SavedCrimewatch>().Find(pilot.PilotId)));
        using (var action = live.Economy.Begin(new(pilots: [pilot.PilotId])))
        {
            action.Remove(action.Set<OwnedItem>().Find(module)!);
            action.Commit();
        }
        int shipType = live.Economy.Inspect(w => w.Set<Ship>().Find(pilot.ShipId)!.TypeId);
        SetHull(670);
        Assert.Contains(
            "not permitted",
            Assert.Throws<InvalidDataException>(() => complexes.Gate(pilot.PilotId, gate.Id, clock.Now)).Message
        );
        SetHull(shipType);
        var destination = complexes.Gate(pilot.PilotId, gate.Id, clock.Now);
        Assert.Equal(gate.Position, complexes.Redirect(SystemId, point.Position, clock.Now));
        Assert.Null(live.Economy.Inspect(w => w.Set<SavedCrimewatch>().Find(pilot.PilotId)));
        Assert.Throws<InvalidDataException>(() => complexes.Gate(pilot.PilotId, gate.Id, clock.Now, gate.Position));
        Assert.Null(live.Economy.Inspect(w => w.Set<SavedCrimewatch>().Find(pilot.PilotId)));

        using var scope = fixture.Services.CreateScope();
        var current = fixture.Store.FindPilot(FirstAccount)!;
        using PilotCallContext context = new(
            FirstAccount,
            current.Account,
            [current],
            p => new(fixture.Store, p, clock, new(), [], complexes: complexes),
            42,
            SignedSettings.Empty
        );
        context.Select(current);
        context.Flight.Snapshot();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Assert.IsType<PilotCallResult.Success>(Call("scanMgr", "SignalTrackerRegister"));
        var fullState = Assert.Single(context.Notifications.OfType<SignalTrackerFullState>());
        Assert.Contains(fullState.Sites, site => site.Id == scout.Id);
        Assert.IsType<PilotCallResult.Success>(Call("keeper", "ActivateAccelerationGate", gate.Id));
        Assert.True(context.Flight.PreparingWarp || context.Flight.InWarp);
        Assert.InRange(destination.DistanceTo(point.Position), 0, MilitiaComplexRules.CaptureRange);
        var timers = live.Economy.Inspect(w => w.Set<SavedCrimewatch>().Find(pilot.PilotId)!.Timers());
        Assert.Equal(CrimewatchFlag.Suspect, timers.Flag(clock.Now));
        Assert.Equal(CrimewatchFlag.None, timers.Flag(clock.Now + CrimewatchRules.FlagDuration.Ticks));

        var directory = Assert.IsType<PilotCallResult.Success>(Call("dungeonInstanceCacheMgr", "GetFactionWarfareInstances"));
        var reply = Assert.IsType<Dictionary<long, object?>>(Packet(directory.Reply));
        var row = Assert
            .IsType<List<object?>>(reply[SystemId])
            .Select(item => BlueRead.Fields(Assert.IsType<BlueInstance>(item).State))
            .Single(d => Equals(d["instanceID"], scout.Id));
        Assert.Equal(33L, row["archetypeID"]);
        Assert.Equal((long)ScoutDungeon, row["dungeonID"]);
        Assert.Equal(scout.TargetId, BlueMarshal.Text(row["targetID"]));
        var capture = BlueRead.Fields(
            BlueRead.Fields(Assert.IsType<BlueInstance>(Packet(new StaticSlimItem(point))).State)["capturePoint"]
        );
        Assert.Null(capture["captureID"]);
        Assert.Equal(0d, capture["points"]);
        Assert.Equal(10d, capture["captureTime"]);

        static object? Packet(BlueModel value) => BlueMarshal.Decode(BlueMarshal.Encode(value));

        PilotCallResult Call(string service, string method, params object?[] args) => router.Call(context, service, method, args, _ => { });

        void SetHull(int type)
        {
            using var action = live.Economy.Begin(new(ships: [pilot.ShipId]));
            action.Set<Ship>().Find(pilot.ShipId)!.TypeId = type;
            action.Commit();
        }
    }

    private static long Progress(LiveWorld live) => live.Economy.Inspect(w => w.Set<SavedMilitiaComplex>().Find(OpenSlot)!.Progress);

    private static void LoadComplexCatalog(WorldDatabase database)
    {
        using CatalogDbContext catalog = new(database.CatalogPath, writable: true);
        catalog.Database.ExecuteSql(
            $"""
            INSERT OR IGNORE INTO SolarSystem(Id,ConstellationId,Name)
            SELECT {SystemId},ConstellationId,'Militia test system' FROM SolarSystem WHERE Id=30000142;
            INSERT OR IGNORE INTO ItemCategory(Id,Name) VALUES (11,'Entity');
            INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES (29,'Capsule',6);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (670,'Capsule',29);
            INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES
                (310,'Beacon',2),(922,'Capture Point',11),(366,'Warp Gate',2),(315,'Warp Core Stabilizer',7),(1186,'FW NPC',11);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Radius) VALUES
                ({CapturePoint},'30km Minmatar Capture Point',922,0),({ScoutBeacon},'Scout Advanced Beacon',310,0),
                ({OpenBeacon},'Open Complex Military Beacon',310,0),({AccelerationGate},'Acceleration Gate',366,500),
                ({Stabilizer},'Warp Core Stabilizer',315,0),({OpenDefender},'Republic Battlecruiser',1186,150);
            """
        );
        Add("client/systems", SystemId, """{"securityStatus":0.2}""");
        Add("client/typelist", 854, """{"includedGroupIDs":[237]}""");
        Add("client/typelist", 984, """{"includedGroupIDs":[315]}""");
        foreach (var (id, beacon, archetype) in new[] { (OpenDungeon, OpenBeacon, 36), (ScoutDungeon, ScoutBeacon, 33) })
        {
            var variant = MilitiaComplexVariants.All.Single(v => v.Minmatar == id);
            var room = new Dictionary<string, object>
            {
                ["1"] = new
                {
                    typeID = beacon,
                    objectID = 1,
                    position = new
                    {
                        x = 6_000,
                        y = 8_000,
                        z = 0,
                    },
                },
                ["2"] = new
                {
                    typeID = CapturePoint,
                    objectID = 2,
                    position = new
                    {
                        x = 6_000,
                        y = 8_000,
                        z = 0,
                    },
                },
                ["3"] = new
                {
                    typeID = variant.DefenderSpawner,
                    objectID = 3,
                    position = new
                    {
                        x = 8_000,
                        y = 8_000,
                        z = 0,
                    },
                },
            };
            var rooms = new Dictionary<string, object> { ["1"] = new { objects = room } };
            var dungeon = new Dictionary<string, object>
            {
                ["archetypeID"] = archetype,
                ["dungeonNameID"] = 1000000,
                ["difficulty"] = 4,
                ["entryTypeID"] = 28356,
                ["rooms"] = rooms,
            };
            if (id == ScoutDungeon)
            {
                rooms["2"] = new { objects = new Dictionary<string, object> { ["4"] = new { typeID = AccelerationGate, objectID = 4 } } };
                dungeon["connections"] = new[]
                {
                    new
                    {
                        fromObjectID = 4,
                        allowedShipsList = 854,
                        disallowedFittedTypesList = 984,
                    },
                };
            }
            Add("client/dungeons", id, JsonSerializer.Serialize(dungeon));
        }
        return;

        void Add(string dataset, int key, string data) =>
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord(Dataset,Key,Data) VALUES ({dataset},{key},{data})");
    }
}
