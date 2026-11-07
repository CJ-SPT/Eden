using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
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

public sealed class ShieldBoosterTierTests
{
    private const int SmallTechTwo = 400;
    private const int CapitalTechTwo = 41507;

    private static Fixture WithTiers(params int[] types)
    {
        Fixture fixture = new();
        var selected = types.ToHashSet();
        selected.Add((int)ShieldBoostSkill.Compensation);
        selected.Add((int)ShieldBoostSkill.CapitalOperation);
        PassiveCatalogFixture.LoadActiveTiers(fixture.CatalogPath, selected);
        return fixture;
    }

    private static IPilotShieldBoosting Boosting(Fixture f) => f.Db.Services.GetRequiredService<IPilotShieldBoosting>();

    private static void Damage(Fixture f, double shield = 25)
    {
        using WorldDbContext saved = new(f.Db.Path);
        var state = saved.Set<ShipHealthState>().Find(f.ShipId) ?? saved.Add(new ShipHealthState { ShipId = f.ShipId }).Entity;
        state.ShieldCharge = shield;
        state.ArmorDamage = 12;
        state.StructureDamage = 9;
        state.UpdatedAt = f.Clock.Now;
        saved.SaveChanges();
    }

    [Fact]
    public void AllEightyNineStaticProfilesFitAcrossTiersWhileFiveMutatedProfilesRemainUnavailable()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadActiveTiers(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var types = catalog.Set<ItemType>().Where(type => type.GroupId == ShieldBoostDefinitions.Group).ToArray();
        var supported = types.Where(type => ShieldBoosterCatalog.Supported(catalog, type.Id, type.GroupId)).ToArray();
        Assert.Equal(94, types.Length);
        Assert.Equal(89, supported.Length);
        Assert.All(supported, type => Assert.Equal(ModuleRack.Medium, FittingCatalog.Definition(catalog, type.Id)!.Rack));
        Assert.All(types.Except(supported), type => Assert.Null(FittingCatalog.Definition(catalog, type.Id)));
        var tiers = supported
            .Select(type => catalog.Set<SourceRecord>().Find("client/types", PassiveCatalog.Key(type.Id))!.Read<ClientTypes>().MetaGroupID)
            .ToHashSet();
        Assert.All(new long[] { 1, 2, 3, 4, 5, 6 }, tier => Assert.Contains(tier, tiers));
    }

