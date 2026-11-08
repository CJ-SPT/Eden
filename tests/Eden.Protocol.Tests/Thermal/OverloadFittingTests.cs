using Eden.Data.Catalog;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Ammunition;
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

public sealed class OverloadFittingTests
{
    private const int ShieldBooster = 399;
    private const int TrackingComputer = 1977;
    private const int TrackingScript = 28999;
    private const int RangeScript = 29001;
    private const int EmHardener = 2293;

    private static Fixture Prepare(int thermodynamics = 3)
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadOverload(f.CatalogPath);
        f.Train(OverloadRules.ThermodynamicsSkill, thermodynamics);
        return f;
    }

    private static InventoryItem Item(Fixture f, long module) => f.Inventory.List(f.PilotId, f.ShipId).Single(m => m.Id == module);

    private static FittingCalculation Calculate(Fixture f, WorldDbContext world, CatalogDbContext catalog, params long[] overloads)
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == f.PilotId);
        return FittingCalculation.Read(world, catalog, pilot, overloadedModules: overloads.ToHashSet());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, .95)]
    [InlineData(3, .85)]
    [InlineData(5, .75)]
    public void EarnedThermodynamicsReducesEffectiveModuleDamageAndSendsTheNativeLocationBrainModifier(int level, double multiplier)
    {
        using Fixture f = Prepare(level);
        long module = f.Fit(ShieldBooster);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var profile = OverloadCatalog.Definition(catalog, ShieldBooster)!;
        var values = f.Fitting.ModuleAttributes(f.PilotId, Item(f, module));
        Assert.Equal(profile.HeatDamage * multiplier, values[DogmaAttributeId.HeatDamage], 8);
        Assert.Equal(profile.HeatAbsorption, values[DogmaAttributeId.HeatAbsorption]);
        var brain = f.Db.Services.GetRequiredService<PilotBrain>().Read(f.PilotId, f.ShipId);
        var modifiers = brain.ShipEffects.Where(e => e.Skills.Contains(OverloadRules.ThermodynamicsSkill)).ToArray();
        if (level == 0)
        {
            Assert.Empty(modifiers);
            return;
        }
        var effect = Assert.Single(modifiers);
        Assert.Equal(f.ShipId, effect.TargetId);
        Assert.Equal(BrainModifier.Location, effect.Modifier);
        Assert.Equal(BrainOperation.PostPercent, effect.Operation);
        Assert.Equal(DogmaAttributeId.HeatDamage, effect.Attribute);
        Assert.Equal(-5 * level, effect.Value);
        Assert.Empty(effect.Extras);
        var wire = Assert.IsType<BlueNewObject>(effect.ToBlue());
        Assert.Equal("L", Assert.IsType<Dictionary<string, object?>>(wire.State)["modifierType"]);
        Assert.Equal(BlueMarshal.Encode(effect), BlueMarshal.Encode(BlueMarshal.Decode(BlueMarshal.Encode(effect))));
    }

    public static IEnumerable<object[]> Programs() =>
        ThermalFixtures
            .Read<ThermalFixtures.Matrix>("ThermalModuleMatrix.json")
            .Profiles.GroupBy(p => p.EffectId)
            .Select(g => new object[] { g.First().TypeId });

    [Theory]
    [MemberData(nameof(Programs))]
    public void EveryNativeProgramComposesWithTheRealFittedGraphWithoutEnablingOrdinaryOverload(int type)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        using WorldDbContext world = new(f.Db.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(f.CatalogPath);
        var normal = Calculate(f, world, catalog);
        var profile = OverloadCatalog.Definition(catalog, type)!;
        var calculation = Calculate(f, world, catalog, module);
        Assert.True(calculation.Transient);
        var normalValues = normal.Graph.Attributes(module);
        foreach (var bonus in profile.Bonuses)
        {
            // Optional neutral missile guidance attributes are absent from an
            // inactive graph. Native dogma supplies their catalog default (1).
            double basis = normalValues.GetValueOrDefault(
                bonus.Attribute,
                catalog.Read(CatalogData.Client.Dogmaattributes, (long)bonus.Attribute)?.DefaultValue ?? 0
            );
            double expected = basis * (1 + normal.Graph.Read(module, bonus.SourceAttribute) / 100);
            Assert.Equal(expected, calculation.Graph.Read(module, bonus.Attribute), 7);
        }
        Assert.Equal(profile.HeatDamage * .85, calculation.Graph.Read(module, DogmaAttributeId.HeatDamage), 8);
        Assert.Equal(profile.HeatAbsorption, calculation.Graph.Read(module, DogmaAttributeId.HeatAbsorption));
    }

    [Fact]
    public void ExplicitCycleCalculationCannotReadOrPublishAnOrdinaryModuleOrShipSnapshot()
    {
        using Fixture f = Prepare();
        long module = f.Fit(ShieldBooster);
        long ship = f.ShipId;
        using ResidentFitCache cache = new(new(f.Db.Path));
        var item = Item(f, module);
        var before = f.Fitting.ModuleAttributes(f.PilotId, item);
        var shipBefore = f.Fitting.ShipAttributes(f.PilotId);
        using (WorldDbContext world = new(f.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            var snapshot = world.CachedFittingSnapshot(f.PilotId, ship, world.FitRevision(f.PilotId));
            var calculated = Calculate(f, world, catalog, module);
            var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == f.PilotId);
            Assert.True(calculated.Graph.Read(module, DogmaAttributeId.Duration) < before[DogmaAttributeId.Duration]);
            _ = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculated);
            Assert.Same(snapshot, world.CachedFittingSnapshot(f.PilotId, ship, world.FitRevision(f.PilotId)));
            transaction.Commit();
        }
        using var metrics = SessionPerformance.Begin("after transient overload", _ => { }, force: true)!;
        Assert.Equal(before.OrderBy(a => a.Key), f.Fitting.ModuleAttributes(f.PilotId, item).OrderBy(a => a.Key));
        Assert.Equal(shipBefore.OrderBy(a => a.Key), f.Fitting.ShipAttributes(f.PilotId).OrderBy(a => a.Key));
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void ActiveHardenerOverloadReachesTheShipAndAnUnselectedModuleRemainsOrdinary()
    {
        using Fixture f = Prepare();
        long hardener = f.Fit(EmHardener);
        long booster = f.Fit(ShieldBooster);
        var ordinaryBooster = f.Fitting.ModuleAttributes(f.PilotId, Item(f, booster));
        using ResidentFitCache cache = new(new(f.Db.Path));
        using WorldDbContext world = new(f.Db.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(f.CatalogPath);
        world.Add(new ActiveFittedModule { ModuleId = hardener, ShipId = f.ShipId });
        world.SaveChanges();
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == f.PilotId);
        var ordinary = FittingCalculation.Read(world, catalog, pilot);
        _ = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: ordinary);
        var overheated = Calculate(f, world, catalog, hardener);
        var shipValues = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: overheated);
        var profile = OverloadCatalog.Definition(catalog, EmHardener)!;
        var binding = Assert.Single(profile.Bonuses);
        double ordinaryBonus = ordinary.Graph.Read(hardener, binding.Attribute);
        double overheatedBonus = ordinaryBonus * (1 + ordinary.Graph.Read(hardener, binding.SourceAttribute) / 100);
        Assert.Equal(
            ordinary.Graph.Read(f.ShipId, DogmaAttributeId.ShieldEmResonance) * (1 + overheatedBonus / 100) / (1 + ordinaryBonus / 100),
            overheated.Graph.Read(f.ShipId, DogmaAttributeId.ShieldEmResonance),
            8
        );
        Assert.True(
            overheated.Graph.Read(f.ShipId, DogmaAttributeId.ShieldEmResonance)
                < ordinary.Graph.Read(f.ShipId, DogmaAttributeId.ShieldEmResonance)
        );
        Assert.Equal(overheated.Graph.Read(f.ShipId, DogmaAttributeId.ShieldEmResonance), shipValues[DogmaAttributeId.ShieldEmResonance]);
        Assert.Equal(ordinaryBooster[DogmaAttributeId.Duration], overheated.Graph.Read(booster, DogmaAttributeId.Duration));
    }

    [Theory]
    [InlineData(TrackingScript)]
    [InlineData(RangeScript)]
    public void LoadedScriptsAndOverloadComposeBeforeTheActiveTrackingBonusReachesItsWeapon(int script)
    {
        using Fixture f = Prepare();
        PassiveCatalogFixture.LoadSupportTiers(f.CatalogPath, new HashSet<int> { TrackingComputer, TrackingScript, RangeScript });
        long gun = f.Fit(450); // Gatling Pulse Laser I, within the Ibis's actual budgets.
        long computer = f.Fit(TrackingComputer);
        var ammunition = f.Db.Services.GetRequiredService<IPilotAmmunition>();
        long stack = f.Grant(script);
        ammunition.Load(f.PilotId, f.ShipId, [computer], [stack], f.StationId, f.Clock.Now);
        using WorldDbContext world = new(f.Db.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(f.CatalogPath);
        world.Add(new ActiveFittedModule { ModuleId = computer, ShipId = f.ShipId });
        world.SaveChanges();
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == f.PilotId);
        var ordinary = FittingCalculation.Read(world, catalog, pilot);
        var overheated = Calculate(f, world, catalog, computer);
        var profile = OverloadCatalog.Definition(catalog, TrackingComputer)!;
        foreach (var binding in profile.Bonuses)
        {
            Assert.Equal(
                ordinary.Graph.Read(computer, binding.Attribute) * (1 + ordinary.Graph.Read(computer, binding.SourceAttribute) / 100),
                overheated.Graph.Read(computer, binding.Attribute),
                8
            );
        }
        DogmaAttributeId modified = script == TrackingScript ? DogmaAttributeId.TrackingSpeed : DogmaAttributeId.MaxRange;
        DogmaAttributeId source = script == TrackingScript ? DogmaAttributeId.TrackingSpeedBonus : DogmaAttributeId.OptimalRangeBonus;
        double ratio = (1 + overheated.Graph.Read(computer, source) / 100) / (1 + ordinary.Graph.Read(computer, source) / 100);
        Assert.Equal(ordinary.Graph.Read(gun, modified) * ratio, overheated.Graph.Read(gun, modified), 8);
        Assert.Equal(script, Assert.Single(ammunition.Read(f.PilotId, f.ShipId)).TypeId);
    }

    [Fact]
    public void AnUncachedCycleCalculationCannotBecomeTheFirstOrdinarySnapshot()
    {
        using Fixture f = Prepare();
        long module = f.Fit(ShieldBooster);
        using ResidentFitCache cache = new(new(f.Db.Path));
        using (WorldDbContext world = new(f.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == f.PilotId);
            var calculation = Calculate(f, world, catalog, module);
            _ = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
            transaction.Commit();
        }
        var item = Item(f, module);
        using var metrics = SessionPerformance.Begin("cold ordinary after cycle", _ => { }, force: true)!;
        double ordinaryDuration = f.Fitting.ModuleAttributes(f.PilotId, item)[DogmaAttributeId.Duration];
        Assert.Equal(1, metrics.FittingCalculations);
        using CatalogDbContext source = new(f.CatalogPath);
        Assert.Equal(source.FittingAttributes(ShieldBooster)[DogmaAttributeId.Duration], ordinaryDuration);
    }

    [Fact]
    public void ThermodynamicsChangesFittedItemsButCannotModifyLooseCargoOrHangarEquipment()
    {
        using Fixture f = Prepare();
        long fitted = f.Fit(ShieldBooster);
        long loose = f.Grant(ShieldBooster);
        long cargo = f.Grant(ShieldBooster, cargo: true);
        double basis = f.Fitting.Attributes(ShieldBooster)[DogmaAttributeId.HeatDamage];
        var hangarItem = f.Inventory.List(f.PilotId, f.StationId).Single(m => m.Id == loose);
        Assert.Equal(basis, f.Fitting.ModuleAttributes(f.PilotId, hangarItem)[DogmaAttributeId.HeatDamage]);
        Assert.Equal(basis, f.Fitting.ModuleAttributes(f.PilotId, Item(f, cargo))[DogmaAttributeId.HeatDamage]);
        Assert.Equal(basis * .85, f.Fitting.ModuleAttributes(f.PilotId, Item(f, fitted))[DogmaAttributeId.HeatDamage], 8);
    }

    [Fact]
    public void TrainingThermodynamicsInvalidatesTheWarmGraphAndBrainOnTheNextRead()
    {
        using Fixture f = Prepare(1);
        long module = f.Fit(ShieldBooster);
        using ResidentFitCache cache = new(new(f.Db.Path));
        var item = Item(f, module);
        double basis = f.Fitting.Attributes(ShieldBooster)[DogmaAttributeId.HeatDamage];
        Assert.Equal(basis * .95, f.Fitting.ModuleAttributes(f.PilotId, item)[DogmaAttributeId.HeatDamage], 8);
        f.Train(OverloadRules.ThermodynamicsSkill, 5);
        using var metrics = SessionPerformance.Begin("trained Thermodynamics", _ => { }, force: true)!;
        Assert.Equal(basis * .75, f.Fitting.ModuleAttributes(f.PilotId, item)[DogmaAttributeId.HeatDamage], 8);
        Assert.Equal(1, metrics.FittingCalculations);
        Assert.Equal(-25, Assert.Single(f.Fitting.SkillBonuses(f.PilotId), b => b.SkillId == OverloadRules.ThermodynamicsSkill).Value);
        Assert.Equal(1, metrics.FittingCalculations);
    }

    [Fact]
    public void RavenHullSkillsAndLauncherOverloadUseTheSameRealBudgetGraph()
    {
        const int Raven = 638;
        const int CaldariBattleship = 3338;
        const int TorpedoLauncher = 503;
        using Fixture f = Prepare();
        PassiveCatalogFixture.LoadMissileTiers(f.CatalogPath, new HashSet<int> { TorpedoLauncher });
        long ship = f.ShipId;
        using (WorldDbContext world = new(f.Db.Path))
        {
            // An existing owned admitted hull, with its unmodified catalog budgets.
            world.Ships.Find(ship)!.TypeId = Raven;
            world.SaveChanges();
        }
        long module = f.Fit(TorpedoLauncher);
        var item = Item(f, module);
        double unskilled = f.Fitting.ModuleAttributes(f.PilotId, item)[DogmaAttributeId.WeaponCycleTime];
        f.Train(CaldariBattleship, 4);
        double skilled = f.Fitting.ModuleAttributes(f.PilotId, item)[DogmaAttributeId.WeaponCycleTime];
        Assert.Equal(unskilled * .8, skilled, 8);
        using WorldDbContext saved = new(f.Db.Path);
        using var transaction = saved.Database.BeginTransaction();
        using CatalogDbContext catalog = new(f.CatalogPath);
        var profile = OverloadCatalog.Definition(catalog, TorpedoLauncher)!;
        var binding = Assert.Single(profile.Bonuses);
        Assert.Equal(DogmaAttributeId.WeaponCycleTime, binding.Attribute);
        var calculation = Calculate(f, saved, catalog, module);
        Assert.Equal(
            skilled * (1 + calculation.Graph.Read(module, binding.SourceAttribute) / 100),
            calculation.Graph.Read(module, DogmaAttributeId.WeaponCycleTime),
            8
        );
        Assert.Equal(4, calculation.SkillLevels[CaldariBattleship]);
    }

    [Theory]
    [InlineData("untrained")]
    [InlineData("offline")]
    [InlineData("burned")]
    [InlineData("unsupported")]
    [InlineData("foreign")]
    public void UnavailableOverloadSelectionsCannotProduceBonusesOrChangeTheWorld(string reason)
    {
        using Fixture f = Prepare(reason == "untrained" ? 0 : 3);
        long module = f.Fit(reason == "unsupported" ? 2048 : ShieldBooster, online: reason != "offline");
        using WorldDbContext world = new(f.Db.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(f.CatalogPath);
        if (reason == "burned")
        {
            ModuleConditionPersistence.ApplyDamage(world, catalog, module, 40, f.Clock.Now);
            world.SaveChanges();
        }
        long selected = reason == "foreign" ? module + 100000 : module;
        Assert.Throws<InvalidDataException>(() => Calculate(f, world, catalog, selected));
        Assert.Equal(reason != "offline" && reason != "burned", world.Set<OwnedItem>().Find(module)!.Online);
        Assert.Empty(world.Set<ActiveFittedModule>());
    }
}
