using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Targeting;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerDefenseModuleTests
{
    private const long Enemy = 90000002;
    private const long EnemyShip = 1000000000002;
    private const int System = 30000142;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefenseChangesOwnedAmmunitionPaysReloadAndDoesNotRepeatIt(bool bank)
    {
        using Fixture f = Prepare(561, DogmaEffect.ProjectileFired);
        long module = f.Fit(561);
        long[] modules = bank ? [module, f.Fit(561)] : [module];
        const int ownedRounds = 2000;
        long iron = f.Grant((int)HybridChargeType.IronSmall, ownedRounds);
        long antimatter = f.Grant((int)HybridChargeType.AntimatterSmall, ownedRounds, cargo: true);
        var ammo = f.Db.Services.GetRequiredService<IPilotAmmunition>();
        ammo.Load(f.PilotId, f.ShipId, modules, [iron], f.StationId, f.Clock.Now);
        f.Clock.Milliseconds += 60_000;
        if (bank)
        {
            f.Db.Services.GetRequiredService<IPilotWeaponBanks>().Change(f.PilotId, f.ShipId, WeaponBankOperation.Link, module, modules[1]);
        }
        Space(f, DogmaEffect.ProjectileFired);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var defense = f.Db.Services.GetRequiredService<CapsuleerDefense>();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        var target = Assert.IsType<CapsuleerAttacker>(defense.PatrolTarget(pilot, EnemyShip, pilot.Position!, f.Clock.Now));
        var locks = f.Db.Services.GetRequiredService<IPilotTargetLocks>();
        locks.Add(f.ShipId, EnemyShip);
        CapsuleerDefenseState state = new() { ShipId = f.ShipId, Locks = [new(EnemyShip, f.Clock.Now)] };
        NavigationState navigation = new();
        var reloading = defense.Advance(pilot, navigation, state, f.Clock.Now, 0, [], proactive: target);
        var paid = Assert.Single(reloading.State!.Cycles, cycle => cycle.ModuleId == module);
        Assert.Equal(DogmaEffect.Online, paid.Effect);
        Assert.True(paid.ReadyAt > f.Clock.Now);
        var magazines = ammo.Read(f.PilotId, f.ShipId).ToArray();
        Assert.Equal(modules.Length, magazines.Length);
        Assert.All(
            magazines,
            magazine =>
            {
                Assert.Equal((int)HybridChargeType.AntimatterSmall, magazine.TypeId);
                Assert.True(magazine.Quantity > 0);
            }
        );
        Assert.All(
            reloading.State.Cycles.Where(cycle => modules.Contains(cycle.ModuleId)),
            cycle =>
            {
                Assert.Equal(DogmaEffect.Online, cycle.Effect);
                Assert.False(cycle.Unavailable);
                Assert.Equal(paid.ReadyAt, cycle.ReadyAt);
            }
        );
        int remaining = f.Inventory.List(f.PilotId, f.ShipId).Where(item => item.Id == antimatter).Sum(item => item.Quantity);
        Assert.Equal(ownedRounds, remaining + magazines.Sum(magazine => magazine.Quantity));
        var duplicate = defense.Advance(reloading.Pilot, navigation, reloading.State, f.Clock.Now, 0, [], proactive: target);
        Assert.Equal(paid.ReadyAt, Assert.Single(duplicate.State!.Cycles, cycle => cycle.ModuleId == module).ReadyAt);
        Assert.Equal(magazines, ammo.Read(f.PilotId, f.ShipId));
        f.Clock.Milliseconds += (paid.ReadyAt - f.Clock.Now) / TimeSpan.TicksPerMillisecond + 1;
        var shooting = defense.Advance(duplicate.Pilot, navigation, duplicate.State, f.Clock.Now, 0, [], proactive: target);
        Assert.Equal(DogmaEffect.ProjectileFired, Assert.Single(shooting.State!.Cycles, cycle => cycle.ModuleId == module).Effect);
        Assert.All(
            modules,
            id =>
                Assert.True(
                    ammo.Read(f.PilotId, f.ShipId).Single(magazine => magazine.ModuleId == id).Quantity
                        < magazines.Single(magazine => magazine.ModuleId == id).Quantity
                )
        );
        Assert.True(Assert.Single(shooting.State.Budgets, budget => budget.ModuleId == module).AmmoCheckAt > f.Clock.Now);
    }

    [Fact]
    public void DefenseHeatsTackleThroughThePaidOwnerAndHonorsItsSavedBudget()
    {
        using Fixture f = Prepare(3242, DogmaEffect.WarpDisruptor);
        PassiveCatalogFixture.LoadOverload(f.CatalogPath);
        PassiveCatalogFixture.LoadWeaponTiers(f.CatalogPath);
        f.Train(OverloadRules.ThermodynamicsSkill, 3);
        long module = f.Fit(3242);
        long gun = f.Fit(561);
        long iron = f.Grant((int)HybridChargeType.IronSmall, 100);
        f.Db.Services.GetRequiredService<IPilotAmmunition>().Load(f.PilotId, f.ShipId, [gun], [iron], f.StationId, f.Clock.Now);
        f.Clock.Milliseconds += 60_000;
        Space(f, DogmaEffect.WarpDisruptor);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        f.Db.Store.SetPosition(EnemyShip, pilot.Position! + new SpacePosition(22_000 + pilot.Flight.ShipRadius + 100, 0, 0), System);
        var contact = f.Db.Store.FindPilot(1000002)!;
        f.Db.Services.GetRequiredService<SharedSpace>().Publish(new(PilotContact.From(contact), SpacePosition.Zero, null, 0, null));
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var defense = f.Db.Services.GetRequiredService<CapsuleerDefense>();
        var target = Assert.IsType<CapsuleerAttacker>(defense.PatrolTarget(pilot, EnemyShip, pilot.Position!, f.Clock.Now));
        var locks = f.Db.Services.GetRequiredService<IPilotTargetLocks>();
        locks.Add(f.ShipId, EnemyShip);
        CapsuleerDefenseState state = new() { ShipId = f.ShipId, Locks = [new(EnemyShip, f.Clock.Now)] };
        NavigationState navigation = new();
        var active = defense.Advance(pilot, navigation, state, f.Clock.Now, 0, [], proactive: target);
        var cycle = Assert.Single(active.State!.Cycles, cycle => cycle.ModuleId == module);
        Assert.Equal(DogmaEffect.WarpDisruptor, cycle.Effect);
        Assert.True(cycle.Tackle!.Range > 22_000);
        Assert.Equal(1, Assert.Single(active.State.Budgets, budget => budget.ModuleId == module).HeatedCycles);
        var heat = f.Db.Services.GetRequiredService<IPilotShipHeat>();
        var initialHeat = heat.Read(f.PilotId, f.ShipId, f.Clock.Now);
        Assert.Contains(initialHeat.Cycles, paid => paid.ModuleId == module && paid.CompletesAt == cycle.ReadyAt && paid.Absorption > 0);
        var duplicate = defense.Advance(active.Pilot, navigation, active.State, f.Clock.Now, 0, [], proactive: target);
        Assert.Equal(active.State.Budgets, duplicate.State!.Budgets);
        Assert.Contains(heat.Read(f.PilotId, f.ShipId, f.Clock.Now).Cycles, paid => paid.ModuleId == module);
        f.Clock.Milliseconds += (cycle.ReadyAt - f.Clock.Now) / TimeSpan.TicksPerMillisecond + 1;
        Assert.True(heat.Read(f.PilotId, f.ShipId, f.Clock.Now).Heat(ModuleRack.Medium) > 0);
        var exhausted = duplicate.State with { Budgets = [new(module, PvpResources.MaximumHeatedCycles)] };
        var cooled = defense.Advance(duplicate.Pilot, navigation, exhausted, f.Clock.Now, 0, [], proactive: target);
        Assert.DoesNotContain(module, f.Db.Services.GetRequiredService<IPilotOverloading>().Requested(f.PilotId, f.ShipId));
        Assert.DoesNotContain(cooled.State!.Cycles, current => current.ModuleId == module && current.Effect == DogmaEffect.WarpDisruptor);
        Assert.True(heat.Read(f.PilotId, f.ShipId, f.Clock.Now).Heat(ModuleRack.Medium) > 0);
    }

    // Supported module IDs from the committed build-3662168 fixture extracts.
    [Theory]
    [InlineData(399, DogmaEffect.ShieldBoosting, 0)]
    [InlineData(523, DogmaEffect.ArmorRepair, 0)]
    [InlineData(524, DogmaEffect.StructureRepair, 0)]
    [InlineData(2293, DogmaEffect.ActiveShieldResistance, 0)]
    [InlineData(11301, DogmaEffect.ActiveArmorResistance, 0)]
    [InlineData(439, DogmaEffect.Afterburner, 0)]
    [InlineData(434, DogmaEffect.Microwarpdrive, 0)]
    [InlineData(1973, DogmaEffect.SensorBooster, 0)]
    [InlineData(1977, DogmaEffect.TrackingComputer, 0)]
    [InlineData(35788, DogmaEffect.MissileGuidanceComputer, 0)]
    [InlineData(23533, DogmaEffect.OmnidirectionalTrackingLink, 0)]
    [InlineData(526, DogmaEffect.StasisWebifier, 0)]
    [InlineData(3242, DogmaEffect.WarpDisruptor, 0)]
    [InlineData(447, DogmaEffect.WarpScrambler, 0)]
    [InlineData(1955, DogmaEffect.PlayerEcm, 0)]
    [InlineData(1968, DogmaEffect.SensorDampener, 0)]
    [InlineData(2108, DogmaEffect.TrackingDisruptor, 0)]
    [InlineData(37543, DogmaEffect.GuidanceDisruptor, 0)]
    [InlineData(12709, DogmaEffect.TargetPainter, 0)]
    [InlineData(533, DogmaEffect.EnergyNeutralizer, 0)]
    [InlineData(530, DogmaEffect.EnergyNosferatu, 0)]
    [InlineData(3566, DogmaEffect.CapacitorBoosting, 3554)]
    [InlineData(32774, DogmaEffect.AncillaryShieldBoosting, 263)]
    [InlineData(33076, DogmaEffect.AncillaryArmorRepair, 28668)]
    [InlineData(499, DogmaEffect.UseMissiles, 212)]
    [InlineData(561, DogmaEffect.ProjectileFired, 215)]
    [InlineData(4409, DogmaEffect.SignatureSuppression, 0)]
    [InlineData(47254, DogmaEffect.AssaultDamageControl, 0)]
    public void DefenseUsesTheRealSupportedModuleAndCannotReplayItsPaidCycle(int type, DogmaEffect effect, int charge)
    {
        using Fixture f = Prepare(type, effect);
        long module = f.Fit(type);
        if (type != (int)CombatWeaponType.CivilianGatlingRailgun && effect != DogmaEffect.ProjectileFired)
        {
            f.Fit((int)CombatWeaponType.CivilianGatlingRailgun);
        }
        if (charge != 0)
        {
            long stack = f.Grant(charge, 100);
            f.Db.Services.GetRequiredService<IPilotAmmunition>().Load(f.PilotId, f.ShipId, [module], [stack], f.StationId, f.Clock.Now);
            f.Clock.Milliseconds += 60_000;
        }
        Space(f, effect);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var controller = f.Db.Services.GetRequiredService<CapsuleerDefense>();
        NavigationState navigation = new();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        var first = controller.Advance(pilot, navigation, null, f.Clock.Now, 1, []);
        Assert.NotNull(first.State);
        f.Clock.Milliseconds += 30_000;
        var second = controller.Advance(first.Pilot, navigation, first.State, f.Clock.Now, 31, []);
        var cycle =
            first.State!.Cycles.FirstOrDefault(cycle => cycle.ModuleId == module)
            ?? Assert.Single(second.State!.Cycles, cycle => cycle.ModuleId == module);
        Assert.Equal(effect, cycle.Effect);
        Assert.True(cycle.ReadyAt > cycle.StartedAt);
        var before = f.Db.Services.GetRequiredService<IPilotHealth>().Read(f.PilotId, f.Clock.Now);
        var duplicate = controller.Advance(second.Pilot, navigation, second.State, f.Clock.Now, 31, []);
        Assert.Equal(
            second.State!.Cycles.Where(value => value.ModuleId == module).Select(value => value.ReadyAt),
            duplicate.State!.Cycles.Where(value => value.ModuleId == module).Select(value => value.ReadyAt)
        );
        Assert.Equal(before.Remaining, f.Db.Services.GetRequiredService<IPilotHealth>().Read(f.PilotId, f.Clock.Now).Remaining);
    }

    internal static Fixture Prepare(int type, DogmaEffect effect)
    {
        Fixture f = new();
        int hull = 601;
        if (effect == DogmaEffect.SignatureSuppression)
        {
            PassiveCatalogFixture.LoadSignatureSuppression(f.CatalogPath);
            hull = 638;
        }
        if (effect == DogmaEffect.AssaultDamageControl)
        {
            PassiveCatalogFixture.LoadAssaultDamageControls(f.CatalogPath);
            hull = 11371;
        }
        if (hull != 601)
        {
            SmallHullTests.TrainCatalog(f);
            using WorldDbContext world = new(f.Db.Path);
            world.Ships.Find(f.ShipId)!.TypeId = hull;
            world.SaveChanges();
        }
        if (effect is DogmaEffect.StasisWebifier or DogmaEffect.WarpDisruptor or DogmaEffect.WarpScrambler)
        {
            PassiveCatalogFixture.LoadTackleTiers(f.CatalogPath);
        }
        if (
            effect
            is DogmaEffect.PlayerEcm
                or DogmaEffect.SensorDampener
                or DogmaEffect.TrackingDisruptor
                or DogmaEffect.GuidanceDisruptor
                or DogmaEffect.TargetPainter
        )
        {
            PassiveCatalogFixture.LoadPlayerEwar(f.CatalogPath);
        }
        if (effect is DogmaEffect.EnergyNeutralizer or DogmaEffect.EnergyNosferatu)
        {
            PassiveCatalogFixture.LoadCapacitorModules(f.CatalogPath);
        }
        if (effect is DogmaEffect.SensorBooster or DogmaEffect.TrackingComputer or DogmaEffect.MissileGuidanceComputer)
        {
            PassiveCatalogFixture.LoadSupportTiers(f.CatalogPath);
        }
        if (effect == DogmaEffect.OmnidirectionalTrackingLink)
        {
            PassiveCatalogFixture.LoadDroneEquipment(f.CatalogPath);
        }
        if (effect is DogmaEffect.AncillaryShieldBoosting or DogmaEffect.AncillaryArmorRepair)
        {
            PassiveCatalogFixture.LoadAncillaryRepairs(f.CatalogPath);
        }
        if (effect == DogmaEffect.StructureRepair)
        {
            PassiveCatalogFixture.LoadHullRepairTiers(f.CatalogPath);
        }
        if (effect == DogmaEffect.UseMissiles)
        {
            PassiveCatalogFixture.LoadMissileTiers(f.CatalogPath);
        }
        if (effect == DogmaEffect.ProjectileFired)
        {
            PassiveCatalogFixture.LoadWeaponTiers(f.CatalogPath);
        }
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        string security = "{\"securityStatus\":0}";
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142',{security})");
        foreach (
            var attribute in new[]
            {
                DogmaAttributeId.HighSlots,
                DogmaAttributeId.MediumSlots,
                DogmaAttributeId.LowSlots,
                DogmaAttributeId.CpuOutput,
                DogmaAttributeId.PowerOutput,
                DogmaAttributeId.CapacitorCapacity,
            }
        )
        {
            double value = attribute is DogmaAttributeId.HighSlots or DogmaAttributeId.MediumSlots or DogmaAttributeId.LowSlots
                ? 8
                : 1_000_000;
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES ({hull},{(int)attribute},{value})");
        }
        return f;
    }

    internal static void Space(Fixture f, DogmaEffect effect)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        var position = new SpacePosition(pilot.X + pilot.Flight.StationRadius + 50_000, pilot.Y, pilot.Z);
        f.Db.Store.SetPosition(f.ShipId, position, System);
        f.Db.Store.SetPosition(EnemyShip, position + new SpacePosition(500, 0, 0), System);
        var health = f.Db.Services.GetRequiredService<IPilotHealth>().Read(f.PilotId, f.Clock.Now);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var state = world.Set<ShipHealthState>().Find(f.ShipId) ?? world.Add(new ShipHealthState { ShipId = f.ShipId }).Entity;
            state.ShieldCharge = health.Defense.Shield * .25;
            state.ArmorDamage = health.Defense.Armor * (effect == DogmaEffect.AssaultDamageControl ? .65 : .3);
            state.StructureDamage = effect == DogmaEffect.StructureRepair ? health.Defense.Structure * .21 : 0;
            state.UpdatedAt = f.Clock.Now;
            var sourceEnergy = world.Set<ShipEnergy>().Find(f.ShipId) ?? world.Add(new ShipEnergy { ShipId = f.ShipId }).Entity;
            sourceEnergy.Charge = effect is DogmaEffect.CapacitorBoosting or DogmaEffect.EnergyNosferatu ? 250_000 : 900_000;
            sourceEnergy.UpdatedAt = f.Clock.Now;
            world.Add(
                new ShipEnergy
                {
                    ShipId = EnemyShip,
                    Charge = 1_000_000,
                    UpdatedAt = f.Clock.Now,
                }
            );
            world.Add(
                new SavedPilotHostility
                {
                    SourceShipId = EnemyShip,
                    TargetShipId = f.ShipId,
                    LastAttackAt = f.Clock.Now,
                    ExpiresAt = f.Clock.Now + TimeSpan.FromMinutes(15).Ticks,
                }
            );
            world.SaveChanges();
        }
        var space = f.Db.Services.GetRequiredService<SharedSpace>();
        foreach (long account in new long[] { 1000001, 1000002 })
        {
            var contact = f.Db.Store.FindPilot(account)!;
            space.Publish(new(PilotContact.From(contact), SpacePosition.Zero, null, 0, null));
        }
    }
}
