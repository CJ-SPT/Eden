using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Persistence;

public sealed class PassiveTierTests
{
    private const double SecondStackingWeight = 0.8691199808003975;

    private static Fixture WithTiers(params int[] types)
    {
        Fixture fixture = new();
        PassiveCatalogFixture.LoadTiers(fixture.CatalogPath, types.ToHashSet());
        return fixture;
    }

    [Fact]
    public void CompletePublishedCohortAdmitsAllFullyImplementedUnrestrictedProfilesAcrossTiers()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadTiers(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var types = catalog.Set<ItemType>().Where(type => PassiveModules.Groups.Contains(type.GroupId)).ToArray();
        var supported = types.Where(type => PassiveCatalog.Supported(catalog, type.Id, type.GroupId)).ToArray();
        Assert.Equal(856, supported.Length);
        Assert.Equal(31, supported.Select(type => type.GroupId).Distinct().Count());
        Assert.All(supported, type => Assert.NotNull(FittingCatalog.Definition(catalog, type.Id)));
        Assert.All(types.Except(supported), type => Assert.Null(FittingCatalog.Definition(catalog, type.Id)));
        var tiers = supported
            .Select(type => catalog.Set<SourceRecord>().Find("client/types", PassiveCatalog.Key(type.Id))!.Read<ClientTypes>().MetaGroupID)
            .ToHashSet();
        Assert.All(new long[] { 1, 2, 3, 4, 5, 6, 19 }, tier => Assert.Contains(tier, tiers));
        Assert.NotNull(FittingCatalog.Definition(catalog, 93905)); // Static officer profile with catalog tech level 5.
        Assert.NotNull(FittingCatalog.Definition(catalog, 49792)); // Static Frostline profile without a tech-level attribute.
    }

    [Theory]
    [InlineData(10190, 1.1, .895)]
    [InlineData(15895, 1.12, .89)]
    [InlineData(15150, 1.145, .89)]
    public void TechTwoFactionAndOfficerHybridUpgradesUseTheirCatalogDamageAndCycle(int type, double damage, double cycle)
    {
        using Fixture f = WithTiers(type);
        long gun = f.Fit((int)CombatWeaponType.GatlingRailgun75mm);
        var combat = f.Db.Services.GetRequiredService<IPilotCombat>();
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(item => item.Id == gun);
        var before = combat.ModuleAttributes(f.PilotId, item);
        long module = f.Fit(type);
        var after = combat.ModuleAttributes(f.PilotId, item);
        Assert.Equal(before[DogmaAttributeId.DamageMultiplier] * damage, after[DogmaAttributeId.DamageMultiplier], 8);
        Assert.Equal(before[DogmaAttributeId.WeaponCycleTime] * cycle, after[DogmaAttributeId.WeaponCycleTime], 8);
        f.Fitting.SetOnline(f.PilotId, f.ShipId, module, false);
        var offline = combat.ModuleAttributes(f.PilotId, item);
        Assert.Equal(before[DogmaAttributeId.DamageMultiplier], offline[DogmaAttributeId.DamageMultiplier], 8);
        Assert.Equal(before[DogmaAttributeId.WeaponCycleTime], offline[DogmaAttributeId.WeaponCycleTime], 8);
    }

