using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Repair;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Content;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Repair;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Repair;

public sealed partial class AncillaryRepairTests
{
    private const int Paste = 28668;
    private const int SmallShield = 32774;
    private const int SmallArmor = 33076;
    private const int Navy25 = 33330;

    private static Fixture Prepare(int hull = 601)
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadAncillaryRepairs(f.CatalogPath, hull);
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        foreach (
            var attribute in new[]
            {
                DogmaAttributeId.LowSlots,
                DogmaAttributeId.MediumSlots,
                DogmaAttributeId.CpuOutput,
                DogmaAttributeId.PowerOutput,
                DogmaAttributeId.ShieldCapacity,
                DogmaAttributeId.ArmorHitPoints,
                DogmaAttributeId.CapacitorCapacity,
            }
        )
        {
            double value = attribute is DogmaAttributeId.LowSlots or DogmaAttributeId.MediumSlots ? 8 : 1000000;
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)attribute},{value})");
        }
        return f;
    }

    private static IPilotAmmunition Ammo(Fixture f) => f.Db.Services.GetRequiredService<IPilotAmmunition>();

    private static IPilotShieldBoosting Shields(Fixture f) => f.Db.Services.GetRequiredService<IPilotShieldBoosting>();

    private static IPilotLocalRepairing Armor(Fixture f) => f.Db.Services.GetRequiredService<IPilotLocalRepairing>();

    private static void SpaceAndDamage(Fixture f, double capacitor = 10000)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        f.Db.Store.SetPosition(f.ShipId, new(pilot.X + pilot.Flight.StationRadius + 50000, pilot.Y, pilot.Z));
        var values = f.Fitting.ShipAttributes(f.PilotId);
        long ship = f.ShipId;
        using WorldDbContext world = new(f.Db.Path);
        world.Add(
            new ShipEnergy
            {
                ShipId = ship,
                Charge = capacitor,
                UpdatedAt = f.Clock.Now,
            }
        );
        world.Add(
            new ShipHealthState
            {
                ShipId = ship,
                ShieldCharge = 0,
                ArmorDamage = values[DogmaAttributeId.ArmorHitPoints] / 2,
                UpdatedAt = f.Clock.Now,
            }
        );
        world.SaveChanges();
    }

    private static Magazine Load(Fixture f, long module, int fuel, int quantity = 200)
    {
        long stack = f.Grant(fuel, quantity);
        var result = Ammo(f).Load(f.PilotId, f.ShipId, [module], [stack], f.StationId, f.Clock.Now);
        Assert.Equal(60000, result.ReloadMilliseconds);
        f.Clock.Milliseconds += 60000;
        return Assert.Single(result.Magazines).Current!;
    }

    [Theory]
    [InlineData(4391, 31990)]
    [InlineData(32772, 33332)]
    [InlineData(32774, 33330)]
    [InlineData(32780, 32006)]
    [InlineData(41504, 41490)]
    [InlineData(88713, 41490)]
    [InlineData(88714, 32006)]
    [InlineData(88715, 31990)]
    [InlineData(88716, 33332)]
    [InlineData(88717, 33330)]
    [InlineData(33076, Paste)]
    [InlineData(33101, Paste)]
    [InlineData(33103, Paste)]
    [InlineData(41503, Paste)]
    [InlineData(88718, Paste)]
    [InlineData(88719, Paste)]
    [InlineData(88720, Paste)]
    [InlineData(88721, Paste)]
    public void AllStaticTiersConsumeRealFuelAndCommitTheirPublishedRepairAmount(int type, int fuel)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        var magazine = Load(f, module, fuel);
        SpaceAndDamage(f);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var raw = catalog.FittingAttributes(type);
        bool shield = catalog.FittingType(type)!.GroupId == AncillaryRepairRules.ShieldGroup;
        int used = checked((int)raw[DogmaAttributeId.ChargeRate]);
        if (shield)
        {
            var result = Shields(f).Boost(f.PilotId, module, f.Clock.Now, ancillary: true);
            Assert.True(result.Booster.Ancillary);
            Assert.Equal(0, result.Booster.CapacitorNeed);
            Assert.Equal(10000, result.Capacitor);
            Assert.Equal(raw[DogmaAttributeId.ShieldBonus], result.Restored, 8);
            Assert.Equal(magazine.Quantity - used, result.Magazine!.Quantity);
        }
        else
        {
            var result = Armor(f).Start(f.PilotId, module, f.Clock.Now, ancillary: true);
            Assert.True(result.Schedule.Ancillary);
            using WorldDbContext saved = new(f.Db.Path);
            Assert.Equal(raw[DogmaAttributeId.ArmorRepairAmount] * 3, saved.Set<PendingLocalRepair>().Find(module)!.Amount, 8);
            Assert.Equal(10000 - raw[DogmaAttributeId.CapacitorNeed], result.Capacitor, 8);
            Assert.Equal(magazine.Quantity - used, result.Magazine!.Quantity);
            var before = Armor(f).Complete(f.PilotId, f.ShipId, result.Schedule.CompletesAt - 1);
            var after = Armor(f).Complete(f.PilotId, f.ShipId, result.Schedule.CompletesAt);
            Assert.Equal(raw[DogmaAttributeId.ArmorRepairAmount] * 3, after.Remaining.Armor - before.Remaining.Armor, 8);
            Assert.Equal(after.Remaining.Armor, Armor(f).Complete(f.PilotId, f.ShipId, result.Schedule.CompletesAt).Remaining.Armor);
        }
        Assert.Throws<InvalidDataException>(() =>
            Ammo(f).Unload(f.PilotId, f.ShipId, [module], f.ShipId, (int)InventoryFlag.Cargo, null, f.Clock.Now)
        );
    }

    [Fact]
    public void StaticAdmissionRejectsAllEightMutationTemplatesAndPricedPasteSupportsBothConsumers()
    {
        using Fixture f = Prepare();
        PassiveCatalogFixture.LoadOverload(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var types = catalog.Set<ItemType>().Where(t => t.GroupId == 1156 || t.GroupId == 1199).ToArray();
        Assert.Equal(26, types.Length);
        int admitted = 0;
        foreach (var type in types)
        {
            bool supported = catalog.Require(CatalogData.Client.Types, (long)type.Id).MetaGroupID != 15;
            Assert.Equal(supported, AncillaryRepairCatalog.Supported(catalog, type.Id, type.GroupId));
            Assert.Equal(supported, FittingCatalog.Definition(catalog, type.Id) is not null);
            admitted += supported ? 1 : 0;
        }
        Assert.Equal(18, admitted);
        Assert.True(AmmunitionCatalog.Supported(catalog, Paste, AncillaryRepairRules.PasteGroup));
        var paste = ShipContentAudit.Read(catalog).Entries.Single(e => e.TypeId == Paste);
        Assert.True(paste.Admitted);
        Assert.Equal(ContentSupportState.Complete, paste.State);
        Assert.Contains(
            paste.Checks,
            c => c.Operation == ContentOperation.ConsumerCoverage && c.Implemented && c.Milestone == ShipContentMilestone.Overheating
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public void UnloadedShieldCostUsesEarnedCompensation(int level)
    {
        using Fixture f = Prepare();
        long module = f.Fit(SmallShield);
        f.Train((int)ShieldBoostSkill.Compensation, level);
        SpaceAndDamage(f);
        var result = Shields(f).Boost(f.PilotId, module, f.Clock.Now, ancillary: true);
        Assert.Equal(66 * (1 - .02 * level), result.Booster.CapacitorNeed, 8);
        Assert.Equal(10000 - 66 * (1 - .02 * level), result.Capacitor, 8);
        Assert.Equal(52, result.Restored);
        Assert.Null(result.Magazine);
    }

    [Theory]
    [InlineData(1, 310.5)]
    [InlineData(2, 414)]
    [InlineData(3, 517.5)]
    public void APartialMediumArmorMagazineScalesOnlyTheExtraBonusAndConsumesAllRemainingPaste(int quantity, double amount)
    {
        using Fixture f = Prepare();
        long module = f.Fit(33101);
        Load(f, module, Paste, quantity);
        SpaceAndDamage(f);
        var result = Armor(f).Start(f.PilotId, module, f.Clock.Now, ancillary: true);
        Assert.Equal(0, result.Magazine!.Quantity);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(amount, world.Set<PendingLocalRepair>().Find(module)!.Amount, 8);
        Assert.Equal(9840, result.Capacitor);
        Assert.Empty(world.Set<OwnedItem>().Where(i => i.LocationId == module));
    }

    [Theory]
    [InlineData("capacitor")]
    [InlineData("offline")]
    [InlineData("owner")]
    [InlineData("singleton")]
    [InlineData("capacity")]
    [InlineData("effect")]
    public void RejectedArmorActivationsLeaveFuelResourcesHealthAndCooldownUnchanged(string rejection)
    {
        using Fixture f = Prepare();
        long module = f.Fit(SmallArmor);
        var magazine = Load(f, module, Paste);
        SpaceAndDamage(f, rejection == "capacitor" ? 0 : 10000);
        long ship = f.ShipId;
        using (WorldDbContext world = new(f.Db.Path))
        {
            if (rejection == "offline")
            {
                world.Set<OwnedItem>().Find(module)!.Online = false;
            }
            var loaded = world.Set<OwnedItem>().Single(i => i.LocationId == module);
            if (rejection == "owner")
            {
                loaded.OwnerId = world.Pilots.Single(p => p.Id != f.PilotId).Id;
            }
            if (rejection == "singleton")
            {
                loaded.Singleton = true;
            }
            if (rejection == "capacity")
            {
                loaded.Quantity = 9;
            }
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => Armor(f).Start(f.PilotId, module, f.Clock.Now, ancillary: rejection != "effect"));
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Empty(verify.Set<PendingLocalRepair>());
        Assert.Equal(rejection == "capacity" ? 9 : magazine.Quantity, verify.Set<OwnedItem>().Single(i => i.LocationId == module).Quantity);
        Assert.Equal(rejection == "capacitor" ? 0 : 10000, verify.Set<ShipEnergy>().Find(ship)!.Charge);
        Assert.Equal(500000, verify.Set<ShipHealthState>().Find(ship)!.ArmorDamage);
        Assert.Equal(ModuleCooldownKind.Reload, verify.Set<ModuleCooldown>().Find(module)!.Kind);
        Assert.Equal(f.Clock.Now, verify.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }

    [Fact]
    public void OnlyOneAncillaryArmorRepairerFitsAndNormalArmorRepairCanStillShareTheHull()
    {
        using Fixture f = Prepare();
        f.Fit(SmallArmor);
        Assert.Throws<InvalidDataException>(() => f.Fit(SmallArmor));
        f.Fit(ArmorRepairDefinitions.SmallTechOne);
    }

    [Fact]
    public async Task CheckpointReopenRetainsSpentPasteAndExactlyOnePaidArmorCompletion()
    {
        using Fixture f = Prepare();
        long module = f.Fit(SmallArmor);
        Load(f, module, Paste);
        SpaceAndDamage(f);
        LocalRepairActivation result;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            result = Armor(f).Start(f.PilotId, module, f.Clock.Now, ancillary: true);
            await live.FlushAsync();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        var restored = Assert.Single(Armor(f).Read(f.PilotId, f.ShipId, f.Clock.Now));
        Assert.True(restored.Ancillary);
        Assert.Equal(result.Schedule, restored);
        using (WorldDbContext saved = new(f.Db.Path))
        {
            Assert.Equal(7, saved.Set<OwnedItem>().Single(i => i.LocationId == module).Quantity);
            Assert.Equal(9960, saved.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
            Assert.Equal(156, saved.Set<PendingLocalRepair>().Find(module)!.Amount);
        }
        var health = Armor(f).Complete(f.PilotId, f.ShipId, result.Schedule.CompletesAt);
        Assert.Equal(health.Defense.Armor / 2 + 156, health.Remaining.Armor, 8);
        Assert.Equal(health.Remaining.Armor, Armor(f).Complete(f.PilotId, f.ShipId, result.Schedule.CompletesAt).Remaining.Armor);
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Empty(verify.Set<PendingLocalRepair>());
        Assert.Equal(7, verify.Set<OwnedItem>().Single(i => i.LocationId == module).Quantity);
    }

    [Theory]
    [InlineData(263, 7)]
    [InlineData(Navy25, 9)]
    public void ChargeVolumeDeterminesBurstLengthWithoutUsingTheCapacitorInjectionAmount(int fuel, int capacity)
    {
        using Fixture f = Prepare();
        long module = f.Fit(SmallShield);
        var loaded = Load(f, module, fuel);
        Assert.Equal(capacity, loaded.Quantity);
        SpaceAndDamage(f, capacitor: 0);
        var result = Shields(f).Boost(f.PilotId, module, f.Clock.Now, ancillary: true);
        Assert.Equal(0, result.Capacitor);
        Assert.Equal(52, result.Restored);
        Assert.Equal(capacity - 1, result.Magazine!.Quantity);
    }

    [Fact]
    public void SmallerChargeSizesCannotExtendALargerShieldBoostersBurstAndFailedLoadsPreserveStacks()
    {
        using Fixture f = Prepare();
        long module = f.Fit(32772);
        long fuel = f.Grant(Navy25, 20);
        Assert.Throws<InvalidDataException>(() => Ammo(f).Load(f.PilotId, f.ShipId, [module], [fuel], f.StationId, f.Clock.Now));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(20, world.Set<OwnedItem>().Find(fuel)!.Quantity);
        Assert.Empty(world.Set<OwnedItem>().Where(i => i.LocationId == module));
        Assert.Null(world.Set<ModuleCooldown>().Find(module));
    }

    [Theory]
    [InlineData(594, SmallArmor, 3328)]
    [InlineData(598, SmallShield, 3329)]
    public void AdmittedHullRepairBonusesReachBothAncillaryFamiliesWithTheirActualFittingBudgets(int hull, int type, int skill)
    {
        using Fixture f = Prepare(hull);
        long ship = f.ShipId;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(ship)!.TypeId = hull;
            world.SaveChanges();
        }
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            Assert.True(HullCatalog.CanAssemble(catalog, catalog.FittingType(hull)!, (int)InventoryCategory.Ship));
        }
        f.Train(skill, 5);
        long module = f.Fit(type);
        Load(f, module, type == SmallArmor ? Paste : Navy25);
        SpaceAndDamage(f);
        if (type == SmallArmor)
        {
            Armor(f).Start(f.PilotId, module, f.Clock.Now, ancillary: true);
            using WorldDbContext saved = new(f.Db.Path);
            Assert.Equal(52 * 1.375 * 3, saved.Set<PendingLocalRepair>().Find(module)!.Amount, 8);
        }
        else
        {
            var result = Shields(f).Boost(f.PilotId, module, f.Clock.Now, ancillary: true);
            Assert.Equal(52 * 1.375, result.Restored, 8);
        }
    }
}
