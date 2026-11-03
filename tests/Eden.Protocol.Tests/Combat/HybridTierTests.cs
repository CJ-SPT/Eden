using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class HybridTierTests
{
    private const int SmallRailTwo = 10680;
    private const int Polarized = 34278;
    private const int SpikeSmall = 12618;
    private const int VoidSmall = 12612;
    private const int IronSmall = 215;
    private const double ArtificialBudget = 1_000_000;

    private static Fixture WithTiers(params int[] types)
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadWeaponTiers(f.CatalogPath, types.Concat(Enum.GetValues<CombatSkill>().Select(s => (int)s)).ToHashSet());
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        foreach (var attribute in new[] { DogmaAttributeId.CpuOutput, DogmaAttributeId.PowerOutput, DogmaAttributeId.CapacitorCapacity })
        {
            catalog
                .Set<TypeAttribute>()
                .Where(a => a.ItemTypeId == 601 && a.AttributeId == (int)attribute)
                .ExecuteUpdate(set => set.SetProperty(a => a.Value, ArtificialBudget));
        }
        catalog.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO DogmaAttribute VALUES (1826,'rookieSHTOptimalBonus')");
        catalog.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO TypeAttribute VALUES (601,1826,20)");
        NpcEncounterTests.Prepare(f.Db);
        catalog
            .Set<TypeAttribute>()
            .Where(a => a.ItemTypeId == 30380 && a.AttributeId == (int)DogmaAttributeId.ShieldCapacity)
            .ExecuteUpdate(set => set.SetProperty(a => a.Value, ArtificialBudget));
        return f;
    }

    private static IPilotAmmunition Ammunition(Fixture f) => f.Db.Services.GetRequiredService<IPilotAmmunition>();

    private static IPilotCombat Combat(Fixture f) => f.Db.Services.GetRequiredService<IPilotCombat>();

    private static Magazine Load(Fixture f, long gun, int type, int quantity = 3)
    {
        long stack = f.Grant(type, quantity);
        Ammunition(f).Load(f.PilotId, f.ShipId, [gun], [stack], f.StationId, f.Clock.Now);
        return Assert.Single(Ammunition(f).Read(f.PilotId, f.ShipId));
    }

    private static NpcContact Space(Fixture f)
    {
        var npc = f.Db.Services.GetRequiredService<INpcEncounters>().List(30000144, f.Clock.Now)[0];
        f.Db.Store.SetPosition(f.ShipId, npc.Position + new SpacePosition(1000, 0, 0), npc.SystemId);
        return npc;
    }

    [Fact]
    public void All216UnrestrictedWeaponsAnd224PublishedChargesHaveCompleteProfilesAndMatchingSizes()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadWeaponTiers(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var guns = catalog.Set<ItemType>().Where(t => t.GroupId == CombatDefinitions.HybridWeaponGroup).ToArray();
        var supported = guns.Where(t => HybridWeaponCatalog.Supported(catalog, t.Id, t.GroupId)).ToArray();
        var charges = catalog
            .Set<ItemType>()
            .Where(t =>
                t.GroupId == AmmunitionDefinitions.HybridChargeGroup
                || t.GroupId == AmmunitionDefinitions.AdvancedRailgunChargeGroup
                || t.GroupId == AmmunitionDefinitions.AdvancedBlasterChargeGroup
            )
            .ToArray();
        Assert.Equal(223, guns.Length);
        Assert.Equal(216, supported.Length);
        Assert.Equal(224, charges.Length);
        Assert.All(charges, t => Assert.True(HybridChargeCatalog.Supported(catalog, t.Id, t.GroupId)));
        var chargeAttributes = charges.ToDictionary(t => t.Id, t => PilotFittingStore.ReadAttributes(catalog, t.Id));
        Assert.All(
            supported,
            t =>
            {
                Assert.Equal(ModuleRack.High, FittingCatalog.Definition(catalog, t.Id)!.Rack);
                Assert.True(FittingCatalog.Definition(catalog, t.Id)!.Turret);
                var attributes = PilotFittingStore.ReadAttributes(catalog, t.Id);
                Assert.Contains(charges, c => AmmunitionDefinitions.CompatibleHybridCharge(attributes, c.GroupId, chargeAttributes[c.Id]));
            }
        );
        Assert.All(guns.Except(supported), t => Assert.Null(FittingCatalog.Definition(catalog, t.Id)));
        Assert.All(new[] { 34278, 34280, 34282 }, type => Assert.Contains(supported, t => t.Id == type));
        var tiers = supported
            .Select(t => catalog.Set<SourceRecord>().Find("client/types", PassiveCatalog.Key(t.Id))!.Read<ClientTypes>().MetaGroupID)
            .ToHashSet();
        Assert.All(new long[] { 1, 2, 3, 4, 5 }, tier => Assert.Contains(tier, tiers));
    }

    [Theory]
    [InlineData(561, IronSmall, 1.65, 2600, 1.169)]
    [InlineData(SmallRailTwo, SpikeSmall, 2.64, 3250, 1.505)]
    [InlineData(20587, SpikeSmall, 3.3, 4250, 2.1)]
    [InlineData(85008, VoidSmall, 5.145, 3500, 1.4161)]
    [InlineData(3082, 12801, 4.17, 5825, 7)]
    [InlineData(13868, 12801, 3.16, 4455, 3.612)]
    [InlineData(3090, 12803, 3.63, 8129, 21)]
    [InlineData(14375, 12787, 2.5265625, 4500, 4.851)]
    [InlineData(20448, 17648, 7.4250594, 20000, 300)]
    [InlineData(37302, 41322, 6.125625, 10000, 100)]
    [InlineData(37303, 41326, 8.16756534, 20000, 300)]
    [InlineData(Polarized, VoidSmall, 4.41, 2800, 1.062075)]
    public void EverySizeAndTierUsesItsOwnCycleDamageCostAndMatchingSkillsAndAmmo(
        int type,
        int charge,
        double damage,
        double cycle,
        double cost
    )
    {
        using Fixture f = WithTiers(type, charge);
        foreach (var skill in Enum.GetValues<CombatSkill>())
        {
            f.Train((int)skill, 5);
        }
        long gun = f.Fit(type);
        var magazine = Load(f, gun, charge);
        f.Clock.Milliseconds += 5000;
        var npc = Space(f);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var basis = PilotFittingStore.ReadAttributes(catalog, type);
        var ammo = PilotFittingStore.ReadAttributes(catalog, charge);
        bool specialized = InventoryDefinitions.RequiredSkills.Any(pair =>
            basis.GetValueOrDefault(pair.Skill)
                is (int)CombatSkill.SmallRailgunSpecialization
                    or (int)CombatSkill.MediumRailgunSpecialization
                    or (int)CombatSkill.LargeRailgunSpecialization
                    or (int)CombatSkill.SmallBlasterSpecialization
                    or (int)CombatSkill.MediumBlasterSpecialization
                    or (int)CombatSkill.LargeBlasterSpecialization
                    or (int)CombatSkill.CapitalRailgunSpecialization
                    or (int)CombatSkill.CapitalBlasterSpecialization
        );
        double multiplier = damage * 1.25 * 1.15 * (specialized ? 1.1 : 1);
        var result = Combat(f).Fire(f.PilotId, gun, npc.Id, SpacePosition.Zero, f.Clock.Now, .5, WeaponActivation.Hybrid);
        Assert.Equal(cycle * .9 * .8, result.Weapon.CycleMilliseconds, 8);
        Assert.Equal(cost * .75 * (1 + ammo.GetValueOrDefault(DogmaAttributeId.CapacitorNeedBonus) / 100), result.Weapon.CapacitorNeed, 8);
        Assert.Equal(ArtificialBudget - result.Weapon.CapacitorNeed, result.Capacitor, 6);
        Assert.Equal(
            multiplier * (ammo[DogmaAttributeId.KineticDamage] + ammo[DogmaAttributeId.ThermalDamage]),
            result.Weapon.Damage.Total,
            8
        );
        double hull = basis[DogmaAttributeId.ChargeSize] == 1 ? 1.2 : 1;
        Assert.Equal(
            basis[DogmaAttributeId.MaxRange] * 1.25 * hull * ammo[DogmaAttributeId.WeaponRangeMultiplier],
            result.Weapon.Optimal,
            8
        );
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
        Assert.Equal(magazine.Quantity - 1, result.Magazine!.Quantity);
        Assert.True(result.Applied > 0);
        Assert.Throws<InvalidDataException>(() => Combat(f).Fire(f.PilotId, gun, npc.Id, SpacePosition.Zero, f.Clock.Now, .5));
        WorldInitialization.Open(f.Db.Path);
        Assert.Equal(result.Magazine.Quantity, Assert.Single(Ammunition(f).Read(f.PilotId, f.ShipId)).Quantity);
    }

    [Theory]
    [InlineData(561, SpikeSmall)]
    [InlineData(SmallRailTwo, VoidSmall)]
    [InlineData(SmallRailTwo, 12801)]
    [InlineData(3082, SpikeSmall)]
    public void GroupAndSizeMismatchesLeaveSourceStacksMagazineAndCooldownUntouched(int type, int charge)
    {
        using Fixture f = WithTiers(type, charge);
        long gun = f.Fit(type);
        foreach (var skill in Enum.GetValues<CombatSkill>())
        {
            f.Train((int)skill, 5);
        }
        long stack = f.Grant(charge, 3);
        Assert.Throws<InvalidDataException>(() => Ammunition(f).Load(f.PilotId, f.ShipId, [gun], [stack], f.StationId, f.Clock.Now));
        Assert.Empty(Ammunition(f).Read(f.PilotId, f.ShipId));
        Assert.Equal(3, f.Inventory.List(f.PilotId, f.StationId).Single(i => i.Id == stack).Quantity);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ModuleCooldown>());
    }

    [Fact]
    public void SpecializationGatesBothLoadingAndFiringAndIsAdvertisedByTheNativeBrain()
    {
        using Fixture f = WithTiers(85008, VoidSmall);
        long gun = f.Fit(85008); // Faction gun advertises advanced charges without requiring the specialization itself.
        long stack = f.Grant(VoidSmall, 3);
        Assert.Throws<InvalidDataException>(() => Ammunition(f).Load(f.PilotId, f.ShipId, [gun], [stack], f.StationId, f.Clock.Now));
        f.Train((int)CombatSkill.SmallBlasterSpecialization, 1);
        Ammunition(f).Load(f.PilotId, f.ShipId, [gun], [stack], f.StationId, f.Clock.Now);
        var brain = f.Db.Services.GetRequiredService<PilotBrain>().Read(f.PilotId, f.ShipId);
        Assert.Contains(
            brain.ShipEffects,
            e =>
                e.Skills.Contains((int)CombatSkill.SmallBlasterSpecialization)
                && e.Extras.Contains((int)CombatSkill.SmallBlasterSpecialization)
                && e.Value == 2
        );
        f.Clock.Milliseconds += 5000;
        var npc = Space(f);
        f.Train((int)CombatSkill.SmallBlasterSpecialization, 0);
        Assert.Throws<InvalidDataException>(() => Combat(f).Fire(f.PilotId, gun, npc.Id, SpacePosition.Zero, f.Clock.Now, .5));
        Assert.Equal(3, Assert.Single(Ammunition(f).Read(f.PilotId, f.ShipId)).Quantity);
    }

    [Fact]
    public void TechTwoFittingRequiresTheSizeSkillAtFiveAndTheMatchingSpecialization()
    {
        using Fixture f = WithTiers(SmallRailTwo);
        long gun = f.Grant(SmallRailTwo);
        f.Train((int)CombatSkill.Gunnery, 1);
        f.Train((int)CombatSkill.SmallHybridTurret, 4);
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.PilotId, [gun], f.StationId, (int)InventoryFlag.AutoFit));
        f.Train((int)CombatSkill.SmallHybridTurret, 5);
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.PilotId, [gun], f.StationId, (int)InventoryFlag.AutoFit));
        f.Train((int)CombatSkill.SmallRailgunSpecialization, 1);
        f.Fitting.Fit(f.PilotId, [gun], f.StationId, (int)InventoryFlag.AutoFit);
        f.Fitting.SetOnline(f.PilotId, f.ShipId, gun, true);
        Assert.True(f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == gun).Online);
    }

    [Theory]
    [InlineData(561, 222, 200)]
    [InlineData(3082, 230, 40)]
    [InlineData(3090, 238, 40)]
    [InlineData(37302, 17648, 80)]
    public void EveryMagazineSizeUsesRealVolumeAndConservesRoundsAcrossReloadAndPartialUnload(int type, int charge, int capacity)
    {
        using Fixture f = WithTiers(type, charge);
        long gun = f.Fit(type);
        Assert.Equal(capacity, Load(f, gun, charge, 1000).Quantity);
        WorldInitialization.Open(f.Db.Path);
        Assert.Throws<InvalidDataException>(() =>
            Ammunition(f).Unload(f.PilotId, f.ShipId, [gun], f.StationId, (int)InventoryFlag.Hangar, null, f.Clock.Now)
        );
        f.Clock.Milliseconds += 5000;
        Ammunition(f).Unload(f.PilotId, f.ShipId, [gun], f.StationId, (int)InventoryFlag.Hangar, 17, f.Clock.Now);
        Assert.Equal(capacity - 17, Assert.Single(Ammunition(f).Read(f.PilotId, f.ShipId)).Quantity);
        Ammunition(f).Unload(f.PilotId, f.ShipId, [gun], f.StationId, (int)InventoryFlag.Hangar, null, f.Clock.Now);
        Assert.Empty(Ammunition(f).Read(f.PilotId, f.ShipId));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(1000, saved.Set<OwnedItem>().Where(i => i.OwnerId == f.PilotId && i.TypeId == charge).Sum(i => i.Quantity));
    }

    [Fact]
    public void UnsupportedLoadedChargeEffectsCannotSpendCapacitorOrConsumeAmmo()
    {
        using Fixture f = WithTiers(SmallRailTwo, IronSmall);
        long gun = f.Fit(SmallRailTwo);
        Load(f, gun, IronSmall);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            string extra = """{"effectID":4}""";
            catalog.Database.ExecuteSql(
                $"""
                UPDATE SourceRecord SET Data=json_insert(Data,'$.dogmaEffects[#]',json({extra}))
                WHERE Dataset='client/typedogma' AND Key='215'
                """
            );
        }
        f.Clock.Milliseconds += 5000;
        var npc = Space(f);
        Assert.Throws<InvalidDataException>(() => Combat(f).Fire(f.PilotId, gun, npc.Id, SpacePosition.Zero, f.Clock.Now, .5));
        Assert.Equal(3, Assert.Single(Ammunition(f).Read(f.PilotId, f.ShipId)).Quantity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PolarizedFittingOverridesEveryResistanceAfterDamageControlEvenWhenOffline(bool online)
    {
        using Fixture f = WithTiers(Polarized);
        f.Fit(2046);
        var before = f.Fitting.ShipAttributes(f.PilotId);
        long gun = f.Fit(Polarized, online);
        var fitted = f.Fitting.ShipAttributes(f.PilotId);
        var resonances = new[]
        {
            DogmaAttributeId.ShieldEmResonance,
            DogmaAttributeId.ShieldThermalResonance,
            DogmaAttributeId.ShieldKineticResonance,
            DogmaAttributeId.ShieldExplosiveResonance,
            DogmaAttributeId.ArmorEmResonance,
            DogmaAttributeId.ArmorThermalResonance,
            DogmaAttributeId.ArmorKineticResonance,
            DogmaAttributeId.ArmorExplosiveResonance,
            DogmaAttributeId.StructureEmResonance,
            DogmaAttributeId.StructureThermalResonance,
            DogmaAttributeId.StructureKineticResonance,
            DogmaAttributeId.StructureExplosiveResonance,
        };
        Assert.All(resonances, attribute => Assert.Equal(1, fitted[attribute]));
        f.Inventory.Move(f.PilotId, f.StationId, new([gun], f.ShipId, (int)InventoryFlag.Hangar, null));
        var removed = f.Fitting.ShipAttributes(f.PilotId);
        Assert.All(resonances, attribute => Assert.Equal(before.GetValueOrDefault(attribute, 1), removed.GetValueOrDefault(attribute, 1)));
    }

    [Theory]
    [InlineData("mutation")]
    [InlineData("restriction")]
    [InlineData("effect")]
    public void UnsupportedStoredWeaponsCannotPayConsumeRoundsOrDamageTheTarget(string reason)
    {
        using Fixture f = WithTiers(SmallRailTwo, IronSmall);
        long gun = f.Fit(SmallRailTwo);
        Load(f, gun, IronSmall);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            if (reason == "mutation")
            {
                catalog.Database.ExecuteSqlRaw(
                    "UPDATE SourceRecord SET Data=json_set(Data,'$.metaGroupID',15) WHERE Dataset='client/types' AND Key='10680'"
                );
            }
            else if (reason == "restriction")
            {
                catalog.Database.ExecuteSqlRaw("INSERT INTO TypeAttribute VALUES (10680,1298,1)");
            }
            else
            {
                string added = """{"effectID":4}""";
                catalog.Database.ExecuteSql(
                    $"""
                    UPDATE SourceRecord SET Data=json_insert(Data,'$.dogmaEffects[#]',json({added}))
                    WHERE Dataset='client/typedogma' AND Key='10680'
                    """
                );
            }
            Assert.Null(FittingCatalog.Definition(catalog, SmallRailTwo));
        }
        f.Clock.Milliseconds += 5000;
        var npc = Space(f);
        Assert.Throws<InvalidDataException>(() => Combat(f).Fire(f.PilotId, gun, npc.Id, SpacePosition.Zero, f.Clock.Now, .5));
        Assert.Equal(3, Assert.Single(Ammunition(f).Read(f.PilotId, f.ShipId)).Quantity);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(ArtificialBudget, saved.Set<WorldNpc>().Find(npc.Id)!.ShieldCharge);
    }
}