    [Theory]
    [InlineData(2364)]
    [InlineData(15810)]
    [InlineData(14812)]
    public void HigherTierHeatSinksStillCannotModifyHybridWeapons(int type)
    {
        using Fixture f = WithTiers(type);
        long gun = f.Fit((int)CombatWeaponType.GatlingRailgun75mm);
        var combat = f.Db.Services.GetRequiredService<IPilotCombat>();
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(item => item.Id == gun);
        var before = combat.ModuleAttributes(f.PilotId, item);
        f.Fit(type);
        var after = combat.ModuleAttributes(f.PilotId, item);
        Assert.Equal(before[DogmaAttributeId.DamageMultiplier], after[DogmaAttributeId.DamageMultiplier]);
        Assert.Equal(before[DogmaAttributeId.WeaponCycleTime], after[DogmaAttributeId.WeaponCycleTime]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedTierDiminishingReturnsRankStrengthIndependentlyOfFitOrder(bool officerFirst)
    {
        using Fixture f = WithTiers(10190, 15150);
        long gun = f.Fit((int)CombatWeaponType.GatlingRailgun75mm);
        var combat = f.Db.Services.GetRequiredService<IPilotCombat>();
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(item => item.Id == gun);
        var before = combat.ModuleAttributes(f.PilotId, item);
        f.Fit(officerFirst ? 15150 : 10190);
        f.Fit(officerFirst ? 10190 : 15150);
        var after = combat.ModuleAttributes(f.PilotId, item);
        Assert.Equal(
            before[DogmaAttributeId.DamageMultiplier] * 1.145 * (1 + .1 * SecondStackingWeight),
            after[DogmaAttributeId.DamageMultiplier],
            8
        );
        Assert.Equal(
            before[DogmaAttributeId.WeaponCycleTime] * .89 * (1 - .105 * SecondStackingWeight),
            after[DogmaAttributeId.WeaponCycleTime],
            8
        );
    }

    [Theory]
    [InlineData(380, 520, 2)]
    [InlineData(31924, 550, 1)]
    public void HigherTierExtendersAddBeforeHullSkillsAndPreserveHealth(int type, double amount, double signature)
    {
        using Fixture f = WithTiers(type);
        f.Train((int)DefenseSkill.ShieldManagement, 3);
        long module = f.Fit(type, online: false);
        var before = f.Fitting.ShipAttributes(f.PilotId);
        using (WorldDbContext saved = new(f.Db.Path))
        {
            saved.Add(
                new ShipHealthState
                {
                    ShipId = f.ShipId,
                    ShieldCharge = before[DogmaAttributeId.ShieldCapacity] / 2,
                    ArmorDamage = 20,
                    StructureDamage = 30,
                    UpdatedAt = f.Clock.Now,
                }
            );
            saved.SaveChanges();
        }
        f.Fitting.SetOnline(f.PilotId, f.ShipId, module, true);
        var after = f.Fitting.ShipAttributes(f.PilotId);
        Assert.Equal(before[DogmaAttributeId.ShieldCapacity] + amount * 1.15, after[DogmaAttributeId.ShieldCapacity], 8);
        Assert.Equal(before[DogmaAttributeId.SignatureRadius] + signature, after[DogmaAttributeId.SignatureRadius], 8);
        using WorldDbContext check = new(f.Db.Path);
        var health = check.Set<ShipHealthState>().Single();
        Assert.Equal(after[DogmaAttributeId.ShieldCapacity] / 2, health.ShieldCharge, 8);
        Assert.Equal(20, health.ArmorDamage);
        Assert.Equal(30, health.StructureDamage);
    }

    [Theory]
    [InlineData(1319, 1.275, .82, .77)]
    [InlineData(93905, 1.29, .95, .85)]
    public void CargoVariantsRetainTheirDistinctOfflinePenaltiesAndOnlineCapacity(
        int type,
        double capacity,
        double velocity,
        double structure
    )
    {
        using Fixture f = WithTiers(type);
        var before = f.Fitting.ShipAttributes(f.PilotId);
        long module = f.Fit(type, online: false);
        var offline = f.Fitting.ShipAttributes(f.PilotId);
        Assert.Equal(before[DogmaAttributeId.CargoCapacity], offline[DogmaAttributeId.CargoCapacity]);
        Assert.Equal(before[DogmaAttributeId.MaxVelocity] * velocity, offline[DogmaAttributeId.MaxVelocity], 8);
        Assert.Equal(before[DogmaAttributeId.StructureHitPoints] * structure, offline[DogmaAttributeId.StructureHitPoints], 8);
        f.Fitting.SetOnline(f.PilotId, f.ShipId, module, true);
        Assert.Equal(
            before[DogmaAttributeId.CargoCapacity] * capacity,
            f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.CargoCapacity],
            8
        );
    }

    [Fact]
    public void DeadspaceResistanceProfilesReceiveTheirRelevantCompensationSkillOnce()
    {
        using Fixture f = WithTiers(18795, 19231);
        f.Train((int)PassiveModuleSkill.EmArmorCompensation, 3);
        f.Train((int)PassiveModuleSkill.EmShieldCompensation, 2);
        var before = f.Fitting.ShipAttributes(f.PilotId);
        f.Fit(18795);
        f.Fit(19231);
        var after = f.Fitting.ShipAttributes(f.PilotId);
        Assert.Equal(before[DogmaAttributeId.ArmorEmResonance] * (1 - .3606 * 1.15), after[DogmaAttributeId.ArmorEmResonance], 8);
        Assert.Equal(before[DogmaAttributeId.ShieldEmResonance] * (1 - .4494 * 1.1), after[DogmaAttributeId.ShieldEmResonance], 8);
    }

    [Theory]
    [InlineData(2048, .85, .6, .875)]
    [InlineData(41210, .78, .475, .79)]
    public void HigherTierDamageControlRemainsExemptFromResistanceStacking(int type, double armor, double hull, double shield)
    {
        using Fixture f = WithTiers(type, 18795);
        var before = f.Fitting.ShipAttributes(f.PilotId);
        f.Fit(type);
        f.Fit(18795);
        var after = f.Fitting.ShipAttributes(f.PilotId);
        Assert.Equal(before[DogmaAttributeId.ArmorEmResonance] * armor * (1 - .3606), after[DogmaAttributeId.ArmorEmResonance], 8);
        Assert.Equal(before[DogmaAttributeId.StructureEmResonance] * hull, after[DogmaAttributeId.StructureEmResonance], 8);
        Assert.Equal(before[DogmaAttributeId.ShieldEmResonance] * shield, after[DogmaAttributeId.ShieldEmResonance], 8);
    }
}
