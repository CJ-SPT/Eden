using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.Combat;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Thermal;

public sealed class MissileHeatTests
{
    private const int Launcher = 499;
    private const int Charge = 212;
    private const double NormalCycle = 14400;
    private const double OverloadedCycle = NormalCycle * .85;
    private static readonly MissileFiringState Motion = new(SpacePosition.Zero, 295, new(0, 0, -1), 42);

    private static Fixture Prepare(bool trained = true, int launcher = Launcher, int charge = Charge)
    {
        var f = MissileCombatTests.Prepare(launcher, charge);
        // Restores the Ibis's native budgets, slots and two launcher hardpoints.
        PassiveCatalogFixture.LoadOverload(f.CatalogPath);
        if (trained)
        {
            f.Train(OverloadRules.ThermodynamicsSkill, 3);
        }
        return f;
    }

    private static long[] LoadBank(Fixture f)
    {
        long first = MissileCombatTests.Load(f, Launcher, Charge);
        long second = MissileCombatTests.Load(f, Launcher, Charge);
        f.Db.Services.GetRequiredService<IPilotWeaponBanks>().Change(f.PilotId, f.ShipId, WeaponBankOperation.Link, first, second);
        return [first, second];
    }

    private static IReadOnlyList<MissileLaunch> Pay(Fixture f, long member, long target) =>
        f
            .Db.Services.GetRequiredService<PilotMissileStore>()
            .LaunchOverloadedBank(f.PilotId, member, target, new HashSet<long> { target }, Motion, f.Clock.Now);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BankPaysOneRoundAndOneHighRackCyclePerLauncher(bool resident)
    {
        using var f = Prepare();
        var bank = LoadBank(f);
        var npc = MissileCombatTests.Space(f);
        using LiveWorld? live = resident ? new(new(f.Db.Path), f.Clock, _ => { }) : null;
        var heat = f.Db.Services.GetRequiredService<IPilotShipHeat>();
        var missiles = MissileCombatTests.Missiles(f);
        long ship = f.ShipId;
        var volley = Pay(f, bank[1], npc.Id);
        Assert.Equal(bank, volley.Select(l => l.Launcher.ItemId));
        Assert.Equal(bank.Length, volley.Select(l => l.Projectile.Id).Distinct().Count());
        Assert.All(
            volley,
            l =>
            {
                Assert.Equal(OverloadedCycle, l.Launcher.CycleMilliseconds, 8);
                Assert.Equal(2, l.Magazine.Quantity);
            }
        );
        var state = heat.Read(f.PilotId, ship, f.Clock.Now);
        long deadline = f.Clock.Now + (long)Math.Ceiling(OverloadedCycle * TimeSpan.TicksPerMillisecond);
        Assert.Equal(bank, state.Cycles.Select(c => c.ModuleId));
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.All(
                state.Cycles,
                c =>
                {
                    Assert.Equal(new(c.ModuleId, f.Clock.Now, deadline), c.Key);
                    Assert.Equal(ModuleRack.High, c.Rack);
                    Assert.True(c.Absorption > 0);
                    Assert.Equal(deadline, world.Set<ModuleCooldown>().Find(c.ModuleId)!.ReadyAt);
                }
            );
        }
        Assert.Throws<InvalidDataException>(() => Pay(f, bank[0], npc.Id));
        f.Clock.Milliseconds += 1000;
        missiles.Advance(npc.SystemId, f.Clock.Now);
        var impacts = missiles.TakeImpacts(f.PilotId);
        Assert.Equal(bank.Length, impacts.Count);
        Assert.All(impacts, i => Assert.Equal(103.75, i.Applied, 7));
        Assert.Empty(missiles.TakeImpacts(f.PilotId));
        var hot = heat.Read(f.PilotId, ship, f.Clock.Now);
        double expected = HeatRules.Calculate(
            state.Racks.Single(r => r.Profile.Rack == ModuleRack.High).Profile,
            0,
            state.Cycles.Sum(c => c.Absorption),
            1000
        );
        Assert.Equal(expected, hot.Heat(ModuleRack.High), 8);
        Assert.Equal(0, hot.Heat(ModuleRack.Medium));
        f.Clock.Milliseconds += (long)OverloadedCycle - 1000;
        Assert.Empty(heat.Read(f.PilotId, ship, f.Clock.Now).Cycles);
        var normal = missiles.LaunchBank(f.PilotId, bank[0], npc.Id, new HashSet<long> { npc.Id }, Motion, f.Clock.Now);
        Assert.All(
            normal,
            l =>
            {
                Assert.Equal(NormalCycle, l.Launcher.CycleMilliseconds, 8);
                Assert.Equal(1, l.Magazine.Quantity);
            }
        );
        Assert.Empty(heat.Read(f.PilotId, ship, f.Clock.Now).Cycles);
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("empty")]
    [InlineData("heat-write")]
    public void RejectedBankPreservesEveryMagazineCooldownAndHeatSource(string rejection)
    {
        using var f = Prepare(trained: rejection != "skill");
        var bank = LoadBank(f);
        var npc = MissileCombatTests.Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var ammo = f.Db.Services.GetRequiredService<IPilotAmmunition>();
        var heat = f.Db.Services.GetRequiredService<IPilotShipHeat>();
        if (rejection == "empty")
        {
            using WorldDbContext world = new(f.Db.Path);
            world.Set<OwnedItem>().Where(i => i.LocationId == bank[1]).ExecuteDelete();
        }
        if (rejection == "heat-write")
        {
            // Fail on the second member, after the first launch has been saved inside the volley transaction.
            using WorldDbContext world = new(f.Db.Path);
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectBankHeat BEFORE UPDATE ON WorldShipHeat BEGIN SELECT RAISE(ABORT,'heat'); END"
            );
        }
        var magazines = ammo.Read(f.PilotId, f.ShipId);
        using (WorldDbContext before = new(f.Db.Path))
        {
            var deadlines = before.Set<ModuleCooldown>().OrderBy(c => c.ModuleId).Select(c => c.ReadyAt).ToArray();
            Assert.True(heat.Read(f.PilotId, f.ShipId, f.Clock.Now).Idle);
            if (rejection == "heat-write")
            {
                Assert.Throws<DbUpdateException>(() => Pay(f, bank[0], npc.Id));
            }
            else
            {
                Assert.Throws<InvalidDataException>(() => Pay(f, bank[0], npc.Id));
            }
            using WorldDbContext after = new(f.Db.Path);
            Assert.Equal(deadlines, after.Set<ModuleCooldown>().OrderBy(c => c.ModuleId).Select(c => c.ReadyAt).ToArray());
            Assert.All(after.Set<ModuleCooldown>(), c => Assert.Equal(ModuleCooldownKind.Reload, c.Kind));
            Assert.Empty(after.Set<WorldMissile>());
            Assert.Empty(after.Set<SavedCrimewatch>());
            Assert.Empty(after.Set<SavedShipHeat>());
        }
        Assert.Equal(magazines.ToArray(), ammo.Read(f.PilotId, f.ShipId).ToArray());
        Assert.True(heat.Read(f.PilotId, f.ShipId, f.Clock.Now).Idle);
    }

    [Theory]
    [InlineData(2404, Charge)]
    [InlineData(8089, Charge)]
    [InlineData(16068, Charge)]
    [InlineData(20595, Charge)]
    [InlineData(34290, 24471)]
    [InlineData(85021, 266)]
    public void LauncherVariantsPayTheirOwnCatalogRateAndKeepTheSameWarhead(int type, int charge)
    {
        using var f = Prepare(launcher: type, charge: charge);
        long module = MissileCombatTests.Load(f, type, charge);
        var npc = MissileCombatTests.Space(f);
        var normal = MissileCombatTests.Launch(f, module, npc.Id);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks((long)Math.Ceiling(normal.Launcher.CycleMilliseconds * TimeSpan.TicksPerMillisecond)));
        var paid = Assert.Single(Pay(f, module, npc.Id));
        using CatalogDbContext catalog = new(f.CatalogPath);
        double bonus = catalog.FittingAttributes(type)[DogmaAttributeId.OverloadRateOfFireBonus];
        Assert.True(bonus < 0);
        Assert.Equal(normal.Launcher.CycleMilliseconds * (1 + bonus / 100), paid.Launcher.CycleMilliseconds, 7);
        Assert.Equal(1, paid.Magazine.Quantity);
        using WorldDbContext saved = new(f.Db.Path);
        var warheads = saved.Set<WorldMissile>().AsEnumerable().Select(m => m.Payload().Warhead).ToArray();
        Assert.Equal(2, warheads.Length);
        Assert.Equal(warheads[0], warheads[1]);
        Assert.Equal(
            module,
            Assert.Single(ShipHeatPersistence.Decode(Assert.Single(saved.Set<SavedShipHeat>()).StateJson).Cycles).ModuleId
        );
    }

    [Fact]
    public void PaidPasteRepairOnAnotherRackExcludesTheOverloadedVolleyButAllowsOrdinaryLaunches()
    {
        using var f = Prepare();
        var bank = LoadBank(f);
        const int ShieldBooster = 399;
        long booster = f.Fit(ShieldBooster);
        f.Grant(NaniteRepairRules.PasteType, 50, cargo: true);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new SavedModuleCondition { ModuleId = booster, Damage = 20 });
            world.SaveChanges();
        }
        var npc = MissileCombatTests.Space(f);
        var repairs = f.Db.Services.GetRequiredService<IPilotNaniteRepair>();
        var repair = Assert.Single(repairs.Start(f.PilotId, f.ShipId, [booster], f.Clock.Now).Started).Schedule!;
        Assert.Throws<InvalidDataException>(() => Pay(f, bank[0], npc.Id));
        var missiles = MissileCombatTests.Missiles(f);
        Assert.Empty(missiles.List(npc.SystemId));
        Assert.True(f.Db.Services.GetRequiredService<IPilotShipHeat>().Read(f.PilotId, f.ShipId, f.Clock.Now).Idle);
        var normal = missiles.LaunchBank(f.PilotId, bank[0], npc.Id, new HashSet<long> { npc.Id }, Motion, f.Clock.Now);
        Assert.All(normal, l => Assert.Equal(2, l.Magazine.Quantity));
        Assert.Equal(repair.Key, Assert.Single(repairs.Read(f.PilotId, f.ShipId, f.Clock.Now)).Key);
    }

    [Fact]
    public void RepeatedHeatAndProjectileContactReadsRemainResidentBetweenLaunchAndImpact()
    {
        using var f = Prepare();
        var bank = LoadBank(f);
        var npc = MissileCombatTests.Space(f);
        long ship = f.ShipId;
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var missiles = MissileCombatTests.Missiles(f);
        var heat = f.Db.Services.GetRequiredService<IPilotShipHeat>();
        using (var start = SessionPerformance.Begin("overloaded bank launch", _ => { }, force: true)!)
        {
            _ = Pay(f, bank[0], npc.Id);
            Assert.Equal(1, start.FittingCalculations);
        }
        var initial = heat.Read(f.PilotId, ship, f.Clock.Now);
        var contacts = missiles.List(npc.SystemId);
        Assert.Equal(2, contacts.Count);
        using var measure = SessionPerformance.Begin("missile heat reads", _ => { }, force: true)!;
        for (int frame = 0; frame < 100; frame++)
        {
            f.Clock.Milliseconds++;
            var observed = heat.Read(f.PilotId, ship, f.Clock.Now);
            Assert.True(observed.Heat(ModuleRack.High) > initial.Heat(ModuleRack.High));
            Assert.Equal(initial.Cycles, observed.Cycles);
            Assert.Equal(contacts, missiles.List(npc.SystemId));
            Assert.Equal(observed, heat.Read(f.PilotId, ship, f.Clock.Now));
        }
        Assert.Equal(0, measure.Commands);
        Assert.Equal(0, measure.Connections);
        Assert.Equal(0, measure.FittingCalculations);
    }

    [Fact]
    public void BurnedLauncherDropsOnlyItsHeatWhileTheCapturedVolleyKeepsBothPaidSources()
    {
        using var f = Prepare();
        var bank = LoadBank(f);
        var npc = MissileCombatTests.Space(f);
        long ship = f.ShipId;
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var heat = f.Db.Services.GetRequiredService<IPilotShipHeat>();
        Pay(f, bank[0], npc.Id);
        var paid = heat.Read(f.PilotId, ship, f.Clock.Now);
        f.Clock.Milliseconds += 1000;
        using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        using (WorldDbContext world = new(f.Db.Path))
        {
            using var tx = world.Database.BeginTransaction();
            using CatalogDbContext catalog = new(f.CatalogPath);
            Assert.True(ModuleConditionPersistence.ApplyDamage(world, catalog, bank[1], 1000, f.Clock.Now).WentOffline);
            world.SaveChanges();
            tx.Commit();
        }
        Assert.Equal(bank[0], Assert.Single(heat.Read(f.PilotId, ship, f.Clock.Now).Cycles).ModuleId);
        Assert.Equal(2, MissileCombatTests.Missiles(f).List(npc.SystemId).Count);
        snapshot.Project();
        using (WorldDbContext captured = new(snapshot.Database))
        {
            var capturedHeat = ShipHeatPersistence.Decode(Assert.Single(captured.Set<SavedShipHeat>()).StateJson);
            Assert.Equal(paid.Cycles.ToArray(), capturedHeat.Cycles.ToArray());
            Assert.All(
                captured.Set<ModuleCooldown>(),
                c => Assert.Equal(paid.Cycles.Single(p => p.ModuleId == c.ModuleId).CompletesAt, c.ReadyAt)
            );
            Assert.Equal(2, captured.Set<WorldMissile>().Count(m => m.Status == MissileFlightStatus.Flying));
        }
        string path = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "missile-heat-checkpoint.sqlite");
        LiveWorld.PersistSnapshot(snapshot.Database, path);
        f.Clock.Milliseconds += (long)OverloadedCycle - 1000;
        WorldInitialization.Initialize(new(path), f.Clock);
        Assert.Empty(new PilotShipHeatStore(new(path)).Read(f.PilotId, ship, f.Clock.Now).Cycles);
        using WorldDbContext reopened = new(path);
        Assert.DoesNotContain(reopened.Set<WorldMissile>(), m => m.Status is MissileFlightStatus.Flying or MissileFlightStatus.Impact);
        Assert.Equal(new[] { 2, 2 }, reopened.Set<OwnedItem>().Where(i => bank.Contains(i.LocationId)).Select(i => i.Quantity).ToArray());
        Assert.Equal(2, reopened.Set<ModuleCooldown>().Count(c => c.ReadyAt == f.Clock.Now));
    }
}
