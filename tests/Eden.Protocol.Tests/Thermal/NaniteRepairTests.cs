using System.Text.Json.Nodes;
using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Thermal;

public sealed class NaniteRepairTests
{
    private const int ShieldBooster = 399;
    private const int DamageControl = 2048;

    private static Fixture Prepare(int level = 0)
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadOverload(f.CatalogPath);
        if (level > 0)
        {
            f.Train((int)NaniteRepairSkill.Operation, level);
            f.Train((int)NaniteRepairSkill.Interfacing, level);
        }
        return f;
    }

    private static NaniteRepairQuote Quote(Fixture f, long module)
    {
        using WorldDbContext world = new(f.Db.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(f.CatalogPath);
        var quote = NaniteRepairPersistence.Quote(world, catalog, f.PilotId, module);
        transaction.Commit();
        return quote;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void EarnedNaniteSkillsUseCharacterRatesAndNativeCharacterBrainEffects(int level)
    {
        using Fixture f = Prepare(level);
        long module = f.Fit(ShieldBooster);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new SavedModuleCondition { ModuleId = module, Damage = 20 });
            world.SaveChanges();
        }
        var quote = Quote(f, module);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var profile = Assert.Single(
            ThermalFixtures.Read<NaniteRepairRuleTests.Reference>("NaniteRepairReference.json").Profiles,
            p => p.TypeId == ShieldBooster
        );
        Assert.Equal(profile.ComputedBasePrice, NaniteRepairCatalog.Price(catalog, ShieldBooster).ComputedBasePrice);
        double expectedUnits = Math.Ceiling(20 * (profile.ComputedBasePrice / 40.0) * (.5 * (1 - .05 * level)) / 6500);
        Assert.Equal((long)expectedUnits, quote.PasteUnits);
        Assert.Equal((long)Math.Ceiling(20 / (10 * (1 + .2 * level)) * TimeSpan.TicksPerMinute), quote.DurationTicks);
        var brain = f.Db.Services.GetRequiredService<PilotBrain>().Read(f.PilotId, f.ShipId);
        foreach (var skill in Enum.GetValues<NaniteRepairSkill>())
        {
            Assert.DoesNotContain(brain.ShipEffects, e => e.Skills.Contains((int)skill));
            var character = brain.CharacterEffects.Where(e => e.Skills.Contains((int)skill)).ToArray();
            if (level == 0)
            {
                Assert.Empty(character);
                continue;
            }
            var effect = Assert.Single(character);
            Assert.Equal(f.PilotId, effect.TargetId);
            Assert.Equal(BrainModifier.Item, effect.Modifier);
            Assert.Equal(BrainOperation.PostPercent, effect.Operation);
            Assert.Equal(skill == NaniteRepairSkill.Operation ? -5 * level : 20 * level, effect.Value);
            Assert.Equal(
                skill == NaniteRepairSkill.Operation ? DogmaAttributeId.ModuleRepairCostMultiplier : DogmaAttributeId.ModuleRepairRate,
                effect.Attribute
            );
            Assert.Empty(effect.Extras);
            var wire = Assert.IsType<BlueNewObject>(effect.ToBlue());
            var state = Assert.IsType<Dictionary<string, object?>>(wire.State);
            Assert.Equal("M", state["modifierType"]);
            Assert.Equal(f.PilotId, state["toItemID"]);
        }
    }

    [Fact]
    public void EveryNativeOverloadRepresentativeAndAPassiveModuleUseCatalogMaterialPricing()
    {
        using Fixture f = Prepare();
        using CatalogDbContext catalog = new(f.CatalogPath);
        var reference = ThermalFixtures.Read<NaniteRepairRuleTests.Reference>("NaniteRepairReference.json");
        var prices = reference.Profiles.Where(p => p.TypeId > 0).ToDictionary(p => p.TypeId);
        var types = ThermalFixtures
            .Read<ThermalFixtures.Matrix>("ThermalModuleMatrix.json")
            .Profiles.GroupBy(p => p.EffectId)
            .Select(g => g.First().TypeId)
            .Append(DamageControl)
            .Distinct()
            .ToArray();
        Assert.Equal(20, types.Length);
        foreach (int type in types)
        {
            Assert.NotNull(FittingCatalog.Definition(catalog, type));
            var expected = prices[type];
            var actual = NaniteRepairCatalog.Price(catalog, type);
            Assert.Equal(expected.ComputedBasePrice, actual.ComputedBasePrice);
            Assert.Equal(reference.PasteBasePrice, actual.PasteBasePrice);
        }
    }

    [Fact]
    public void ZeroNativeValueQuoteDoesNotApplyTheFreeRepairDuringARead()
    {
        using Fixture f = Prepare();
        long module = f.Fit(ShieldBooster);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            var source = catalog.Set<SourceRecord>().Find("client/types", ShieldBooster.ToString())!;
            var data = JsonNode.Parse(source.Data)!;
            data["basePrice"] = 0;
            source.Data = data.ToJsonString();
            catalog.Update(source);
            var materials = catalog.Set<SourceRecord>().Find("client/typematerials", ShieldBooster.ToString())!;
            materials.Data = "{\"materials\":[]}";
            catalog.Update(materials);
            catalog.SaveChanges();
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new SavedModuleCondition { ModuleId = module, Damage = 20 });
            world.SaveChanges();
        }
        var quote = Quote(f, module);
        Assert.Equal(0, quote.PasteUnits);
        Assert.Equal(0, quote.DurationTicks);
        Assert.Equal(20, quote.Condition.Damage);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(20, saved.Set<SavedModuleCondition>().Find(module)!.Damage);
    }

    [Fact]
    public void QuoteUsesActualSavedDamageWithoutHealingOrSpendingCargo()
    {
        using Fixture f = Prepare(3);
        long module = f.Fit(ShieldBooster);
        long paste = f.Grant(NaniteRepairRules.PasteType, 50, cargo: true);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new SavedModuleCondition { ModuleId = module, Damage = 12.5 });
            world.SaveChanges();
        }
        Assert.Equal(12.5, Quote(f, module).Condition.Damage);
        Assert.Equal(Quote(f, module), Quote(f, module));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(12.5, saved.Set<SavedModuleCondition>().Find(module)!.Damage);
        Assert.Equal(50, saved.Set<OwnedItem>().Find(paste)!.Quantity);
        Assert.Empty(saved.Set<ModuleCooldown>());
        Assert.True(saved.Set<OwnedItem>().Find(module)!.Online);
    }

    [Fact]
    public void ForeignLooseRigAndBurnedOutModulesCannotReceivePasteQuotes()
    {
        using Fixture f = Prepare();
        long module = f.Fit(ShieldBooster);
        long loose = f.Grant(ShieldBooster);
        Assert.Throws<InvalidDataException>(() => Quote(f, loose));
        using (WorldDbContext world = new(f.Db.Path))
        {
            using CatalogDbContext catalog = new(f.CatalogPath);
            Assert.Throws<InvalidDataException>(() => NaniteRepairPersistence.Quote(world, catalog, f.PilotId + 1, module));
            world.Set<OwnedItem>().Find(module)!.FlagId = (int)InventoryFlag.RigSlot0;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => Quote(f, module));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(module)!.FlagId = (int)InventoryFlag.MediumSlot0;
            world.Add(new SavedModuleCondition { ModuleId = module, Damage = 40 });
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => Quote(f, module));
        using WorldDbContext unchanged = new(f.Db.Path);
        Assert.Equal(40, unchanged.Set<SavedModuleCondition>().Find(module)!.Damage);
    }

    [Fact]
    public void TrainingChangesRefreshTheWarmNaniteBonusesOnce()
    {
        using Fixture f = Prepare(1);
        f.Fit(ShieldBooster);
        using ResidentFitCache cache = new(new(f.Db.Path));
        var before = f.Fitting.SkillBonuses(f.PilotId);
        Assert.Equal(20, Assert.Single(before, b => b.SkillId == (int)NaniteRepairSkill.Interfacing).Value);
        f.Train((int)NaniteRepairSkill.Interfacing, 5);
        using var metrics = SessionPerformance.Begin("trained nanite skill", _ => { }, force: true)!;
        var after = f.Fitting.SkillBonuses(f.PilotId);
        Assert.Equal(100, Assert.Single(after, b => b.SkillId == (int)NaniteRepairSkill.Interfacing).Value);
        Assert.All(
            after.Where(b => Enum.IsDefined((NaniteRepairSkill)b.SkillId)),
            b => Assert.Equal(ModifierDomain.Character, b.Modifier.Domain)
        );
        Assert.Equal(1, metrics.FittingCalculations);
        Assert.Equal(after, f.Fitting.SkillBonuses(f.PilotId));
        Assert.Equal(1, metrics.FittingCalculations);
    }

    [Theory]
    [InlineData(3521, "domain")]
    [InlineData(3521, "phase")]
    [InlineData(3522, "operation")]
    [InlineData(3522, "source")]
    [InlineData(3723, "target")]
    [InlineData(3723, "function")]
    [InlineData(3524, "operation")]
    public void ChangedNaniteProgramsCannotEnterTheFittingOrBrainGraph(int effect, string change)
    {
        using Fixture f = Prepare(3);
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        var row = catalog.Set<SourceRecord>().Find("client/dogmaeffects", effect.ToString())!;
        var data = JsonNode.Parse(row.Data)!;
        var modifier = data["modifierInfo"]![0]!;
        switch (change)
        {
            case "domain":
                modifier["domain"] = "shipID";
                break;
            case "phase":
                data["effectCategory"] = 1;
                break;
            case "operation":
                modifier["operation"] = (int)DogmaOperation.Add;
                break;
            case "source":
                modifier["modifyingAttributeID"] = (int)DogmaAttributeId.Damage;
                break;
            case "target":
                modifier["modifiedAttributeID"] = (int)DogmaAttributeId.StructureHitPoints;
                break;
            case "function":
                modifier["func"] = "LocationModifier";
                break;
        }
        row.Data = data.ToJsonString();
        catalog.Update(row);
        catalog.SaveChanges();
        int skill = effect is 3521 or 3522 ? (int)NaniteRepairSkill.Operation : (int)NaniteRepairSkill.Interfacing;
        Assert.Throws<InvalidDataException>(() => NaniteRepairCatalog.SkillModifiers(catalog, skill));
        Assert.Throws<InvalidDataException>(() => f.Fitting.SkillBonuses(f.PilotId));
    }
}
