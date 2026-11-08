using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Docking;
using Eden.Data.World.Insurance;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;
using Eden.Protocol.Combat;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Insurance;
using Eden.World.Inventory;
using Eden.World.Loot;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class PilotMissileTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissileCapsuleImpactRespawnsAtHomeWithoutInsuranceOrNpcRewards(bool resident)
    {
        using var f = Prepare();
        using LiveWorld? live = resident ? new(new(f.Db.Path), f.Clock, _ => { }) : null;
        long launcher = MissileCombatTests.Load(f, Launcher, Charge);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(VictimShip)!.TypeId = FleetDefinitions.CapsuleType;
            world.SaveChanges();
        }
        Space(f);
        AlmostLost(f);
        var home = f.Db.Services.GetRequiredService<Eden.Gameplay.Characters.IPilotHomeStation>().Read(Victim);
        List<PilotDamage> messages = [];
        f.Db.Services.GetRequiredService<PilotDamageSignal>().Applied += messages.Add;
        Launch(f, launcher);
        Advance(f);
        Assert.True(Assert.Single(Store(f).TakeImpacts(f.PilotId)).Destroyed);
        var loss = Assert.Single(messages);
        Assert.Null(loss.Capsule!.Position);
        Assert.Equal(home.Id, loss.Capsule.StationId);
        Assert.Null(loss.Insurance);
        Assert.Equal(2, loss.InventoryChanges.Count);
        Advance(f);
        Assert.Single(messages);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Single(saved.Set<SavedHomeCloneTransfer>());
        Assert.Equal(2, saved.Set<SavedCombatReport>().Count());
        Assert.Empty(saved.Set<WorldWreck>());
        Assert.Empty(saved.Set<NpcBounty>());
        Assert.Empty(saved.Set<SavedInsuranceLedger>());
    }

    private const int Launcher = 499;
    private const int Charge = 212;
    private const int AutomaticCharge = 269;
    private const long Victim = 90000002;
    private const long VictimShip = 1000000000002;
    private const int SystemId = 30000144;

    private static Fixture Prepare(double security = 0)
    {
        var f = MissileCombatTests.Prepare(Launcher, Charge, AutomaticCharge);
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        string data = System.Text.Json.JsonSerializer.Serialize(new { securityStatus = security });
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000144',{data})");
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES (29,'Capsule',6);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Capacity,Mass,Radius) VALUES (670,'Capsule',29,0,32000,2);
            INSERT OR IGNORE INTO TypeAttribute SELECT 670,AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=601;
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','670','{{"published":1,"volume":1000}}');
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','601','{{"wreckTypeID":26557}}');
            """
        );
        return f;
    }

    private static SharedSpace Space(Fixture f, SpacePosition? velocity = null)
    {
        var npc = MissileCombatTests.Space(f);
        f.Db.Store.SetPosition(VictimShip, npc.Position + new SpacePosition(11000, 0, 0), SystemId);
        var space = f.Db.Services.GetRequiredService<SharedSpace>();
        Publish(f, space, 1000001);
        Publish(f, space, 1000002, velocity);
        return space;
    }

    private static void Publish(Fixture f, SharedSpace space, long account, SpacePosition? velocity = null)
    {
        var pilot = f.Db.Store.FindPilot(account)!;
        space.Publish(new(PilotContact.From(pilot), velocity ?? SpacePosition.Zero, null, 0, null));
    }

    private static IPilotMissiles Store(Fixture f) => MissileCombatTests.Missiles(f);

    private static MissileLaunch Launch(Fixture f, long launcher, long? target = VictimShip, bool locked = true) =>
        Store(f)
            .Launch(
                f.PilotId,
                launcher,
                target,
                locked ? new HashSet<long> { target ?? 0 } : new HashSet<long>(),
                new(SpacePosition.Zero, 295, new(0, 0, -1), 42),
                f.Clock.Now
            );

    private static void Advance(Fixture f, int seconds = 8)
    {
        for (int second = 0; second < seconds; second++)
        {
            f.Clock.Milliseconds += 1000;
            Store(f).Advance(SystemId, f.Clock.Now);
        }
    }

    private static void AlmostLost(Fixture f)
    {
        var attributes = f.Db.Services.GetRequiredService<IPilotFitting>().ShipAttributes(Victim);
        var defense = ShipHealth.DefenseFrom(attributes);
        using WorldDbContext world = new(f.Db.Path);
        world.Add(
            new ShipHealthState
            {
                ShipId = VictimShip,
                ShieldCharge = 0,
                ArmorDamage = defense.Armor,
                StructureDamage = defense.Structure - 1,
                UpdatedAt = f.Clock.Now,
            }
        );
        world.SaveChanges();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaunchPaysBeforeFlightAndOneImpactNotifiesTheVictimWithoutNpcRewards(bool resident)
    {
        using var f = Prepare();
        using LiveWorld? live = resident ? new(new(f.Db.Path), f.Clock, _ => { }) : null;
        long launcher = MissileCombatTests.Load(f, Launcher, Charge);
        Space(f);
        List<PilotDamage> hits = [];
        var signal = f.Db.Services.GetRequiredService<PilotDamageSignal>();
        signal.Applied += hits.Add;
        var launch = Launch(f, launcher);
        Assert.Equal(2, launch.Magazine.Quantity);
        Assert.Empty(hits);
        Assert.Empty(Store(f).TakeImpacts(f.PilotId));
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(MissileTargetKind.Pilot, Assert.Single(world.Set<WorldMissile>()).TargetKind);
            Assert.True(world.Set<SavedCrimewatch>().Find(f.PilotId)!.Timers().Weapons.Running(f.Clock.Now));
            Assert.True(world.Set<SavedCrimewatch>().Find(Victim)!.Timers().Pvp.Running(f.Clock.Now));
            Assert.Empty(world.Set<ShipHealthState>());
        }
        Assert.Throws<InvalidDataException>(() => Launch(f, launcher));
        DroneCombatTriggerTests.AssertModule(f.Db.Path, f.ShipId, VictimShip, f.Clock.Now);
        var firedAt = f.Clock.Now;
        Advance(f);
        var outcome = Assert.Single(Store(f).TakeImpacts(f.PilotId));
        var hit = Assert.Single(hits);
        Assert.True(outcome.Applied > 0);
        Assert.Equal(outcome.Applied, hit.Applied);
        Assert.Equal(Victim, hit.PilotId);
        Assert.Equal(Charge, hit.WeaponType);
        Assert.Null(outcome.Reward);
        Assert.False(outcome.Destroyed);
        Advance(f);
        Assert.Empty(Store(f).TakeImpacts(f.PilotId));
        Assert.Single(hits);
        DroneCombatTriggerTests.AssertModule(f.Db.Path, f.ShipId, VictimShip, firedAt);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<NpcBounty>());
        Assert.Empty(saved.Set<SavedSecurityStatus>());
    }

    [Theory]
    [InlineData(.2, ShipSafetyLevel.Full, true)]
    [InlineData(.8, ShipSafetyLevel.Disabled, true)]
    [InlineData(0, ShipSafetyLevel.Full, false)]
    public void RejectedSafetyAndMissingLocksPreserveAmmoCooldownAndAggression(double security, ShipSafetyLevel safety, bool locked)
    {
        using var f = Prepare(security);
        long launcher = MissileCombatTests.Load(f, Launcher, Charge);
        Space(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<Account>().Find(1000001L)!.CloneGrade = AccountCloneGrade.Omega;
            world.Pilots.Find(f.PilotId)!.SafetyLevel = safety;
            world.SaveChanges();
        }
        var ammo = f.Db.Services.GetRequiredService<IPilotAmmunition>();
        var before = Assert.Single(ammo.Read(f.PilotId, f.ShipId));
        Assert.Throws<InvalidDataException>(() => Launch(f, launcher, locked: locked));
        Assert.Equal(before, Assert.Single(ammo.Read(f.PilotId, f.ShipId)));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<SavedCrimewatch>());
        Assert.Empty(saved.Set<SavedPilotHostility>());
        Assert.Empty(saved.Set<WorldMissile>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverloadedLauncherPaysAtLegalLaunchAndDamagesThePilotOnlyAtImpact(bool automatic)
    {
        using var f = Prepare(security: .2);
        PassiveCatalogFixture.LoadOverload(f.CatalogPath);
        f.Train(OverloadRules.ThermodynamicsSkill, 3);
        long launcher = MissileCombatTests.Load(f, Launcher, automatic ? AutomaticCharge : Charge);
        Space(f);
        var store = f.Db.Services.GetRequiredService<PilotMissileStore>();
        var heat = f.Db.Services.GetRequiredService<IPilotShipHeat>();
        IReadOnlyList<MissileLaunch> Pay() =>
            store.LaunchOverloadedBank(
                f.PilotId,
                launcher,
                automatic ? null : VictimShip,
                automatic ? new HashSet<long>() : new HashSet<long> { VictimShip },
                new(SpacePosition.Zero, 295, new(0, 0, -1), 42),
                f.Clock.Now
            );

        f.Db.Services.GetRequiredService<IPilotSafety>().Set(f.PilotId, ShipSafetyLevel.Full);
        Assert.Throws<InvalidDataException>(() => Pay());
        Assert.True(heat.Read(f.PilotId, f.ShipId, f.Clock.Now).Idle);
        Assert.Empty(store.List(SystemId));
        Assert.Equal(3, Assert.Single(f.Db.Services.GetRequiredService<IPilotAmmunition>().Read(f.PilotId, f.ShipId)).Quantity);
        if (automatic)
        {
            using WorldDbContext world = new(f.Db.Path);
            CrimewatchPersistence.Player(
                world,
                Victim,
                f.PilotId,
                SystemId,
                new(CrimewatchFlag.None, false),
                f.Clock.Now,
                f.Clock.Now + TimeSpan.TicksPerSecond
            );
            world.SaveChanges();
        }
        f.Db.Services.GetRequiredService<IPilotSafety>().Set(f.PilotId, ShipSafetyLevel.Partial);
        var launch = Assert.Single(Pay());
        Assert.Equal(VictimShip, launch.Projectile.TargetId);
        Assert.Equal(automatic, launch.Launcher.Automatic);
        Assert.Equal(2, launch.Magazine.Quantity);
        var cycle = Assert.Single(heat.Read(f.PilotId, f.ShipId, f.Clock.Now).Cycles);
        Assert.Equal(f.Clock.Now + (long)Math.Ceiling(launch.Launcher.CycleMilliseconds * TimeSpan.TicksPerMillisecond), cycle.CompletesAt);
        Assert.Empty(store.TakeImpacts(f.PilotId));
        Advance(f);
        Assert.True(Assert.Single(store.TakeImpacts(f.PilotId)).Applied > 0);
        Assert.Empty(store.TakeImpacts(f.PilotId));
    }

    [Fact]
    public void LowsecLaunchFlagsBeforeImpactAndLaterSafetyChangeDoesNotUndoAPaidMissile()
    {
        using var f = Prepare(.2);
        long launcher = MissileCombatTests.Load(f, Launcher, Charge);
        Space(f);
        var safety = f.Db.Services.GetRequiredService<IPilotSafety>();
        safety.Set(f.PilotId, ShipSafetyLevel.Partial);
        Launch(f, launcher);
        var crimewatch = f.Db.Services.GetRequiredService<IPilotCrimewatch>();
        Assert.Equal(CrimewatchFlag.Suspect, crimewatch.Read(f.PilotId, SystemId).Timers.Flag(f.Clock.Now));
        safety.Set(f.PilotId, ShipSafetyLevel.Full);
        Advance(f);
        Assert.True(Assert.Single(Store(f).TakeImpacts(f.PilotId)).Applied > 0);
    }

    [Theory]
    [InlineData("warp")]
    [InlineData("docked")]
    [InlineData("system")]
    [InlineData("capsule")]
    [InlineData("expired")]
    public void LostTargetsOrphanPaidMissilesWithoutReacquiring(string condition)
    {
        using var f = Prepare();
        long launcher = MissileCombatTests.Load(f, Launcher, Charge);
        var space = Space(f);
        Launch(f, launcher);
        space.Leave(VictimShip);
        using (WorldDbContext world = new(f.Db.Path))
        {
            if (condition == "docked")
            {
                world.Remove(world.Set<FlightPosition>().Find(VictimShip)!);
            }
            else if (condition == "system")
            {
                world.Set<FlightPosition>().Find(VictimShip)!.SystemId = 30000142;
            }
            else if (condition == "capsule")
            {
                // Ship loss creates a new capsule identity; a paid projectile
                // must never reacquire that replacement as its original target.
                var old = world.Ships.Find(VictimShip)!;
                var capsule = world
                    .Ships.Add(
                        new Ship
                        {
                            Id = InventoryPersistence.NextId(world),
                            OwnerId = Victim,
                            TypeId = FleetDefinitions.CapsuleType,
                            StationId = old.StationId,
                            Name = "Escaped capsule",
                        }
                    )
                    .Entity;
                world.Pilots.Find(Victim)!.ActiveShip = capsule;
            }
            else if (condition == "expired")
            {
                world.Add(
                    new SavedLogoff
                    {
                        PilotId = Victim,
                        ShipId = VictimShip,
                        SystemId = SystemId,
                        ExpiresAt = f.Clock.Now,
                    }
                );
            }
            world.SaveChanges();
        }
        Advance(f, 1);
        Assert.True(Assert.Single(Store(f).List(SystemId)).Flight.Orphaned);
        if (condition == "warp")
        {
            Publish(f, space, 1000002);
        }
        Advance(f, 30);
        Assert.Empty(Store(f).TakeImpacts(f.PilotId));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ShipLoss>());
        Assert.Empty(saved.Set<ShipHealthState>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutomaticMissilesChooseOnlyAnActualPilotAggressorAndRejectExpiredHostility(bool offline)
    {
        using var f = Prepare();
        long launcher = MissileCombatTests.Load(f, Launcher, AutomaticCharge);
        var space = Space(f);
        Assert.Throws<InvalidDataException>(() => Launch(f, launcher, target: null, locked: false));
        using (WorldDbContext world = new(f.Db.Path))
        {
            var attacker = world.Pilots.Find(Victim)!;
            var victim = world.Pilots.Find(f.PilotId)!;
            CrimewatchPersistence.Player(
                world,
                attacker.Id,
                victim.Id,
                SystemId,
                new(CrimewatchFlag.None, false),
                f.Clock.Now,
                f.Clock.Now + TimeSpan.TicksPerSecond
            );
            world.SaveChanges();
        }
        if (offline)
        {
            f.Db.Services.GetRequiredService<IPilotCrimewatch>().Disconnect(Victim, VictimShip);
            space.Leave(VictimShip);
        }
        Assert.Equal(VictimShip, Launch(f, launcher, target: null, locked: false).Projectile.TargetId);
        f.Clock.AdvanceWallTime(CrimewatchRules.PvpDuration + TimeSpan.FromSeconds(2));
        Assert.Throws<InvalidDataException>(() => Launch(f, launcher, target: null, locked: false));
    }

    [Fact]
    public async Task CompetingFatalImpactsSettleOnePersistentLossAndInsurancePayment()
    {
        using var f = Prepare();
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        long launcher = MissileCombatTests.Load(f, Launcher, Charge);
        InsuranceTestData.Prepare(f.Db);
        Space(f);
        AlmostLost(f);
        Launch(f, launcher);
        f.Clock.Milliseconds += 3000;
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => Store(f).Advance(SystemId, f.Clock.Now))));
        Assert.True(Assert.Single(Store(f).TakeImpacts(f.PilotId)).Destroyed);
        long capsule = f.Db.Store.FindPilot(1000002)!.ShipId;
        live.Dispose();
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(capsule, f.Db.Store.FindPilot(1000002)!.ShipId);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Single(saved.Set<ShipLoss>());
        Assert.Single(saved.Set<WorldWreck>());
        Assert.Equal(2, saved.Set<SavedCombatReport>().Count());
        var payout = Assert.Single(saved.Set<SavedInsuranceLedger>().Where(i => i.IsPayout));
        Assert.Equal(Victim, payout.PilotId);
        Assert.Equal(40000, payout.AmountCents);
        Assert.Empty(saved.Set<NpcBounty>());
        Assert.Empty(Store(f).TakeImpacts(f.PilotId));
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(2000, true, true)]
    [InlineData(10000, false, false)]
    public void TargetSpeedAndDirectionGovernInterceptionAndDamageApplication(int speed, bool approaching, bool hits)
    {
        using var f = Prepare();
        long launcher = MissileCombatTests.Load(f, Launcher, Charge);
        var space = Space(f);
        Launch(f, launcher);
        var origin = f.Db.Store.FindPilot(1000002)!.Position!;
        int velocity = approaching ? -speed : speed;
        for (int second = 1; second <= 8; second++)
        {
            f.Db.Store.SetPosition(VictimShip, origin + new SpacePosition(velocity * second, 0, 0), SystemId);
            Publish(f, space, 1000002, new SpacePosition(velocity, 0, 0));
            Advance(f, 1);
        }
        var outcomes = Store(f).TakeImpacts(f.PilotId);
        if (!hits)
        {
            Assert.Empty(outcomes);
            return;
        }
        var outcome = Assert.Single(outcomes);
        using CatalogDbContext catalog = new(f.CatalogPath);
        using WorldDbContext saved = new(f.Db.Path);
        var payload = Assert.Single(saved.Set<WorldMissile>()).Payload();
        double signature = f.Db.Services.GetRequiredService<IPilotFitting>().ShipAttributes(Victim)[DogmaAttributeId.SignatureRadius];
        double expected = payload.Warhead.Damage.Total * MissileRules.Application(payload.Warhead, signature, speed);
        Assert.Equal(expected, outcome.Damage.Total, 7);
    }

    [Fact]
    public void VersionNinetyOneAddsTargetKindsWithoutChangingSavedPaymentsOrProjectileIdentity()
    {
        using var f = Prepare();
        long launcher = MissileCombatTests.Load(f, Launcher, Charge);
        var npc = MissileCombatTests.Space(f);
        var launch = MissileCombatTests.Launch(f, launcher, npc.Id);
        string snapshot;
        using (WorldDbContext world = new(f.Db.Path))
        {
            snapshot = Assert.Single(world.Set<WorldMissile>()).Snapshot;
            world.Database.ExecuteSqlRaw(
                "ALTER TABLE WorldMissile DROP COLUMN TargetKind; DROP TABLE WorldPilotHostility; PRAGMA user_version=91"
            );
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext saved = new(f.Db.Path);
        var row = Assert.Single(saved.Set<WorldMissile>());
        Assert.Equal(launch.Projectile.Id, row.Id);
        Assert.Equal(snapshot, row.Snapshot);
        Assert.Equal(MissileTargetKind.Npc, row.TargetKind);
        Assert.Equal(MissileFlightStatus.Expired, row.Status);
        Assert.Equal(2, Assert.Single(f.Db.Services.GetRequiredService<IPilotAmmunition>().Read(f.PilotId, f.ShipId)).Quantity);
        Assert.Empty(saved.Set<SavedPilotHostility>());
        Assert.Empty(Store(f).TakeImpacts(f.PilotId));
    }

    [Fact]
    public void OfflineVictimRemainsHittableAndItsPvpDeadlineExtendsAtImpact()
    {
        using var f = Prepare();
        long launcher = MissileCombatTests.Load(f, Launcher, Charge);
        var space = Space(f);
        Launch(f, launcher);
        var crimewatch = f.Db.Services.GetRequiredService<IPilotCrimewatch>();
        crimewatch.Disconnect(Victim, VictimShip);
        space.Leave(VictimShip);
        Assert.Null(space.FindOnline(SystemId, VictimShip));
        Assert.NotNull(space.Find(SystemId, VictimShip));
        long initial = Assert.Single(crimewatch.LoggedOff(SystemId)).ExpiresAt;
        Advance(f, 3);
        Assert.True(Assert.Single(Store(f).TakeImpacts(f.PilotId)).Applied > 0);
        // The paid cycle already extends PvP beyond this missile's early impact.
        Assert.True(Assert.Single(crimewatch.LoggedOff(SystemId)).ExpiresAt >= initial);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.True(saved.Set<SavedCrimewatch>().Find(Victim)!.Timers().Pvp.Running(f.Clock.Now));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeLaunchAndVictimNotificationUseTheExistingMissileAndDamageWires(bool fatal)
    {
        using var f = Prepare();
        long launcher = MissileCombatTests.Load(f, Launcher, Charge);
        Space(f);
        using var sourceScope = f.Db.Services.CreateScope();
        using var targetScope = f.Db.Services.CreateScope();
        PilotCallContext Client(long account, IServiceProvider services)
        {
            var pilot = f.Db.Store.FindPilot(account)!;
            var factory = services.GetRequiredService<FlightSessionFactory>();
            PilotCallContext context = new(account, pilot.Account, [pilot], factory.Create, account, SignedSettings.Empty);
            context.Select(pilot);
            context.Flight.Snapshot();
            return context;
        }
        using var source = Client(1000001, sourceScope.ServiceProvider);
        using var victim = Client(1000002, targetScope.ServiceProvider);
        var targeting = sourceScope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var handler = sourceScope.ServiceProvider.GetRequiredService<MissileHandler>();
        var monitor = targetScope.ServiceProvider.GetRequiredService<PilotDamageMonitor>();
        monitor.Enter(victim);
        targeting.Call(source, "dogmaIM", "AddTarget", [VictimShip]);
        f.Clock.Milliseconds += 10000;
        targeting.Advance(source);
        Assert.True(targeting.IsLocked(VictimShip));
        if (fatal)
        {
            AlmostLost(f);
        }
        Assert.Same(PilotCallResult.None, handler.Call(source, "dogmaIM", "Activate", [launcher, "useMissiles", VictimShip, 0L]));
        Assert.Single(source.Flight.VisibleMissiles);
        Advance(f, 3);
        handler.Advance(source);
        targetScope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>().ApplyPlayerDamage(victim);
        var message = Assert.Single(victim.NotificationsBeforeFlight.OfType<DamageMessageNotification>()).Message.ToBlue();
        Assert.Equal(new object?[] { 2, f.PilotId }, Assert.IsType<object?[]>(message["owner"]));
        Assert.Equal(Charge, message["weapon"]);
        Assert.True(Assert.IsType<double>(message["damage"]) > 0);
        Assert.Single(source.NotificationsBeforeFlight.OfType<DamageMessageNotification>());
        Assert.Contains(victim.Flight.Updates, u => u.Command is DestinyCommand.OnDamageStateChange);
        Assert.Equal(fatal, victim.Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupedLaunchesPayEveryMemberOrRollBackEveryMember(bool failSecond)
    {
        using var f = Prepare();
        long first = MissileCombatTests.Load(f, Launcher, Charge);
        long second = MissileCombatTests.Load(f, Launcher, Charge);
        new PilotWeaponBankStore(new(f.Db.Path), f.Clock).Change(f.PilotId, f.ShipId, WeaponBankOperation.Link, first, second);
        if (failSecond)
        {
            using WorldDbContext world = new(f.Db.Path);
            world.RemoveRange(world.Set<OwnedItem>().Where(i => i.LocationId == second));
            world.SaveChanges();
        }
        Space(f);
        var ammunition = f.Db.Services.GetRequiredService<IPilotAmmunition>();
        var before = ammunition.Read(f.PilotId, f.ShipId).ToArray();
        IReadOnlyList<MissileLaunch> Attack() =>
            Store(f)
                .LaunchBank(
                    f.PilotId,
                    first,
                    VictimShip,
                    new HashSet<long> { VictimShip },
                    new(SpacePosition.Zero, 295, new(0, 0, -1), 42),
                    f.Clock.Now
                );
        if (failSecond)
        {
            Assert.Throws<InvalidDataException>(() => Attack());
            Assert.Equal(before, ammunition.Read(f.PilotId, f.ShipId));
            using WorldDbContext saved = new(f.Db.Path);
            Assert.Empty(saved.Set<WorldMissile>());
            Assert.Empty(saved.Set<SavedCrimewatch>());
            Assert.Empty(saved.Set<SavedPilotHostility>());
        }
        else
        {
            var bank = Attack();
            Assert.Equal(2, bank.Count);
            Assert.All(bank, launch => Assert.Equal(2, launch.Magazine.Quantity));
            Advance(f);
            Assert.Equal(2, Store(f).TakeImpacts(f.PilotId).Count);
        }
    }

    [Fact]
    public void FailedFatalImpactRollsBackHealthReportsAndFlightButKeepsThePaidLaunch()
    {
        using var f = Prepare();
        long launcher = MissileCombatTests.Load(f, Launcher, Charge);
        Space(f);
        AlmostLost(f);
        List<PilotDamage> hits = [];
        f.Db.Services.GetRequiredService<PilotDamageSignal>().Applied += hits.Add;
        Launch(f, launcher);
        using (WorldDbContext setup = new(f.Db.Path))
        {
            setup.Database.ExecuteSqlRaw(
                "CREATE TRIGGER reject_missile_loss BEFORE INSERT ON WorldShipLoss BEGIN SELECT RAISE(ABORT,'test impact failure'); END"
            );
        }
        f.Clock.Milliseconds += 3000;
        Assert.Throws<DbUpdateException>(() => Store(f).Advance(SystemId, f.Clock.Now));
        using (WorldDbContext saved = new(f.Db.Path))
        {
            Assert.Equal(MissileFlightStatus.Flying, Assert.Single(saved.Set<WorldMissile>()).Status);
            Assert.Equal(VictimShip, saved.Pilots.Find(Victim)!.ActiveShipId);
            Assert.Empty(saved.Set<ShipLoss>());
            Assert.Empty(saved.Set<WorldWreck>());
            Assert.Empty(saved.Set<SavedCombatReport>());
            Assert.Empty(saved.Set<SavedInsuranceLedger>());
            saved.Database.ExecuteSqlRaw("DROP TRIGGER reject_missile_loss");
        }
        Assert.Empty(hits);
        Assert.Equal(2, Assert.Single(f.Db.Services.GetRequiredService<IPilotAmmunition>().Read(f.PilotId, f.ShipId)).Quantity);
        Store(f).Advance(SystemId, f.Clock.Now);
        Assert.True(Assert.Single(Store(f).TakeImpacts(f.PilotId)).Destroyed);
        Assert.Single(hits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(333)]
    [InlineData(999)]
    public void MovingPilotFlightStartsAtTheExactLaunchInstantForEveryClockPhase(int milliseconds)
    {
        using var f = Prepare();
        long launcher = MissileCombatTests.Load(f, Launcher, Charge);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond - f.Clock.Now % TimeSpan.TicksPerSecond));
        SpacePosition velocity = new(-2000, 0, 0);
        var space = Space(f, velocity);
        var start = f.Db.Store.FindPilot(1000002)!.Position!;
        long launchedAt = f.Clock.Now;
        var launch = Launch(f, launcher);
        var expected = MissileFlightRules.Tick(launch.Projectile.Flight, new(start, velocity, launch.Projectile.TargetRadius));
        f.Db.Store.SetPosition(VictimShip, start + velocity, SystemId);
        Publish(f, space, 1000002, velocity);
        Advance(f, 1);
        var next = Assert.Single(Store(f).List(SystemId)).Flight;
        Assert.Equal(expected.Position, next.Position);
        Assert.Equal(expected.Velocity, next.Velocity);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(launchedAt, Assert.Single(saved.Set<WorldMissile>()).LaunchedAt);
    }
}
