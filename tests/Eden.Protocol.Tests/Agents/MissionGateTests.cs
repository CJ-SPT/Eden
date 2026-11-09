using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Protocol.Exploration;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionGateTests
{
    [Fact]
    public void GatedMissionKeepsEntranceEmptyAndStartsCombatOnlyInDestinationRoom()
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision, live: true);
        var mission = Accept(f, 875);
        var sites = f.Missions.Sites(mission.SystemId);
        Assert.Equal(2, sites.Count);
        var entrance = sites.Single(s => s.Position == mission.Position);
        var room = sites.Single(s => s.Position != mission.Position);
        var gate = Assert.Single(entrance.Gates);
        Assert.Equal(room.Position, gate.Destination);
        Assert.True(entrance.Started);
        Assert.False(room.Started);
        f.Missions.Arrive(mission.SystemId, mission.Position);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Empty(world.Set<SavedMissionMember>());
        }
        f.Missions.Arrive(mission.SystemId, room.Position);
        f.Missions.Arrive(mission.SystemId, room.Position);
        Assert.True(f.Missions.Sites(mission.SystemId).Single(s => s.RoomId == room.RoomId).Started);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(8, world.Set<SavedMissionMember>().Count(m => m.MissionId == mission.Id));
            Assert.All(
                world.Set<WorldNpc>().Where(n => n.AnchorKind == NpcAnchorKind.Mission),
                n => Assert.InRange(new SpacePosition(n.X, n.Y, n.Z).DistanceTo(room.Position), 19000, 61000)
            );
        }
        Assert.False(f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.ObjectiveComplete);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndependentRoomWavesSurviveCheckpointAndCompletionRequiresFinalRoom(bool clearFirst)
    {
        using MissionFixture f = new(4, MissionRules.SecurityDivision, live: true);
        Standing(f);
        var mission = Accept(f, 2537);
        var sites = f.Missions.Sites(mission.SystemId);
        Assert.Equal(3, sites.Count);
        var first = sites.Single(s => s.RoomId == 17284);
        var last = sites.Single(s => s.RoomId == 17285);
        f.Missions.Arrive(mission.SystemId, first.Position);
        if (clearFirst)
        {
            KillWave(f, mission.Id, 0);
            KillWave(f, mission.Id, 1);
        }
        Assert.False(f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.ObjectiveComplete);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(clearFirst ? 21 : 13, world.Set<SavedMissionMember>().Count(m => m.MissionId == mission.Id));
            Assert.Equal(clearFirst ? 1 : 0, world.Set<SavedMissionRoom>().Find(mission.Id, first.RoomId)!.Wave);
            Assert.Null(world.Set<SavedMissionRoom>().Find(mission.Id, last.RoomId));
        }
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        f.StopResident();
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using var reopened = Eden.Server.ServerServices.Build(new(f.Db.Path), f.Clock);
        var missions = reopened.GetRequiredService<IPilotMissions>();
        missions.Arrive(mission.SystemId, first.Position);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(clearFirst ? 21 : 13, world.Set<SavedMissionMember>().Count(m => m.MissionId == mission.Id));
        }
        missions.Arrive(mission.SystemId, last.Position);
        if (!clearFirst)
        {
            // An earlier room's delayed trigger must still work after a later room has started.
            KillWave(f, mission.Id, 0);
            using WorldDbContext world = new(f.Db.Path);
            Assert.Equal(1, world.Set<SavedMissionRoom>().Find(mission.Id, first.RoomId)!.Wave);
            Assert.Equal(2, world.Set<SavedMissionRoom>().Find(mission.Id, last.RoomId)!.Wave);
        }
        for (int wave = 2; wave <= 4; wave++)
        {
            KillWave(f, mission.Id, wave);
            if (wave < 4)
            {
                Assert.False(missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.ObjectiveComplete);
            }
        }
        Assert.True(missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.ObjectiveComplete);
        Assert.Equal(3, missions.Sites(mission.SystemId).Count);
        Assert.All(missions.Sites(mission.SystemId), s => Assert.True(s.Started));
        if (!clearFirst)
        {
            using WorldDbContext world = new(f.Db.Path);
            Assert.Contains(world.Set<WorldNpc>(), n => n.AnchorKind == NpcAnchorKind.Mission && n.DestroyedAt == null);
        }
        var paid = missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, mission.Id);
        Assert.Empty(missions.Sites(mission.SystemId));
        Assert.Equal(mission.Rewards.IskCents, paid.Offer!.Rewards.IskCents);
        Assert.Throws<InvalidDataException>(() =>
            missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, mission.Id)
        );
    }

    [Fact]
    public void GateAuthorityRejectsDistanceWrongSystemCapsulesAndClosedSitesWithoutChangingProgress()
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision);
        var mission = Accept(f, 875);
        var gate = Assert.Single(f.Missions.Sites(mission.SystemId).SelectMany(s => s.Gates));
        Move(f, mission.SystemId, gate.Position + new SpacePosition(1_000_000, 0, 0));
        Assert.Throws<InvalidDataException>(() => f.Missions.Gate(MissionFixture.PilotId, gate.Id));
        Move(f, 30000144, gate.Position);
        Assert.Throws<InvalidDataException>(() => f.Missions.Gate(MissionFixture.PilotId, gate.Id));
        Move(f, mission.SystemId, gate.Position);
        Assert.Equal(gate, f.Missions.Gate(MissionFixture.PilotId, gate.Id));
        int shipType = f.Context.Pilot.ShipTypeId;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<Ship>().Find(f.Context.Pilot.ShipId)!.TypeId = 670;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Missions.Gate(MissionFixture.PilotId, gate.Id));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<Ship>().Find(f.Context.Pilot.ShipId)!.TypeId = shipType;
            world.SaveChanges();
            Assert.Empty(world.Set<SavedMissionMember>());
            Assert.Empty(world.Set<SavedMissionRoom>());
            Assert.False(world.Set<SavedMission>().Find(mission.Id)!.ObjectiveComplete);
        }
        f.Dock(MissionFixture.StationId);
        f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Quit, mission.Id);
        Move(f, mission.SystemId, gate.Position);
        Assert.Throws<InvalidDataException>(() => f.Missions.Gate(MissionFixture.PilotId, gate.Id));
    }

    [Fact]
    public void LegacySingleRoomSaveUpgradesWithoutRespawningKilledEnemies()
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision);
        var mission = f.Accept(f.Request());
        f.Missions.Arrive(mission.SystemId, mission.Position);
        using (WorldDbContext world = new(f.Db.Path))
        {
            f.Kill(world.Set<SavedMissionMember>().First().NpcId);
            world.Database.ExecuteSqlRaw("DROP TABLE WorldMissionRoom; PRAGMA user_version=123;");
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        f.Missions.Arrive(mission.SystemId, mission.Position);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(9, saved.Set<SavedMissionMember>().Count());
        Assert.Equal(8, saved.Set<WorldNpc>().Count(n => n.AnchorKind == NpcAnchorKind.Mission && n.DestroyedAt == null));
        Assert.Empty(saved.Set<SavedMissionRoom>());
        Assert.Equal(mission.Rewards.IskCents, saved.Set<SavedMission>().Find(mission.Id)!.IskCents);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void WarpDisruptionBlocksGateEvenWhenOrdinaryWarpCoreStrengthWouldAllowWarp(bool stabilized, bool whileAligning)
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision);
        var mission = Accept(f, 875);
        var gate = Assert.Single(f.Missions.Sites(mission.SystemId).SelectMany(s => s.Gates));
        Move(f, mission.SystemId, gate.Position);
        var disruption = new DisruptedShip(f.Context.Pilot.ShipId, stabilized) { Active = !whileAligning };
        using var flight = new FlightSession(f.Db.Store, f.Context.Pilot, f.Clock, new(), [], tackle: disruption, missions: f.Missions);
        flight.Snapshot();
        var result = flight.Execute(new FlightCommand.ActivateAccelerationGate(gate.Id));
        if (whileAligning)
        {
            Assert.IsType<PilotCallResult.Success>(result);
            disruption.Active = true;
            flight.RefreshTackle(true);
            Assert.Contains(flight.Updates, u => u.Command is DestinyCommand.Stop);
        }
        else
        {
            Assert.IsType<PilotCallResult.Failure>(result);
            Assert.DoesNotContain(flight.Updates, u => u.Command is DestinyCommand.WarpTo);
        }
        f.Clock.Seconds += 120;
        flight.Checkpoint();
        Assert.InRange(flight.Pilot.Position!.DistanceTo(gate.Position), 0, 2000);
        Assert.False(flight.InWarp);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<SavedMissionRoom>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeGateCallStartsWarpAndRejectsInvalidOrUnavailableGates(bool resident)
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision, live: resident);
        var mission = Accept(f, 875);
        var gate = Assert.Single(f.Missions.Sites(mission.SystemId).SelectMany(s => s.Gates));
        Assert.IsType<PilotCallResult.Failure>(f.Call("keeper", "ActivateAccelerationGate", [gate.Id]));
        Move(f, mission.SystemId, gate.Position);
        var monitor = new CombatAnomalyHandler(f.Services.GetRequiredService<ICombatAnomalies>(), f.Missions, f.Clock);
        monitor.Advance(f.Context);
        Assert.Contains(
            f.Context.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.AddBalls>(),
            addition => addition.Addition.Slims.OfType<StaticSlimItem>().Any(s => s.Body.Id == gate.Id)
        );
        foreach (var args in new object?[][] { [], [null], ["gate"], [gate.Id, 1L], [-1L] })
        {
            Assert.IsType<PilotCallResult.Failure>(f.Call("keeper", "ActivateAccelerationGate", args));
        }
        Assert.IsType<PilotCallResult.Failure>(f.Call("keeper", "ActivateAccelerationGate", [gate.Id], new() { ["extra"] = 1L }));
        Assert.Null(MissionFixture.Reply(f.Call("keeper", "ActivateAccelerationGate", [gate.Id], new())));
        Assert.Null(MissionFixture.Reply(f.Call("keeper", "ActivateAccelerationGate", [gate.Id])));
        var warp = Assert.Single(f.Context.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.WarpTo>());
        Assert.InRange(new SpacePosition(warp.X, warp.Y, warp.Z).DistanceTo(gate.Destination), 0, 2000);
        f.Clock.Seconds += 120;
        f.Context.Flight.Checkpoint();
        Assert.InRange(f.Context.Pilot.Position!.DistanceTo(gate.Destination), 0, 2000);
        f.Missions.Arrive(mission.SystemId, f.Context.Pilot.Position!);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(8, world.Set<SavedMissionMember>().Count(m => m.MissionId == mission.Id));
    }

    [Fact]
    public void DeadspaceWarpsRedirectToEntranceAndRejectInternalBypassesUntilSiteCloses()
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision, live: true);
        var mission = Accept(f, 875);
        var rooms = f.Missions.Sites(mission.SystemId);
        var inner = rooms.Single(s => s.Position != mission.Position);
        var outside = mission.Position + new SpacePosition(MissionGateRules.RoomSpacing * 4, 0, 0);
        Assert.Equal(mission.Position, f.Missions.WarpDestination(mission.SystemId, outside, inner.Position));
        Assert.Throws<InvalidDataException>(() => f.Missions.WarpDestination(mission.SystemId, mission.Position, inner.Position));
        Assert.Throws<InvalidDataException>(() => f.Missions.WarpDestination(mission.SystemId, inner.Position, mission.Position));
        Assert.Equal(outside, f.Missions.WarpDestination(mission.SystemId, inner.Position, outside));
        Assert.Equal(inner.Position, f.Missions.WarpDestination(mission.SystemId + 1, outside, inner.Position));
        var warmed = f.Missions.Sites(mission.SystemId);
        using (var measure = Eden.Infrastructure.Diagnostics.SessionPerformance.Begin("warm deadspace", _ => { }, force: true)!)
        {
            Assert.Equal(mission.Position, f.Missions.WarpDestination(mission.SystemId, outside, inner.Position));
            Assert.Same(warmed, f.Missions.Sites(mission.SystemId));
            Assert.Equal(0, measure.Commands);
            Assert.Equal(0, measure.Connections);
            Assert.Equal(0, measure.FittingCalculations);
        }
        f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Quit, mission.Id);
        Assert.Equal(inner.Position, f.Missions.WarpDestination(mission.SystemId, outside, inner.Position));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlightOwnerRedirectsInnerWarpIncludingStandoffAcrossThePocketBoundary(bool boundary)
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision);
        var mission = Accept(f, 875);
        var inner = f.Missions.Sites(mission.SystemId).Single(s => s.Position != mission.Position);
        var origin = inner.Position + new SpacePosition(MissionGateRules.RoomSpacing * 4, 0, 0);
        Move(f, mission.SystemId, origin);
        var anchor = boundary ? inner.Position - new SpacePosition(300_000, 0, 0) : inner.Position;
        double range = boundary ? 100_000 : 0;
        // The requested boundary anchor is outside the pocket, but its standoff would land inside it.
        Assert.IsType<PilotCallResult.Success>(f.Context.Flight.Execute(new FlightCommand.WarpToPoint(-1, anchor, range)));
        var warp = Assert.Single(f.Context.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.WarpTo>());
        Assert.InRange(new SpacePosition(warp.X, warp.Y, warp.Z).DistanceTo(mission.Position), 0, 2000);
        Assert.True(new SpacePosition(warp.X, warp.Y, warp.Z).DistanceTo(inner.Position) > 1_000_000);
    }

    [Theory]
    [InlineData(2500, 25, false, true)]
    [InlineData(2500.01, 25, false, false)]
    [InlineData(0, 29, false, false)]
    [InlineData(0, 485, false, false)]
    [InlineData(0, 25, true, false)]
    public void TypedGateRulesEnforceRangeShipAndRoomLocks(double gap, int shipGroup, bool locked, bool allowed)
    {
        MissionGate gate = new(1, SpacePosition.Zero, 100, new(1_000_000, 0, 0), locked);
        void Validate() => MissionGateRules.Validate(new(100 + 10 + gap, 0, 0), 10, shipGroup, gate, cleared: false);
        if (allowed)
        {
            Validate();
        }
        else
        {
            Assert.Throws<InvalidDataException>(Validate);
        }
    }

    [Fact]
    public void TypedRoomProgressWaitsForRequiredWavesAndHandlesLateOrRepeatedTriggers()
    {
        MissionEnemyProgress trigger = new(1, 0, true, true, false);
        MissionEnemyProgress survivor = new(2, 0, false, true, false);
        Assert.Equal(new(true, false), MissionEncounterRules.Killed(trigger, 0, 1, [trigger, survivor], new HashSet<long> { 2 }, false));
        Assert.Equal(new(false, false), MissionEncounterRules.Killed(trigger, 1, null, [trigger, survivor], new HashSet<long> { 2 }, true));
        Assert.Equal(new(false, false), MissionEncounterRules.Killed(survivor, 0, null, [trigger, survivor], new HashSet<long>(), false));
        Assert.Equal(new(false, true), MissionEncounterRules.Killed(survivor, 0, null, [trigger, survivor], new HashSet<long>(), true));
        Assert.Equal(
            new(false, true),
            MissionEncounterRules.Killed(
                trigger with
                {
                    CompletesMission = true,
                },
                0,
                1,
                [trigger, survivor],
                new HashSet<long> { 2 },
                false
            )
        );
    }

    private sealed class DisruptedShip(long shipId, bool stabilized) : IPilotTackle
    {
        public bool Active { get; set; } = true;

        public IReadOnlyList<TackleEffect> Incoming(long target) =>
            Active
                ?
                [
                    new(
                        1,
                        2,
                        new(3, 4, DogmaEffect.WarpDisruptor, 10000, 0, 20000, 1, 0, 0, 0),
                        new(shipId, TackleTargetKind.Ship),
                        0,
                        long.MaxValue
                    ),
                ]
                : [];

        public IReadOnlyDictionary<DogmaAttributeId, double> ShipAttributes(long pilotId) =>
            new Dictionary<DogmaAttributeId, double>
            {
                [DogmaAttributeId.WarpScrambleStatus] = stabilized || !Active ? 0 : 1,
                [DogmaAttributeId.MaxVelocity] = 100,
            };

        public void Sweep(long now) { }

        public void Stop(long pilotId, long ship, long moduleId) { }

        public void Cancel(long pilotId, long ship) { }

        public void Disconnect(long pilotId, long ship) { }

        public TackleActivation Activate(
            long pilotId,
            long moduleId,
            DogmaEffect effect,
            TackleTarget? target,
            IReadOnlySet<long> lockedTargets,
            long now
        ) => throw new NotSupportedException();
    }

    private static MissionOffer Accept(MissionFixture f, int missionId)
    {
        for (int i = 0; i < 10; i++)
        {
            var offer = f.Accept(f.Request());
            if (offer.MissionId == missionId)
            {
                return offer;
            }
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Quit, offer.Id);
        }
        throw new InvalidOperationException("Mission is absent from the agent pool.");
    }

    private static void Standing(MissionFixture f)
    {
        using WorldDbContext world = new(f.Db.Path);
        world.Add(
            new SavedNpcStanding
            {
                PilotId = MissionFixture.PilotId,
                SourceId = 1000044,
                Value = 8,
            }
        );
        world.SaveChanges();
    }

    private static void KillWave(MissionFixture f, long missionId, int wave)
    {
        using WorldDbContext world = new(f.Db.Path);
        var ids = world.Set<SavedMissionMember>().Where(m => m.MissionId == missionId && m.Wave == wave).Select(m => m.NpcId).ToArray();
        Assert.NotEmpty(ids);
        foreach (long id in ids)
        {
            f.Kill(id);
        }
    }

    private static void Move(MissionFixture f, int systemId, SpacePosition position)
    {
        f.Db.Store.SetPosition(f.Context.Pilot.ShipId, position, systemId);
        f.Context.ChangeShip(f.Db.Store.FindPilot(1000001)!);
        f.Context.Flight.Snapshot();
    }
}
