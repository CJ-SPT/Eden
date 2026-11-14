using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Data.World.Skills;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Combat;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Tests.Ammunition;
using Eden.Protocol.Tests.Propulsion;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Propulsion;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class CrimewatchTests
{
    private const int SystemId = 30000142;
    private const long WingPilot = 90000002;
    private const long WingShip = 1000000000002;

    [Fact]
    public async Task WarmTravelAndLogoffWritesUseResidentTimersAndSurviveCheckpoints()
    {
        using Fixture f = new();
        Space(f);
        long pilot = f.PilotId;
        long ship = f.ShipId;
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using ResidentFitCache fits = new(new(f.Db.Path));
        live.Economy.Warm();
        var store = Store(f);
        store.CheckTravel(pilot, CrimewatchTravel.Dock);
        store.Disconnect(pilot, ship);
        store.LoggedOff(SystemId);
        store.Connect(pilot);

        long weaponUntil = f.Clock.Now + TimeSpan.TicksPerSecond;
        using (var action = live.Economy.Begin(new(pilots: [pilot])))
        {
            var row = CrimewatchPersistence.State(action, pilot);
            row.SystemId = SystemId;
            row.WeaponsUntil = weaponUntil;
            row.PvpUntil = f.Clock.Now + TimeSpan.TicksPerMinute;
            action.Commit();
        }
        using (var measured = SessionPerformance.Begin("resident travel and logoff", _ => { }, force: true)!)
        {
            Assert.Throws<InvalidDataException>(() => store.CheckTravel(pilot, CrimewatchTravel.Dock));
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            store.CheckTravel(pilot, CrimewatchTravel.Dock);
            store.Disconnect(pilot, WingShip);
            Assert.Empty(store.LoggedOff(SystemId));
            store.Disconnect(pilot, ship);
            Assert.Equal(f.Clock.Now + CrimewatchRules.PvpDuration.Ticks, Assert.Single(store.LoggedOff(SystemId)).ExpiresAt);
            Assert.Equal(0, measured.Commands);
            Assert.Equal(0, measured.Connections);
            Assert.Equal(0, measured.FittingCalculations);
        }
        await live.FlushAsync();
        using (var disk = WorldConnections.FileConnection(f.Db.Path))
        using (WorldDbContext reopened = new(disk))
        {
            var saved = reopened.Set<SavedLogoff>().Single();
            Assert.Equal(ship, saved.ShipId);
            Assert.Equal(SystemId, saved.SystemId);
            Assert.Equal(f.Clock.Now + CrimewatchRules.PvpDuration.Ticks, saved.ExpiresAt);
        }
        using (var measured = SessionPerformance.Begin("resident reconnect", _ => { }, force: true)!)
        {
            store.Connect(pilot);
            store.Connect(pilot);
            Assert.Empty(store.LoggedOff(SystemId));
            Assert.Equal(0, measured.Commands);
            Assert.Equal(0, measured.Connections);
            Assert.Equal(0, measured.FittingCalculations);
        }
        await live.FlushAsync();
        using var clearedDisk = WorldConnections.FileConnection(f.Db.Path);
        using WorldDbContext cleared = new(clearedDisk);
        Assert.Empty(cleared.Set<SavedLogoff>());
    }

    [Fact]
    public void LoggedOffContactsRefreshDirtyPositionsWithoutSqlAndKeepExactDeadlines()
    {
        using Fixture f = new();
        Space(f);
        long ship = f.ShipId;
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using ResidentFitCache fits = new(new(f.Db.Path));
        live.Economy.Warm();
        var store = Store(f);
        store.Disconnect(f.PilotId, ship);
        var first = Assert.Single(store.LoggedOff(SystemId));
        Assert.Empty(store.LoggedOff(SystemId + 1));
        for (int frame = 1; frame <= 3; frame++)
        {
            long x = 10_000_000 + frame;
            live.Economy.Read(
                world =>
                {
                    world.Positions.Find(ship)!.X = x;
                    PilotTrainingStore.CreditSilentFreePoints(world, WingPilot, 1);
                    return true;
                },
                new(systems: [SystemId])
            );
            using var metrics = SessionPerformance.Begin("dirty logoff contact refresh", _ => { }, force: true)!;
            var current = Assert.Single(store.LoggedOff(SystemId));
            Assert.Equal(x, current.Pilot.Position!.X);
            Assert.Equal(first.ExpiresAt, current.ExpiresAt);
            Assert.Equal(10_000_000, first.Pilot.Position!.X);
            Assert.Empty(store.LoggedOff(SystemId + 1));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using (var transaction = live.Economy.Begin(new(systems: [SystemId])))
        {
            transaction.Set<SavedLogoff>().Find(f.PilotId)!.ExpiresAt += TimeSpan.TicksPerMinute;
        }
        Assert.Equal(first.ExpiresAt, Assert.Single(store.LoggedOff(SystemId)).ExpiresAt);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(first.ExpiresAt - f.Clock.Now - 1));
        Assert.Single(store.LoggedOff(SystemId));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Empty(store.LoggedOff(SystemId));
        store.Connect(f.PilotId);
        Assert.Empty(store.LoggedOff(SystemId));
    }

    private static void Security(Fixture f, double value)
    {
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        string json = System.Text.Json.JsonSerializer.Serialize(new { securityStatus = value });
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142',{json})");
    }

    private static void Space(Fixture f)
    {
        f.Db.Store.SetPosition(f.ShipId, new(10_000_000, 0, 0));
        f.Db.Store.SetPosition(WingShip, new(10_001_000, 0, 0));
    }

    private static IPilotCrimewatch Store(Fixture f) => f.Db.Services.GetRequiredService<IPilotCrimewatch>();

    private static TackleActivation Attack(Fixture f, long module, bool wing = false) =>
        f
            .Db.Services.GetRequiredService<IPilotTackle>()
            .Activate(
                wing ? WingPilot : f.PilotId,
                module,
                DogmaEffect.StasisWebifier,
                new(wing ? f.ShipId : WingShip, TackleTargetKind.Ship),
                new HashSet<long> { wing ? f.ShipId : WingShip },
                f.Clock.Now
            );

    private static long WingFit(Fixture f)
    {
        long module = f.Fit(526);
        using WorldDbContext world = new(f.Db.Path);
        var item = world.Set<OwnedItem>().Find(module)!;
        item.OwnerId = WingPilot;
        item.LocationId = WingShip;
        foreach (var skill in world.Set<PilotSkill>().Where(s => s.PilotId == f.PilotId).ToArray())
        {
            var saved =
                world.Set<PilotSkill>().Find(WingPilot, skill.TypeId)
                ?? world.Add(new PilotSkill { PilotId = WingPilot, TypeId = skill.TypeId }).Entity;
            saved.Points = skill.Points;
        }
        world.SaveChanges();
        return module;
    }

    private static PilotCallContext Client(Fixture f, long account)
    {
        var pilot = f.Db.Store.FindPilot(account)!;
        var flights = f.Db.Services.GetRequiredService<FlightSessionFactory>();
        PilotCallContext context = new(account, pilot.Account, [pilot], flights.Create, account, SignedSettings.Empty);
        context.Select(pilot);
        context.Flight.Snapshot();
        context.Notifications.Clear();
        return context;
    }

    [Theory]
    [InlineData(-1, ShipSafetyLevel.Full, false, false, CrimewatchFlag.None)]
    [InlineData(0, ShipSafetyLevel.Full, true, false, CrimewatchFlag.None)]
    [InlineData(.449, ShipSafetyLevel.Full, false, true, CrimewatchFlag.None)]
    [InlineData(.449, ShipSafetyLevel.Partial, false, false, CrimewatchFlag.Suspect)]
    [InlineData(.1, ShipSafetyLevel.Disabled, true, false, CrimewatchFlag.Criminal)]
    [InlineData(.45, ShipSafetyLevel.Full, false, true, CrimewatchFlag.None)]
    [InlineData(1, ShipSafetyLevel.Full, true, true, CrimewatchFlag.None)]
    public void SecuritySafetyAndExistingRightsDetermineLegality(
        double security,
        ShipSafetyLevel safety,
        bool capsule,
        bool engagement,
        CrimewatchFlag expected
    )
    {
        var decision = CrimewatchRules.Decide(new(security, safety, false, capsule, CrimewatchFlag.None, false, engagement));
        Assert.Equal(expected, decision.Flag);
        Assert.Equal(engagement, decision.Engagement);
    }

    [Theory]
    [InlineData(.1, ShipSafetyLevel.Full, false)]
    [InlineData(.1, ShipSafetyLevel.Partial, true)]
    [InlineData(.45, ShipSafetyLevel.Full, false)]
    [InlineData(.45, ShipSafetyLevel.Partial, false)]
    public void IllegalActionsAreRejectedBySafety(double security, ShipSafetyLevel safety, bool capsule)
    {
        Assert.Throws<InvalidDataException>(() =>
            CrimewatchRules.Decide(new(security, safety, false, capsule, CrimewatchFlag.None, false, false))
        );
    }

    [Theory]
    [InlineData(CrimewatchFlag.Suspect, false)]
    [InlineData(CrimewatchFlag.Criminal, false)]
    [InlineData(CrimewatchFlag.None, true)]
    public void FlaggedPilotsAndOutlawsCanBeEngagedWithFullSafety(CrimewatchFlag flag, bool outlaw)
    {
        Assert.Equal(new(CrimewatchFlag.None, true), CrimewatchRules.Decide(new(1, ShipSafetyLevel.Full, true, true, flag, outlaw, false)));
    }

    [Fact]
    public void TimerBoundariesAndCriminalPrecedenceNeverShortenExistingDeadlines()
    {
        long now = DateTime.UtcNow.ToFileTimeUtc();
        var criminal = CrimewatchRules.Player(CrimewatchTimers.Empty, now, CrimewatchFlag.Criminal, true, now);
        var refreshed = CrimewatchRules.Player(criminal, now + TimeSpan.TicksPerSecond, CrimewatchFlag.Suspect, true, now);
        Assert.Equal(CrimewatchFlag.Criminal, refreshed.Flag(now));
        Assert.Equal(0, refreshed.Suspect.ExpiresAt);
        Assert.True(refreshed.Criminal.ExpiresAt > criminal.Criminal.ExpiresAt);
        Assert.Equal(refreshed, CrimewatchRules.Player(refreshed, now, CrimewatchFlag.None, true, now));
        Assert.True(refreshed.Weapons.Running(refreshed.Weapons.ExpiresAt - 1));
        Assert.False(refreshed.Weapons.Running(refreshed.Weapons.ExpiresAt));
        Assert.Equal(CrimewatchFlag.None, refreshed.Flag(refreshed.Criminal.ExpiresAt));
    }

    [Theory]
    [InlineData(.1, ShipSafetyLevel.Full)]
    [InlineData(.8, ShipSafetyLevel.Partial)]
    public void RejectedPlayerTackleLeavesCapacitorCooldownsEffectsAndTimersUnchanged(double security, ShipSafetyLevel safety)
    {
        using Fixture f = TackleTests.Prepare(526);
        long module = f.Fit(526);
        Security(f, security);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<Account>().Find(1000001L)!.CloneGrade = AccountCloneGrade.Omega;
            world.SaveChanges();
        }
        f.Db.Services.GetRequiredService<IPilotSafety>().Set(f.PilotId, safety);
        Space(f);
        using WorldDbContext before = new(f.Db.Path);
        var energy = before.Set<ShipEnergy>().AsNoTracking().SingleOrDefault(s => s.ShipId == f.ShipId);
        Assert.Throws<InvalidDataException>(() => Attack(f, module));
        using WorldDbContext after = new(f.Db.Path);
        Assert.Equal(energy?.Charge, after.Set<ShipEnergy>().Find(f.ShipId)?.Charge);
        Assert.Equal(energy?.UpdatedAt, after.Set<ShipEnergy>().Find(f.ShipId)?.UpdatedAt);
        Assert.Null(after.Set<ModuleCooldown>().Find(module));
        Assert.Empty(after.Set<WorldTackle>());
        Assert.Empty(after.Set<SavedCrimewatch>());
        Assert.Empty(after.Set<SavedEngagement>());
        Assert.Empty(after.Set<SavedLogoff>());
    }

    [Fact]
    public void LowSecurityAggressionAndFullSafetyRetaliationPersistThroughCheckpointAndReopen()
    {
        using Fixture f = TackleTests.Prepare(526);
        long source = f.Fit(526);
        long wing = WingFit(f);
        Security(f, .1);
        f.Db.Services.GetRequiredService<IPilotSafety>().Set(f.PilotId, ShipSafetyLevel.Partial);
        Space(f);
        var shot = Attack(f, source);
        var attacker = Store(f).Read(f.PilotId, SystemId);
        var victim = Store(f).Read(WingPilot, SystemId);
        Assert.Equal(CrimewatchFlag.Suspect, attacker.Timers.Flag(f.Clock.Now));
        Assert.True(attacker.Timers.Weapons.Active(f.Clock.Now));
        Assert.Equal(shot.ReadyAt + CrimewatchRules.PvpDuration.Ticks, victim.Timers.Pvp.ExpiresAt);
        Assert.False(victim.Timers.Weapons.Running(f.Clock.Now));
        Attack(f, wing, wing: true);
        attacker = Store(f).Read(f.PilotId, SystemId);
        victim = Store(f).Read(WingPilot, SystemId);
        Assert.Equal(WingPilot, Assert.Single(attacker.Engagements).OtherPilotId);
        Assert.Equal(f.PilotId, Assert.Single(victim.Engagements).OtherPilotId);
        Assert.Equal(CrimewatchFlag.None, victim.Timers.Flag(f.Clock.Now));
        string checkpoint = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "crimewatch-checkpoint.sqlite");
        File.Copy(f.Db.Path, checkpoint);
        var reopened = new PilotCrimewatchStore(new(checkpoint), f.Clock);
        Assert.Equal(attacker.Timers, reopened.Read(f.PilotId, SystemId).Timers);
        Assert.Equal(attacker.Engagements, reopened.Read(f.PilotId, SystemId).Engagements);
        f.Db.Services.GetRequiredService<IPilotSafety>().Set(f.PilotId, ShipSafetyLevel.Full);
        f.Clock.Milliseconds += 6000;
        Attack(f, source);
        Assert.True(Store(f).Read(f.PilotId, SystemId).Engagements[0].ExpiresAt > attacker.Engagements[0].ExpiresAt);
    }

    [Fact]
    public void AlphaHighSecuritySafetyCannotBeLoweredAndSavedChoiceIsProjectedAsFull()
    {
        using Fixture f = TackleTests.Prepare();
        var safety = f.Db.Services.GetRequiredService<IPilotSafety>();
        safety.Set(f.PilotId, ShipSafetyLevel.Disabled);
        Security(f, .45);
        Assert.Throws<InvalidDataException>(() => safety.Set(f.PilotId, ShipSafetyLevel.Partial));
        Assert.Equal(ShipSafetyLevel.Disabled, safety.Read(f.PilotId));
        Assert.Equal(ShipSafetyLevel.Full, Store(f).Read(f.PilotId, SystemId).Safety);
    }

    [Fact]
    public void NativeStatesAndNotificationsFollowActiveCountdownExpiryAndEngagementChangesExactlyOnce()
    {
        using Fixture f = TackleTests.Prepare(526);
        long source = f.Fit(526);
        long wing = WingFit(f);
        Security(f, .1);
        var safety = f.Db.Services.GetRequiredService<IPilotSafety>();
        safety.Set(f.PilotId, ShipSafetyLevel.Partial);
        Space(f);
        using var client = Client(f, 1000001);
        CrimewatchHandler handler = new(Store(f), safety, f.Clock);
        var idle = Assert.IsType<object?[]>(handler.CallWire(client, "crimewatch", "GetClientStates", []));
        Assert.Equal(4, idle.Length);
        var timers = Assert.IsType<object?[]>(idle[0]);
        Assert.Equal(new object?[] { 100, null }, Assert.IsType<object?[]>(timers[0]));
        Assert.Equal(1, idle[3]);
        var shot = Attack(f, source);
        handler.Advance(client);
        Assert.Equal(
            new object?[] { 101, null },
            Assert.Single(client.Notifications.OfType<CombatTimerChanged>(), n => n.Name == "OnWeaponsTimerUpdate").Arguments
        );
        Assert.Contains(client.Notifications, n => n is CrimewatchFlagsChanged);
        client.Notifications.Clear();
        handler.Advance(client);
        Assert.Empty(client.Notifications);
        Attack(f, wing, wing: true);
        handler.Advance(client);
        var created = Assert.Single(client.Notifications.OfType<EngagementChanged>());
        Assert.Equal(new object?[] { WingPilot, -1L }, created.Arguments);
        client.Notifications.Clear();
        f.Clock.Milliseconds += 5000;
        handler.Advance(client);
        Assert.Contains(
            client.Notifications.OfType<CombatTimerChanged>(),
            n =>
                n.Name == "OnWeaponsTimerUpdate"
                && n.Timer.State == CrimewatchTimerState.WeaponsCounting
                && n.Timer.ExpiresAt == shot.ReadyAt + CrimewatchRules.WeaponsDuration.Ticks
        );
        Assert.Equal("OnCrimewatchEngagementStartTimeout", Assert.Single(client.Notifications.OfType<EngagementChanged>()).Name);
        client.Notifications.Clear();
        Attack(f, wing, wing: true);
        handler.Advance(client);
        var stopped = Assert.Single(client.Notifications.OfType<EngagementChanged>());
        Assert.Equal("OnCrimewatchEngagementStopTimeout", stopped.Name);
        Assert.Equal(new object?[] { WingPilot }, stopped.Arguments);
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(16));
        client.Notifications.Clear();
        handler.Advance(client);
        Assert.Equal(
            CrimewatchTimerState.WeaponsIdle,
            Assert.Single(client.Notifications.OfType<CombatTimerChanged>(), n => n.Name == "OnWeaponsTimerUpdate").Timer.State
        );
        Assert.Equal("OnCrimewatchEngagementEnded", Assert.Single(client.Notifications.OfType<EngagementChanged>()).Name);
        Assert.Equal(new[] { f.PilotId }, Assert.Single(client.Notifications.OfType<CrimewatchFlagsChanged>()).Idle);
        client.Notifications.Clear();
        handler.Advance(client);
        Assert.Empty(client.Notifications);
        Assert.False(handler.IsMonitoring);
    }

    [Theory]
    [InlineData(CrimewatchTravel.Dock)]
    [InlineData(CrimewatchTravel.Jump)]
    [InlineData(CrimewatchTravel.ChangeShip)]
    [InlineData(CrimewatchTravel.SafeLogoff)]
    public void WeaponsBlockTravelUntilExactDeadlineButAllowLowSecurityWarp(CrimewatchTravel action)
    {
        using Fixture f = TackleTests.Prepare(526);
        long module = f.Fit(526);
        Security(f, .1);
        f.Db.Services.GetRequiredService<IPilotSafety>().Set(f.PilotId, ShipSafetyLevel.Partial);
        Space(f);
        var shot = Attack(f, module);
        Assert.Throws<InvalidDataException>(() => Store(f).CheckTravel(f.PilotId, action));
        Store(f).CheckTravel(f.PilotId, CrimewatchTravel.Warp);
        var before = f.Db.Store.FindPilot(1000001)!;
        Assert.Throws<InvalidDataException>(() => f.Db.Store.Dock(f.PilotId, f.ShipId, before.StationId));
        Assert.Equal(before.Position, f.Db.Store.FindPilot(1000001)!.Position);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(shot.ReadyAt + CrimewatchRules.WeaponsDuration.Ticks - f.Clock.Now));
        if (action == CrimewatchTravel.SafeLogoff)
        {
            Assert.Throws<InvalidDataException>(() => Store(f).CheckTravel(f.PilotId, action));
        }
        else
        {
            Store(f).CheckTravel(f.PilotId, action);
        }
    }

    [Fact]
    public void OfflineVictimRemainsVisibleAndTackleableAndNewAggressionExtendsSavedPresence()
    {
        using Fixture f = TackleTests.Prepare(526);
        long module = f.Fit(526);
        Security(f, .1);
        f.Db.Services.GetRequiredService<IPilotSafety>().Set(f.PilotId, ShipSafetyLevel.Partial);
        Space(f);
        using var source = Client(f, 1000001);
        var victim = Client(f, 1000002);
        Attack(f, module);
        victim.Dispose();
        var tackle = f.Db.Services.GetRequiredService<IPilotTackle>();
        Assert.Single(tackle.Incoming(WingShip));
        var space = f.Db.Services.GetRequiredService<SharedSpace>();
        Assert.Single(space.Peers(source.Pilot), s => s.Pilot.ShipId == WingShip);
        long deadline = Assert.Single(Store(f).LoggedOff(SystemId)).ExpiresAt;
        f.Clock.Milliseconds += 6000;
        Attack(f, module);
        long extended = Assert.Single(Store(f).LoggedOff(SystemId)).ExpiresAt;
        Assert.True(extended > deadline);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(extended - f.Clock.Now));
        Assert.Empty(Store(f).LoggedOff(SystemId));
        Assert.DoesNotContain(space.Peers(source.Pilot), s => s.Pilot.ShipId == WingShip);
        Assert.Throws<InvalidDataException>(() => Attack(f, module));
        tackle.Sweep(f.Clock.Now);
        Assert.Empty(tackle.Incoming(WingShip));
        using var reconnected = Client(f, 1000002);
        Assert.Single(space.Peers(source.Pilot), s => s.Pilot.ShipId == WingShip);
        Assert.Empty(Store(f).LoggedOff(SystemId));
    }

    [Fact]
    public void CrashReconciliationKeepsSavedCombatDeadlineAcrossRepeatedRestarts()
    {
        using Fixture f = TackleTests.Prepare(526);
        long module = f.Fit(526);
        Space(f);
        var shot = Attack(f, module);
        var database = new WorldDatabase(f.Db.Path);
        WorldInitialization.Initialize(database, f.Clock);
        long deadline = shot.ReadyAt + CrimewatchRules.PvpDuration.Ticks;
        Assert.All(Store(f).LoggedOff(SystemId), s => Assert.Equal(deadline, s.ExpiresAt));
        Assert.Equal(2, Store(f).LoggedOff(SystemId).Count);
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
        WorldInitialization.Initialize(database, f.Clock);
        Assert.All(Store(f).LoggedOff(SystemId), s => Assert.Equal(deadline, s.ExpiresAt));
        Store(f).Connect(f.PilotId);
        Assert.Single(Store(f).LoggedOff(SystemId));
        Assert.True(Store(f).Read(f.PilotId, SystemId).Timers.Pvp.Running(f.Clock.Now));
    }

    [Fact]
    public void NativeGateJumpRejectsWeaponsTimerWithoutTravelEffectsAndSucceedsAfterExpiry()
    {
        using Fixture f = TackleTests.Prepare(526);
        long module = f.Fit(526);
        var gate = f.Db.Objects.Single(o => o.DestinationGateId.HasValue);
        var position = gate.Position + new SpacePosition(gate.Radius + 100, 0, 0);
        f.Db.Store.SetPosition(f.ShipId, position);
        f.Db.Store.SetPosition(WingShip, position + new SpacePosition(1000, 0, 0));
        var shot = Attack(f, module);
        using var client = Client(f, 1000001);
        var systems = f.Db.Services.GetRequiredService<ISystemObjects>();
        object?[] args = [(long)gate.Id, (long)gate.DestinationGateId!.Value, f.ShipId];
        client.Flight.Updates.Clear();
        Assert.IsType<PilotCallResult.Failure>(client.Flight.StartJump(args, systems));
        Assert.Empty(client.Flight.Updates);
        Assert.Equal(position, client.Flight.Pilot.Position);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(shot.ReadyAt + CrimewatchRules.WeaponsDuration.Ticks - f.Clock.Now));
        Assert.Same(PilotCallResult.None, client.Flight.StartJump(args, systems));
        Assert.NotEmpty(client.Flight.Updates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NpcAndPvpLogoffRetainTheShipForTheirDistinctFullDurations(bool pvp)
    {
        using Fixture f = TackleTests.Prepare();
        Space(f);
        long now = f.Clock.Now;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var saved = new SavedCrimewatch { PilotId = f.PilotId, SystemId = SystemId };
            saved.Apply(
                pvp
                    ? CrimewatchTimers.Empty with
                    {
                        Pvp = new(now, now + TimeSpan.TicksPerSecond),
                    }
                    : CrimewatchTimers.Empty with
                    {
                        Npc = new(now, now + TimeSpan.TicksPerSecond),
                    }
            );
            world.Add(saved);
            world.SaveChanges();
        }
        Store(f).Disconnect(f.PilotId, f.ShipId);
        long deadline = Assert.Single(Store(f).LoggedOff(SystemId)).ExpiresAt;
        Assert.Equal(now + (pvp ? CrimewatchRules.PvpDuration : CrimewatchRules.NpcDuration).Ticks, deadline);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(deadline - f.Clock.Now - 1));
        Assert.Single(Store(f).LoggedOff(SystemId));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Empty(Store(f).LoggedOff(SystemId));
    }

    [Fact]
    public async Task ResidentNpcTimerIsSqlFreeAfterWarmupAndPersistsWithThePaidShot()
    {
        using AmmunitionTests.Fixture f = new(liveWorld: true);
        f.Load(quantity: 400);
        f.Clock.Milliseconds += 5000;
        f.Space();
        f.Fire();
        var pilot = f.Pilot;
        var store = f.Db.Services.GetRequiredService<IPilotCrimewatch>();
        _ = store.Read(pilot.PilotId, pilot.SystemId);
        f.Clock.Milliseconds += 3000;
        CombatShot shot;
        using (var measure = SessionPerformance.Begin("resident crimewatch", _ => { }, force: true)!)
        {
            shot = f.Combat.Fire(pilot.PilotId, f.Gun, f.Npc.Id, SpacePosition.Zero, f.Clock.Now, .5, WeaponActivation.AmmoFed);
            var timer = store.Read(pilot.PilotId, pilot.SystemId).Timers.Npc;
            Assert.True(timer.Active(f.Clock.Now));
            Assert.Equal(
                f.Clock.Now
                    + (long)Math.Ceiling(shot.Weapon.CycleMilliseconds * TimeSpan.TicksPerMillisecond)
                    + CrimewatchRules.NpcDuration.Ticks,
                timer.ExpiresAt
            );
            Assert.Equal(0, measure.Commands);
            Assert.Equal(0, measure.Connections);
        }
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        using WorldDbContext projected = new(f.Db.Path);
        Assert.Equal(
            store.Read(pilot.PilotId, pilot.SystemId).Timers.Npc,
            projected.Set<SavedCrimewatch>().Find(pilot.PilotId)!.Timers().Npc
        );
    }

    [Fact]
    public void VersionEightyNineUpgradePreservesPilotsAndSafetyAndAddsEmptyIdempotentCrimewatchTables()
    {
        using Fixture f = TackleTests.Prepare();
        f.Db.Services.GetRequiredService<IPilotSafety>().Set(f.PilotId, ShipSafetyLevel.Partial);
        var before = f.Db.Store.FindPilot(1000001)!;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "DROP TABLE WorldCrimewatch; DROP TABLE WorldEngagement; DROP TABLE WorldLogoff; PRAGMA user_version=89"
            );
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(before, f.Db.Store.FindPilot(1000001));
        Assert.Equal(ShipSafetyLevel.Partial, Store(f).Read(f.PilotId, SystemId).Safety);
        using WorldDbContext reopened = new(f.Db.Path);
        Assert.Empty(reopened.Set<SavedCrimewatch>());
        Assert.Empty(reopened.Set<SavedEngagement>());
        Assert.Empty(reopened.Set<SavedLogoff>());
        Assert.Equal(WorldMigration.CurrentVersion, reopened.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }
}
