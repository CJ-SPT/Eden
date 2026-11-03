using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Repair;
using Eden.Protocol.Targeting;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class NpcAggressionTests
{
    private static readonly NpcTargetCandidate s_pilot = new(NpcCombatTests.Fixture.PilotId, NpcCombatTests.Fixture.ShipId);
    private static readonly NpcTargetCandidate s_wing = new(90000002, 1000000000002);

    [Fact]
    public void DetectionStartsARealFightWithoutAPlayerShotAndPreservesReactionDelay()
    {
        using NpcCombatTests.Fixture f = new();
        f.Combat.Acquire([s_pilot], f.Clock.Now);
        Assert.Equal(new[] { f.Npc.Id }, f.Combat.Aggressors(s_pilot.ShipId));
        f.Clock.Milliseconds = 7499;
        f.Combat.Acquire([s_pilot], f.Clock.Now);
        Assert.Null(f.Fire());
        f.Clock.Milliseconds = 7500;
        Assert.NotNull(f.Fire());
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(20, saved.Set<WorldNpc>().Find(f.Npc.Id)!.ShieldCharge);
        Assert.Equal(197.525, f.Health.Read(s_pilot.PilotId, f.Clock.Now).Remaining.Shield, 8);
        Assert.Empty(saved.Set<NpcBounty>());
        Assert.Empty(saved.Set<PilotWallet>());
        Assert.Empty(saved.Set<ModuleCooldown>());
    }

    [Fact]
    public async Task ClosestShipWinsDeterministicallyAndConcurrentViewersDoNotResetItsDeadline()
    {
        using NpcCombatTests.Fixture f = new();
        f.Db.Store.SetPosition(s_wing.ShipId, f.Pilot.Position!, f.Npc.SystemId);
        await Task.WhenAll(
            Enumerable
                .Range(0, 3)
                .Select(index => Task.Run(() => f.Combat.Acquire(index % 2 == 0 ? [s_wing, s_pilot] : [s_pilot, s_wing], f.Clock.Now)))
        );
        using (WorldDbContext saved = new(f.Db.Path))
        {
            var aggression = Assert.Single(saved.Set<NpcAggression>());
            Assert.Equal(s_pilot.ShipId, aggression.TargetShipId);
            Assert.Equal(f.Clock.Now + 7500 * TimeSpan.TicksPerMillisecond, aggression.ReadyAt);
        }
        f.Db.Store.SetPosition(s_wing.ShipId, f.Npc.Position + new SpacePosition(200, 0, 0), f.Npc.SystemId);
        f.Clock.Milliseconds = 1000;
        f.Combat.Acquire([s_wing, s_pilot], f.Clock.Now);
        Assert.Empty(f.Combat.Aggressors(s_wing.ShipId));
        using WorldDbContext unchanged = new(f.Db.Path);
        Assert.Equal(f.Clock.Now + 6500 * TimeSpan.TicksPerMillisecond, unchanged.Set<NpcAggression>().Single().ReadyAt);
        f.Combat.Disengage(s_pilot.ShipId);
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        _ = encounters.List(f.Npc.SystemId, f.Clock.Now);
        f.Clock.Milliseconds += 1000;
        _ = encounters.List(f.Npc.SystemId, f.Clock.Now);
        f.Combat.Acquire([s_pilot, s_wing], f.Clock.Now);
        Assert.Equal(new[] { f.Npc.Id }, f.Combat.Aggressors(s_wing.ShipId));
    }

    [Fact]
    public void InactiveMismatchedDockedRemoteAndCapsuleCandidatesCannotStartAnEncounter()
    {
        using NpcCombatTests.Fixture f = new();
        f.Combat.Acquire([new(s_pilot.PilotId, s_wing.ShipId), new(s_wing.PilotId, s_pilot.ShipId)], f.Clock.Now);
        Assert.Empty(f.Combat.Aggressors(s_pilot.ShipId));
        f.Combat.Acquire([], f.Clock.Now);
        f.Db.Store.SetPosition(s_pilot.ShipId, null);
        f.Combat.Acquire([s_pilot], f.Clock.Now);
        f.Db.Store.SetPosition(s_pilot.ShipId, f.Npc.Position, 30000142);
        f.Combat.Acquire([s_pilot], f.Clock.Now);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Empty(world.Set<NpcAggression>());
            world.Ships.Find(s_pilot.ShipId)!.TypeId = FleetDefinitions.CapsuleType;
            world.SaveChanges();
        }
        f.Db.Store.SetPosition(s_pilot.ShipId, f.Npc.Position, f.Npc.SystemId);
        f.Combat.Acquire([s_pilot], f.Clock.Now);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<NpcAggression>());
        Assert.Empty(saved.Set<ShipHealthState>());
    }

    [Fact]
    public void DetectionUsesSurfaceDistanceAndNeverTargetsDestroyedNpcs()
    {
        using NpcCombatTests.Fixture f = new();
        double boundary = NpcEncounterDefinitions.DetectionRange + f.Npc.Radius + f.Pilot.Flight.ShipRadius;
        f.Db.Store.SetPosition(s_pilot.ShipId, f.Npc.Position + new SpacePosition(boundary + 1, 0, 0), f.Npc.SystemId);
        f.Combat.Acquire([s_pilot], f.Clock.Now);
        Assert.Empty(f.Combat.Aggressors(s_pilot.ShipId));
        f.Db.Store.SetPosition(s_pilot.ShipId, f.Npc.Position + new SpacePosition(boundary, 0, 0), f.Npc.SystemId);
        f.Combat.Acquire([s_pilot], f.Clock.Now);
        Assert.Single(f.Combat.Aggressors(s_pilot.ShipId));
        f.Combat.Disengage(s_pilot.ShipId);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var npc = world.Set<WorldNpc>().Find(f.Npc.Id)!;
            npc.DestroyedAt = f.Clock.Now;
            npc.Navigation = NpcNavigationState.Idle;
            world.SaveChanges();
        }
        f.Combat.Acquire([s_pilot], f.Clock.Now);
        Assert.Empty(f.Combat.Aggressors(s_pilot.ShipId));
    }

    [Fact]
    public void ReturningNpcCompletesItsReturnBeforeAcquiringWithAFreshDelay()
    {
        using NpcCombatTests.Fixture f = new();
        f.Db.Store.SetPosition(s_pilot.ShipId, f.Npc.Position + new SpacePosition(8000, 0, 0), f.Npc.SystemId);
        f.Combat.Acquire([s_pilot], f.Clock.Now);
        f.Clock.Milliseconds = 7500;
        f.Fire();
        for (int tick = 0; tick < 10; tick++)
        {
            f.Clock.Milliseconds += 1000;
            f.Fire();
        }
        f.Combat.Disengage(s_pilot.ShipId);
        f.Combat.Acquire([s_pilot], f.Clock.Now);
        Assert.Empty(f.Combat.Aggressors(s_pilot.ShipId));
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        for (int tick = 0; tick < 100; tick++)
        {
            f.Clock.Milliseconds += 1000;
            _ = encounters.List(f.Npc.SystemId, f.Clock.Now);
        }
        f.Combat.Acquire([s_pilot], f.Clock.Now);
        using WorldDbContext saved = new(f.Db.Path);
        var aggression = Assert.Single(saved.Set<NpcAggression>());
        Assert.Equal(f.Clock.Now + 7500 * TimeSpan.TicksPerMillisecond, aggression.ReadyAt);
        Assert.Null(aggression.LastShotAt);
        Assert.Null(f.Fire());
    }

    [Fact]
    public void FirstPlayerAttackerRetainsItsTargetAndInvalidReactionDelayCannotSaveAggression()
    {
        using NpcCombatTests.Fixture f = new();
        f.Aggress();
        f.Db.Store.SetPosition(s_wing.ShipId, f.Npc.Position, f.Npc.SystemId);
        f.Combat.Acquire([s_wing, s_pilot], f.Clock.Now);
        Assert.Empty(f.Combat.Aggressors(s_wing.ShipId));
        Assert.Single(f.Combat.Aggressors(s_pilot.ShipId));
        Assert.Throws<InvalidDataException>(() => NpcAggression.Start(f.Npc.Id, s_pilot.ShipId, double.NaN, f.Clock.Now));
        Assert.Throws<InvalidDataException>(() => NpcAggression.Start(f.Npc.Id, s_pilot.ShipId, -1, f.Clock.Now));
    }

    [Fact]
    public void SharedCandidatesExcludeShipsThatCannotParticipateInCombat()
    {
        using NpcCombatTests.Fixture f = new();
        SharedSpace space = new();
        ShipContact contact = new(f.Pilot, SpacePosition.Zero, null, 0, null);
        space.Publish(contact);
        Assert.Equal(new[] { s_pilot }, space.NpcTargets(f.Npc.SystemId));
        Assert.Empty(space.NpcTargets(30000142));
        foreach (
            var unavailable in new[]
            {
                contact with
                {
                    Warp = new(f.Pilot.Position!, f.Pilot.Position! + new SpacePosition(1_000_000, 0, 0), 1, f.Pilot.Flight, 0),
                },
                contact with
                {
                    AlignTo = f.Npc.Position,
                },
                contact with
                {
                    DepartureGate = 123,
                },
                contact with
                {
                    Arriving = true,
                },
                contact with
                {
                    Pilot = f.Pilot with { ShipGroupId = FleetDefinitions.CapsuleGroup },
                },
                contact with
                {
                    Pilot = f.Pilot with { Position = null },
                },
                contact with
                {
                    Health = new(f.Pilot.ShipId, f.Npc.Health.Defense, new(0, 0, 0)),
                },
            }
        )
        {
            space.Publish(unavailable);
            Assert.Empty(space.NpcTargets(f.Npc.SystemId));
        }
        space.Publish(contact);
        space.Leave(contact.Pilot.ShipId);
        Assert.Empty(space.NpcTargets(f.Npc.SystemId));
    }

    [Fact]
    public void SessionDetectionNotifiesIncomingLockAndWarpDisengagesWithoutAPaidShot()
    {
        using NpcCombatTests.Fixture f = new();
        SharedSpace space = new();
        SharedTargetLocks locks = new();
        var fitting = f.Db.Services.GetRequiredService<IPilotFitting>();
        using TargetingHandler targeting = new(f.Clock, fitting, locks);
        MiningHandler mining = new(f.Clock, f.Db.Services.GetRequiredService<IPilotMining>(), targeting);
        CombatHandler weapons = new(f.Clock, f.Weapons, targeting);
        using NpcCombatHandler handler = new(
            f.Clock,
            f.Combat,
            locks,
            mining,
            weapons,
            new(f.Clock, f.Db.Services.GetRequiredService<IPilotShieldBoosting>()),
            new(f.Clock, f.Db.Services.GetRequiredService<IPilotPropulsion>()),
            space
        );
        StaticSpaceObject destination = new(999, 0, 0, 0, 0, f.Pilot.Position! + new SpacePosition(1_000_000, 0, 0), 1);
        using PilotCallContext context = new(
            1000001,
            f.Pilot.Account,
            [f.Pilot],
            p =>
                new(
                    f.Db.Store,
                    p,
                    f.Clock,
                    space,
                    [destination],
                    health: f.Health,
                    encounters: f.Db.Services.GetRequiredService<INpcEncounters>()
                ),
            42,
            []
        );
        context.Select(f.Pilot);
        handler.Enter(context);
        context.Flight.Snapshot();
        handler.Advance(context);
        targeting.Advance(context);
        Assert.True(locks.Contains(f.Npc.Id, s_pilot.ShipId));
        Assert.Contains(context.Notifications, n => n.Name == "OnTarget" && Equals(n.Arguments[0], "otheradd"));
        Assert.Empty(context.NotificationsBeforeFlight);
        Assert.Null(context.Flight.CallWire("beyonce", "CmdWarpToStuff", ["item", destination.Id]));
        handler.Advance(context);
        targeting.Advance(context);
        Assert.Empty(f.Combat.Aggressors(s_pilot.ShipId));
        Assert.False(locks.Contains(f.Npc.Id, s_pilot.ShipId));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ShipHealthState>());
    }
}
