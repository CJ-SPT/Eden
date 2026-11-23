using System.Collections.Immutable;
using Eden.Data.Catalog.Agents;
using Eden.Data.World.Agents;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Docking;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Server;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionReinforcementTests
{
    private const int System = 30000142;
    private const int Room = 42;
    private const int Definition = 9010001;
    private const int Drone = 9010002;
    private const long Mission = 9010003;
    private static readonly SpacePosition s_position = new(125000, 25000, 50000);
    private static readonly TimeSpan s_firstDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_secondDelay = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredTimerAndKillTriggeredWaveKeepSeparateProgressAcrossReopen(bool killAtDeadline)
    {
        using MissionFixture fixture = Create();
        WorldDatabase database = new(fixture.Db.Path);
        using var registration = CatalogProvider.Retain(database.CatalogPath);
        InstallProfile(database, [Immediate(), Immediate(required: false), Timed(s_firstDelay), Timed(s_secondDelay, required: false)]);
        long arrival = fixture.Clock.Now;
        using (var services = ServerServices.Build(database, fixture.Clock, liveWorld: true))
        {
            var live = services.GetRequiredService<LiveWorld>();
            var missions = services.GetRequiredService<IPilotMissions>();
            var reinforcements = services.GetRequiredService<IMissionReinforcements>();
            Seed(live, arrival, arrival + TimeSpan.FromMinutes(5).Ticks);
            missions.Arrive(System, s_position);
            missions.Arrive(System, s_position);
            Assert.Equal([0], Spawned(live));
            Assert.Equal([(2, arrival + s_firstDelay.Ticks), (3, arrival + s_secondDelay.Ticks)], Pending(live));
            fixture.Clock.AdvanceWallTime(s_firstDelay - TimeSpan.FromTicks(1));
            reinforcements.Advance();
            Assert.Equal([0], Spawned(live));
            if (killAtDeadline)
            {
                fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
            }
            Kill(live, database, 0, fixture.Clock.Now);
            Assert.Equal(killAtDeadline ? [0, 1, 2] : new[] { 0, 1 }, Spawned(live));
            Assert.False(Complete(live));
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(database, fixture.Clock, liveWorld: true);
        var resident = reopened.GetRequiredService<LiveWorld>();
        reopened.GetRequiredService<IPilotMissions>().Arrive(System, s_position);
        var expected = killAtDeadline
            ? new[] { (3, arrival + s_secondDelay.Ticks) }
            : [(2, arrival + s_firstDelay.Ticks), (3, arrival + s_secondDelay.Ticks)];
        Assert.Equal(expected, Pending(resident));
        fixture.Clock.AdvanceWallTime(s_secondDelay - TimeSpan.FromTicks(fixture.Clock.Now - arrival));
        using (var action = resident.Economy.Begin(new(systems: [System])))
        using (CatalogDbContext catalog = new(database.CatalogPath))
        {
            var mission = action.Set<SavedMission>().Find(Mission)!;
            PilotMissionStore.SettleMissionReinforcements(
                action,
                catalog,
                mission,
                MissionCatalog.Find(catalog, Definition),
                fixture.Clock.Now
            );
        }
        Assert.Equal(expected, Pending(resident));
        var contacts = reopened.GetRequiredService<INpcEncounters>().List(System, fixture.Clock.Now);
        Assert.Equal(3, contacts.Count(contact => contact.TypeId == Drone));
        var scheduler = reopened.GetRequiredService<IMissionReinforcements>();
        scheduler.Advance();
        scheduler.Advance();
        Assert.Equal([0, 1, 2, 3], Spawned(resident));
        Assert.Empty(Pending(resident));
        Assert.False(Complete(resident));
        Kill(resident, database, 2, fixture.Clock.Now);
        Assert.True(Complete(resident));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClearGateWaitsForRequiredTimersAndIgnoresOptionalTimers(bool required)
    {
        using MissionFixture fixture = Create();
        WorldDatabase database = new(fixture.Db.Path);
        using var registration = CatalogProvider.Retain(database.CatalogPath);
        InstallProfile(database, [Immediate(), Timed(s_firstDelay, required)], withGate: true);
        using var services = ServerServices.Build(database, fixture.Clock, liveWorld: true);
        var live = services.GetRequiredService<LiveWorld>();
        var missions = services.GetRequiredService<IPilotMissions>();
        long arrival = fixture.Clock.Now;
        Seed(live, arrival, arrival + TimeSpan.FromMinutes(5).Ticks);
        MoveToGate(live);
        var gate = Assert.Single(missions.Sites(System).SelectMany(site => site.Gates));
        missions.Arrive(System, s_position);
        Kill(live, database, 0, fixture.Clock.Now);
        if (required)
        {
            Assert.Throws<InvalidDataException>(() => missions.Gate(MissionFixture.PilotId, gate.Id));
        }
        else
        {
            Assert.Equal(gate, missions.Gate(MissionFixture.PilotId, gate.Id));
        }
        fixture.Clock.AdvanceWallTime(s_firstDelay);
        services.GetRequiredService<IMissionReinforcements>().Advance();
        if (required)
        {
            Assert.Throws<InvalidDataException>(() => missions.Gate(MissionFixture.PilotId, gate.Id));
            Kill(live, database, 1, fixture.Clock.Now);
        }
        Assert.Equal(gate, missions.Gate(MissionFixture.PilotId, gate.Id));
    }

    [Fact]
    public void TimerOnlyRoomArmsOnceAndExpiresAtTheExactDeadline()
    {
        using MissionFixture fixture = Create();
        WorldDatabase database = new(fixture.Db.Path);
        using var registration = CatalogProvider.Retain(database.CatalogPath);
        InstallProfile(database, [Timed(s_firstDelay)]);
        using var services = ServerServices.Build(database, fixture.Clock, liveWorld: true);
        var live = services.GetRequiredService<LiveWorld>();
        var missions = services.GetRequiredService<IPilotMissions>();
        long arrival = fixture.Clock.Now;
        Seed(live, arrival, arrival + s_firstDelay.Ticks);
        Assert.False(Assert.Single(missions.Sites(System)).Started);
        missions.Arrive(System, s_position);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        missions.Arrive(System, s_position);
        Assert.True(Assert.Single(missions.Sites(System)).Started);
        Assert.Equal([(0, arrival + s_firstDelay.Ticks)], Pending(live));
        fixture.Clock.AdvanceWallTime(s_firstDelay - TimeSpan.FromSeconds(1));
        services.GetRequiredService<IMissionReinforcements>().Advance();
        Assert.Empty(Spawned(live));
        Assert.Empty(Pending(live));
        Assert.Empty(missions.Sites(System));
    }

    private static MissionFixture Create() =>
        new(configure: catalog =>
        {
            catalog.Database.ExecuteSqlRaw("INSERT INTO ItemType(Id,Name,GroupId,Radius) VALUES (9010002,'Test survey drone',596,10)");
        });

    private static MissionWave Immediate(bool required = true) => new([new([Drone], 1, 15000, Required: required)], Room);

    private static MissionWave Timed(TimeSpan delay, bool required = true) =>
        new([new([Drone], 1, 35000, Required: required)], Room, MissionWaveTrigger.RoomArrivalDelay, DelayTicks: delay.Ticks);

    private static void InstallProfile(WorldDatabase database, ImmutableArray<MissionWave> waves, bool withGate = false)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        const int secondRoom = Room + 1;
        SpacePosition origin = new(0, 0, 0);
        ImmutableArray<MissionRoomProfile> rooms = withGate
            ?
            [
                new(
                    Room,
                    origin,
                    [new(1, 17831, MissionGateRules.AccelerationGateGroup, 2, 0, origin, 100)],
                    [new(1, secondRoom, true, MissionGateAccess.Default)]
                ),
                new(secondRoom, new SpacePosition(MissionGateRules.RoomSpacing, 0, 0), [], []),
            ]
            : [new(Room, origin, [], [])];
        MissionProfile profile = new(
            new(Definition, 1, 500, false, "Test authored", "1", waves),
            "Survey beacon drill",
            0,
            0,
            "",
            Definition,
            0,
            0,
            0,
            0,
            0,
            rooms
        );
        // The fixture's catalog path is private. Its original test encounter never enters the shipped catalog.
        var definition = new CatalogDefinition<MissionProfile?>("agent-mission", (_, id) => id == Definition ? profile : null);
        Assert.Same(profile, catalog.Definition(definition, Definition));
        Assert.Same(profile, MissionCatalog.Find(catalog, Definition));
    }

    private static void MoveToGate(LiveWorld live)
    {
        using var action = live.Economy.Begin(new(systems: [System]));
        var pilot = action.Pilots.Find(MissionFixture.PilotId)!;
        var position =
            action.Set<FlightPosition>().Find(pilot.ActiveShipId) ?? action.Add(new FlightPosition { ShipId = pilot.ActiveShipId }).Entity;
        (position.SystemId, position.X, position.Y, position.Z) = (System, s_position.X, s_position.Y, s_position.Z);
        action.Commit();
    }

    private static void Seed(LiveWorld live, long now, long expires)
    {
        using var action = live.Economy.Begin(new(systems: [System]));
        action.Add(
            new SavedMission
            {
                Id = Mission,
                MissionId = Definition,
                PilotId = MissionFixture.PilotId,
                AgentId = MissionFixture.AgentId,
                SystemId = System,
                State = MissionState.Accepted,
                AcceptedAt = now,
                ExpiresAt = expires,
                X = s_position.X,
                Y = s_position.Y,
                Z = s_position.Z,
            }
        );
        action.Commit();
    }

    private static (int Wave, long Due)[] Pending(LiveWorld live) =>
        live.Economy.Inspect(action =>
            action
                .Filter<SavedMissionReinforcement>(row => row.MissionId == Mission)
                .OrderBy(row => row.Wave)
                .Select(row => ValueTuple.Create(row.Wave, row.DueAt))
                .ToArray()
        );

    private static int[] Spawned(LiveWorld live) =>
        live.Economy.Inspect(action =>
            action.Filter<SavedMissionMember>(member => member.MissionId == Mission).Select(member => member.Wave).Order().ToArray()
        );

    private static bool Complete(LiveWorld live) =>
        live.Economy.Inspect(action => action.Set<SavedMission>().Find(Mission)!.ObjectiveComplete);

    private static void Kill(LiveWorld live, WorldDatabase database, int wave, long now)
    {
        using var action = live.Economy.Begin(new(systems: [System]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var member = action.Filter<SavedMissionMember>(m => m.MissionId == Mission && m.Wave == wave).Single();
        var npc = action.Set<WorldNpc>().Find(member.NpcId)!;
        npc.DestroyedAt = now;
        PilotMissionStore.Killed(action, catalog, npc, now);
        action.Commit();
    }
}
