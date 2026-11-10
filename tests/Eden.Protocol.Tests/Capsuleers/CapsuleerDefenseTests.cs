using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Targeting;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Combat;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.Protocol.Tests.Combat;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerDefenseTests
{
    private const long Attacker = 90000002;
    private const long AttackerShip = 1000000000002;
    private const int SystemId = 30000144;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeLockSurvivesPeacefulDefenseAndAlignmentUntilTheTargetEntersWarp(bool resident)
    {
        using NpcCombatTests.Fixture f = new(liveWorld: resident, useTestClock: true);
        ConcordTests.Catalog(f, 0);
        var services = f.Db.Services;
        var space = services.GetRequiredService<SharedSpace>();
        var locks = services.GetRequiredService<SharedTargetLocks>();
        var controller = services.GetRequiredService<CapsuleerDefense>();
        var scene = services.GetRequiredService<ICapsuleerScene>();
        var target = f.Db.Store.FindPilot(1000002)!;
        NavigationState navigation = new();
        var destination = target.Position! + new SpacePosition(1_000_000, 0, 0);
        Assert.True(navigation.StartWarpPoint(target.ShipId, target.Flight, target.Position!, destination, 0, 0, 0));
        void Publish() =>
            scene.Publish(
                new HaulerObservation(
                    target,
                    new(HaulerActivity.Flying, space.Stamp(f.Clock), 0, target.ShipId, target.StationId, 0, navigation.Capture()),
                    f.Health.Read(target.PilotId, f.Clock.Now)
                )
            );
        Publish();
        using PilotCallContext context = new(
            1000001,
            f.Pilot.Account,
            [f.Pilot],
            pilot => new(f.Db.Store, pilot, f.Clock, space, [], health: f.Health),
            42,
            SignedSettings.Empty
        );
        context.Select(f.Pilot);
        context.Flight.Snapshot();
        using TargetingHandler targeting = new(f.Clock, services.GetRequiredService<IPilotFitting>(), locks);
        var request = Assert.IsType<PilotCallResult.Success>(targeting.Call(context, "dogmaIM", "AddTarget", [target.ShipId]));
        var reply = Assert.IsType<TargetLockReply>(request.Reply);
        f.Clock.Seconds += 10;
        targeting.Advance(context);
        Assert.True(targeting.IsLocked(target.ShipId));
        Assert.True(reply.Completion!.IsCompletedSuccessfully);
        context.Notifications.Clear();
        for (int turn = 0; turn < 3; turn++)
        {
            f.Clock.Seconds++;
            Assert.Null(controller.Advance(target, navigation, null, f.Clock.Now, turn, []).State);
            Publish();
            targeting.Advance(context);
            Assert.True(targeting.IsLocked(target.ShipId));
            Assert.True(locks.Contains(context.Pilot.ShipId, target.ShipId));
            Assert.NotNull(navigation.WarpDestination);
            Assert.Null(navigation.Warp);
        }
        Assert.Empty(context.Notifications);
        CombatHandler weapons = new(f.Clock, f.Weapons, targeting);
        Assert.Same(PilotCallResult.None, weapons.Call(context, "dogmaIM", "Activate", [f.Module, "targetAttack", target.ShipId, 100L]));
        Assert.Equal(target.ShipId, weapons.ActiveEffect(context.Pilot, f.Module)!.TargetId);
        navigation.SetVelocity((destination - target.Position!).Unit * target.Flight.MaxSpeed);
        var departure = navigation.Advance(target.ShipId, 1, target.Flight, target.Position!, _ => null, []);
        target = target with { Position = departure.Position };
        Assert.NotNull(navigation.Warp);
        Publish();
        targeting.Advance(context);
        weapons.Advance(context);
        Assert.False(targeting.IsLocked(target.ShipId));
        Assert.False(locks.Contains(context.Pilot.ShipId, target.ShipId));
        Assert.Contains(
            context.Notifications,
            notification => notification.Name == "OnTarget" && Equals(notification.Arguments[0], "lost")
        );
        Assert.Null(weapons.ActiveEffect(context.Pilot, f.Module));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(.2)]
    [InlineData(.8)]
    public void CombatPilotLocksAndRetaliatesAgainstAMissingShotWithNormalCostsAndLegality(double security)
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true, useTestClock: true);
        ConcordTests.Catalog(f, security);
        var database = new WorldDatabase(f.Db.Path);
        using (CatalogDbContext catalog = new(database.CatalogPath, writable: true))
        {
            string data = System.Text.Json.JsonSerializer.Serialize(new { securityStatus = security });
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000144',{data})");
        }
        long enemyGun;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<Account>().Find(1000002L)!.CloneGrade = AccountCloneGrade.Omega;
            world.Pilots.Find(Attacker)!.SafetyLevel = ShipSafetyLevel.Disabled;
            world.Pilots.Find(NpcCombatTests.Fixture.PilotId)!.SafetyLevel = ShipSafetyLevel.Full;
            enemyGun = InventoryPersistence.NextId(world);
            world.Add(
                new OwnedItem
                {
                    Id = enemyGun,
                    OwnerId = Attacker,
                    LocationId = AttackerShip,
                    TypeId = (int)CombatWeaponType.CivilianGatlingRailgun,
                    Singleton = true,
                    Online = true,
                    Quantity = 1,
                    FlagId = (int)InventoryFlag.HighSlot0,
                }
            );
            world.SaveChanges();
        }
        f.Db.Store.SetPosition(AttackerShip, f.Pilot.Position! + new SpacePosition(500, 0, 0), SystemId);
        var space = f.Db.Services.GetRequiredService<SharedSpace>();
        foreach (long account in new long[] { 1000001, 1000002 })
        {
            var pilot = f.Db.Store.FindPilot(account)!;
            space.Publish(new(PilotContact.From(pilot), SpacePosition.Zero, null, 0, null));
        }
        var controller = f.Db.Services.GetRequiredService<CapsuleerDefense>();
        NavigationState navigation = new();
        var peaceful = controller.Advance(f.Pilot, navigation, null, f.Clock.Now, 0, []);
        Assert.Null(peaceful.State);
        var miss = f.Weapons.FirePlayerBank(
            Attacker,
            enemyGun,
            new(f.Pilot.ShipId, new(0, 1_000_000, 0)),
            SpacePosition.Zero,
            f.Clock.Now,
            .99999
        );
        Assert.Equal(0, miss.Shot.Applied);
        var before = f.Health.Read(Attacker, f.Clock.Now);
        var locking = controller.Advance(f.Pilot, navigation, null, f.Clock.Now, 0, []);
        Assert.True(locking.NewThreat);
        Assert.True(locking.HoldsNavigation);
        Assert.False(locking.Escape);
        Assert.Equal(AttackerShip, locking.State!.TargetId);
        Assert.Empty(locking.State.Cycles);
        Assert.False(f.Db.Services.GetRequiredService<IPilotTargetLocks>().Contains(f.Pilot.ShipId, AttackerShip));
        f.Clock.Milliseconds += (locking.State.Locks.Single().ReadyAt - f.Clock.Now) / TimeSpan.TicksPerMillisecond + 1;
        var retaliation = controller.Advance(locking.Pilot, navigation, locking.State, f.Clock.Now, 10, []);
        Assert.Contains(retaliation.State!.Cycles, cycle => cycle.ModuleId == f.Module && cycle.Effect == DogmaEffect.LaserFired);
        Assert.True(f.Health.Read(Attacker, f.Clock.Now).Remaining.Shield < before.Remaining.Shield);
        Assert.Contains(f.Pilot.ShipId, f.Db.Services.GetRequiredService<IPilotTargetLocks>().Targeters(AttackerShip));
        var after = f.Health.Read(Attacker, f.Clock.Now);
        controller.Advance(retaliation.Pilot, navigation, retaliation.State, f.Clock.Now, 10, []);
        Assert.Equal(after.Remaining, f.Health.Read(Attacker, f.Clock.Now).Remaining);
        space.Leave(AttackerShip);
        var lost = controller.Advance(retaliation.Pilot, navigation, retaliation.State, f.Clock.Now, 10, []);
        Assert.Null(lost.State);
        Assert.Empty(f.Db.Services.GetRequiredService<IPilotTargetLocks>().Targeters(AttackerShip));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<NpcBounty>());
        Assert.False(saved.Set<SavedCrimewatch>().Find(NpcCombatTests.Fixture.PilotId)!.Timers().Criminal.Running(f.Clock.Now));
    }

    [Fact]
    public void WarmUnthreatenedDefenseDoesNotReadSqlOrRebuildFittings()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true, useTestClock: true);
        var controller = f.Db.Services.GetRequiredService<CapsuleerDefense>();
        var pilot = f.Pilot;
        NavigationState navigation = new();
        controller.Advance(pilot, navigation, null, f.Clock.Now, 0, []);
        using var metrics = SessionPerformance.Begin("warm peaceful defense", _ => { }, force: true)!;
        for (int turn = 0; turn < 100; turn++)
        {
            var result = controller.Advance(pilot, navigation, null, f.Clock.Now, turn, []);
            Assert.Null(result.State);
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void ACommittedJamPreventsLocksOnOtherAttackersUntilItEnds()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true, useTestClock: true);
        f.Db.Store.SetPosition(AttackerShip, f.Pilot.Position! + new SpacePosition(500, 0, 0), SystemId);
        var enemy = f.Db.Store.FindPilot(1000002)!;
        f.Db.Services.GetRequiredService<SharedSpace>().Publish(new(PilotContact.From(enemy), SpacePosition.Zero, null, 0, null));
        f.Aggress();
        using (WorldDbContext world = new(f.Db.Path))
        {
            NpcEwarAbility ability = new(NpcEwarKind.Jamming, DogmaEffect.NpcEcm, 20_000, 100_000, 0, 1, new());
            world.Add(
                new WorldNpcEwar
                {
                    NpcId = f.Npc.Id,
                    Kind = NpcEwarKind.Jamming,
                    TypeId = f.Npc.TypeId,
                    TargetShipId = f.Pilot.ShipId,
                    StartedAt = f.Clock.Now - 1,
                    ReadyAt = f.Clock.Now + TimeSpan.FromSeconds(20).Ticks,
                    AbilityJson = System.Text.Json.JsonSerializer.Serialize(ability),
                    AppliedJson = "{}",
                }
            );
            world.Add(
                new SavedPilotHostility
                {
                    SourceShipId = AttackerShip,
                    TargetShipId = f.Pilot.ShipId,
                    LastAttackAt = f.Clock.Now,
                    ExpiresAt = f.Clock.Now + TimeSpan.FromMinutes(15).Ticks,
                }
            );
            world.SaveChanges();
        }
        var controller = f.Db.Services.GetRequiredService<CapsuleerDefense>();
        NavigationState navigation = new();
        var jammed = controller.Advance(f.Pilot, navigation, null, f.Clock.Now, 0, []);
        Assert.Equal(AttackerShip, jammed.State!.TargetId);
        Assert.Empty(jammed.State.Locks);
        Assert.Empty(jammed.State.Cycles);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldNpcEwar>().ExecuteDelete();
        }
        var clear = controller.Advance(jammed.Pilot, navigation, jammed.State, f.Clock.Now, 0, []);
        Assert.Single(clear.State!.Locks);
        Assert.True(clear.State.Locks[0].ReadyAt > f.Clock.Now);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NpcMissProvokesDefenseAndRetreatStopsOffensiveRepeats(bool electronic)
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true, useTestClock: true);
        f.Aggress();
        f.Clock.Milliseconds = 7500;
        if (electronic)
        {
            using WorldDbContext world = new(f.Db.Path);
            NpcEwarAbility ability = new(NpcEwarKind.Jamming, DogmaEffect.NpcEcm, 10_000, 100_000, 0, 0, new());
            world.Add(
                new WorldNpcEwar
                {
                    NpcId = f.Npc.Id,
                    Kind = NpcEwarKind.Jamming,
                    TypeId = f.Npc.TypeId,
                    TargetShipId = f.Pilot.ShipId,
                    StartedAt = f.Clock.Now,
                    ReadyAt = f.Clock.Now + TimeSpan.FromSeconds(10).Ticks,
                    AbilityJson = System.Text.Json.JsonSerializer.Serialize(ability),
                    AppliedJson = null,
                }
            );
            world.SaveChanges();
        }
        else
        {
            Assert.NotNull(f.Fire(.99999));
        }
        var controller = f.Db.Services.GetRequiredService<CapsuleerDefense>();
        NavigationState navigation = new();
        var scan = controller.Advance(f.Pilot, navigation, null, f.Clock.Now, 8, []);
        Assert.Equal(f.Npc.Id, scan.State!.TargetId);
        f.Clock.Milliseconds += 30_000;
        var fighting = controller.Advance(scan.Pilot, navigation, scan.State, f.Clock.Now, 38, []);
        Assert.Contains(fighting.State!.Cycles, cycle => cycle.ModuleId == f.Module);
        var fleeing = controller.Advance(fighting.Pilot, navigation, fighting.State, f.Clock.Now, 38, [], forceRetreat: true);
        Assert.True(fleeing.Escape);
        Assert.False(fleeing.HoldsNavigation);
        Assert.DoesNotContain(fleeing.State!.Cycles, cycle => cycle.Effect == DogmaEffect.LaserFired);
    }

    [Fact]
    public void ActivationRulesUseTheTankLayerAndReserveCapacitor()
    {
        HullDefense defense = new(100, 100, 100, 1000, new(1, 1, 1, 1), new(1, 1, 1, 1), new(1, 1, 1, 1));
        ShipHealth health = new(1, defense, new(10, 90, 100));
        bool Enabled(DogmaEffect effect, double cap, bool shield) =>
            CapsuleerDefenseRules.ShouldActivate(effect, 1, health, cap, 100, true, true, false, shield);
        Assert.True(Enabled(DogmaEffect.AssaultDamageControl, 10, true));
        Assert.False(Enabled(DogmaEffect.AssaultDamageControl, 10, false));
        Assert.True(Enabled(DogmaEffect.CapacitorBoosting, 29, true));
        Assert.False(Enabled(DogmaEffect.CapacitorBoosting, 30, true));
        Assert.True(Enabled(DogmaEffect.ShieldBoosting, 10, true));
        Assert.False(Enabled(DogmaEffect.SensorBooster, 19, true));
        Assert.False(Enabled(DogmaEffect.SensorBooster, 20, true));
        Assert.True(Enabled(DogmaEffect.SensorBooster, 21, true));
    }

    [Fact]
    public void TacticalRulesPrioritizeTackleAndRetreatAtTheDefinedBoundaries()
    {
        CapsuleerAttacker Recent(long ship, bool tackle, long at) =>
            new(ship, ship, 1, SpacePosition.Zero, SpacePosition.Zero, 10, 10, at, tackle, false);
        Assert.Equal(1, CapsuleerDefenseRules.Select([Recent(2, false, 10), Recent(1, true, 1)])!.ShipId);
        Assert.Equal(1, CapsuleerDefenseRules.Select([Recent(2, false, 10), Recent(1, false, 10)])!.ShipId);
        Assert.True(CapsuleerDefenseRules.CombatShip(HullClass.Corvette, 1, true));
        Assert.False(CapsuleerDefenseRules.CombatShip(HullClass.Hauler, 1, true));
        Assert.False(CapsuleerDefenseRules.CombatShip(HullClass.Frigate, 1, false));
        Assert.True(CapsuleerDefenseRules.Offensive(false, true, true));
        Assert.False(CapsuleerDefenseRules.Offensive(false, true, false));
        Assert.False(CapsuleerDefenseRules.Offensive(true, true, true));
        HullDefense defense = new(100, 100, 100, 1000, new(1, 1, 1, 1), new(1, 1, 1, 1), new(1, 1, 1, 1));
        Assert.False(CapsuleerDefenseRules.Retreat(new(1, defense, new(0, 30, 75)), true));
        Assert.True(CapsuleerDefenseRules.Retreat(new(1, defense, new(0, 29, 75)), true));
        Assert.True(CapsuleerDefenseRules.Retreat(new(1, defense, new(100, 100, 74)), true));
        var state = new CapsuleerDefenseState { Cycles = [new(1, 1, DogmaEffect.LaserFired, 2, 10, 15)] };
        Assert.Equal(105, CapsuleerDefenseRules.NextDeadline(state, 10, 100, 110));
    }
}