    [Theory]
    [InlineData(SmallTechTwo, 35, 20, 2000)]
    [InlineData(13952, 28, 16, 1600)]
    [InlineData(20617, 28, 20, 2000)]
    [InlineData(19179, 76, 20, 2000)]
    [InlineData(19173, 57, 13, 2000)]
    [InlineData(84998, 66, 14, 2000)]
    [InlineData(84999, 86, 20, 2000)]
    public void HigherTierBoostsUseTheirOwnImmediateHealingCycleAndSkillAdjustedPayment(
        int type,
        double amount,
        double cost,
        double duration
    )
    {
        using Fixture f = WithTiers(type);
        long module = f.Fit(type);
        f.Train((int)ShieldBoostSkill.Compensation, 3);
        Damage(f);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        double before = f.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.PilotId, f.Clock.Now);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(item => item.Id == module);
        Assert.Equal(cost * .94, f.Fitting.ModuleAttributes(f.PilotId, item)[DogmaAttributeId.CapacitorNeed], 8);
        var paid = Boosting(f).Boost(f.PilotId, module, f.Clock.Now);
        Assert.Equal(amount, paid.Restored, 8);
        Assert.Equal(25 + amount, paid.Health.Remaining.Shield, 8);
        Assert.Equal(cost * .94, paid.Booster.CapacitorNeed, 8);
        Assert.Equal(before - cost * .94, paid.Capacitor, 8);
        Assert.Equal(duration, paid.Booster.DurationMilliseconds);
        Assert.Equal(12, paid.Health.Defense.Armor - paid.Health.Remaining.Armor, 8);
        Assert.Equal(9, paid.Health.Defense.Structure - paid.Health.Remaining.Structure, 8);
        Assert.Throws<InvalidDataException>(() => Boosting(f).Boost(f.PilotId, module, f.Clock.Now));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(f.Clock.Now + (long)(duration * TimeSpan.TicksPerMillisecond), saved.Set<ModuleCooldown>().Single().ReadyAt);
        var brain = f.Db.Services.GetRequiredService<PilotBrain>().Read(f.PilotId, f.ShipId);
        Assert.Contains(brain.ShipEffects, effect => effect.Skills.Contains((int)ShieldBoostSkill.Compensation) && effect.Value == -6);
    }

    [Fact]
    public void CapitalSkillAndCompensationHaveSeparateRequiredSkillSelectorsAndApplyOnce()
    {
        using Fixture f = WithTiers(CapitalTechTwo, SmallTechTwo);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            // Artificial fitting/capacitor budgets isolate the capital rule, not a real Ibis fit.
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
        long capital = f.Fit(CapitalTechTwo);
        long small = f.Fit(SmallTechTwo);
        f.Train((int)ShieldBoostSkill.Compensation, 5);
        f.Train((int)ShieldBoostSkill.CapitalOperation, 4);
        var items = f.Inventory.List(f.PilotId, f.ShipId);
        Assert.Equal(
            2649.6,
            Boosting(f).ModuleAttributes(f.PilotId, items.Single(item => item.Id == capital))[DogmaAttributeId.CapacitorNeed],
            8
        );
        Assert.Equal(
            18,
            Boosting(f).ModuleAttributes(f.PilotId, items.Single(item => item.Id == small))[DogmaAttributeId.CapacitorNeed],
            8
        );
        var brain = f.Db.Services.GetRequiredService<PilotBrain>().Read(f.PilotId, f.ShipId);
        Assert.Contains(brain.ShipEffects, effect => effect.Skills.Contains((int)ShieldBoostSkill.CapitalOperation) && effect.Value == -8);
        Damage(f);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        var paid = Boosting(f).Boost(f.PilotId, capital, f.Clock.Now);
        Assert.Equal(8640, paid.Booster.ShieldAmount);
        Assert.Equal(7350.4, paid.Capacitor, 8);
        Assert.Equal(paid.Health.Defense.Shield, paid.Health.Remaining.Shield);
        Assert.Equal(12, paid.Health.Defense.Armor - paid.Health.Remaining.Armor, 8);
        Assert.Equal(9, paid.Health.Defense.Structure - paid.Health.Remaining.Structure, 8);
    }

    [Fact]
    public void AmplificationAndNearFullClippingPreserveTheActualPayment()
    {
        const int amplifier = 24443;
        using Fixture f = WithTiers(SmallTechTwo);
        PassiveCatalogFixture.LoadTiers(f.CatalogPath, new HashSet<int> { amplifier });
        long module = f.Fit(SmallTechTwo);
        f.Fit(amplifier);
        f.Train((int)ShieldBoostSkill.Compensation, 3);
        double maximum = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ShieldCapacity];
        Damage(f, maximum - 5);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        var paid = Boosting(f).Boost(f.PilotId, module, f.Clock.Now);
        Assert.Equal(35 * 1.36, paid.Booster.ShieldAmount, 8);
        Assert.Equal(5, paid.Restored, 8);
        Assert.Equal(maximum, paid.Health.Remaining.Shield, 8);
        Assert.Equal(18.8, paid.Booster.CapacitorNeed, 8);
    }

    [Fact]
    public void TechTwoSkillRejectionAndRestartKeepPaymentAndShieldStateAuthoritative()
    {
        using Fixture f = WithTiers(SmallTechTwo);
        long module = f.Fit(SmallTechTwo);
        f.Train((int)DefenseSkill.ShieldOperation, 2);
        Damage(f);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        Assert.Throws<InvalidDataException>(() => Boosting(f).Boost(f.PilotId, module, f.Clock.Now));
        using (WorldDbContext rejected = new(f.Db.Path))
        {
            Assert.Empty(rejected.Set<ModuleCooldown>());
            Assert.Equal(25, rejected.Set<ShipHealthState>().Single().ShieldCharge);
        }
        f.Train((int)DefenseSkill.ShieldOperation, 3);
        var paid = Boosting(f).Boost(f.PilotId, module, f.Clock.Now);
        WorldInitialization.Open(f.Db.Path);
        Assert.Throws<InvalidDataException>(() => Boosting(f).Boost(f.PilotId, module, f.Clock.Now));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(paid.Capacitor, saved.Set<ShipEnergy>().Single().Charge, 8);
        Assert.Equal(paid.Health.Remaining.Shield, saved.Set<ShipHealthState>().Single().ShieldCharge, 8);
        Assert.Equal(f.Clock.Now + 2000 * TimeSpan.TicksPerMillisecond, saved.Set<ModuleCooldown>().Single().ReadyAt);
    }

    [Theory]
    [InlineData("effect")]
    [InlineData("mutation")]
    [InlineData("restriction")]
    public void UnsupportedProfilesCannotBeActivatedThroughAStoredFittedItem(string reason)
    {
        using Fixture f = WithTiers(SmallTechTwo);
        long module = f.Fit(SmallTechTwo);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            switch (reason)
            {
                case "effect":
                    string addedEffect = """{"effectID":6730}""";
                    catalog.Database.ExecuteSql(
                        $"""
                        UPDATE SourceRecord SET Data=json_insert(Data,'$.dogmaEffects[#]',json({addedEffect}))
                        WHERE Dataset='client/typedogma' AND Key='400'
                        """
                    );
                    break;
                case "mutation":
                    catalog.Database.ExecuteSqlRaw(
                        "UPDATE SourceRecord SET Data=json_set(Data,'$.metaGroupID',15) WHERE Dataset='client/types' AND Key='400'"
                    );
                    break;
                case "restriction":
                    catalog.Database.ExecuteSql(
                        $"INSERT INTO TypeAttribute VALUES ({SmallTechTwo},{(int)DogmaAttributeId.CanFitShipGroup1},1)"
                    );
                    break;
            }
            Assert.Null(FittingCatalog.Definition(catalog, SmallTechTwo));
        }
        Damage(f);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        Assert.Throws<InvalidDataException>(() => Boosting(f).Boost(f.PilotId, module, f.Clock.Now));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ModuleCooldown>());
        Assert.Equal(25, saved.Set<ShipHealthState>().Single().ShieldCharge);
    }
}
