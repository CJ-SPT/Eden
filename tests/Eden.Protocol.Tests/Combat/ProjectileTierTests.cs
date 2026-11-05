using System.Text.Json.Nodes;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class ProjectileTierTests
{
    private const int Autocannon = 484;
    private const int AutocannonTwo = 2873;
    private const int LeadSmall = 178;
    private const int HailSmall = 12608;
    private const double ArtificialBudget = 1_000_000;

    internal static Fixture WithTiers(params int[] types)
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadProjectileTiers(
            f.CatalogPath,
            types.Concat(Enum.GetValues<CombatSkill>().Select(s => (int)s)).ToHashSet()
        );
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        foreach (var attribute in new[] { DogmaAttributeId.CpuOutput, DogmaAttributeId.PowerOutput, DogmaAttributeId.CapacitorCapacity })
        {
            catalog
                .Set<TypeAttribute>()
                .Where(a => a.ItemTypeId == 601 && a.AttributeId == (int)attribute)
                .ExecuteUpdate(set => set.SetProperty(a => a.Value, ArtificialBudget));
        }
        NpcEncounterTests.Prepare(f.Db);
        catalog
            .Set<TypeAttribute>()
            .Where(a => a.ItemTypeId == 30380 && a.AttributeId == (int)DogmaAttributeId.ShieldCapacity)
            .ExecuteUpdate(set => set.SetProperty(a => a.Value, ArtificialBudget));
        return f;
    }

    private static IPilotAmmunition Ammo(Fixture f) => f.Db.Services.GetRequiredService<IPilotAmmunition>();

    private static IPilotCombat Combat(Fixture f) => f.Db.Services.GetRequiredService<IPilotCombat>();

    private static Magazine Load(Fixture f, long gun, int charge, int quantity = 3)
    {
        long stack = f.Grant(charge, quantity);
        Ammo(f).Load(f.PilotId, f.ShipId, [gun], [stack], f.StationId, f.Clock.Now);
        return Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId));
    }

    private static NpcContact Space(Fixture f)
    {
        var npc = f.Db.Services.GetRequiredService<INpcEncounters>().List(30000144, f.Clock.Now)[0];
        f.Db.Store.SetPosition(f.ShipId, npc.Position + new SpacePosition(100, 0, 0), npc.SystemId);
        return npc;
    }

    [Fact]
    public void All160UnrestrictedGunsAnd144ChargesHaveCompleteProfilesAndMatchingAmmunition()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadProjectileTiers(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var guns = catalog.Set<ItemType>().Where(t => t.GroupId == CombatDefinitions.ProjectileWeaponGroup).ToArray();
        var supported = guns.Where(t => WeaponCatalog.Supported(catalog, t.Id, t.GroupId)).ToArray();
        var charges = catalog
            .Set<ItemType>()
            .Where(t =>
                t.GroupId == AmmunitionDefinitions.ProjectileChargeGroup
                || t.GroupId == AmmunitionDefinitions.AdvancedAutocannonChargeGroup
                || t.GroupId == AmmunitionDefinitions.AdvancedArtilleryChargeGroup
            )
            .ToArray();
        Assert.Equal(167, guns.Length);
        Assert.Equal(160, supported.Length);
        Assert.Equal(144, charges.Length);
        Assert.All(charges, t => Assert.True(TurretChargeCatalog.Supported(catalog, t.Id, t.GroupId)));
        var payloads = charges.ToDictionary(t => t.Id, t => PilotFittingStore.ReadAttributes(catalog, t.Id));
        Assert.All(
            supported,
            t =>
            {
                var definition = FittingCatalog.Definition(catalog, t.Id)!;
                Assert.Equal(ModuleRack.High, definition.Rack);
                Assert.True(definition.Turret);
                Assert.Equal(DogmaEffect.ProjectileFired, FittingCatalog.DefaultEffect(catalog, t.Id));
                var attributes = PilotFittingStore.ReadAttributes(catalog, t.Id);
                Assert.Equal(0, attributes[DogmaAttributeId.CapacitorNeed]);
                Assert.Contains(
                    charges,
                    charge => AmmunitionDefinitions.CompatibleTurretCharge(attributes, charge.GroupId, payloads[charge.Id])
                );
            }
        );
        Assert.All(guns.Except(supported), t => Assert.Null(FittingCatalog.Definition(catalog, t.Id)));
        var tiers = supported
            .Select(t => catalog.Set<SourceRecord>().Find("client/types", PassiveCatalog.Key(t.Id))!.Read<ClientTypes>().MetaGroupID)
            .ToHashSet();
        Assert.All(new long[] { 1, 2, 3, 4, 5 }, tier => Assert.Contains(tier, tiers));
    }

    [Theory]
    [InlineData(Autocannon, LeadSmall, 2.0625, 3000)]
    [InlineData(AutocannonTwo, HailSmall, 2.475, 3000)]
    [InlineData(13773, 12625, 2.57895, 3000)]
    [InlineData(21545, 185, 3.17625, 3750)]
    [InlineData(85010, 12625, 4.0432, 3750)]
    [InlineData(489, 193, 2.0625, 4365)]
    [InlineData(2897, 12773, 2.772, 4583)]
    [InlineData(85011, 12773, 4.0432, 5456)]
    [InlineData(2865, 12761, 6.098, 21038)]
    [InlineData(14457, 12775, 3.70524, 7875)]
    [InlineData(20452, 17664, 4.125, 8000)]
    [InlineData(37306, 41314, 4.5375, 8000)]
    [InlineData(37307, 41318, 11.34375, 30000)]
    [InlineData(34284, HailSmall, 3.465, 3000)]
    public void EverySizeAndTierFiresItsOwnDamageCycleAndChargeEffectsWithoutSpendingCapacitor(
        int type,
        int charge,
        double damage,
        double cycle
    )
    {
        using Fixture f = WithTiers(type, charge);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        foreach (var skill in Enum.GetValues<CombatSkill>())
        {
            f.Train((int)skill, 5);
        }
        long gun = f.Fit(type);
        var magazine = Load(f, gun, charge);
        Assert.Equal(f.Clock.Now + 10000 * TimeSpan.TicksPerMillisecond, magazine.ReadyAt);
        f.Clock.Milliseconds += 10000;
        var npc = Space(f);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var basis = PilotFittingStore.ReadAttributes(catalog, type);
        var ammo = PilotFittingStore.ReadAttributes(catalog, charge);
        double multiplier = damage * 1.25 * 1.15 * (basis[DogmaAttributeId.TechLevel] == AmmunitionDefinitions.TechTwo ? 1.1 : 1);
        var result = Combat(f).Fire(f.PilotId, gun, npc.Id, SpacePosition.Zero, f.Clock.Now, .5, WeaponActivation.AmmoFed);
        Assert.Equal(cycle * .9 * .8, result.Weapon.CycleMilliseconds, 8);
        Assert.Equal(0, result.Weapon.CapacitorNeed);
        Assert.Equal(ArtificialBudget, result.Capacitor);
        Assert.Equal(ammo[DogmaAttributeId.EmDamage] * multiplier, result.Weapon.Damage.Em, 8);
        Assert.Equal(ammo[DogmaAttributeId.ExplosiveDamage] * multiplier, result.Weapon.Damage.Explosive, 8);
        Assert.Equal(ammo[DogmaAttributeId.KineticDamage] * multiplier, result.Weapon.Damage.Kinetic, 8);
        Assert.Equal(ammo[DogmaAttributeId.ThermalDamage] * multiplier, result.Weapon.Damage.Thermal, 8);
        Assert.Equal(basis[DogmaAttributeId.MaxRange] * 1.25 * ammo[DogmaAttributeId.WeaponRangeMultiplier], result.Weapon.Optimal, 8);
        Assert.Equal(
            basis[DogmaAttributeId.TrackingSpeed] * 1.25 * ammo.GetValueOrDefault(DogmaAttributeId.ChargeTrackingMultiplier, 1),
            result.Weapon.Tracking,
            8
        );
        Assert.Equal(
            basis[DogmaAttributeId.AccuracyFalloff] * 1.25 * ammo.GetValueOrDefault(DogmaAttributeId.ChargeFalloffMultiplier, 1),
            result.Weapon.Falloff,
            8
        );
        var attributes = Combat(f).ModuleAttributes(f.PilotId, f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == gun));
        Assert.InRange(attributes[DogmaAttributeId.BarrageDamageMultiplier], multiplier - 1e-10, multiplier + 1e-10);
        Assert.Equal(result.Weapon.Falloff, attributes[DogmaAttributeId.BarrageFalloff], 8);
        Assert.Equal(magazine.Quantity - 1, result.Magazine!.Quantity);
        Assert.True(result.Applied > 0);
        WorldInitialization.Open(f.Db.Path);
        Assert.Throws<InvalidDataException>(() => Combat(f).Fire(f.PilotId, gun, npc.Id, SpacePosition.Zero, f.Clock.Now, .5));
        Assert.Equal(result.Magazine.Quantity, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
    }

    [Fact]
    public void AnEmptyCapacitorStillAllowsAProjectileMissAndConsumesExactlyOneRound()
    {
        using Fixture f = WithTiers(Autocannon, LeadSmall);
        long gun = f.Fit(Autocannon);
        Load(f, gun, LeadSmall);
        f.Clock.Milliseconds += 10000;
        var npc = Space(f);
        var attributes = Combat(f).ModuleAttributes(f.PilotId, f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == gun));
        f.Db.Store.SetPosition(
            f.ShipId,
            npc.Position
                + new SpacePosition(attributes[DogmaAttributeId.MaxRange] + 3 * attributes[DogmaAttributeId.AccuracyFalloff] + 100, 0, 0),
            npc.SystemId
        );
        using (WorldDbContext world = new(f.Db.Path))
        {
            var energy = world.Set<ShipEnergy>().Find(f.ShipId) ?? world.Set<ShipEnergy>().Add(new() { ShipId = f.ShipId }).Entity;
            energy.Charge = 0;
            energy.UpdatedAt = f.Clock.Now;
            world.SaveChanges();
        }
        var result = Combat(f).Fire(f.PilotId, gun, npc.Id, SpacePosition.Zero, f.Clock.Now, .5);
        Assert.Equal(0, result.Applied);
        Assert.Equal(0, result.Capacitor);
        Assert.Equal(2, result.Magazine!.Quantity);
        Assert.Equal(ArtificialBudget, result.Health.Remaining.Shield);
    }

    [Theory]
    [InlineData(Autocannon, 215)]
    [InlineData(561, LeadSmall)]
    [InlineData(489, LeadSmall)]
    [InlineData(Autocannon, HailSmall)]
    [InlineData(AutocannonTwo, 12631)]
    [InlineData(2865, 12775)]
    public void WrongFamilySizeOrAdvancedGroupCannotMoveAmmoOrStartReload(int type, int charge)
    {
        using Fixture f = WithTiers(type, charge);
        long gun = f.Fit(type);
        foreach (var skill in Enum.GetValues<CombatSkill>())
        {
            f.Train((int)skill, 5);
        }
        long stack = f.Grant(charge, 3);
        Assert.Throws<InvalidDataException>(() => Ammo(f).Load(f.PilotId, f.ShipId, [gun], [stack], f.StationId, f.Clock.Now));
        Assert.Empty(Ammo(f).Read(f.PilotId, f.ShipId));
        Assert.Equal(3, f.Inventory.List(f.PilotId, f.StationId).Single(i => i.Id == stack).Quantity);
    }

    [Theory]
    [InlineData(Autocannon, LeadSmall, 200)]
    [InlineData(489, 186, 200)]
    [InlineData(2865, 194, 40)]
    [InlineData(37306, 17664, 80)]
    public void MagazineVolumesAndTenSecondReloadsConserveAllSizesThroughRestartAndUnload(int type, int charge, int capacity)
    {
        using Fixture f = WithTiers(type, charge);
        long gun = f.Fit(type);
        Assert.Equal(capacity, Load(f, gun, charge, 1000).Quantity);
        WorldInitialization.Open(f.Db.Path);
        Assert.Throws<InvalidDataException>(() =>
            Ammo(f).Unload(f.PilotId, f.ShipId, [gun], f.StationId, (int)InventoryFlag.Hangar, null, f.Clock.Now)
        );
        f.Clock.Milliseconds += 10000;
        Ammo(f).Unload(f.PilotId, f.ShipId, [gun], f.StationId, (int)InventoryFlag.Hangar, 17, f.Clock.Now);
        Assert.Equal(capacity - 17, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
        Ammo(f).Unload(f.PilotId, f.ShipId, [gun], f.StationId, (int)InventoryFlag.Hangar, null, f.Clock.Now);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(1000, saved.Set<OwnedItem>().Where(i => i.OwnerId == f.PilotId && i.TypeId == charge).Sum(i => i.Quantity));
    }

    [Fact]
    public void MatchingSpecializationGatesAdvancedAmmoOnFactionGunsAndIsCheckedAgainOnFire()
    {
        using Fixture f = WithTiers(13773, HailSmall);
        long gun = f.Fit(13773);
        long stack = f.Grant(HailSmall, 3);
        Assert.Throws<InvalidDataException>(() => Ammo(f).Load(f.PilotId, f.ShipId, [gun], [stack], f.StationId, f.Clock.Now));
        f.Train((int)CombatSkill.SmallAutocannonSpecialization, 1);
        Ammo(f).Load(f.PilotId, f.ShipId, [gun], [stack], f.StationId, f.Clock.Now);
        var brain = f.Db.Services.GetRequiredService<PilotBrain>().Read(f.PilotId, f.ShipId);
        Assert.Contains(
            brain.ShipEffects,
            e =>
                e.Skills.Contains((int)CombatSkill.SmallAutocannonSpecialization)
                && e.Extras.Contains((int)CombatSkill.SmallAutocannonSpecialization)
                && e.Value == 2
        );
        f.Clock.Milliseconds += 10000;
        var npc = Space(f);
        f.Train((int)CombatSkill.SmallAutocannonSpecialization, 0);
        Assert.Throws<InvalidDataException>(() => Combat(f).Fire(f.PilotId, gun, npc.Id, SpacePosition.Zero, f.Clock.Now, .5));
        Assert.Equal(3, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
    }

    [Fact]
    public void TechTwoAutocannonRequiresGunneryTwoProjectileFiveAndItsSpecialization()
    {
        using Fixture f = WithTiers(AutocannonTwo);
        long gun = f.Grant(AutocannonTwo);
        f.Train((int)CombatSkill.Gunnery, 2);
        f.Train((int)CombatSkill.SmallProjectileTurret, 4);
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.PilotId, [gun], f.StationId, (int)InventoryFlag.AutoFit));
        f.Train((int)CombatSkill.SmallProjectileTurret, 5);
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.PilotId, [gun], f.StationId, (int)InventoryFlag.AutoFit));
        f.Train((int)CombatSkill.SmallAutocannonSpecialization, 1);
        f.Fitting.Fit(f.PilotId, [gun], f.StationId, (int)InventoryFlag.AutoFit);
        f.Fitting.SetOnline(f.PilotId, f.ShipId, gun, true);
        Assert.True(f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == gun).Online);
    }

    [Fact]
    public void NativeModulePrimeIncludesProjectileSkillsLoadedAmmoAndThePaidReloadDeadline()
    {
        using Fixture f = WithTiers(AutocannonTwo, HailSmall);
        long gun = f.Fit(AutocannonTwo);
        f.Train((int)CombatSkill.RapidFiring, 4);
        f.Train((int)CombatSkill.Sharpshooter, 2);
        f.Train((int)CombatSkill.MotionPrediction, 3);
        f.Train((int)CombatSkill.TrajectoryAnalysis, 1);
        f.Train((int)CombatSkill.SurgicalStrike, 2);
        var loaded = Load(f, gun, HailSmall);
        var module = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == gun);
        using var scope = f.Db.Services.CreateScope();
        var prime = scope.ServiceProvider.GetRequiredService<ShipDogmaBuilder>().PrimeModule(f.Db.Store.FindPilot(1000001)!, module);
        Assert.Equal(3.6073125, prime.Attributes[DogmaAttributeId.DamageMultiplier], 8);
        Assert.Equal(2268, prime.Attributes[DogmaAttributeId.WeaponCycleTime], 8);
        Assert.Equal(528, prime.Attributes[DogmaAttributeId.MaxRange], 8);
        Assert.Equal(359.6625, prime.Attributes[DogmaAttributeId.TrackingSpeed], 8);
        Assert.Equal(3386.25, prime.Attributes[DogmaAttributeId.AccuracyFalloff], 8);
        Assert.Equal(loaded.ReadyAt, prime.Attributes[DogmaAttributeId.NextActivationTime]);
    }

    [Theory]
    [InlineData(CombatSkill.SmallProjectileTurret, ChargeSize.Small, 2.53)]
    [InlineData(CombatSkill.MediumProjectileTurret, ChargeSize.Medium, 2.3)]
    public void CorvetteRoleAndLegacyBarrageDerivationsUseTheCorrectSkilledValues(CombatSkill required, ChargeSize size, double expected)
    {
        Dictionary<DogmaAttributeId, double> attributes = new()
        {
            [DogmaAttributeId.TechLevel] = AmmunitionDefinitions.TechOne,
            [DogmaAttributeId.ChargeSize] = (int)size,
            [DogmaAttributeId.RequiredSkill1] = (int)required,
            [DogmaAttributeId.RequiredSkill2] = (int)CombatSkill.Gunnery,
            [DogmaAttributeId.DamageMultiplier] = 2,
            [DogmaAttributeId.MaxRange] = 800,
            [DogmaAttributeId.AccuracyFalloff] = 4300,
            [DogmaAttributeId.BarrageDamageBonus] = 20,
            [DogmaAttributeId.BarrageFalloffBonus] = 30,
        };
        Dictionary<CombatSkill, int> levels = new() { [required] = 3, [CombatSkill.SmallHybridTurret] = 5 };
        var result = WeaponRules.ModuleAttributes(
            new(Autocannon, CombatDefinitions.ProjectileWeaponGroup, attributes, levels, 20, null, 0, 10)
        );
        Assert.Equal(expected, result[DogmaAttributeId.DamageMultiplier], 8);
        Assert.Equal(expected * 1.2, result[DogmaAttributeId.BarrageDamageMultiplier], 8);
        Assert.Equal(4300 * 1.3, result[DogmaAttributeId.BarrageFalloff], 8);
        Assert.Equal(800, result[DogmaAttributeId.MaxRange]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedChargeEffectsCannotBeLoadedOrFiredFromAnExistingMagazine(bool missingEffect)
    {
        using Fixture f = WithTiers(Autocannon, LeadSmall);
        long gun = f.Fit(Autocannon);
        Load(f, gun, LeadSmall);
        f.Clock.Milliseconds += 10000;
        long extra = f.Grant(LeadSmall, 3);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            var record = catalog.Set<SourceRecord>().Find("client/typedogma", LeadSmall.ToString())!;
            var data = JsonNode.Parse(record.Data)!;
            var effects = data["dogmaEffects"]!.AsArray();
            if (missingEffect)
            {
                effects.Remove(effects.Single(e => e!["effectID"]!.GetValue<int>() == (int)TurretChargeEffect.Tracking));
            }
            else
            {
                effects.Add(new JsonObject { ["effectID"] = 4 });
            }
            string json = data.ToJsonString();
            catalog
                .Set<SourceRecord>()
                .Where(r => r.Dataset == "client/typedogma" && r.Key == LeadSmall.ToString())
                .ExecuteUpdate(set => set.SetProperty(r => r.Data, json));
        }
        Assert.Throws<InvalidDataException>(() => Ammo(f).Load(f.PilotId, f.ShipId, [gun], [extra], f.StationId, f.Clock.Now));
        Assert.Equal(3, f.Inventory.List(f.PilotId, f.StationId).Single(i => i.Id == extra).Quantity);
        var npc = Space(f);
        Assert.Throws<InvalidDataException>(() => Combat(f).Fire(f.PilotId, gun, npc.Id, SpacePosition.Zero, f.Clock.Now, .5));
        Assert.Equal(3, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
    }

    [Theory]
    [InlineData(34284, false)]
    [InlineData(34286, true)]
    [InlineData(34288, true)]
    public void PolarizedAutocannonsOverrideDamageControlResistancesWhileFitted(int type, bool online)
    {
        using Fixture f = WithTiers(type);
        f.Fit(2046);
        var before = f.Fitting.ShipAttributes(f.PilotId);
        long gun = f.Fit(type, online);
        var fitted = f.Fitting.ShipAttributes(f.PilotId);
        var resistances = before
            .Keys.Where(a =>
                a
                    is >= DogmaAttributeId.ArmorEmResonance
                        and <= DogmaAttributeId.ShieldThermalResonance
                        or DogmaAttributeId.StructureEmResonance
                        or DogmaAttributeId.StructureExplosiveResonance
                        or DogmaAttributeId.StructureKineticResonance
                        or DogmaAttributeId.StructureThermalResonance
            )
            .ToArray();
        Assert.Equal(12, resistances.Length);
        Assert.All(resistances, a => Assert.Equal(1, fitted[a]));
        f.Inventory.Move(f.PilotId, f.StationId, new([gun], f.ShipId, (int)InventoryFlag.Hangar, null));
        var removed = f.Fitting.ShipAttributes(f.PilotId);
        Assert.All(resistances, a => Assert.Equal(before[a], removed[a]));
    }

    [Fact]
    public void GyrostabilizersHaveDiminishingReturnsAndOtherWeaponUpgradeFamiliesDoNotApply()
    {
        using Fixture f = WithTiers(Autocannon);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog
                .Set<TypeAttribute>()
                .Where(a => a.ItemTypeId == 601 && a.AttributeId == (int)DogmaAttributeId.LowSlots)
                .ExecuteUpdate(set => set.SetProperty(a => a.Value, 8));
        }
        long gun = f.Fit(Autocannon);
        var module = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == gun);
        var basis = Combat(f).ModuleAttributes(f.PilotId, module);
        f.Fit(2363);
        f.Fit(9944);
        var wrong = Combat(f).ModuleAttributes(f.PilotId, module);
        Assert.Equal(basis[DogmaAttributeId.DamageMultiplier], wrong[DogmaAttributeId.DamageMultiplier]);
        Assert.Equal(basis[DogmaAttributeId.WeaponCycleTime], wrong[DogmaAttributeId.WeaponCycleTime]);
        for (int count = 0; count < 3; count++)
        {
            f.Fit(520);
        }
        var boosted = Combat(f).ModuleAttributes(f.PilotId, module);
        const double SecondBonusFactor = .8691199808003975;
        const double ThirdBonusFactor = .5705831435105602;
        Assert.Equal(
            basis[DogmaAttributeId.DamageMultiplier] * 1.07 * (1 + .07 * SecondBonusFactor) * (1 + .07 * ThirdBonusFactor),
            boosted[DogmaAttributeId.DamageMultiplier],
            8
        );
        Assert.Equal(
            basis[DogmaAttributeId.WeaponCycleTime] * .92 * (1 - .08 * SecondBonusFactor) * (1 - .08 * ThirdBonusFactor),
            boosted[DogmaAttributeId.WeaponCycleTime],
            8
        );
    }

    [Theory]
    [InlineData("mutation")]
    [InlineData("restriction")]
    [InlineData("effect")]
    [InlineData("default")]
    [InlineData("derivation")]
    public void UnsupportedStoredProfilesCannotConsumeAmmoOrDamageTheTarget(string reason)
    {
        using Fixture f = WithTiers(Autocannon, LeadSmall);
        long gun = f.Fit(Autocannon);
        Load(f, gun, LeadSmall);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            if (reason == "mutation")
            {
                catalog.Database.ExecuteSqlRaw(
                    "UPDATE SourceRecord SET Data=json_set(Data,'$.metaGroupID',15) WHERE Dataset='client/types' AND Key='484'"
                );
            }
            else if (reason == "restriction")
            {
                catalog.Database.ExecuteSqlRaw("INSERT INTO TypeAttribute VALUES (484,1298,1)");
            }
            else
            {
                var source = catalog.Set<SourceRecord>().Find("client/typedogma", "484")!;
                var data = JsonNode.Parse(source.Data)!;
                var effects = data["dogmaEffects"]!.AsArray();
                if (reason == "effect")
                {
                    effects.Add(new JsonObject { ["effectID"] = 4 });
                }
                else if (reason == "default")
                {
                    foreach (var effect in effects)
                    {
                        effect!["isDefault"] = effect["effectID"]!.GetValue<int>() == 263 ? 1 : 0;
                    }
                }
                else
                {
                    effects.Remove(effects.Single(e => e!["effectID"]!.GetValue<int>() == 253));
                }
                string json = data.ToJsonString();
                catalog
                    .Set<SourceRecord>()
                    .Where(r => r.Dataset == "client/typedogma" && r.Key == "484")
                    .ExecuteUpdate(set => set.SetProperty(r => r.Data, json));
            }
            Assert.Null(FittingCatalog.Definition(catalog, Autocannon));
        }
        f.Clock.Milliseconds += 10000;
        var npc = Space(f);
        Assert.Throws<InvalidDataException>(() => Combat(f).Fire(f.PilotId, gun, npc.Id, SpacePosition.Zero, f.Clock.Now, .5));
        Assert.Equal(3, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(ArtificialBudget, saved.Set<WorldNpc>().Find(npc.Id)!.ShieldCharge);
    }
}
