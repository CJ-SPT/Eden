using System.Text.Json.Nodes;
using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Combat.Weapons;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class MissileFittingTests
{
    private const int LightLauncher = 499;
    private const int RocketLauncher = 10629;
    private const int LightMissile = 212;
    private const int ScourgeRocket = 266;
    private const double ArtificialBudget = 1_000_000;
    private const double SecondStackingWeight = .8691199808003975;

    private static Fixture WithTiers(params int[] types)
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadMissileTiers(f.CatalogPath, types.Concat(MissileDefinitions.Skills).Append(601).ToHashSet());
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        foreach (var attribute in new[] { DogmaAttributeId.CpuOutput, DogmaAttributeId.PowerOutput })
        {
            catalog
                .Set<TypeAttribute>()
                .Where(a => a.ItemTypeId == 601 && a.AttributeId == (int)attribute)
                .ExecuteUpdate(set => set.SetProperty(a => a.Value, ArtificialBudget));
        }
        foreach (var attribute in new[] { DogmaAttributeId.LauncherSlotsLeft, DogmaAttributeId.HighSlots, DogmaAttributeId.LowSlots })
        {
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)attribute},8)");
        }
        return f;
    }

    private static IPilotAmmunition Ammo(Fixture f) => f.Db.Services.GetRequiredService<IPilotAmmunition>();

    private static Magazine Load(Fixture f, long module, int type, int quantity = 3)
    {
        long stack = f.Grant(type, quantity);
        Ammo(f).Load(f.PilotId, f.ShipId, [module], [stack], f.StationId, f.Clock.Now);
        return Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId));
    }

    private static double CharacterDamage(Fixture f)
    {
        using WorldDbContext world = new(f.Db.Path);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == f.PilotId);
        return FittingCalculation.Read(world, catalog, pilot).Graph.Read(pilot.Id, DogmaAttributeId.MissileDamageMultiplier);
    }

    [Fact]
    public void CompleteCohortFits139UnrestrictedLaunchersAndAccepts201ChargesAcrossAllSizesAndTiers()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadMissileTiers(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var groups = MissileDefinitions.LauncherGroups.Concat([512, 1673]).ToArray();
        var launchers = catalog.Set<ItemType>().Where(t => groups.Contains(t.GroupId)).ToArray();
        var supported = launchers.Where(t => MissileLauncherCatalog.Supported(catalog, t.Id, t.GroupId)).ToArray();
        var charges = catalog.Set<ItemType>().Where(t => MissileDefinitions.ChargeGroups.Contains(t.GroupId)).ToArray();
        Assert.Equal(145, launchers.Length);
        Assert.Equal(139, supported.Length);
        Assert.Equal(201, charges.Length);
        var attributes = supported.ToDictionary(t => t.Id, t => PilotFittingStore.ReadAttributes(catalog, t.Id));
        Assert.All(charges, t => Assert.True(MissileChargeCatalog.Supported(catalog, t.Id, t.GroupId), t.Name));
        Assert.All(
            supported,
            t =>
            {
                var definition = FittingCatalog.Definition(catalog, t.Id)!;
                Assert.Equal(ModuleRack.High, definition.Rack);
                Assert.False(definition.Turret);
                Assert.Equal(t.Id != MissileDefinitions.CivilianLauncher, definition.Launcher);
                Assert.Equal(DogmaEffect.UseMissiles, FittingCatalog.DefaultEffect(catalog, t.Id));
                Assert.Contains(charges, c => MissileDefinitions.CompatibleCharge(attributes[t.Id], c.GroupId));
            }
        );
        Assert.All(charges, c => Assert.Contains(supported, t => MissileDefinitions.CompatibleCharge(attributes[t.Id], c.GroupId)));
        Assert.All(launchers.Except(supported), t => Assert.Null(FittingCatalog.Definition(catalog, t.Id)));
        var tiers = supported
            .Select(t => catalog.Set<SourceRecord>().Find("client/types", PassiveCatalog.Key(t.Id))!.Read<ClientTypes>().MetaGroupID)
            .ToHashSet();
        Assert.All(new long[] { 1, 2, 3, 4, 5 }, tier => Assert.Contains(tier, tiers));
    }

    [Theory]
    [InlineData(LightLauncher, LightMissile, 40, 10000)]
    [InlineData(RocketLauncher, ScourgeRocket, 37, 10000)]
    [InlineData(1875, LightMissile, 19, 35000)]
    [InlineData(503, 2510, 30, 10000)]
    [InlineData(17485, 24531, 27, 10000)]
    [InlineData(32444, 32436, 15, 10000)]
    public void CatalogMagazineVolumesAndRapidReloadsConserveRoundsAcrossRestartAndUnload(int type, int charge, int capacity, int reload)
    {
        using Fixture f = WithTiers(type, charge);
        long launcher = f.Fit(type);
        foreach (int skill in MissileDefinitions.Skills)
        {
            f.Train(skill, 5);
        }
        var magazine = Load(f, launcher, charge, 1000);
        Assert.Equal(capacity, magazine.Quantity);
        Assert.Equal(f.Clock.Now + reload * TimeSpan.TicksPerMillisecond, magazine.ReadyAt);
        var module = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == launcher);
        Assert.Equal(magazine.ReadyAt, f.Fitting.ModuleAttributes(f.PilotId, module)[DogmaAttributeId.NextActivationTime]);
        WorldInitialization.Open(f.Db.Path);
        Assert.Equal(magazine, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)));
        Assert.Throws<InvalidDataException>(() =>
            Ammo(f).Unload(f.PilotId, f.ShipId, [launcher], f.StationId, (int)InventoryFlag.Hangar, null, f.Clock.Now)
        );
        f.Clock.Milliseconds += reload;
        Ammo(f).Unload(f.PilotId, f.ShipId, [launcher], f.StationId, (int)InventoryFlag.Hangar, 7, f.Clock.Now);
        Assert.Equal(capacity - 7, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
        Ammo(f).Unload(f.PilotId, f.ShipId, [launcher], f.StationId, (int)InventoryFlag.Hangar, null, f.Clock.Now);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(1000, saved.Set<OwnedItem>().Where(i => i.OwnerId == f.PilotId && i.TypeId == charge).Sum(i => i.Quantity));
    }

    [Theory]
    [InlineData(LightLauncher, ScourgeRocket)]
    [InlineData(RocketLauncher, LightMissile)]
    [InlineData(LightLauncher, 24471)]
    [InlineData(RocketLauncher, 2510)]
    [InlineData(LightLauncher, 222)]
    public void WrongFamiliesAndAdvancedGroupsCannotMoveAmmunition(int type, int charge)
    {
        using Fixture f = WithTiers(type, charge);
        long launcher = f.Fit(type);
        foreach (int skill in MissileDefinitions.Skills)
        {
            f.Train(skill, 5);
        }
        long stack = f.Grant(charge, 3);
        Assert.Throws<InvalidDataException>(() => Ammo(f).Load(f.PilotId, f.ShipId, [launcher], [stack], f.StationId, f.Clock.Now));
        Assert.Empty(Ammo(f).Read(f.PilotId, f.ShipId));
        Assert.Equal(3, f.Inventory.List(f.PilotId, f.StationId).Single(i => i.Id == stack).Quantity);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Null(saved.Set<ModuleCooldown>().Find(launcher));
    }

    [Theory]
    [InlineData(2404, 12800, 20210)]
    [InlineData(10631, 4000, 20209)]
    [InlineData(1877, 6240, 20210)]
    [InlineData(13925, 11200, 0)]
    [InlineData(32444, 170970, 0)]
    public void OperationRapidLaunchAndSpecializationApplyOnceToTheirRequiredLaunchers(int type, double cycle, int specialization)
    {
        using Fixture f = WithTiers(type);
        long launcher = f.Fit(type);
        f.Train((int)MissileSkill.MissileLauncherOperation, 5);
        f.Train((int)MissileSkill.RapidLaunch, 4);
        f.Train((int)MissileSkill.LightMissileSpecialization, 3);
        f.Train((int)MissileSkill.RocketSpecialization, 3);
        var module = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == launcher);
        double expected = cycle * .9 * .88 * (specialization == 0 ? 1 : .94);
        Assert.Equal(expected, f.Fitting.ModuleAttributes(f.PilotId, module)[DogmaAttributeId.WeaponCycleTime], 8);
    }

    [Theory]
    [InlineData(LightLauncher, LightMissile, 83, 1, 1)]
    [InlineData(LightLauncher, 27361, 95, 1, 1.1)]
    [InlineData(RocketLauncher, ScourgeRocket, 33, 1, 1.1)]
    [InlineData(10631, 24471, 51.3, 1, 1.1)]
    [InlineData(LightLauncher, 269, 62, 1.25, 1)]
    [InlineData(503, 2510, 450, 1, 1)]
    [InlineData(32444, 32436, 4750, 1, 1.1)]
    public void LoadedWarheadsUseEarnedDamageAndOnlyTheKineticRookieRole(
        int type,
        int charge,
        double damage,
        double automaticBonus,
        double kineticRole
    )
    {
        using Fixture f = WithTiers(type, charge);
        long launcher = f.Fit(type);
        foreach (int skill in MissileDefinitions.Skills)
        {
            f.Train(skill, 5);
        }
        f.Train((int)MissileSkill.WarheadUpgrades, 4);
        Load(f, launcher, charge);
        var attributes = Ammo(f).ChargeAttributes(f.PilotId, f.ShipId, launcher);
        var warhead = MissileRules.Warhead(attributes, CharacterDamage(f));
        Assert.Equal(damage * 1.25 * automaticBonus * 1.08 * kineticRole, warhead.Damage.Total, 8);
        f.Fit(12274);
        Assert.Equal(
            warhead.Damage.Total * 1.07,
            MissileRules.Warhead(Ammo(f).ChargeAttributes(f.PilotId, f.ShipId, launcher), CharacterDamage(f)).Damage.Total,
            8
        );
        // The character's ballistic multiplier is kept separate from charge
        // attributes, as in native dogma, rather than baked into them twice.
        Assert.Equal(warhead, MissileRules.Warhead(Ammo(f).ChargeAttributes(f.PilotId, f.ShipId, launcher)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedBallisticControlsDiminishByStrengthInEitherFittingOrder(bool officerFirst)
    {
        using Fixture f = WithTiers(LightLauncher, LightMissile);
        PassiveCatalogFixture.LoadTiers(f.CatalogPath, new HashSet<int> { 22291, 14684 });
        long launcher = f.Fit(LightLauncher);
        Load(f, launcher, LightMissile);
        var module = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == launcher);
        double cycle = f.Fitting.ModuleAttributes(f.PilotId, module)[DogmaAttributeId.WeaponCycleTime];
        f.Fit(officerFirst ? 14684 : 22291);
        long second = f.Fit(officerFirst ? 22291 : 14684);
        Assert.Equal(1.13 * (1 + .1 * SecondStackingWeight), CharacterDamage(f), 10);
        Assert.Equal(
            cycle * .89 * (1 - .105 * SecondStackingWeight),
            f.Fitting.ModuleAttributes(f.PilotId, module)[DogmaAttributeId.WeaponCycleTime],
            8
        );
        f.Fitting.SetOnline(f.PilotId, f.ShipId, second, false);
        Assert.Equal(officerFirst ? 1.13 : 1.1, CharacterDamage(f), 10);
    }

    [Fact]
    public void GuidanceAndSkillsModifyLoadedMissilesWithSeparateDiminishingAndExemptBonuses()
    {
        using Fixture f = WithTiers(LightLauncher, LightMissile);
        PassiveCatalogFixture.LoadTiers(f.CatalogPath, new HashSet<int> { 35771 });
        long launcher = f.Fit(LightLauncher);
        Load(f, launcher, LightMissile);
        f.Train((int)MissileSkill.MissileProjection, 3);
        f.Train((int)MissileSkill.MissileBombardment, 4);
        f.Train((int)MissileSkill.GuidedMissilePrecision, 3);
        f.Train((int)MissileSkill.TargetNavigationPrediction, 4);
        var basis = Ammo(f).ChargeAttributes(f.PilotId, f.ShipId, launcher);
        Assert.Equal(3750 * 1.3, basis[DogmaAttributeId.MaxVelocity], 8);
        Assert.Equal(5000 * 1.4, basis[DogmaAttributeId.MissileFlightTime], 8);
        Assert.Equal(40 * .85, basis[DogmaAttributeId.ExplosionRadius], 8);
        Assert.Equal(170 * 1.4, basis[DogmaAttributeId.ExplosionVelocity], 8);
        f.Fit(35770);
        long enhancer = f.Fit(35771);
        var modified = Ammo(f).ChargeAttributes(f.PilotId, f.ShipId, launcher);
        Assert.Equal(
            basis[DogmaAttributeId.MaxVelocity] * 1.06 * (1 + .045 * SecondStackingWeight),
            modified[DogmaAttributeId.MaxVelocity],
            8
        );
        Assert.Equal(
            basis[DogmaAttributeId.MissileFlightTime] * 1.06 * (1 + .045 * SecondStackingWeight),
            modified[DogmaAttributeId.MissileFlightTime],
            8
        );
        Assert.Equal(
            basis[DogmaAttributeId.ExplosionRadius] * .94 * (1 - .044 * SecondStackingWeight),
            modified[DogmaAttributeId.ExplosionRadius],
            8
        );
        Assert.Equal(
            basis[DogmaAttributeId.ExplosionVelocity] * 1.06 * (1 + .044 * SecondStackingWeight),
            modified[DogmaAttributeId.ExplosionVelocity],
            8
        );
        f.Fitting.SetOnline(f.PilotId, f.ShipId, enhancer, false);
        Assert.Equal(
            basis[DogmaAttributeId.ExplosionRadius] * .956,
            Ammo(f).ChargeAttributes(f.PilotId, f.ShipId, launcher)[DogmaAttributeId.ExplosionRadius],
            8
        );
    }

    [Fact]
    public void HeatSinksHybridAndProjectileUpgradesCannotModifyMissiles()
    {
        using Fixture f = WithTiers(LightLauncher, LightMissile);
        long launcher = f.Fit(LightLauncher);
        Load(f, launcher, LightMissile);
        var module = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == launcher);
        double cycle = f.Fitting.ModuleAttributes(f.PilotId, module)[DogmaAttributeId.WeaponCycleTime];
        var warhead = MissileRules.Warhead(Ammo(f).ChargeAttributes(f.PilotId, f.ShipId, launcher));
        foreach (int type in new[] { 2363, 9944, 520 })
        {
            f.Fit(type);
        }
        Assert.Equal(1, CharacterDamage(f));
        Assert.Equal(cycle, f.Fitting.ModuleAttributes(f.PilotId, module)[DogmaAttributeId.WeaponCycleTime]);
        Assert.Equal(warhead, MissileRules.Warhead(Ammo(f).ChargeAttributes(f.PilotId, f.ShipId, launcher)));
    }

    [Fact]
    public void NativePrimesAndOwnerBrainUseTheSameEarnedMissileInputs()
    {
        using Fixture f = WithTiers(LightLauncher, LightMissile);
        long launcher = f.Fit(LightLauncher);
        var magazine = Load(f, launcher, LightMissile);
        f.Train((int)MissileSkill.MissileBombardment, 4);
        f.Train((int)MissileSkill.WarheadUpgrades, 3);
        using var scope = f.Db.Services.CreateScope();
        var prime = scope.ServiceProvider.GetRequiredService<MagazineWire>().Prime(f.PilotId, magazine);
        Assert.Equal(7000, prime.Attributes[DogmaAttributeId.MissileFlightTime], 8);
        Assert.Equal(83 * 1.25 * 1.06, prime.Attributes[DogmaAttributeId.EmDamage], 8);
        Assert.Equal(3, prime.Attributes[DogmaAttributeId.Quantity]);
        var brain = scope.ServiceProvider.GetRequiredService<PilotBrain>().Read(f.PilotId, f.ShipId);
        var ownerEffects = brain.CharacterEffects.Where(e => e.Modifier == BrainModifier.OwnerRequiredSkill).ToArray();
        Assert.Equal(9, ownerEffects.Length);
        Assert.All(
            ownerEffects,
            effect =>
            {
                Assert.Equal(f.PilotId, effect.TargetId);
                var state = Assert.IsType<BlueNewObject>(WireAssertions.Materialize(effect)).State;
                Assert.Equal("ORS", Assert.IsType<Dictionary<string, object?>>(state)["modifierType"]);
            }
        );
        Assert.DoesNotContain(brain.ShipEffects, e => e.Modifier == BrainModifier.OwnerRequiredSkill);
        var duration = Assert.Single(ownerEffects, e => e.Attribute == DogmaAttributeId.MissileFlightTime);
        Assert.Equal(40, duration.Value);
        Assert.Equal([(int)MissileSkill.MissileLauncherOperation], duration.Extras);
        Assert.Equal([(int)MissileSkill.MissileBombardment], duration.Skills);
    }

    [Theory]
    [InlineData("mutation")]
    [InlineData("restriction")]
    [InlineData("effect")]
    [InlineData("default")]
    public void UnsupportedLauncherProfilesFailBeforeFittingOrMovingRounds(string reason)
    {
        using Fixture f = WithTiers(LightLauncher, LightMissile);
        long launcher = f.Fit(LightLauncher);
        long stack = f.Grant(LightMissile, 3);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            if (reason == "mutation")
            {
                catalog.Database.ExecuteSqlRaw(
                    "UPDATE SourceRecord SET Data=json_set(Data,'$.metaGroupID',15) WHERE Dataset='client/types' AND Key='499'"
                );
            }
            else if (reason == "restriction")
            {
                catalog.Database.ExecuteSqlRaw("INSERT INTO TypeAttribute VALUES (499,1298,1)");
            }
            else
            {
                var source = catalog.Set<SourceRecord>().Find("client/typedogma", "499")!;
                var data = JsonNode.Parse(source.Data)!;
                var effects = data["dogmaEffects"]!.AsArray();
                if (reason == "effect")
                {
                    effects.Add(new JsonObject { ["effectID"] = 4 });
                }
                else
                {
                    foreach (var effect in effects)
                    {
                        effect!["isDefault"] = 0;
                    }
                }
                string json = data.ToJsonString();
                catalog
                    .Set<SourceRecord>()
                    .Where(r => r.Dataset == "client/typedogma" && r.Key == "499")
                    .ExecuteUpdate(set => set.SetProperty(r => r.Data, json));
            }
            Assert.Null(FittingCatalog.Definition(catalog, LightLauncher));
        }
        Assert.Throws<InvalidDataException>(() => Ammo(f).Load(f.PilotId, f.ShipId, [launcher], [stack], f.StationId, f.Clock.Now));
        Assert.Empty(Ammo(f).Read(f.PilotId, f.ShipId));
        Assert.Equal(3, f.Inventory.List(f.PilotId, f.StationId).Single(i => i.Id == stack).Quantity);
    }

    [Fact]
    public void ForeignOrEmptyMagazinesCannotBePrimedAsOwnedCharges()
    {
        using Fixture f = WithTiers(LightLauncher, LightMissile);
        long launcher = f.Fit(LightLauncher);
        Assert.Throws<InvalidDataException>(() => Ammo(f).ChargeAttributes(f.PilotId, f.ShipId, launcher));
        Load(f, launcher, LightMissile);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Single(i => i.LocationId == launcher).OwnerId++;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => Ammo(f).ChargeAttributes(f.PilotId, f.ShipId, launcher));
        Assert.Throws<InvalidDataException>(() => Ammo(f).ChargeAttributes(f.PilotId, f.ShipId + 1, launcher));
    }

    [Fact]
    public void InvalidMissileMotorCannotLoadAChargeWithAnOtherwiseValidWarhead()
    {
        using Fixture f = WithTiers(LightLauncher, LightMissile);
        long launcher = f.Fit(LightLauncher);
        long stack = f.Grant(LightMissile, 3);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog
                .Set<TypeAttribute>()
                .Where(a => a.ItemTypeId == LightMissile && a.AttributeId == (int)DogmaAttributeId.Agility)
                .ExecuteUpdate(set => set.SetProperty(a => a.Value, 0));
        }
        Assert.Throws<InvalidDataException>(() => Ammo(f).Load(f.PilotId, f.ShipId, [launcher], [stack], f.StationId, f.Clock.Now));
        Assert.Empty(Ammo(f).Read(f.PilotId, f.ShipId));
        Assert.Equal(3, f.Inventory.List(f.PilotId, f.StationId).Single(i => i.Id == stack).Quantity);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Null(saved.Set<ModuleCooldown>().Find(launcher));
    }

    [Fact]
    public void CivilianLauncherUsesAHighSlotWithoutInventingALauncherHardpoint()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadMissileTiers(
            f.CatalogPath,
            MissileDefinitions.Skills.Concat([601, LightLauncher, MissileDefinitions.CivilianLauncher]).ToHashSet()
        );
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            // Deliberately remove launcher hardpoints from this test hull;
            // the actual catalog Ibis has launcher hardpoints of its own.
            catalog.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO TypeAttribute VALUES (601,101,0)");
        }
        Assert.Throws<InvalidDataException>(() => f.Fit(LightLauncher));
        long civilian = f.Fit(MissileDefinitions.CivilianLauncher);
        Assert.True(f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == civilian).Online);
        Assert.Equal(0, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.LauncherSlotsLeft]);
    }

    [Fact]
    public void AdvancedAmmunitionChecksItsOwnRequirementsBeforeStartingReload()
    {
        using Fixture f = WithTiers(10631, 24471);
        long launcher = f.Fit(10631);
        f.Train((int)MissileSkill.Rockets, 4);
        long stack = f.Grant(24471, 3);
        Assert.Throws<InvalidDataException>(() => Ammo(f).Load(f.PilotId, f.ShipId, [launcher], [stack], f.StationId, f.Clock.Now));
        Assert.Empty(Ammo(f).Read(f.PilotId, f.ShipId));
        using (WorldDbContext saved = new(f.Db.Path))
        {
            Assert.Null(saved.Set<ModuleCooldown>().Find(launcher));
        }
        f.Train((int)MissileSkill.Rockets, 5);
        Assert.Equal(3, Load(f, launcher, 24471).Quantity);
    }

    [Theory]
    [InlineData(34290)]
    [InlineData(34292)]
    [InlineData(34294)]
    public void PolarizedLaunchersUseTheSharedFittedResistanceOverrideEvenOffline(int type)
    {
        using Fixture f = WithTiers(type);
        long launcher = f.Fit(type, online: false);
        Assert.Equal(1, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ShieldEmResonance]);
        Assert.Equal(1, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ArmorEmResonance]);
        f.Inventory.Move(f.PilotId, f.StationId, new([launcher], f.ShipId, (int)InventoryFlag.Hangar, null));
        Assert.Equal(.92, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ShieldEmResonance], 8);
        Assert.Equal(.5, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ArmorEmResonance], 8);
    }

    [Fact]
    public void MissileFittingNeverTurnsTheTurretFireOperationIntoAnInstantMissileHit()
    {
        using Fixture f = WithTiers(LightLauncher, LightMissile);
        long launcher = f.Fit(LightLauncher);
        Load(f, launcher, LightMissile);
        f.Clock.Milliseconds += 10000;
        NpcEncounterTests.Prepare(f.Db);
        var npc = f.Db.Services.GetRequiredService<INpcEncounters>().List(30000144, f.Clock.Now)[0];
        f.Db.Store.SetPosition(f.ShipId, npc.Position, npc.SystemId);
        Assert.Throws<InvalidDataException>(() =>
            f
                .Db.Services.GetRequiredService<IPilotCombat>()
                .Fire(f.PilotId, launcher, npc.Id, Eden.Gameplay.Flight.SpacePosition.Zero, f.Clock.Now, .5)
        );
        Assert.Equal(3, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(0, saved.Set<WorldNpc>().Find(npc.Id)!.StructureDamage);
    }
}
