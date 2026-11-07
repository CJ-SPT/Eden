using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Repair;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.Protocol.Skills;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Repair;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Repair;

public sealed class RepairTierTests
{
    private const int SmallArmorRepairerTwo = 1183;
    private const int CapitalArmorRepairerTwo = 41500;
    private const int ShieldHardenerTwo = 2301;
    private const int ArmorHardenerTwo = 11642;
    private const double SecondStackingWeight = 0.8691199808003975;

    private static Fixture WithTiers(params int[] types)
    {
        Fixture fixture = new();
        PassiveCatalogFixture.LoadActiveTiers(fixture.CatalogPath, types.ToHashSet());
        return fixture;
    }

    private static IPilotLocalRepairing Repairing(Fixture f) => f.Db.Services.GetRequiredService<IPilotLocalRepairing>();

    private static IPilotHardening Hardening(Fixture f) => f.Db.Services.GetRequiredService<IPilotHardening>();

    private static void DamageArmor(Fixture f, double damage = 100)
    {
        using WorldDbContext saved = new(f.Db.Path);
        var state = saved.Set<ShipHealthState>().Find(f.ShipId) ?? saved.Add(new ShipHealthState { ShipId = f.ShipId }).Entity;
        state.ArmorDamage = damage;
        state.StructureDamage = 9;
        state.ShieldCharge = 25;
        state.UpdatedAt = f.Clock.Now;
        saved.SaveChanges();
    }

    [Fact]
    public void CompletePublishedCohortsSupportAllStaticRepairersAndHardenersAcrossTiers()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadActiveTiers(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var repairs = catalog.Set<ItemType>().Where(type => type.GroupId == ArmorRepairDefinitions.Group).ToArray();
        var supported = repairs.Where(type => ArmorRepairCatalog.Supported(catalog, type.Id, type.GroupId)).ToArray();
        Assert.Equal(105, repairs.Length);
        Assert.Equal(101, supported.Length);
        Assert.All(supported, type => Assert.Equal(ModuleRack.Low, FittingCatalog.Definition(catalog, type.Id)!.Rack));
        Assert.All(repairs.Except(supported), type => Assert.Null(FittingCatalog.Definition(catalog, type.Id)));
        var hardeners = catalog
            .Set<ItemType>()
            .Where(type => type.GroupId == HardenerDefinitions.ShieldGroup || type.GroupId == HardenerDefinitions.ArmorGroup)
            .ToArray();
        Assert.Equal(103, hardeners.Count(type => type.GroupId == HardenerDefinitions.ShieldGroup));
        Assert.Equal(148, hardeners.Count(type => type.GroupId == HardenerDefinitions.ArmorGroup));
        Assert.All(
            hardeners,
            type =>
            {
                Assert.True(HardenerCatalog.Supported(catalog, type.Id, type.GroupId));
                var rack = type.GroupId == HardenerDefinitions.ShieldGroup ? ModuleRack.Medium : ModuleRack.Low;
                Assert.Equal(rack, FittingCatalog.Definition(catalog, type.Id)!.Rack);
            }
        );
        var tiers = supported
            .Select(type => catalog.Set<SourceRecord>().Find("client/types", PassiveCatalog.Key(type.Id))!.Read<ClientTypes>().MetaGroupID)
            .ToHashSet();
        Assert.All(new long[] { 1, 2, 3, 4, 5, 6 }, tier => Assert.Contains(tier, tiers));
    }

