using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Repair;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Persistence;

public sealed class EquipmentBonusTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void HybridUpgradesChangeAuthoritativeDamageAndCycleWithoutPenalizingSkills(int count)
    {
        using Fixture f = new();
        long gun = f.Fit(561);
        var combat = f.Db.Services.GetRequiredService<IPilotCombat>();
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == gun);
        var before = combat.ModuleAttributes(f.PilotId, item);
        long first = f.Fit(9944);
        if (count == 2)
        {
            f.Fit(9944);
        }
        var after = combat.ModuleAttributes(f.PilotId, item);
        double damage = 1.07 * (count == 2 ? 1 + 0.07 * 0.8691199808003975 : 1);
        double cycle = 0.92 * (count == 2 ? 1 - 0.08 * 0.8691199808003975 : 1);
        Assert.Equal(before[DogmaAttributeId.DamageMultiplier] * damage, after[DogmaAttributeId.DamageMultiplier], 8);
        Assert.Equal(before[DogmaAttributeId.WeaponCycleTime] * cycle, after[DogmaAttributeId.WeaponCycleTime], 8);
        f.Fitting.SetOnline(f.PilotId, f.ShipId, first, false);
        var offline = combat.ModuleAttributes(f.PilotId, item);
        Assert.Equal(before[DogmaAttributeId.DamageMultiplier] * (count == 2 ? 1.07 : 1), offline[DogmaAttributeId.DamageMultiplier], 8);
    }

    [Fact]
    public void HeatSinkCannotModifyHybridDamageOrCycle()
    {
        using Fixture f = new();
        long gun = f.Fit(561);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == gun);
        var combat = f.Db.Services.GetRequiredService<IPilotCombat>();
        var basis = combat.ModuleAttributes(f.PilotId, item);
        f.Fit(2363);
        var heatSink = combat.ModuleAttributes(f.PilotId, item);
        Assert.Equal(basis[DogmaAttributeId.DamageMultiplier], heatSink[DogmaAttributeId.DamageMultiplier]);
        Assert.Equal(basis[DogmaAttributeId.WeaponCycleTime], heatSink[DogmaAttributeId.WeaponCycleTime]);
        f.Fit(9944);
        var magnetic = combat.ModuleAttributes(f.PilotId, item);
        Assert.Equal(basis[DogmaAttributeId.DamageMultiplier] * 1.07, magnetic[DogmaAttributeId.DamageMultiplier], 8);
        Assert.Equal(basis[DogmaAttributeId.WeaponCycleTime] * 0.92, magnetic[DogmaAttributeId.WeaponCycleTime], 8);
    }

    [Fact]
    public void TrackingEnhancerTargetsGunneryWithoutChangingAnUnrelatedMiner()
    {
        using Fixture f = new();
        long gun = f.Fit(561);
        long miner = f.Fit(483);
        var combat = f.Db.Services.GetRequiredService<IPilotCombat>();
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        var gunItem = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == gun);
        var minerItem = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == miner);
        var before = combat.ModuleAttributes(f.PilotId, gunItem);
        var miningBefore = mining.ModuleAttributes(f.PilotId, minerItem);
        f.Fit(1998);
        var after = combat.ModuleAttributes(f.PilotId, gunItem);
        Assert.Equal(before[DogmaAttributeId.MaxRange] * 1.0725, after[DogmaAttributeId.MaxRange], 8);
        Assert.Equal(before[DogmaAttributeId.TrackingSpeed] * 1.07, after[DogmaAttributeId.TrackingSpeed], 8);
        Assert.Equal(before[DogmaAttributeId.AccuracyFalloff] * 1.145, after[DogmaAttributeId.AccuracyFalloff], 8);
        Assert.Equal(miningBefore[DogmaAttributeId.MaxRange], mining.ModuleAttributes(f.PilotId, minerItem)[DogmaAttributeId.MaxRange]);
    }

    [Fact]
    public void NativeRefreshPublishesDiminishedSecondEnhancerAndRestoresOfflineValues()
    {
        using Fixture f = new();
        long gun = f.Fit(3638);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == gun);
        var combat = f.Db.Services.GetRequiredService<IPilotCombat>();
        var basis = combat.ModuleAttributes(f.PilotId, item);
        f.Fit(1998);
        long second = f.Fit(1998);
        using var scope = f.Db.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<FittingOperations>();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => new(f.Db.Store, selected, f.Clock, new(), f.Db.Objects),
            10,
            SignedSettings.Empty
        );
        context.Select(pilot);
        AssertRefresh(1.1400795105071109, 1.289295644812386, 1.1350970865619499);
        operations.SetOnline(context, f.ShipId, second, false);
        AssertRefresh(1.0725, 1.145, 1.07);

        void AssertRefresh(double optimal, double falloff, double tracking)
        {
            context.Notifications.Clear();
            operations.NotifyShipAttributes(context);
            var changes = Assert.Single(context.Notifications.OfType<ModuleAttributesChanged>()).Changes;
            AssertValue(DogmaAttributeId.MaxRange, optimal);
            AssertValue(DogmaAttributeId.AccuracyFalloff, falloff);
            AssertValue(DogmaAttributeId.TrackingSpeed, tracking);

            void AssertValue(DogmaAttributeId attribute, double multiplier)
            {
                var change = Assert.Single(changes, c => c.Item == new DogmaItemKey.Item(gun) && c.Attribute == attribute);
                Assert.Equal(basis[attribute] * multiplier, change.Value, 8);
            }
        }
    }

    [Fact]
    public void MiningUpgradeAppliesItsCpuPenaltyWhileOfflineAndItsYieldOnlyOnline()
    {
        using Fixture f = new();
        long miner = f.Fit(483);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == miner);
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        var before = mining.ModuleAttributes(f.PilotId, item);
        long upgrade = f.Fit(22542, online: false);
        var offline = mining.ModuleAttributes(f.PilotId, item);
        Assert.Equal(before[DogmaAttributeId.MiningAmount], offline[DogmaAttributeId.MiningAmount]);
        // Mining Upgrades V reduces the upgrade's 10% fitting penalty to 7.5%.
        Assert.Equal(Math.Round(before[DogmaAttributeId.Cpu] * 1.075, 2), offline[DogmaAttributeId.Cpu]);
        f.Fitting.SetOnline(f.PilotId, f.ShipId, upgrade, true);
        var online = mining.ModuleAttributes(f.PilotId, item);
        Assert.Equal(before[DogmaAttributeId.MiningAmount] * 1.05, online[DogmaAttributeId.MiningAmount], 8);
        Assert.Equal(offline[DogmaAttributeId.Cpu], online[DogmaAttributeId.Cpu]);
        Assert.Equal(online[DogmaAttributeId.Cpu] + 30, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.CpuLoad], 8);
    }

    [Fact]
    public void CapacitorRelayPenaltyAndAmplifierBothAffectARealShieldBoost()
    {
        using Fixture f = new();
        long booster = f.Fit(399);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == booster);
        var boosts = f.Db.Services.GetRequiredService<IPilotShieldBoosting>();
        double basis = boosts.ModuleAttributes(f.PilotId, item)[DogmaAttributeId.ShieldBonus];
        double recharge = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.CapacitorRechargeTime];
        f.Fit(11561);
        f.Fit(1445);
        Assert.Equal(basis * 1.3 * 0.9, boosts.ModuleAttributes(f.PilotId, item)[DogmaAttributeId.ShieldBonus], 8);
        Assert.Equal(recharge * 0.8, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.CapacitorRechargeTime], 8);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var health = world.Set<ShipHealthState>().Find(f.ShipId)!;
            health.ShieldCharge = 0;
            health.UpdatedAt = f.Clock.Now;
            world.SaveChanges();
        }
        var result = boosts.Boost(f.PilotId, booster, f.Clock.Now);
        Assert.Equal(basis * 1.3 * 0.9, result.Restored, 8);
    }

    [Fact]
    public void NativeEmptyAmmoUnloadAllowsPassiveUnfitWithoutMovingOrInventingItems()
    {
        using Fixture f = new();
        long enhancer = f.Fit(1998);
        var before = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == enhancer);
        var ammo = f.Db.Services.GetRequiredService<IPilotAmmunition>();
        var result = ammo.Unload(f.PilotId, f.ShipId, [enhancer], f.StationId, (int)InventoryFlag.Hangar, null, f.Clock.Now);
        Assert.Empty(result.Items);
        Assert.Empty(result.Magazines);
        Assert.Equal(before, f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == enhancer));
        Assert.Throws<InvalidDataException>(() =>
            ammo.Unload(f.PilotId, f.ShipId, [enhancer + 1], f.StationId, (int)InventoryFlag.Hangar, null, f.Clock.Now)
        );
        f.Inventory.Move(f.PilotId, f.StationId, new([enhancer], f.ShipId, (int)InventoryFlag.Hangar, null));
        Assert.Equal(enhancer, f.Inventory.List(f.PilotId, f.StationId).Single(i => i.TypeId == 1998).Id);
    }

    [Fact]
    public void BallisticControlsDiminishTheCharacterAttributeOnlyWhileOnline()
    {
        using Fixture f = new();
        long first = f.Fit(12274);
        f.Fit(12274);
        Assert.Equal(1.07 * (1 + .07 * .8691199808003975), Multiplier(), 8);
        f.Fitting.SetOnline(f.PilotId, f.ShipId, first, false);
        Assert.Equal(1.07, Multiplier(), 8);

        double Multiplier()
        {
            using WorldDbContext world = new(f.Db.Path);
            using CatalogDbContext catalog = new(f.CatalogPath);
            var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == f.PilotId);
            return FittingCalculation.Read(world, catalog, pilot).Graph.Read(f.PilotId, DogmaAttributeId.MissileDamageMultiplier);
        }
    }

    [Fact]
    public void UnknownOrMissingEffectDefinitionsCannotEnableAPartialProfile()
    {
        using Fixture f = new();
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            var effect = catalog.Set<SourceRecord>().Find("client/dogmaeffects", "93")!;
            effect.Data = effect.Data.Replace("LocationGroupModifier", "UnimplementedModifier", StringComparison.Ordinal);
            catalog.Update(effect);
            catalog.SaveChanges();
            Assert.Null(FittingCatalog.Definition(catalog, 9944));
        }
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            var effect = catalog.Set<SourceRecord>().Find("client/dogmaeffects", "89")!;
            catalog.Remove(effect);
            catalog.SaveChanges();
            Assert.Null(FittingCatalog.Definition(catalog, 520));
        }
    }
}
