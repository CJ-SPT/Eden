using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Drones;
using Eden.Data.World.Insurance;
using Eden.Data.World.Loot;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Combat;
using Eden.Protocol.Drones;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Combat.CombatDroneTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class PilotDroneTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DronesDestroyCapsulesAndLeaveTheReplacementDockedAtHome(bool resident)
    {
        using var f = Prepare();
        using LiveWorld? live = resident ? new(new(f.Base.Db.Path), f.Base.Clock, _ => { }) : null;
        var ids = f.Launch();
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            world.Ships.Find(VictimShip)!.TypeId = FleetDefinitions.CapsuleType;
            world.SaveChanges();
        }
        Space(f);
        AlmostLost(f);
        var home = f.Base.Db.Services.GetRequiredService<Eden.Gameplay.Characters.IPilotHomeStation>().Read(Victim);
        List<PilotDamage> messages = [];
        f.Base.Db.Services.GetRequiredService<PilotDamageSignal>().Applied += messages.Add;
        Engage(f, ids);
        Assert.True(Assert.Single(f.Tick().Hits).Health.Remaining.Destroyed);
        var loss = Assert.Single(messages);
        Assert.Null(loss.Capsule!.Position);
        Assert.Equal(home.Id, loss.Capsule.StationId);
        Assert.Null(loss.Insurance);
        Assert.Equal(2, loss.InventoryChanges.Count);
        Assert.Empty(f.Tick(30).Hits);
        using WorldDbContext saved = new(f.Base.Db.Path);
        Assert.Single(saved.Set<SavedHomeCloneTransfer>());
        Assert.Equal(2, saved.Set<SavedCombatReport>().Count());
        Assert.Empty(saved.Set<WorldWreck>());
        Assert.Empty(saved.Set<NpcBounty>());
        Assert.Empty(saved.Set<SavedInsuranceLedger>());
    }

    private const long Victim = 90000002;
    private const long VictimShip = 1000000000002;
    private const int SystemId = 30000144;
    private const int WarriorTwo = 2488;

    private static Fixture Prepare(double security = 0)
    {
        Fixture f = new();
        using CatalogDbContext catalog = new(f.Base.CatalogPath, writable: true);
        string data = System.Text.Json.JsonSerializer.Serialize(new { securityStatus = security });
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000144',{data})");
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES (29,'Capsule',6);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Capacity,Mass,Radius) VALUES (670,'Capsule',29,0,32000,2);
            INSERT OR IGNORE INTO TypeAttribute SELECT 670,AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=601;
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','670','{{"published":1,"volume":1000}}');
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','601','{{"wreckTypeID":26557,"basePrice":1000000}}');
            """
        );
        // Remove accuracy randomness from these persistence/ordering tests.
        catalog.Database.ExecuteSql(
            $"UPDATE TypeAttribute SET Value=1000000 WHERE ItemTypeId={WarriorTwo} AND AttributeId={(int)DogmaAttributeId.TrackingSpeed}"
        );
        return f;
    }

    private static SharedSpace Space(Fixture f)
    {
        f.Space();
        f.Base.Db.Store.SetPosition(VictimShip, f.Npc.Position + new SpacePosition(1100, 0, 0), SystemId);
        var space = f.Base.Db.Services.GetRequiredService<SharedSpace>();
        foreach (long account in new long[] { 1000001, 1000002 })
        {
            var pilot = f.Base.Db.Store.FindPilot(account)!;
            space.Publish(new(PilotContact.From(pilot), SpacePosition.Zero, null, 0, null));
        }
        return space;
    }

    private static void Engage(Fixture f, IReadOnlyList<long> ids, long target = VictimShip, bool locked = true) =>
        f.Drones.Order(f.Pilot, ids, DroneOrder.Engage, target, locked ? new HashSet<long> { target } : new HashSet<long>(), f.Now);

    private static void AlmostLost(Fixture f)
    {
        var defense = f.Base.Db.Services.GetRequiredService<IPilotHealth>().Read(Victim, f.Now).Defense;
        using WorldDbContext world = new(f.Base.Db.Path);
        world.Add(
            new ShipHealthState
            {
                ShipId = VictimShip,
                ShieldCharge = 0,
                ArmorDamage = defense.Armor,
                StructureDamage = defense.Structure - 1,
                UpdatedAt = f.Now,
            }
        );
        world.SaveChanges();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AttacksUsePilotDefensesAndPublishOnceWithoutNpcRewards(bool resident)
    {
        using var f = Prepare();
        using LiveWorld? live = resident ? new(new(f.Base.Db.Path), f.Base.Clock, _ => { }) : null;
        var ids = f.Launch();
        Space(f);
        List<PilotDamage> messages = [];
        f.Base.Db.Services.GetRequiredService<PilotDamageSignal>().Applied += messages.Add;
        Engage(f, ids);
        Assert.Empty(messages);
        var update = f.Tick();
        Assert.Equal(5, update.Hits.Count);
        Assert.Equal(5, messages.Count);
        Assert.All(
            messages,
            hit =>
            {
                Assert.Equal(Victim, hit.PilotId);
                Assert.Equal(f.Pilot, hit.AttackerId);
                Assert.Equal(f.Ship, hit.AttackerShipId);
                Assert.Equal(WarriorTwo, hit.WeaponType);
                Assert.True(hit.Applied > 0);
                Assert.Null(hit.Capsule);
            }
        );
        var hit = update.Hits[0];
        var expected = CombatDamage.Apply(hit.Health.Defense.Undamaged, hit.Health.Defense, hit.Damage);
        Assert.Equal(expected.Applied, hit.Applied, 8);
        Assert.Equal(expected.Remaining, hit.Health.Remaining);
        Assert.Empty(f.Drones.Advance(f.Pilot, f.Now, 1).Hits);
        Assert.Equal(5, messages.Count);
        var crimewatch = f.Base.Db.Services.GetRequiredService<IPilotCrimewatch>();
        Assert.True(crimewatch.Read(f.Pilot, SystemId).Timers.Weapons.Running(f.Now));
        Assert.True(crimewatch.Read(Victim, SystemId).Timers.Pvp.Running(f.Now));
        using WorldDbContext saved = new(f.Base.Db.Path);
        Assert.Empty(saved.Set<NpcBounty>());
        Assert.Empty(saved.Set<SavedSecurityStatus>());
        Assert.Equal(f.Now, Assert.Single(saved.Set<SavedPilotHostility>()).LastAttackAt);
        Assert.Equal(update.Hits.Sum(h => h.Applied), Assert.Single(saved.Set<SavedCombatDamage>()).Damage, 8);
    }

    [Theory]
    [InlineData(0, ShipSafetyLevel.Full, "lock")]
    [InlineData(.2, ShipSafetyLevel.Full, "safety")]
    [InlineData(.8, ShipSafetyLevel.Disabled, "safety")]
    [InlineData(0, ShipSafetyLevel.Full, "range")]
    [InlineData(.2, ShipSafetyLevel.Partial, "capsule")]
    [InlineData(0, ShipSafetyLevel.Full, "dock")]
    [InlineData(0, ShipSafetyLevel.Full, "system")]
    [InlineData(0, ShipSafetyLevel.Full, "absent")]
    [InlineData(0, ShipSafetyLevel.Full, "self")]
    [InlineData(0, ShipSafetyLevel.Full, "foreign")]
    public void RejectedOrdersPreserveControlHealthAndAggression(double security, ShipSafetyLevel safety, string failure)
    {
        using var f = Prepare(security);
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        var ids = f.Launch();
        var space = Space(f);
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            world.Pilots.Find(f.Pilot)!.SafetyLevel = safety;
            if (failure == "capsule")
            {
                world.Ships.Find(VictimShip)!.TypeId = FleetDefinitions.CapsuleType;
            }
            if (failure == "foreign")
            {
                world.Set<WorldDrone>().Find(ids[^1])!.PilotId = Victim;
            }
            world.SaveChanges();
        }
        if (failure is "range" or "system" or "dock")
        {
            f.Base.Db.Store.SetPosition(
                VictimShip,
                failure == "dock" ? null : f.Npc.Position + new SpacePosition(100000, 0, 0),
                failure == "system" ? SystemId + 1 : SystemId
            );
            if (failure != "system")
            {
                var moved = f.Base.Db.Store.FindPilot(1000002)!;
                space.Publish(new(PilotContact.From(moved), SpacePosition.Zero, null, 0, null));
            }
        }
        if (failure == "absent")
        {
            space.Leave(VictimShip);
        }
        var before = f.Drones.List(SystemId, f.Now).ToArray();
        Assert.Throws<InvalidDataException>(() => Engage(f, ids, failure == "self" ? f.Ship : VictimShip, failure != "lock"));
        Assert.Equal(before, f.Drones.List(SystemId, f.Now));
        using WorldDbContext saved = new(f.Base.Db.Path);
        Assert.Empty(saved.Set<ShipHealthState>());
        Assert.Empty(saved.Set<SavedCrimewatch>());
        Assert.Empty(saved.Set<SavedPilotHostility>());
        Assert.Empty(saved.Set<SavedCombatDamage>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FatalDroneVolleySettlesOnePersistentLossAndNeverCreatesNpcRewards(bool resident)
    {
        using var f = Prepare();
        using (LiveWorld? live = resident ? new(new(f.Base.Db.Path), f.Base.Clock, _ => { }) : null)
        {
            var ids = f.Launch();
            Space(f);
            AlmostLost(f);
            List<PilotDamage> messages = [];
            f.Base.Db.Services.GetRequiredService<PilotDamageSignal>().Applied += messages.Add;
            Engage(f, ids);
            var hit = Assert.Single(f.Tick().Hits);
            Assert.True(hit.Health.Remaining.Destroyed);
            var damage = Assert.Single(messages);
            Assert.Equal(FleetDefinitions.CapsuleGroup, damage.Capsule!.ShipGroupId);
            Assert.NotNull(damage.Insurance);
            Assert.Empty(f.Tick(10).Hits);
            if (live is not null)
            {
                await live.FlushAsync();
            }
        }
        using WorldDbContext saved = new(f.Base.Db.Path);
        var loss = Assert.Single(saved.Set<ShipLoss>());
        Assert.Equal(f.Pilot, loss.AttackerPilotId);
        Assert.Null(loss.NpcId);
        Assert.Single(saved.Set<WorldWreck>());
        Assert.Single(saved.Set<SavedInsuranceLedger>());
        Assert.Equal(2, saved.Set<SavedCombatReport>().Count());
        Assert.Empty(saved.Set<NpcBounty>());
        Assert.Empty(saved.Set<SavedSecurityStatus>());
        Assert.NotEqual(VictimShip, saved.Pilots.Find(Victim)!.ActiveShipId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedFatalSettlementRollsBackDroneDeadlinesDamageAndNotifications(bool resident)
    {
        using var f = Prepare();
        using LiveWorld? live = resident ? new(new(f.Base.Db.Path), f.Base.Clock, _ => { }) : null;
        var ids = f.Launch();
        Space(f);
        AlmostLost(f);
        Engage(f, ids);
        List<PilotDamage> messages = [];
        f.Base.Db.Services.GetRequiredService<PilotDamageSignal>().Applied += messages.Add;
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER reject_drone_loss BEFORE INSERT ON WorldShipLoss BEGIN SELECT RAISE(ABORT,'test failure'); END"
            );
        }
        var before = f.Drones.List(SystemId, f.Now).ToArray();
        Assert.Throws<DbUpdateException>(() => f.Tick());
        Assert.Empty(messages);
        Assert.Equal(before, f.Drones.List(SystemId, f.Now));
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            Assert.Empty(world.Set<ShipLoss>());
            Assert.Empty(world.Set<WorldWreck>());
            Assert.Empty(world.Set<SavedCombatReport>());
            Assert.Empty(world.Set<SavedCombatDamage>());
            Assert.Equal(VictimShip, world.Pilots.Find(Victim)!.ActiveShipId);
            Assert.All(world.Set<WorldDrone>(), d => Assert.Equal(0, d.ReadyAt));
            world.Database.ExecuteSqlRaw("DROP TRIGGER reject_drone_loss");
        }
        Assert.True(Assert.Single(f.Drones.Advance(f.Pilot, f.Now, 1).Hits).Health.Remaining.Destroyed);
        Assert.Single(messages);
    }

    [Fact]
    public async Task WarmPilotCombatAndObserversUseNoSqlAndCheckpointDamageWithDeadlines()
    {
        using var f = Prepare();
        long end;
        ShipHealth health;
        DroneContact[] drones;
        using (LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { }))
        {
            var ids = f.Launch(quantity: 1);
            long shipId = f.Ship;
            Space(f);
            Engage(f, ids);
            Assert.True(f.Drones.HasControlled(f.Pilot, shipId));
            f.Tick();
            var crimewatch = f.Base.Db.Services.GetRequiredService<IPilotCrimewatch>();
            crimewatch.Read(f.Pilot, SystemId);
            crimewatch.Read(Victim, SystemId);
            Assert.True(f.Drones.HasControlled(f.Pilot, shipId));
            using (var metrics = SessionPerformance.Begin("warmed pilot drones", _ => { }, force: true))
            {
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    Assert.Single(f.Tick(5).Hits);
                    Assert.True(metrics!.Commands == 0, $"cycle {cycle} advance SQL {metrics.Commands}");
                    Assert.Empty(f.Drones.Advance(f.Pilot, f.Now, 1).Hits);
                    Assert.True(metrics.Commands == 0, $"cycle {cycle} duplicate SQL {metrics.Commands}");
                    for (int observer = 0; observer < 5; observer++)
                    {
                        Assert.Single(f.Drones.List(SystemId, f.Now));
                        Assert.True(f.Drones.HasControlled(f.Pilot, shipId));
                        Assert.True(crimewatch.Read(Victim, SystemId).Timers.Pvp.Running(f.Now));
                        Assert.True(metrics.Commands == 0, $"cycle {cycle} observer {observer} SQL {metrics.Commands}");
                    }
                }
                Assert.Equal(0, metrics!.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            health = f.Base.Db.Services.GetRequiredService<IPilotHealth>().Read(Victim, f.Now);
            end = crimewatch.Read(f.Pilot, SystemId).Timers.Weapons.ActiveUntil;
            drones = f.Drones.List(SystemId, f.Now).ToArray();
            await live.FlushAsync();
        }
        Assert.Equal(health, f.Base.Db.Services.GetRequiredService<IPilotHealth>().Read(Victim, f.Now));
        Assert.Equal(drones, f.Drones.List(SystemId, f.Now));
        using WorldDbContext saved = new(f.Base.Db.Path);
        Assert.Equal(end, saved.Set<SavedCrimewatch>().Find(f.Pilot)!.WeaponsActive);
        Assert.Single(saved.Set<SavedCombatDamage>());
        WorldInitialization.Initialize(new(f.Base.Db.Path), f.Base.Clock);
        Assert.All(f.Drones.List(SystemId, f.Now), d => Assert.Equal(0, d.ShipId));
        Assert.Empty(f.Tick(60).Hits);
    }

    [Theory]
    [InlineData(false, "warp")]
    [InlineData(true, "warp")]
    [InlineData(true, "dock")]
    [InlineData(true, "system")]
    [InlineData(true, "safety")]
    [InlineData(true, "recall")]
    public void ContinuingAttacksStopWhenTargetsLeaveOrSafetyDisallowsFurtherShots(bool resident, string reason)
    {
        using var f = Prepare(reason == "safety" ? .2 : 0);
        using LiveWorld? live = resident ? new(new(f.Base.Db.Path), f.Base.Clock, _ => { }) : null;
        var ids = f.Launch();
        var space = Space(f);
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            world.Pilots.Find(f.Pilot)!.SafetyLevel = ShipSafetyLevel.Partial;
            world.SaveChanges();
        }
        Engage(f, ids);
        Assert.NotEmpty(f.Tick().Hits);
        if (reason == "warp")
        {
            space.Leave(VictimShip);
        }
        if (reason == "dock")
        {
            f.Base.Db.Store.SetPosition(VictimShip, null);
        }
        if (reason == "system")
        {
            f.Base.Db.Store.SetPosition(VictimShip, f.Npc.Position, 30000142);
        }
        if (reason == "safety")
        {
            using WorldDbContext world = new(f.Base.Db.Path);
            world.Pilots.Find(f.Pilot)!.SafetyLevel = ShipSafetyLevel.Full;
            world.SaveChanges();
        }
        if (reason == "recall")
        {
            f.Drones.Order(f.Pilot, ids, DroneOrder.ReturnToBay, null, new HashSet<long>(), f.Now);
        }
        Assert.Empty(f.Tick(10).Hits);
        Assert.DoesNotContain(f.Drones.List(SystemId, f.Now), d => d.Activity == DroneActivity.Fighting);
    }

    [Theory]
    [InlineData(false, "recent", true)]
    [InlineData(true, "recent", true)]
    [InlineData(true, "passive", false)]
    [InlineData(true, "old", false)]
    [InlineData(true, "expired", false)]
    [InlineData(true, "safety", false)]
    public void AutomaticDefenseRequiresANewLiveAggressorAndLegalSafety(bool resident, string condition, bool fires)
    {
        using var f = Prepare(condition == "safety" ? .8 : 0);
        using LiveWorld? live = resident ? new(new(f.Base.Db.Path), f.Base.Clock, _ => { }) : null;
        f.Launch();
        Space(f);
        f.Drones.ChangeSettings(f.Pilot, condition != "passive", true);
        f.Base.Clock.Seconds++;
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            long attackAt = condition == "old" ? f.Now - TimeSpan.TicksPerSecond * 2 : f.Now;
            CrimewatchPersistence.Player(world, Victim, f.Pilot, SystemId, new(CrimewatchFlag.None, false), attackAt, attackAt);
            if (condition == "expired")
            {
                world.Set<SavedPilotHostility>().Local.Single().ExpiresAt = f.Now;
            }
            world.SaveChanges();
        }
        var hits = f.Tick().Hits;
        Assert.Equal(fires ? 5 : 0, hits.Count);
        Assert.All(hits, h => Assert.Equal(VictimShip, h.TargetId));
        if (!fires)
        {
            Assert.All(f.Drones.List(SystemId, f.Now), d => Assert.Equal(DroneActivity.Idle, d.Activity));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativePilotEngageProducesDroneEffectsAndVictimDamageOrCapsule(bool fatal)
    {
        using var f = Prepare();
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        var ids = f.Launch(quantity: 1);
        Space(f);
        using var sourceScope = f.Base.Db.Services.CreateScope();
        using var victimScope = f.Base.Db.Services.CreateScope();
        PilotCallContext Client(long account, IServiceProvider services)
        {
            var pilot = f.Base.Db.Store.FindPilot(account)!;
            var factory = services.GetRequiredService<FlightSessionFactory>();
            PilotCallContext context = new(account, pilot.Account, [pilot], factory.Create, account, SignedSettings.Empty);
            context.Select(pilot);
            context.Flight.Snapshot();
            return context;
        }
        using var source = Client(1000001, sourceScope.ServiceProvider);
        using var victim = Client(1000002, victimScope.ServiceProvider);
        var targeting = sourceScope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var handler = sourceScope.ServiceProvider.GetRequiredService<DroneHandler>();
        var monitor = victimScope.ServiceProvider.GetRequiredService<PilotDamageMonitor>();
        monitor.Enter(victim);
        targeting.Call(source, "dogmaIM", "AddTarget", [VictimShip]);
        f.Base.Clock.Seconds += 10;
        targeting.Advance(source);
        Assert.True(targeting.IsLocked(VictimShip));
        if (fatal)
        {
            AlmostLost(f);
        }
        var reply = Assert.IsType<PilotCallResult.Success>(
            handler.Call(source, "entity", "CmdEngage", [ids.Select(id => (object?)id).ToArray(), VictimShip])
        );
        Assert.IsType<DroneCommandReply>(reply.Reply);
        handler.Advance(source);
        monitor.Advance(victim);
        var message = Assert.Single(victim.NotificationsBeforeFlight.OfType<DamageMessageNotification>()).Message.ToBlue();
        Assert.Equal(new object?[] { 2, f.Pilot }, Assert.IsType<object?[]>(message["owner"]));
        Assert.Equal(WarriorTwo, message["weapon"]);
        Assert.True(Assert.IsType<double>(message["damage"]) > 0);
        Assert.Single(source.NotificationsBeforeFlight.OfType<DamageMessageNotification>());
        Assert.Contains(source.Flight.Updates, u => u.Command is DestinyCommand.TimedSpecialEffect);
        Assert.Contains(victim.Flight.Updates, u => u.Command is DestinyCommand.OnDamageStateChange);
        Assert.Equal(fatal, victim.Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup);
    }

    [Fact]
    public void VersionOneHundredPreservesOldHostilityWithoutFabricatingANewAttack()
    {
        using var f = Prepare();
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldPilotHostility DROP COLUMN LastAttackAt; PRAGMA user_version=99");
            world.Database.ExecuteSql(
                $"INSERT INTO WorldPilotHostility(SourceShipId,TargetShipId,ExpiresAt) VALUES ({VictimShip},{f.Ship},{f.Now})"
            );
        }
        WorldInitialization.Initialize(new(f.Base.Db.Path), f.Base.Clock);
        WorldInitialization.Initialize(new(f.Base.Db.Path), f.Base.Clock);
        using WorldDbContext saved = new(f.Base.Db.Path);
        var hostility = Assert.Single(saved.Set<SavedPilotHostility>());
        Assert.Equal(f.Now, hostility.ExpiresAt);
        Assert.Equal(0, hostility.LastAttackAt);
        Assert.Equal(100, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Empty(saved.Set<SavedCrimewatch>());
        Assert.Empty(saved.Set<ShipLoss>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OfflineVictimsStayPresentUntilTheExtendedPvpDeadline(bool resident)
    {
        using var f = Prepare();
        using LiveWorld? live = resident ? new(new(f.Base.Db.Path), f.Base.Clock, _ => { }) : null;
        var ids = f.Launch(quantity: 1);
        var space = Space(f);
        var crimewatch = f.Base.Db.Services.GetRequiredService<IPilotCrimewatch>();
        crimewatch.Disconnect(Victim, VictimShip);
        space.Leave(VictimShip);
        Engage(f, ids);
        long first = Assert.Single(crimewatch.LoggedOff(SystemId)).ExpiresAt;
        Assert.Single(f.Tick(5).Hits);
        long extended = Assert.Single(crimewatch.LoggedOff(SystemId)).ExpiresAt;
        Assert.True(extended > first);
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            Assert.Equal(extended, world.Set<SavedLogoff>().Find(Victim)!.ExpiresAt);
        }
        // Equality is expired. A late callback cannot resurrect or replay combat.
        f.Base.Clock.Seconds += (extended - f.Now) / TimeSpan.TicksPerSecond;
        Assert.Empty(f.Drones.Advance(f.Pilot, extended, 1).Hits);
        Assert.Empty(crimewatch.LoggedOff(SystemId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveOutlawChangesInvalidateLegalAggressionBeforeTheNextShot(bool resident)
    {
        using var f = Prepare(.8);
        using LiveWorld? live = resident ? new(new(f.Base.Db.Path), f.Base.Clock, _ => { }) : null;
        var ids = f.Launch(quantity: 1);
        Space(f);
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            world.Add(new SavedSecurityStatus { PilotId = Victim, Value = CrimewatchRules.OutlawSecurity });
            world.SaveChanges();
        }
        Engage(f, ids);
        Assert.Single(f.Tick().Hits);
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            world.Set<SavedSecurityStatus>().Find(Victim)!.Value = 0;
            world.Set<SavedEngagement>().ExecuteDelete();
            world.SaveChanges();
        }
        Assert.Empty(f.Tick(5).Hits);
    }

    [Fact]
    public async Task ConcurrentFatalCallbacksSettleAndPublishOneLoss()
    {
        using var f = Prepare();
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        var ids = f.Launch();
        Space(f);
        AlmostLost(f);
        Engage(f, ids);
        List<PilotDamage> messages = [];
        f.Base.Db.Services.GetRequiredService<PilotDamageSignal>().Applied += messages.Add;
        f.Base.Clock.Seconds++;
        var updates = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => f.Drones.Advance(f.Pilot, f.Now, 1))));
        Assert.Single(updates.SelectMany(u => u.Hits));
        Assert.Single(messages);
        using WorldDbContext saved = new(f.Base.Db.Path);
        Assert.Single(saved.Set<ShipLoss>());
        Assert.Single(saved.Set<WorldWreck>());
        Assert.Single(saved.Set<SavedInsuranceLedger>());
        Assert.Equal(2, saved.Set<SavedCombatReport>().Count());
    }

    [Fact]
    public void DestroyedControllersCannotOrderAttacksOrKeepAdvancingDrones()
    {
        using var f = Prepare();
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        var ids = f.Launch();
        Space(f);
        long ship = f.Ship;
        var defense = f.Base.Db.Services.GetRequiredService<IPilotHealth>().Read(f.Pilot, f.Now).Defense;
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            world.Add(
                new ShipHealthState
                {
                    ShipId = ship,
                    ShieldCharge = 0,
                    ArmorDamage = defense.Armor,
                    StructureDamage = defense.Structure,
                    UpdatedAt = f.Now,
                }
            );
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => Engage(f, ids));
        Assert.Empty(f.Tick().Hits);
        Assert.False(f.Drones.HasControlled(f.Pilot, ship));
        Assert.All(f.Drones.List(SystemId, f.Now), d => Assert.Equal(0, d.ShipId));
        using WorldDbContext saved = new(f.Base.Db.Path);
        Assert.Empty(saved.Set<SavedPilotHostility>());
        Assert.Empty(saved.Set<SavedCrimewatch>());
    }

    [Fact]
    public async Task DetachedProjectionKeepsOneGenerationAndRetriesTimersDamageAndDeadlinesAtomically()
    {
        using var f = Prepare();
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        var ids = f.Launch(quantity: 1);
        Space(f);
        Engage(f, ids);
        var first = Assert.Single(f.Tick().Hits);
        var crimewatch = f.Base.Db.Services.GetRequiredService<IPilotCrimewatch>();
        long firstEnd = crimewatch.Read(f.Pilot, SystemId).Timers.Weapons.ActiveUntil;
        using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        Assert.Single(f.Tick(5).Hits);
        long latestEnd = crimewatch.Read(f.Pilot, SystemId).Timers.Weapons.ActiveUntil;
        Assert.True(latestEnd > firstEnd);
        using (var command = snapshot.Database.CreateCommand())
        {
            command.CommandText =
                "CREATE TRIGGER reject_drone_projection BEFORE UPDATE ON WorldCrimewatch BEGIN SELECT RAISE(ABORT,'test'); END";
            command.ExecuteNonQuery();
        }
        Assert.Throws<DbUpdateException>(() => snapshot.Project());
        using (WorldDbContext captured = new(snapshot.Database))
        {
            Assert.Empty(captured.Set<ShipHealthState>());
            Assert.Empty(captured.Set<SavedCombatDamage>());
            Assert.Equal(0, captured.Set<WorldDrone>().Single().ShotAt);
        }
        using (var command = snapshot.Database.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER reject_drone_projection";
            command.ExecuteNonQuery();
        }
        snapshot.Project();
        using (WorldDbContext captured = new(snapshot.Database))
        {
            Assert.Equal(firstEnd, captured.Set<SavedCrimewatch>().Find(f.Pilot)!.WeaponsActive);
            Assert.Equal(first.Health.Remaining.Shield, captured.Set<ShipHealthState>().Find(VictimShip)!.ShieldCharge);
            Assert.Equal(first.Applied, Assert.Single(captured.Set<SavedCombatDamage>()).Damage);
        }
        await live.FlushAsync();
        string path = Path.Combine(Path.GetDirectoryName(f.Base.Db.Path)!, "drone-checkpoint.sqlite");
        File.Copy(f.Base.Db.Path, path);
        using WorldDbContext reopened = new(path);
        Assert.Equal(latestEnd, reopened.Set<SavedCrimewatch>().Find(f.Pilot)!.WeaponsActive);
        Assert.Equal(latestEnd, reopened.Set<WorldDrone>().Single().ReadyAt);
        Assert.True(Assert.Single(reopened.Set<SavedCombatDamage>()).Damage > first.Applied);
    }
}
