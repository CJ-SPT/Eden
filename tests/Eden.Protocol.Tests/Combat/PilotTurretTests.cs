using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Docking;
using Eden.Data.World.Fittings;
using Eden.Data.World.Insurance;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Market;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Combat;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.Protocol.Tests.Ammunition;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Insurance;
using Eden.World.Inventory;
using Eden.World.Loot;
using Eden.World.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Combat.NpcCombatTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class PilotTurretTests
{
    private const long Attacker = Fixture.PilotId;
    private const long Victim = 90000002;
    private const long VictimShip = 1000000000002;
    private const int SystemId = 30000144;
    private const int TestCapsuleVariant = 9991670;

    private static Fixture Prepare(double security = 0, bool live = false)
    {
        Fixture f = new(liveWorld: live, useTestClock: true);
        f.Db.Store.SetPosition(VictimShip, f.Pilot.Position! + new SpacePosition(500, 0, 0), SystemId);
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true);
        string data = System.Text.Json.JsonSerializer.Serialize(new { securityStatus = security });
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000144',{data})");
        catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=1 WHERE ItemTypeId=3638 AND AttributeId=6");
        return f;
    }

    private static CombatShot Fire(Fixture f, double roll = .5, long target = VictimShip, SpacePosition? velocity = null) =>
        f
            .Weapons.FirePlayerBank(Attacker, f.Module, new(target, velocity ?? SpacePosition.Zero), SpacePosition.Zero, f.Clock.Now, roll)
            .Shot;

    private static void AlmostLost(Fixture f)
    {
        using WorldDbContext world = new(f.Db.Path);
        world.Add(
            new ShipHealthState
            {
                ShipId = VictimShip,
                ShieldCharge = 0,
                ArmorDamage = 125,
                StructureDamage = 174,
                UpdatedAt = f.Clock.Now,
            }
        );
        world.SaveChanges();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaidDamageUsesFittedDefenseAndPersistsBothTimersWithoutNpcRewards(bool live)
    {
        using var f = Prepare(live: live);
        var before = f.Health.Read(Victim, f.Clock.Now);
        var shot = Fire(f);
        Assert.True(shot.Applied > 0);
        Assert.Equal(before.Remaining.Shield - shot.Applied, shot.Health.Remaining.Shield, 8);
        Assert.Null(shot.Reward);
        Assert.False(shot.Destroyed);
        Assert.Equal(shot.Health.Remaining.Shield, f.Health.Read(Victim, f.Clock.Now).Remaining.Shield, 8);
        using WorldDbContext reopened = new(f.Db.Path);
        Assert.Empty(reopened.Set<NpcBounty>());
        Assert.Empty(reopened.Set<SavedSecurityStatus>());
        Assert.True(reopened.Set<SavedCrimewatch>().Find(Attacker)!.Timers().Weapons.Running(f.Clock.Now));
        var victim = reopened.Set<SavedCrimewatch>().Find(Victim)!.Timers();
        Assert.True(victim.Pvp.Running(f.Clock.Now));
        Assert.False(victim.Weapons.Running(f.Clock.Now));
        Assert.False(victim.Npc.Running(f.Clock.Now));
        Assert.Throws<InvalidDataException>(() => Fire(f));
        Assert.Equal(shot.Health.Remaining.Shield, f.Health.Read(Victim, f.Clock.Now).Remaining.Shield, 8);
    }

    [Theory]
    [InlineData(.2, ShipSafetyLevel.Full)]
    [InlineData(.8, ShipSafetyLevel.Full)]
    [InlineData(.8, ShipSafetyLevel.Disabled)]
    public void RejectedAggressionLeavesHealthCapacitorCooldownAndTimersUnchanged(double security, ShipSafetyLevel safety)
    {
        using var f = Prepare(security);
        using (WorldDbContext setup = new(f.Db.Path))
        {
            setup.Set<Account>().Find(1000001L)!.CloneGrade = AccountCloneGrade.Omega;
            setup.SaveChanges();
        }
        f.Db.Services.GetRequiredService<IPilotSafety>().Set(Attacker, safety);
        var before = f.Health.Read(Victim, f.Clock.Now);
        Assert.Throws<InvalidDataException>(() => Fire(f));
        Assert.Equal(before, f.Health.Read(Victim, f.Clock.Now));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<ModuleCooldown>());
        Assert.Empty(world.Set<ShipEnergy>());
        Assert.Empty(world.Set<SavedCrimewatch>());
        Assert.Empty(world.Set<ShipLoss>());
    }

    [Fact]
    public void LowsecAttackFlagsAttackerAndAllowsFullSafetyRetaliation()
    {
        using var f = Prepare(.2);
        f.Db.Services.GetRequiredService<IPilotSafety>().Set(Attacker, ShipSafetyLevel.Partial);
        Fire(f);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(CrimewatchFlag.Suspect, world.Set<SavedCrimewatch>().Find(Attacker)!.Timers().Flag(f.Clock.Now));
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var source = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == Victim);
        var target = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == Attacker);
        Assert.Equal(
            new AggressionDecision(CrimewatchFlag.None, true),
            CrimewatchPersistence.CheckPlayer(world, catalog, source, target, SystemId, f.Clock.Now)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FatalShotSettlesOneLossCapsuleWreckInsuranceAndBothHistoriesAcrossReopen(bool live)
    {
        using var f = Prepare(live: live);
        InsuranceTestData.Prepare(f.Db);
        AlmostLost(f);
        Assert.True(Fire(f, 0).Destroyed);
        long capsule = f.Db.Store.FindPilot(1000002)!.ShipId;
        Assert.NotEqual(VictimShip, capsule);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var loss = Assert.Single(world.Set<ShipLoss>());
            Assert.Equal(Attacker, loss.AttackerPilotId);
            Assert.Null(loss.NpcId);
            Assert.Equal(capsule, loss.CapsuleId);
            Assert.Null(world.Ships.Find(VictimShip));
            Assert.Single(world.Set<WorldWreck>().Where(w => w.ShipId == VictimShip));
            Assert.Empty(world.Set<NpcBounty>());
            var payout = Assert.Single(world.Set<SavedInsuranceLedger>().Where(i => i.IsPayout));
            Assert.Equal(Victim, payout.PilotId);
            Assert.Equal(40000, payout.AmountCents);
            Assert.Equal(payout.BalanceCents, world.Set<PilotWallet>().Find(Victim)!.Cents);
            var reports = world.Set<SavedCombatReport>().ToArray();
            Assert.Equal(2, reports.Length);
            Assert.Contains(reports, r => r.PilotId == Victim && r.IsLoss);
            Assert.Contains(reports, r => r.PilotId == Attacker && !r.IsLoss);
        }
        f.Clock.Milliseconds += 2000;
        Assert.Throws<InvalidDataException>(() => Fire(f));
        Assert.Throws<InvalidDataException>(() => Fire(f, target: capsule));
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(capsule, f.Db.Store.FindPilot(1000002)!.ShipId);
        using WorldDbContext reopened = new(f.Db.Path);
        Assert.Single(reopened.Set<ShipLoss>());
        Assert.Equal(2, reopened.Set<SavedCombatReport>().Count());
        Assert.Single(reopened.Set<SavedInsuranceLedger>().Where(i => i.IsPayout));
        Assert.Empty(reopened.Database.SqlQueryRaw<string>("SELECT \"table\" AS Value FROM pragma_foreign_key_check"));
    }

    [Theory]
    [InlineData("self")]
    [InlineData("docked")]
    [InlineData("distant")]
    [InlineData("system")]
    [InlineData("expired")]
    [InlineData("velocity")]
    public void UnavailableTargetsCannotConsumeAWeaponCycle(string condition)
    {
        using var f = Prepare();
        long target = condition == "self" ? Fixture.ShipId : VictimShip;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var position = world.Set<FlightPosition>().Find(VictimShip)!;
            if (condition == "docked")
            {
                world.Remove(position);
            }
            if (condition == "distant")
            {
                position.X += 1_000_000;
            }
            if (condition == "system")
            {
                position.SystemId = 30000142;
            }
            if (condition == "expired")
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
        var velocity = condition == "velocity" ? new SpacePosition(double.NaN, 0, 0) : SpacePosition.Zero;
        Assert.Throws<InvalidDataException>(() => Fire(f, target: target, velocity: velocity));
        using WorldDbContext check = new(f.Db.Path);
        Assert.Empty(check.Set<ModuleCooldown>());
        Assert.Empty(check.Set<SavedCrimewatch>());
        Assert.Empty(check.Set<ShipLoss>());
    }

    [Fact]
    public void OfflineVictimReceivesExtendedDeadlineAndCapsuleWithoutDuplicatePresence()
    {
        using var f = Prepare();
        var crimewatch = f.Db.Services.GetRequiredService<IPilotCrimewatch>();
        crimewatch.Disconnect(Victim, VictimShip);
        long first = Assert.Single(crimewatch.LoggedOff(SystemId)).ExpiresAt;
        AlmostLost(f);
        Fire(f, 0);
        var offline = Assert.Single(crimewatch.LoggedOff(SystemId));
        Assert.True(offline.ExpiresAt > first);
        Assert.Equal(FleetDefinitions.CapsuleType, offline.Pilot.ShipTypeId);
        Assert.NotEqual(VictimShip, offline.Pilot.ShipId);
    }

    private static PilotCallContext Client(Fixture f, long account)
    {
        var pilot = f.Db.Store.FindPilot(account)!;
        var space = f.Db.Services.GetRequiredService<SharedSpace>();
        PilotCallContext context = new(
            account,
            pilot.Account,
            [pilot],
            p =>
                new(
                    f.Db.Store,
                    p,
                    f.Clock,
                    space,
                    [],
                    health: f.Health,
                    wrecks: f.Db.Services.GetRequiredService<Eden.Gameplay.Loot.IWrecks>()
                ),
            account,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        return context;
    }

    [Fact]
    public void NativeDamageNotifiesVictimAndReplacesDestroyedHullOnce()
    {
        using var f = Prepare();
        AlmostLost(f);
        using var source = Client(f, 1000001);
        using var target = Client(f, 1000002);
        using var scope = f.Db.Services.CreateScope();
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var combat = scope.ServiceProvider.GetRequiredService<CombatHandler>();
        using var victimScope = f.Db.Services.CreateScope();
        var monitor = victimScope.ServiceProvider.GetRequiredService<PilotDamageMonitor>();
        var victimTargeting = victimScope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var updates = victimScope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>();
        monitor.Enter(target);
        targeting.Call(source, "dogmaIM", "AddTarget", [VictimShip]);
        victimTargeting.Call(target, "dogmaIM", "AddTarget", [Fixture.ShipId]);
        f.Clock.Milliseconds += 10_000;
        targeting.Advance(source);
        victimTargeting.Advance(target);
        Assert.True(targeting.IsLocked(VictimShip));
        Assert.True(victimTargeting.IsLocked(Fixture.ShipId));
        // Fatal at any non-miss quality; exercise deterministic settlement separately above.
        var hit = Fire(f, 0);
        updates.ApplyPlayerDamage(target);
        Assert.Equal(FleetDefinitions.CapsuleType, target.Pilot.ShipTypeId);
        Assert.False(victimTargeting.IsLocked(Fixture.ShipId));
        Assert.Contains(target.Notifications.OfType<TargetChanged>(), n => n.Change == TargetChange.Lost);
        var message = Assert.Single(target.NotificationsBeforeFlight.OfType<DamageMessageNotification>()).Message.ToBlue();
        Assert.Equal(new object?[] { 2, Attacker }, Assert.IsType<object?[]>(message["owner"]));
        Assert.Equal(Fixture.ShipId, message["source"]);
        Assert.Equal(hit.Applied, message["damage"]);
        Assert.Contains(target.Flight.Updates, u => u.Command is DestinyCommand.RemoveBall { Id: VictimShip });
        int notifications = target.NotificationsBeforeFlight.Count;
        monitor.Advance(target);
        Assert.Equal(notifications, target.NotificationsBeforeFlight.Count);
        Assert.IsType<PilotCallResult.Failure>(combat.Call(source, "dogmaIM", "Activate", [f.Module, "targetAttack", VictimShip, 0L]));
    }

    [Fact]
    public void NativeActivationStartsAndRepeatsAgainstALockedPilot()
    {
        using var f = Prepare();
        using var source = Client(f, 1000001);
        using var target = Client(f, 1000002);
        using var scope = f.Db.Services.CreateScope();
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var combat = scope.ServiceProvider.GetRequiredService<CombatHandler>();
        var monitor = scope.ServiceProvider.GetRequiredService<PilotDamageMonitor>();
        monitor.Enter(target);
        Assert.IsType<PilotCallResult.Failure>(combat.Call(source, "dogmaIM", "Activate", [f.Module, "targetAttack", VictimShip, 1L]));
        targeting.Call(source, "dogmaIM", "AddTarget", [VictimShip]);
        f.Clock.Milliseconds += 10_000;
        targeting.Advance(source);
        Assert.Same(PilotCallResult.None, combat.Call(source, "dogmaIM", "Activate", [f.Module, "targetAttack", VictimShip, 1L]));
        monitor.Advance(target);
        Assert.Single(target.NotificationsBeforeFlight.OfType<DamageMessageNotification>());
        f.Clock.Milliseconds += 2000;
        combat.Advance(source);
        monitor.Advance(target);
        Assert.Equal(2, target.NotificationsBeforeFlight.OfType<DamageMessageNotification>().Count());
        combat.Advance(source);
        monitor.Advance(target);
        Assert.Equal(2, target.NotificationsBeforeFlight.OfType<DamageMessageNotification>().Count());
    }

    [Fact]
    public void VersionNinetyMigrationKeepsNpcLossAndWreckReferencesAndIsIdempotent()
    {
        using var f = Prepare();
        f.AlmostLost();
        f.Aggress();
        f.Clock.Milliseconds += 7500;
        Assert.NotNull(f.Fire(0)!.Capsule);
        ShipLoss before;
        using (WorldDbContext world = new(f.Db.Path))
        {
            before = Assert.Single(world.Set<ShipLoss>());
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldShipLoss DROP COLUMN AttackerPilotId; PRAGMA user_version=90");
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext reopened = new(f.Db.Path);
        var after = Assert.Single(reopened.Set<ShipLoss>());
        Assert.Equal(before.NpcId, after.NpcId);
        Assert.Equal(before.CapsuleId, after.CapsuleId);
        Assert.Equal(before.ItemsJson, after.ItemsJson);
        Assert.Equal(before.DroppedItemIdsJson, after.DroppedItemIdsJson);
        Assert.Null(after.AttackerPilotId);
        Assert.Single(reopened.Set<WorldWreck>().Where(w => w.ShipId == before.ShipId));
        Assert.Empty(reopened.Database.SqlQueryRaw<string>("SELECT \"table\" AS Value FROM pragma_foreign_key_check"));
    }

    [Fact]
    public void FailedFatalSettlementRollsBackDamagePaymentTimersAndLoss()
    {
        using var f = Prepare();
        AlmostLost(f);
        using (WorldDbContext setup = new(f.Db.Path))
        {
            setup.Database.ExecuteSqlRaw(
                "CREATE TRIGGER reject_loss BEFORE INSERT ON WorldShipLoss BEGIN SELECT RAISE(ABORT,'test loss failure'); END"
            );
        }
        Assert.Throws<DbUpdateException>(() => Fire(f, 0));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(174, saved.Set<ShipHealthState>().Find(VictimShip)!.StructureDamage);
        Assert.Equal(VictimShip, saved.Pilots.Find(Victim)!.ActiveShipId);
        Assert.Empty(saved.Set<ModuleCooldown>());
        Assert.Empty(saved.Set<ShipEnergy>());
        Assert.Empty(saved.Set<SavedCrimewatch>());
        Assert.Empty(saved.Set<ShipLoss>());
        Assert.Empty(saved.Set<WorldWreck>());
        Assert.Empty(saved.Set<SavedCombatReport>());
        Assert.Empty(saved.Set<SavedInsuranceLedger>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupedAmmunitionIsPaidAtomicallyAgainstPilots(bool failSecond)
    {
        using AmmunitionTests.Fixture f = new();
        long second = f.Buy(561, 1);
        f.Fitting.Fit(Attacker, [second], f.Pilot.StationId, (int)InventoryFlag.AutoFit);
        f.Fitting.SetOnline(Attacker, f.Pilot.ShipId, second, true);
        new PilotWeaponBankStore(new(f.Db.Path), f.Clock).Change(Attacker, f.Pilot.ShipId, WeaponBankOperation.Link, f.Gun, second);
        f.Load(quantity: 400);
        f.Clock.Milliseconds += 5000;
        f.Space();
        f.Db.Store.SetPosition(VictimShip, f.Pilot.Position! + new SpacePosition(500, 0, 0), SystemId);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000144','{{\"securityStatus\":0}}')"
            );
        }
        if (failSecond)
        {
            using WorldDbContext setup = new(f.Db.Path);
            setup.RemoveRange(setup.Set<OwnedItem>().Where(i => i.LocationId == second));
            setup.SaveChanges();
        }
        var before = f.Ammo.Read(Attacker, f.Pilot.ShipId).ToArray();
        CombatVolley Attack() =>
            f.Combat.FirePlayerBank(
                Attacker,
                f.Gun,
                new(VictimShip, SpacePosition.Zero),
                SpacePosition.Zero,
                f.Clock.Now,
                0,
                WeaponActivation.AmmoFed
            );
        if (failSecond)
        {
            Assert.Throws<InvalidDataException>(() => Attack());
            Assert.Equal(before, f.Ammo.Read(Attacker, f.Pilot.ShipId));
            using WorldDbContext check = new(f.Db.Path);
            Assert.Empty(check.Set<SavedCrimewatch>());
            Assert.Empty(check.Set<ShipHealthState>());
            Assert.Empty(check.Set<ShipEnergy>());
        }
        else
        {
            var volley = Attack();
            Assert.Equal(2, volley.Weapons.Count);
            Assert.Equal(2, volley.Magazines.Count);
            Assert.All(volley.Magazines, m => Assert.Equal(199, m.Quantity));
            Assert.True(volley.Shot.Applied > 0);
            Assert.Equal(398, f.Total(222));
        }
    }

    [Fact]
    public async Task CompetingFatalShotsCreateOnlyOneLoss()
    {
        using var f = Prepare(live: true);
        AlmostLost(f);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => Record.Exception(() => Fire(f, 0)))));
        Assert.Single(outcomes, e => e is null);
        Assert.IsType<InvalidDataException>(Assert.Single(outcomes, e => e is not null));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Single(saved.Set<ShipLoss>());
        Assert.Single(saved.Set<WorldWreck>());
        Assert.Equal(2, saved.Set<SavedCombatReport>().Count());
    }

    [Theory]
    [InlineData(FleetDefinitions.CapsuleType)]
    [InlineData(TestCapsuleVariant)]
    public void CapsuleExclusionUsesTheCatalogGroupForEveryVariant(int type)
    {
        using var f = Prepare();
        if (type == TestCapsuleVariant)
        {
            using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true);
            catalog.Database.ExecuteSql(
                $"INSERT INTO ItemType(Id,Name,GroupId) VALUES ({type},'Test capsule',{FleetDefinitions.CapsuleGroup})"
            );
        }
        using (WorldDbContext setup = new(f.Db.Path))
        {
            setup.Ships.Find(VictimShip)!.TypeId = type;
            setup.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => Fire(f));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ModuleCooldown>());
        Assert.Empty(saved.Set<SavedCrimewatch>());
        Assert.Empty(saved.Set<ShipLoss>());
    }
}
