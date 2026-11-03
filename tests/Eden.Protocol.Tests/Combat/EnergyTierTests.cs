using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class EnergyTierTests
{
    private const int Pulse = 450;
    private const int BeamTwo = 2993;
    private const int Radio = 239;
    private const int FactionRadio = 19978;
    private const int Gleam = 12557;
    private const int Scorch = 12563;
    private const double ArtificialBudget = 1_000_000;

    private static Fixture WithTiers(params int[] types)
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadEnergyTiers(f.CatalogPath, types.Concat(Enum.GetValues<CombatSkill>().Select(s => (int)s)).ToHashSet());
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        // Deliberate test-only budgets exercise large/capital weapons on the
        // fixture's Ibis without claiming they fit a real corvette.
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

    private static Magazine Load(Fixture f, long gun, int charge)
    {
        long stack = f.Grant(charge, 3);
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
    public void All204FunctionalUnrestrictedGunsAnd200CrystalsHaveCompleteMatchingProfiles()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadEnergyTiers(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var guns = catalog
            .Set<ItemType>()
            .Where(t => t.GroupId == CombatDefinitions.EnergyWeaponGroup && t.Id != (int)CombatWeaponType.CivilianGatlingRailgun)
            .ToArray();
        var supported = guns.Where(t => TurretWeaponCatalog.Supported(catalog, t.Id, t.GroupId)).ToArray();
        var charges = catalog
            .Set<ItemType>()
            .Where(t =>
                t.GroupId == AmmunitionDefinitions.FrequencyCrystalGroup
                || t.GroupId == AmmunitionDefinitions.AdvancedBeamCrystalGroup
                || t.GroupId == AmmunitionDefinitions.AdvancedPulseCrystalGroup
            )
            .ToArray();
        Assert.Equal(216, guns.Length);
        Assert.Equal(204, supported.Length);
        Assert.Equal(200, charges.Length);
        Assert.All(charges, t => Assert.True(TurretChargeCatalog.Supported(catalog, t.Id, t.GroupId), t.Name));
        var payloads = charges.ToDictionary(t => t.Id, t => PilotFittingStore.ReadAttributes(catalog, t.Id));
        Assert.All(
            supported,
            t =>
            {
                var definition = FittingCatalog.Definition(catalog, t.Id)!;
                Assert.Equal(ModuleRack.High, definition.Rack);
                Assert.True(definition.Turret);
                Assert.Equal(DogmaEffect.LaserFired, FittingCatalog.DefaultEffect(catalog, t.Id));
                var attributes = PilotFittingStore.ReadAttributes(catalog, t.Id);
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
    [InlineData(Pulse, Radio)]
    [InlineData(6633, Radio)]
    [InlineData(BeamTwo, Gleam)]
    [InlineData(3017, Scorch)]
    [InlineData(13795, Gleam)]
    [InlineData(85012, Scorch)]
    [InlineData(3009, 12826)]
    [InlineData(85014, 12818)]
    [InlineData(3065, 12828)]
    [InlineData(34276, 12820)]
    [InlineData(37298, 41330)]
    [InlineData(37299, 41334)]
    [InlineData(41118, 41334)]
    public void SizesAndTiersSpendCapacitorAndUseTheirOwnCrystalsAndSkills(int type, int charge)
    {
        using Fixture f = WithTiers(type, charge);
        foreach (var skill in Enum.GetValues<CombatSkill>())
        {
            f.Train((int)skill, 5);
        }
        long gun = f.Fit(type);
        var magazine = Load(f, gun, charge);
        Assert.Equal(1, magazine.Quantity);
        Assert.Equal(f.Clock.Now + 100, magazine.ReadyAt);
        f.Clock.Milliseconds++;
        var npc = Space(f);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var basis = PilotFittingStore.ReadAttributes(catalog, type);
        var payload = PilotFittingStore.ReadAttributes(catalog, charge);
        double multiplier = basis[DogmaAttributeId.DamageMultiplier] * 1.25 * 1.15;
        if (basis[DogmaAttributeId.TechLevel] == AmmunitionDefinitions.TechTwo)
        {
            multiplier *= 1.1;
        }
        var result = Combat(f).Fire(f.PilotId, gun, npc.Id, SpacePosition.Zero, f.Clock.Now, .5, WeaponActivation.Laser, .9);
        Assert.Equal((int)DogmaEffect.LaserFired, result.Weapon.EffectId);
        Assert.Equal(payload.GetValueOrDefault(DogmaAttributeId.EmDamage) * multiplier, result.Weapon.Damage.Em, 8);
        Assert.Equal(payload.GetValueOrDefault(DogmaAttributeId.ThermalDamage) * multiplier, result.Weapon.Damage.Thermal, 8);
        Assert.Equal(basis[DogmaAttributeId.WeaponCycleTime] * .9 * .8, result.Weapon.CycleMilliseconds, 8);
        double cost =
            basis[DogmaAttributeId.CapacitorNeed] * .75 * (1 + payload.GetValueOrDefault(DogmaAttributeId.CapacitorNeedBonus) / 100);
        Assert.Equal(cost, result.Weapon.CapacitorNeed, 8);
        Assert.Equal(ArtificialBudget - cost, result.Capacitor, 8);
        Assert.Equal(1, result.Magazine!.Quantity);
        Assert.Equal(basis[DogmaAttributeId.MaxRange] * 1.25 * payload[DogmaAttributeId.WeaponRangeMultiplier], result.Weapon.Optimal, 8);
        Assert.Equal(
            basis[DogmaAttributeId.TrackingSpeed] * 1.25 * payload.GetValueOrDefault(DogmaAttributeId.ChargeTrackingMultiplier, 1),
            result.Weapon.Tracking,
            8
        );
        Assert.Equal(
            basis[DogmaAttributeId.AccuracyFalloff] * 1.25 * payload.GetValueOrDefault(DogmaAttributeId.ChargeFalloffMultiplier, 1),
            result.Weapon.Falloff,
            8
        );
    }

    [Theory]
    [InlineData(Radio, 0, 0)]
    [InlineData(FactionRadio, .999, .00025)]
    [InlineData(Gleam, .099999, .01)]
    [InlineData(Gleam, .1, 0)]
    [InlineData(Scorch, .9, 0)]
    public void CrystalWearUsesTheCatalogFlagProbabilityAndIncrement(int type, double roll, double expected)
    {
        using Fixture f = WithTiers(type);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var attributes = PilotFittingStore.ReadAttributes(catalog, type);
        var result = CrystalRules.Use(attributes, 0, roll);
        Assert.Equal(expected, result.Damage);
        Assert.False(result.Destroyed);
        if (type == FactionRadio)
        {
            double damage = 0;
            for (int i = 0; i < 4000; i++)
            {
                var wear = CrystalRules.Use(attributes, damage, roll);
                Assert.Equal(i == 3999, wear.Destroyed);
                damage = wear.Damage;
            }
            Assert.Equal(1, damage);
        }
    }

    [Fact]
    public void WearPersistsThroughReloadSwapUnloadMoveUnfitAndRestartWithIndividualIdentity()
    {
        using Fixture f = WithTiers(Pulse, FactionRadio);
        long gun = f.Fit(Pulse);
        Load(f, gun, FactionRadio);
        f.Clock.Milliseconds++;
        var npc = Space(f);
        var shot = Combat(f).Fire(f.PilotId, gun, npc.Id, SpacePosition.Zero, f.Clock.Now, .999, crystalRoll: 0);
        Assert.Equal(.00025, shot.Magazine!.Damage);
        f.Clock.Milliseconds += 10000;
        f.Db.Store.SetPosition(f.ShipId, null);
        var unloaded = Assert
            .Single(Ammo(f).Unload(f.PilotId, f.ShipId, [gun], f.StationId, (int)InventoryFlag.Hangar, null, f.Clock.Now).Items)
            .Item;
        Assert.True(unloaded.Singleton);
        Assert.Equal(.00025, unloaded.CrystalDamage);
        f.Inventory.Move(f.PilotId, f.ShipId, new([unloaded.Id], f.StationId, (int)InventoryFlag.Cargo, null));
        Ammo(f).Load(f.PilotId, f.ShipId, [gun], [unloaded.Id], f.ShipId, f.Clock.Now);
        f.Clock.Milliseconds++;
        // Swapping the same crystal type returns the worn individual instead
        // of merging it with, or transferring wear to, the fresh stack.
        var fresh = f.Inventory.List(f.PilotId, f.StationId).Single(i => i.TypeId == FactionRadio && !i.Singleton);
        Ammo(f).Load(f.PilotId, f.ShipId, [gun], [fresh.Id], f.StationId, f.Clock.Now);
        Assert.Equal(0, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Damage);
        Assert.Equal(.00025, f.Inventory.List(f.PilotId, f.StationId).Single(i => i.Id == unloaded.Id).CrystalDamage);
        f.Clock.Milliseconds++;
        Ammo(f).Load(f.PilotId, f.ShipId, [gun], [unloaded.Id], f.StationId, f.Clock.Now);
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Equal(.00025, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Damage);
        var changes = f.Inventory.Move(f.PilotId, f.StationId, new([gun], f.ShipId, (int)InventoryFlag.Hangar, null));
        var returned = changes.Single(c => c.Item.Id == unloaded.Id).Item;
        Assert.Equal(.00025, returned.CrystalDamage);
        Assert.Equal(1, returned.Quantity);
        Assert.Equal(3, f.Inventory.List(f.PilotId, f.StationId).Where(i => i.TypeId == FactionRadio).Sum(i => i.Quantity));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(.999999)]
    public void LastCrystalShotIsPaidAndBreaksEvenWhenItMissesThenFurtherFireFails(double accuracy)
    {
        using Fixture f = WithTiers(Pulse, FactionRadio);
        long gun = f.Fit(Pulse);
        Load(f, gun, FactionRadio);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Single(i => i.LocationId == gun).CrystalDamage = .99975;
            world.SaveChanges();
        }
        f.Clock.Milliseconds++;
        var npc = Space(f);
        var shot = Combat(f).Fire(f.PilotId, gun, npc.Id, new SpacePosition(0, 1_000_000, 0), f.Clock.Now, accuracy, crystalRoll: .5);
        Assert.Equal(0, shot.Magazine!.Quantity);
        Assert.Equal(1, shot.Magazine.Damage);
        Assert.True(shot.Capacitor < ArtificialBudget);
        Assert.Empty(Ammo(f).Read(f.PilotId, f.ShipId));
        f.Clock.Milliseconds += 10000;
        Assert.Throws<InvalidDataException>(() => Combat(f).Fire(f.PilotId, gun, npc.Id, SpacePosition.Zero, f.Clock.Now, .5));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<OwnedItem>().Where(i => i.LocationId == gun));
    }

    [Fact]
    public void FailedActivationCannotSpendCapacitorWearCrystalOrStartCycle()
    {
        using Fixture f = WithTiers(Pulse, FactionRadio);
        long gun = f.Fit(Pulse);
        Load(f, gun, FactionRadio);
        var npc = Space(f);
        // Reload is still in progress.
        Assert.Throws<InvalidDataException>(() =>
            Combat(f).Fire(f.PilotId, gun, npc.Id, SpacePosition.Zero, f.Clock.Now, .5, crystalRoll: 0)
        );
        f.Clock.Milliseconds++;
        Assert.Throws<InvalidDataException>(() =>
            Combat(f).Fire(f.PilotId, gun, npc.Id, SpacePosition.Zero, f.Clock.Now, .5, WeaponActivation.AmmoFed)
        );
        Assert.Throws<InvalidDataException>(() =>
            Combat(f).Fire(f.PilotId, gun, npc.Id, SpacePosition.Zero, f.Clock.Now, .5, crystalRoll: double.NaN)
        );
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<ShipEnergy>());
        Assert.Equal(0, world.Set<OwnedItem>().Single(i => i.LocationId == gun).CrystalDamage);
        Assert.Equal(ModuleCooldownKind.Reload, world.Set<ModuleCooldown>().Single().Kind);
    }

    [Theory]
    [InlineData(Pulse, Gleam)]
    [InlineData(BeamTwo, Scorch)]
    [InlineData(Pulse, 247)]
    public void WrongBeamPulseOrSizeCrystalsLeaveStacksAndMagazineUntouched(int type, int charge)
    {
        using Fixture f = WithTiers(type, charge);
        long gun = f.Fit(type);
        f.Train((int)CombatSkill.SmallBeamLaserSpecialization, 5);
        f.Train((int)CombatSkill.SmallPulseLaserSpecialization, 5);
        long stack = f.Grant(charge, 3);
        Assert.Throws<InvalidDataException>(() => Ammo(f).Load(f.PilotId, f.ShipId, [gun], [stack], f.StationId, f.Clock.Now));
        Assert.Empty(Ammo(f).Read(f.PilotId, f.ShipId));
        Assert.Equal(3, f.Inventory.List(f.PilotId, f.StationId).Single(i => i.Id == stack).Quantity);
    }

    [Fact]
    public void NativePrimeIncludesSkilledLaserAttributesAndSavedCrystalDamage()
    {
        using Fixture f = WithTiers(BeamTwo, Gleam);
        long gun = f.Fit(BeamTwo);
        Load(f, gun, Gleam);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Single(i => i.LocationId == gun).CrystalDamage = .42;
            world.SaveChanges();
        }
        var module = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == gun);
        using var scope = f.Db.Services.CreateScope();
        var prime = scope.ServiceProvider.GetRequiredService<ShipDogmaBuilder>().PrimeModule(f.Db.Store.FindPilot(1000001)!, module);
        Assert.Equal(3.6 * 1.25 * 1.1, prime.Attributes[DogmaAttributeId.DamageMultiplier], 8);
        var magazine = Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId));
        var charge = scope.ServiceProvider.GetRequiredService<MagazineWire>().Prime(magazine);
        Assert.Equal(.42, charge.Attributes[DogmaAttributeId.Damage]);
        Assert.Equal(1, charge.Attributes[DogmaAttributeId.Quantity]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NativeDamageNotificationPrecedesRemovalOfABrokenCrystal(int quantity)
    {
        using Fixture f = new();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new(), f.Db.Objects),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        Magazine magazine = new(1234, pilot.ShipId, (int)InventoryFlag.HighSlot0, FactionRadio, quantity, 0, .42);
        MagazineWire.NotifyState(context, magazine, f.Clock.Now);
        var changes = context.Notifications.OfType<ModuleAttributesChanged>().SelectMany(n => n.Changes).ToArray();
        Assert.Equal(2, changes.Length);
        Assert.All(changes, c => Assert.Equal(MagazineWire.Key(magazine), c.Item));
        Assert.Equal(DogmaAttributeId.Damage, changes[quantity == 0 ? 0 : 1].Attribute);
        Assert.Equal(.42, changes.Single(c => c.Attribute == DogmaAttributeId.Damage).Value);
        Assert.Equal(quantity, changes.Single(c => c.Attribute == DogmaAttributeId.Quantity).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeItemInfoReadsOwnedLooseCrystalConditionWithoutPrimingAnActiveModule(bool cargo)
    {
        using Fixture f = WithTiers(FactionRadio);
        long id = f.Grant(FactionRadio, cargo: cargo);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var item = world.Set<OwnedItem>().Find(id)!;
            item.Singleton = true;
            item.CrystalDamage = .42;
            world.SaveChanges();
        }
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new(), f.Db.Objects),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        using var scope = f.Db.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<InventoryHandler>();
        var success = Assert.IsType<PilotCallResult.Success>(handler.Call(context, "dogmaIM", "ItemGetInfo", [id]));
        var info = Assert.IsType<DogmaItemInfo<double>>(success.Reply);
        Assert.Equal(new DogmaItemKey.Item(id), info.Key);
        Assert.Equal(.42, info.Attributes[DogmaAttributeId.Damage]);
        Assert.Equal(1, info.Attributes[DogmaAttributeId.Quantity]);
        Assert.Empty(info.ActiveEffects);
        Assert.Empty(context.Notifications);
        Assert.IsType<PilotCallResult.UnsupportedCall>(handler.Call(context, "dogmaIM", "ItemGetInfo", [id + 100000]));
    }

    [Fact]
    public void HeatSinksStackOnEnergyWeaponsAndOtherDamageUpgradesDoNotApply()
    {
        using Fixture f = WithTiers(Pulse);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog
                .Set<TypeAttribute>()
                .Where(a => a.ItemTypeId == 601 && a.AttributeId == (int)DogmaAttributeId.LowSlots)
                .ExecuteUpdate(set => set.SetProperty(a => a.Value, 8));
        }
        long gun = f.Fit(Pulse);
        var module = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == gun);
        var basis = Combat(f).ModuleAttributes(f.PilotId, module);
        f.Fit(520);
        f.Fit(9944);
        Assert.Equal(
            basis[DogmaAttributeId.DamageMultiplier],
            Combat(f).ModuleAttributes(f.PilotId, module)[DogmaAttributeId.DamageMultiplier]
        );
        for (int i = 0; i < 3; i++)
        {
            f.Fit(2363);
        }
        var boosted = Combat(f).ModuleAttributes(f.PilotId, module);
        Assert.Equal(
            basis[DogmaAttributeId.DamageMultiplier] * 1.07 * (1 + .07 * .8691199808003975) * (1 + .07 * .5705831435105602),
            boosted[DogmaAttributeId.DamageMultiplier],
            8
        );
        Assert.Equal(
            basis[DogmaAttributeId.WeaponCycleTime] * .92 * (1 - .08 * .8691199808003975) * (1 - .08 * .5705831435105602),
            boosted[DogmaAttributeId.WeaponCycleTime],
            8
        );
    }

    [Theory]
    [InlineData(34272, false)]
    [InlineData(34274, true)]
    [InlineData(34276, false)]
    public void PolarizedLasersOverrideAllResistancesWhileFittedAndRestoreThemOnUnfit(int type, bool online)
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
    public void Version31AddsPristineConditionWithoutGrantsOrLosingOwnedItems()
    {
        using Fixture f = WithTiers(Pulse, FactionRadio);
        long gun = f.Fit(Pulse);
        Load(f, gun, FactionRadio);
        long id;
        using (WorldDbContext world = new(f.Db.Path))
        {
            id = world.Set<OwnedItem>().Single(i => i.LocationId == gun).Id;
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldItem DROP COLUMN CrystalDamage; PRAGMA user_version=31");
        }
        _ = WorldInitialization.Open(f.Db.Path);
        _ = WorldInitialization.Open(f.Db.Path);
        using WorldDbContext saved = new(f.Db.Path);
        var crystal = saved.Set<OwnedItem>().Single(i => i.Id == id);
        Assert.Equal(0, crystal.CrystalDamage);
        Assert.Equal(1, crystal.Quantity);
        Assert.Equal(gun, crystal.LocationId);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Theory]
    [InlineData(CombatSkill.SmallEnergyTurret, ChargeSize.Small, 1.265, .64)]
    [InlineData(CombatSkill.MediumEnergyTurret, ChargeSize.Medium, 1.15, .8)]
    public void CorvetteEnergyRolesAndUnrelatedSkillsRespectTheRequiredSkillSelector(
        CombatSkill required,
        ChargeSize size,
        double multiplier,
        double capacitor
    )
    {
        Dictionary<DogmaAttributeId, double> attributes = new()
        {
            [DogmaAttributeId.TechLevel] = AmmunitionDefinitions.TechOne,
            [DogmaAttributeId.ChargeSize] = (int)size,
            [DogmaAttributeId.RequiredSkill1] = (int)required,
            [DogmaAttributeId.RequiredSkill2] = (int)CombatSkill.Gunnery,
            [DogmaAttributeId.DamageMultiplier] = 1,
            [DogmaAttributeId.CapacitorNeed] = 1,
        };
        Dictionary<CombatSkill, int> levels = new()
        {
            [required] = 3,
            [CombatSkill.ControlledBursts] = 4,
            [CombatSkill.SmallHybridTurret] = 5,
            [CombatSkill.SmallProjectileTurret] = 5,
            [CombatSkill.SmallBeamLaserSpecialization] = 5,
            [CombatSkill.SmallPulseLaserSpecialization] = 5,
        };
        var result = WeaponRules.ModuleAttributes(
            new(Pulse, CombatDefinitions.EnergyWeaponGroup, attributes, levels, 20, null, 0, 10, 10, -20)
        );
        Assert.Equal(multiplier, result[DogmaAttributeId.DamageMultiplier], 8);
        Assert.Equal(capacitor, result[DogmaAttributeId.CapacitorNeed], 8);
    }

    [Fact]
    public void AdvancedCrystalRequiresItsSpecializationEvenInAnEligibleFactionGun()
    {
        using Fixture f = WithTiers(13795, Gleam);
        long gun = f.Fit(13795);
        long stack = f.Grant(Gleam, 3);
        Assert.Throws<InvalidDataException>(() => Ammo(f).Load(f.PilotId, f.ShipId, [gun], [stack], f.StationId, f.Clock.Now));
        f.Train((int)CombatSkill.SmallBeamLaserSpecialization, 1);
        Ammo(f).Load(f.PilotId, f.ShipId, [gun], [stack], f.StationId, f.Clock.Now);
        f.Clock.Milliseconds++;
        var npc = Space(f);
        f.Train((int)CombatSkill.SmallBeamLaserSpecialization, 0);
        Assert.Throws<InvalidDataException>(() => Combat(f).Fire(f.PilotId, gun, npc.Id, SpacePosition.Zero, f.Clock.Now, .5));
        Assert.Equal(1, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
        Assert.Equal(0, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Damage);
    }

    [Theory]
    [InlineData("effect")]
    [InlineData("mutation")]
    [InlineData("restriction")]
    public void UnsupportedStoredLaserProfilesCannotWearCrystalsOrDamageTargets(string reason)
    {
        using Fixture f = WithTiers(Pulse, FactionRadio);
        long gun = f.Fit(Pulse);
        Load(f, gun, FactionRadio);
        f.Clock.Milliseconds++;
        var npc = Space(f);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            if (reason == "mutation")
            {
                catalog.Database.ExecuteSqlRaw(
                    "UPDATE SourceRecord SET Data=json_set(Data,'$.metaGroupID',15) WHERE Dataset='client/types' AND Key='450'"
                );
            }
            else if (reason == "restriction")
            {
                catalog.Database.ExecuteSqlRaw("INSERT INTO TypeAttribute VALUES (450,1298,1)");
            }
            else
            {
                catalog.Database.ExecuteSqlRaw(
                    "UPDATE SourceRecord SET Data=json_insert(Data,'$.dogmaEffects[#]',json_object('effectID',34)) "
                        + "WHERE Dataset='client/typedogma' AND Key='450'"
                );
            }
        }
        Assert.Throws<InvalidDataException>(() =>
            Combat(f).Fire(f.PilotId, gun, npc.Id, SpacePosition.Zero, f.Clock.Now, .5, crystalRoll: 0)
        );
        Assert.Equal(0, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Damage);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ShipEnergy>());
        Assert.Equal(0, saved.Set<WorldNpc>().Single(n => n.Id == npc.Id).StructureDamage);
    }
}