    [Theory]
    [InlineData(SmallArmorRepairerTwo, 92, 40, 5100)]
    [InlineData(13962, 83, 36, 5100)]
    [InlineData(22887, 104, 40, 5737.5)]
    [InlineData(19015, 120, 40, 5100)]
    [InlineData(84970, 130, 40, 5100)]
    [InlineData(84971, 145, 45, 5100)]
    public void HigherTierRepairersPayTheirOwnCostAndCompleteTheirOwnAmountAtTheExactDeadline(
        int type,
        double amount,
        double cost,
        double duration
    )
    {
        using Fixture f = WithTiers(type);
        long module = f.Fit(type);
        f.Train(ArmorRepairDefinitions.RepairSystemsSkill, 3);
        DamageArmor(f);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        double capacitor = f.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.PilotId, f.Clock.Now);
        var paid = Repairing(f).Start(f.PilotId, module, f.Clock.Now);
        Assert.Equal(capacitor - cost, paid.Capacitor, 8);
        Assert.Equal(duration, paid.Schedule.DurationMilliseconds);
        using (WorldDbContext scheduled = new(f.Db.Path))
        {
            Assert.Equal(amount, scheduled.Set<PendingLocalRepair>().Single().Amount);
        }
        var before = Repairing(f).Complete(f.PilotId, f.ShipId, paid.Schedule.CompletesAt - 1);
        Assert.Equal(100, before.Defense.Armor - before.Remaining.Armor, 8);
        var after = Repairing(f).Complete(f.PilotId, f.ShipId, paid.Schedule.CompletesAt);
        Assert.Equal(Math.Max(0, 100 - amount), after.Defense.Armor - after.Remaining.Armor, 8);
        Assert.Equal(9, after.Defense.Structure - after.Remaining.Structure, 8);
        var duplicate = Repairing(f).Complete(f.PilotId, f.ShipId, paid.Schedule.CompletesAt + 1);
        Assert.Equal(after.Remaining.Armor, duplicate.Remaining.Armor);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<PendingLocalRepair>());
        Assert.Equal(capacitor - cost, saved.Set<ShipEnergy>().Single().Charge, 8);
        Assert.Equal(paid.Schedule.CompletesAt, saved.Set<ModuleCooldown>().Single().ReadyAt);
    }

    [Fact]
    public void CapitalArmorRepairersUseTheirOwnSkillWithoutApplyingOrdinaryRepairSystems()
    {
        int capitalSkill = (int)PassiveModuleSkill.CapitalRepairSystems;
        using Fixture f = WithTiers(CapitalArmorRepairerTwo, SmallArmorRepairerTwo, capitalSkill);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            // Artificial budgets isolate the module/skill rule; this is not a real Ibis fit.
            catalog
                .Set<TypeAttribute>()
                .Where(attribute => attribute.ItemTypeId == 601 && attribute.AttributeId == (int)DogmaAttributeId.PowerOutput)
                .ExecuteUpdate(updates => updates.SetProperty(attribute => attribute.Value, 1000000));
            catalog
                .Set<TypeAttribute>()
                .Where(attribute => attribute.ItemTypeId == 601 && attribute.AttributeId == (int)DogmaAttributeId.CpuOutput)
                .ExecuteUpdate(updates => updates.SetProperty(attribute => attribute.Value, 1000));
            catalog
                .Set<TypeAttribute>()
                .Where(attribute => attribute.ItemTypeId == 601 && attribute.AttributeId == (int)DogmaAttributeId.CapacitorCapacity)
                .ExecuteUpdate(updates => updates.SetProperty(attribute => attribute.Value, 10000));
        }
        long capital = f.Fit(CapitalArmorRepairerTwo);
        long small = f.Fit(SmallArmorRepairerTwo);
        f.Train(capitalSkill, 4);
        f.Train(ArmorRepairDefinitions.RepairSystemsSkill, 3);
        var items = f.Inventory.List(f.PilotId, f.ShipId);
        Assert.Equal(24000, f.Fitting.ModuleAttributes(f.PilotId, items.Single(item => item.Id == capital))[DogmaAttributeId.Duration]);
        Assert.Equal(5100, f.Fitting.ModuleAttributes(f.PilotId, items.Single(item => item.Id == small))[DogmaAttributeId.Duration]);
        var brain = f.Db.Services.GetRequiredService<PilotBrain>().Read(f.PilotId, f.ShipId);
        Assert.Contains(brain.ShipEffects, effect => effect.Skills.Contains(capitalSkill) && effect.Value == -20);
        DamageArmor(f);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        var paid = Repairing(f).Start(f.PilotId, capital, f.Clock.Now);
        Assert.Equal(7120, paid.Capacitor, 8);
        Assert.Equal(24000, paid.Schedule.DurationMilliseconds);
        using (WorldDbContext saved = new(f.Db.Path))
        {
            Assert.Equal(11520, saved.Set<PendingLocalRepair>().Single().Amount);
        }
        var completed = Repairing(f).Complete(f.PilotId, f.ShipId, paid.Schedule.CompletesAt);
        Assert.Equal(completed.Defense.Armor, completed.Remaining.Armor);
        Assert.Equal(9, completed.Defense.Structure - completed.Remaining.Structure);
    }

    [Theory]
    [InlineData(ShieldHardenerTwo, .55, 20, 10000)]
    [InlineData(13965, .5624, 20, 15000)]
    [InlineData(20639, .55, 20, 10000)]
    [InlineData(19282, .635, 20, 12000)]
    [InlineData(14755, .64, 20, 12000)]
    [InlineData(ArmorHardenerTwo, .55, 30, 20000)]
    [InlineData(13986, .55, 10, 24000)]
    [InlineData(18973, .6288, 46, 20000)]
    [InlineData(15028, .64, 46, 20000)]
    public void HigherTierHardenersApplyTheirOwnRealMitigationPaymentAndDeadline(int type, double bonus, double cost, double duration)
    {
        using Fixture f = WithTiers(type);
        long module = f.Fit(type);
        f.Train((int)PassiveModuleSkill.EmArmorCompensation, 5);
        f.Train((int)PassiveModuleSkill.EmShieldCompensation, 5);
        var before = f.Fitting.ShipAttributes(f.PilotId);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        int group = f.Inventory.List(f.PilotId, f.ShipId).Single(item => item.Id == module).GroupId;
        var effect = HardenerDefinitions.Effect(group)!.Value;
        bool armor = group == HardenerDefinitions.ArmorGroup;
        var resistance = armor ? DogmaAttributeId.ArmorEmResonance : DogmaAttributeId.ShieldEmResonance;
        double capacitor = f.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.PilotId, f.Clock.Now);
        var paid = Hardening(f).Activate(f.PilotId, module, effect, f.Clock.Now);
        Assert.Equal(capacitor - cost, paid.Capacitor, 8);
        Assert.Equal(duration, paid.Module.DurationMilliseconds);
        Assert.Equal(before[resistance] * (1 - bonus), paid.ShipAttributes[resistance], 8);
        var health = f.Db.Services.GetRequiredService<IPilotHealth>().Read(f.PilotId, f.Clock.Now);
        var remaining = armor ? health.Remaining with { Shield = 0 } : health.Remaining;
        var damage = CombatDamage.Apply(remaining, health.Defense, new(10, 0, 0, 0));
        Assert.Equal(10 * before[resistance] * (1 - bonus), damage.Applied, 8);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(f.Clock.Now + (long)(duration * TimeSpan.TicksPerMillisecond), saved.Set<ModuleCooldown>().Single().ReadyAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedTierActiveStackingRanksStrengthWithoutPenalizingDamageControl(bool officerFirst)
    {
        const int officer = 14755;
        const int damageControl = 2048;
        using Fixture f = WithTiers(ShieldHardenerTwo, officer);
        PassiveCatalogFixture.LoadTiers(f.CatalogPath, new HashSet<int> { damageControl });
        f.Fit(damageControl);
        long techTwo = f.Fit(ShieldHardenerTwo);
        long modified = f.Fit(officer);
        var before = f.Fitting.ShipAttributes(f.PilotId);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        Hardening(f).Activate(f.PilotId, officerFirst ? modified : techTwo, DogmaEffect.ActiveShieldResistance, f.Clock.Now);
        var second = Hardening(f).Activate(f.PilotId, officerFirst ? techTwo : modified, DogmaEffect.ActiveShieldResistance, f.Clock.Now);
        Assert.Equal(
            before[DogmaAttributeId.ShieldEmResonance] * .36 * (1 - .55 * SecondStackingWeight),
            second.ShipAttributes[DogmaAttributeId.ShieldEmResonance],
            8
        );
        var stopped = Hardening(f).Stop(f.PilotId, f.ShipId, modified);
        Assert.Equal(before[DogmaAttributeId.ShieldEmResonance] * .45, stopped[DogmaAttributeId.ShieldEmResonance], 8);
    }

    [Theory]
    [InlineData(SmallArmorRepairerTwo, ArmorRepairDefinitions.RepairSystemsSkill, 2)]
    [InlineData(ShieldHardenerTwo, HardenerDefinitions.TacticalShieldManipulationSkill, 3)]
    [InlineData(ArmorHardenerTwo, (int)DefenseSkill.HullUpgrades, 4)]
    public void HigherSkillRequirementsRejectActivationBeforeAnyPaymentOrLiveState(int type, int skill, int level)
    {
        using Fixture f = WithTiers(type);
        long module = f.Fit(type);
        f.Train(skill, level);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        double before = f.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.PilotId, f.Clock.Now);
        if (type == SmallArmorRepairerTwo)
        {
            Assert.Throws<InvalidDataException>(() => Repairing(f).Start(f.PilotId, module, f.Clock.Now));
        }
        else
        {
            var effect = type == ShieldHardenerTwo ? DogmaEffect.ActiveShieldResistance : DogmaEffect.ActiveArmorResistance;
            Assert.Throws<InvalidDataException>(() => Hardening(f).Activate(f.PilotId, module, effect, f.Clock.Now));
        }
        Assert.Equal(before, f.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.PilotId, f.Clock.Now), 8);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<PendingLocalRepair>());
        Assert.Empty(saved.Set<ActiveFittedModule>());
        Assert.Empty(saved.Set<ModuleCooldown>());
    }

    [Theory]
    [InlineData(SmallArmorRepairerTwo, "effect")]
    [InlineData(SmallArmorRepairerTwo, "mutation")]
    [InlineData(SmallArmorRepairerTwo, "restriction")]
    [InlineData(ShieldHardenerTwo, "effect")]
    [InlineData(ShieldHardenerTwo, "mutation")]
    [InlineData(ShieldHardenerTwo, "restriction")]
    public void AddedUnimplementedEffectsMutationsAndShipRestrictionsNeverGrantTierEligibility(int type, string reason)
    {
        using Fixture f = WithTiers(type);
        f.Train(ArmorRepairDefinitions.RepairSystemsSkill, 3);
        f.Train(HardenerDefinitions.TacticalShieldManipulationSkill, 4);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            string key = PassiveCatalog.Key(type);
            switch (reason)
            {
                case "effect":
                    string addedEffect = """{"effectID":6730}""";
                    catalog.Database.ExecuteSql(
                        $"""
                        UPDATE SourceRecord SET Data=json_insert(Data,'$.dogmaEffects[#]',json({addedEffect}))
                        WHERE Dataset='client/typedogma' AND Key={key}
                        """
                    );
                    break;
                case "mutation":
                    catalog.Database.ExecuteSql(
                        $"UPDATE SourceRecord SET Data=json_set(Data,'$.metaGroupID',15) WHERE Dataset='client/types' AND Key={key}"
                    );
                    break;
                case "restriction":
                    catalog.Database.ExecuteSql($"INSERT INTO TypeAttribute VALUES ({type},{(int)DogmaAttributeId.CanFitShipGroup1},1)");
                    break;
            }
            Assert.Null(FittingCatalog.Definition(catalog, type));
        }
        long item = f.Grant(type);
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.PilotId, [item], f.StationId, (int)InventoryFlag.AutoFit));
        Assert.Contains(f.Inventory.List(f.PilotId, f.StationId), saved => saved.Id == item && !saved.Singleton);
    }

    [Fact]
    public void RestartKeepsThePaidRepairOnceButRemovesHigherTierHardeningWithoutRefunding()
    {
        using Fixture f = WithTiers(SmallArmorRepairerTwo, 15028);
        long repair = f.Fit(SmallArmorRepairerTwo);
        long hardener = f.Fit(15028);
        f.Train(ArmorRepairDefinitions.RepairSystemsSkill, 3);
        DamageArmor(f);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        var paidRepair = Repairing(f).Start(f.PilotId, repair, f.Clock.Now);
        var paidHardener = Hardening(f).Activate(f.PilotId, hardener, DogmaEffect.ActiveArmorResistance, f.Clock.Now);
        WorldInitialization.Open(f.Db.Path);
        Assert.Throws<InvalidDataException>(() =>
            Hardening(f).Activate(f.PilotId, hardener, DogmaEffect.ActiveArmorResistance, f.Clock.Now)
        );
        Assert.Single(Repairing(f).Read(f.PilotId, f.ShipId, f.Clock.Now));
        var healed = Repairing(f).Complete(f.PilotId, f.ShipId, paidRepair.Schedule.CompletesAt);
        Assert.Equal(8, healed.Defense.Armor - healed.Remaining.Armor, 8);
        Assert.Equal(.5, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ArmorEmResonance], 8);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ActiveFittedModule>());
        Assert.Empty(saved.Set<PendingLocalRepair>());
        Assert.Equal(paidHardener.Capacitor, saved.Set<ShipEnergy>().Single().Charge, 8);
        Assert.Equal(2, saved.Set<ModuleCooldown>().Count());
    }
}
