using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Repair;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Repair;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class AncillaryRemoteRepairTests
{
    private const int Paste = 28668;
    private const int SmallShield = 41480;
    private const int SmallArmor = 41476;
    private const int Navy25 = 33330;

    private static Fixture Prepare(int hull = 601)
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadAncillaryRemoteRepairs(f.CatalogPath, hull);
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            "INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142','{{\"securityStatus\":0}}')"
        );
        foreach (
            var attribute in new[]
            {
                DogmaAttributeId.HighSlots,
                DogmaAttributeId.CpuOutput,
                DogmaAttributeId.PowerOutput,
                DogmaAttributeId.CapacitorCapacity,
                DogmaAttributeId.ShieldCapacity,
                DogmaAttributeId.ArmorHitPoints,
            }
        )
        {
            double value = attribute == DogmaAttributeId.HighSlots ? 8 : 1000000;
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)attribute},{value})");
        }
        return f;
    }

    private static long Target(Fixture f) => f.Db.Store.FindPilot(1000002)!.ShipId;

    private static IPilotAmmunition Ammo(Fixture f) => f.Db.Services.GetRequiredService<IPilotAmmunition>();

    private static IPilotRemoteRepairing Store(Fixture f) => f.Db.Services.GetRequiredService<IPilotRemoteRepairing>();

    private static ShipHealth Health(Fixture f) =>
        f.Db.Services.GetRequiredService<IPilotHealth>().Read(f.Db.Store.FindPilot(1000002)!.PilotId, f.Clock.Now);

    private static void Space(Fixture f, double capacitor = 100000)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        SpacePosition origin = new(pilot.X + pilot.Flight.StationRadius + 50000, pilot.Y, pilot.Z);
        f.Db.Store.SetPosition(f.ShipId, origin);
        f.Db.Store.SetPosition(Target(f), origin + new SpacePosition(1000, 0, 0));
        using WorldDbContext world = new(f.Db.Path);
        world.Add(
            new ShipEnergy
            {
                ShipId = f.ShipId,
                Charge = capacitor,
                UpdatedAt = f.Clock.Now,
            }
        );
        world.Add(
            new ShipHealthState
            {
                ShipId = Target(f),
                ShieldCharge = 0,
                ArmorDamage = 500000,
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

    private static RemoteRepairActivation Start(Fixture f, long module, DogmaEffect effect) =>
        Store(f).Start(f.PilotId, module, effect, Target(f), new HashSet<long> { Target(f) }, f.Clock.Now);

    [Theory]
    [InlineData(41480, Navy25, DogmaEffect.AncillaryRemoteShieldBoost)]
    [InlineData(41481, 33332, DogmaEffect.AncillaryRemoteShieldBoost)]
    [InlineData(41482, 31990, DogmaEffect.AncillaryRemoteShieldBoost)]
    [InlineData(41483, 41490, DogmaEffect.AncillaryRemoteShieldBoost)]
    [InlineData(41476, Paste, DogmaEffect.AncillaryRemoteArmorRepair)]
    [InlineData(41477, Paste, DogmaEffect.AncillaryRemoteArmorRepair)]
    [InlineData(41478, Paste, DogmaEffect.AncillaryRemoteArmorRepair)]
    [InlineData(41479, Paste, DogmaEffect.AncillaryRemoteArmorRepair)]
    public void EveryStaticProfilePaysRealFuelAndRepairsAtItsOwnPhase(int type, int fuel, DogmaEffect effect)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        var magazine = Load(f, module, fuel);
        Space(f);
        var before = Health(f);
        var result = Start(f, module, effect);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var raw = catalog.FittingAttributes(type);
        bool shield = result.Cycle.Module.Kind == RemoteRepairKind.Shield;
        double expected = raw[shield ? DogmaAttributeId.ShieldBonus : DogmaAttributeId.ArmorRepairAmount] * (shield ? 1 : 3);
        Assert.Equal(expected, result.Cycle.Module.Amount, 8);
        Assert.Equal(shield ? 0 : raw[DogmaAttributeId.CapacitorNeed] * .75, result.Cycle.Module.CapacitorNeed, 8);
        Assert.Equal(100000 - result.Cycle.Module.CapacitorNeed, result.Capacitor, 8);
        Assert.Equal(magazine.Quantity - (int)raw[DogmaAttributeId.ChargeRate], result.Magazine!.Quantity);
        var immediate = Health(f);
        Assert.Equal(shield ? expected : before.Remaining.Shield, immediate.Remaining.Shield, 8);
        Assert.Equal(before.Remaining.Armor, immediate.Remaining.Armor, 8);
        f.Clock.Milliseconds += (long)result.Cycle.Module.DurationMilliseconds - 1;
        Store(f).Complete(f.PilotId, module, f.Clock.Now);
        Assert.Equal(before.Remaining.Armor, Health(f).Remaining.Armor, 8);
        f.Clock.Milliseconds++;
        Store(f).Complete(f.PilotId, module, f.Clock.Now);
        var after = Health(f);
        Assert.Equal(before.Remaining.Armor + (shield ? 0 : expected), after.Remaining.Armor, 8);
        Store(f).Complete(f.PilotId, module, f.Clock.Now);
        Assert.Equal(after.Remaining.Armor, Health(f).Remaining.Armor);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<WorldRemoteRepair>());
        Assert.Equal(result.Cycle.CompletesAt, world.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }

    [Theory]
    [InlineData(0, 145)]
    [InlineData(1, 217.5)]
    [InlineData(2, 290)]
    [InlineData(3, 362.5)]
    [InlineData(4, 435)]
    public void PartialRemotePasteScalesOnlyTheExtraBonusAndAlwaysPaysCapacitor(int quantity, double amount)
    {
        using Fixture f = Prepare();
        long module = f.Fit(41477);
        if (quantity > 0)
        {
            Load(f, module, Paste, quantity);
        }
        Space(f);
        var result = Start(f, module, DogmaEffect.AncillaryRemoteArmorRepair);
        Assert.Equal(amount, result.Cycle.Module.Amount);
        Assert.Equal(155 * .75, result.Cycle.Module.CapacitorNeed);
        Assert.Equal(quantity == 0 ? null : 0, result.Magazine?.Quantity);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<OwnedItem>().Where(i => i.LocationId == module));
    }

    [Theory]
    [InlineData("capacitor")]
    [InlineData("lock")]
    [InlineData("owner")]
    [InlineData("offline")]
    [InlineData("capacity")]
    [InlineData("effect")]
    [InlineData("safety")]
    [InlineData("range")]
    public void RejectionPreservesFuelHealthResourcesAndPaidDeadline(string rejection)
    {
        using Fixture f = Prepare();
        long module = f.Fit(SmallArmor);
        var magazine = Load(f, module, Paste);
        Space(f, rejection == "capacitor" ? 0 : 100000);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var loaded = world.Set<OwnedItem>().Single(i => i.LocationId == module);
            if (rejection == "owner")
            {
                loaded.OwnerId = world.Pilots.Single(p => p.Id != f.PilotId).Id;
            }
            if (rejection == "capacity")
            {
                loaded.Quantity = 9;
            }
            if (rejection == "offline")
            {
                world.Set<OwnedItem>().Find(module)!.Online = false;
            }
            if (rejection == "safety")
            {
                CrimewatchPersistence.State(world, f.Db.Store.FindPilot(1000002)!.PilotId).SuspectUntil =
                    f.Clock.Now + TimeSpan.FromMinutes(15).Ticks;
            }
            world.SaveChanges();
        }
        if (rejection == "range")
        {
            f.Db.Store.SetPosition(Target(f), new SpacePosition(1e15, 0, 0));
        }
        if (rejection == "safety")
        {
            using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
            catalog.Database.ExecuteSqlRaw(
                "UPDATE SourceRecord SET Data='{{\"securityStatus\":1}}' WHERE Dataset='client/systems' AND Key='30000142'"
            );
        }
        var effect = rejection == "effect" ? DogmaEffect.RemoteArmorRepair : DogmaEffect.AncillaryRemoteArmorRepair;
        Assert.Throws<InvalidDataException>(() =>
            Store(f).Start(f.PilotId, module, effect, Target(f), rejection == "lock" ? [] : new HashSet<long> { Target(f) }, f.Clock.Now)
        );
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(rejection == "capacity" ? 9 : magazine.Quantity, verify.Set<OwnedItem>().Single(i => i.LocationId == module).Quantity);
        Assert.Equal(rejection == "capacitor" ? 0 : 100000, verify.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Equal(500000, verify.Set<ShipHealthState>().Find(Target(f))!.ArmorDamage);
        Assert.Empty(verify.Set<WorldRemoteRepair>());
        Assert.Equal(magazine.ReadyAt, verify.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }

    [Theory]
    [InlineData(582, SmallShield, 3330, DogmaEffect.AncillaryRemoteShieldBoost)]
    [InlineData(590, SmallArmor, 3331, DogmaEffect.AncillaryRemoteArmorRepair)]
    public void RealBudgetLogisticsHullBonusesReachAncillaryProfiles(int hull, int type, int skill, DogmaEffect effect)
    {
        using Fixture f = Prepare(hull);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(f.ShipId)!.TypeId = hull;
            world.SaveChanges();
        }
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            Assert.True(HullCatalog.CanAssemble(catalog, catalog.FittingType(hull)!, (int)InventoryCategory.Ship));
        }
        f.Train(skill, 5);
        long module = f.Fit(type);
        Load(f, module, type == SmallShield ? Navy25 : Paste);
        Space(f);
        var result = Start(f, module, effect);
        Assert.Equal(type == SmallShield ? 125 * 1.5 : 37 * 1.5 * 3, result.Cycle.Module.Amount, 8);
        Assert.Equal(type == SmallShield ? 6500 * 4 : 2500 * 7, result.Cycle.Module.Falloff, 8);
        Assert.Equal(type == SmallShield ? 4000 : 7000 * 1.5, result.Cycle.Module.Optimal, 8);
    }

    [Fact]
    public async Task RestartRetainsSpentFuelAndCooldownButNeverReplaysConnectionOwnedArmor()
    {
        using Fixture f = Prepare();
        long module = f.Fit(SmallArmor);
        Load(f, module, Paste);
        Space(f);
        RemoteRepairActivation result;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            result = Start(f, module, DogmaEffect.AncillaryRemoteArmorRepair);
            await live.FlushAsync();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        f.Clock.Milliseconds += (long)result.Cycle.Module.DurationMilliseconds;
        Store(f).Complete(f.PilotId, module, f.Clock.Now);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(7, world.Set<OwnedItem>().Single(i => i.LocationId == module).Quantity);
        Assert.Equal(100000 - 38 * .75, world.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Equal(result.Cycle.CompletesAt, world.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Empty(world.Set<WorldRemoteRepair>());
        Assert.Equal(500000, world.Set<ShipHealthState>().Find(Target(f))!.ArmorDamage);
    }

    [Fact]
    public void RemoteAndLocalFuelDefinitionsUseTheirOwnCatalogGroupField()
    {
        Dictionary<DogmaAttributeId, double> module = new() { [DogmaAttributeId.ChargeGroup1] = 87 };
        Dictionary<DogmaAttributeId, double> charge = [];
        Assert.True(AncillaryRepairRules.Compatible(AncillaryRepairKind.Shield, 87, module, charge, DogmaAttributeId.ChargeGroup1));
        Assert.False(AncillaryRepairRules.Compatible(AncillaryRepairKind.Shield, 87, module, charge));
        module.Clear();
        module[DogmaAttributeId.ChargeGroup2] = 87;
        Assert.True(AncillaryRepairRules.Compatible(AncillaryRepairKind.Shield, 87, module, charge));
        Assert.False(AncillaryRepairRules.Compatible(AncillaryRepairKind.Shield, 87, module, charge, DogmaAttributeId.ChargeGroup1));
    }

    [Fact]
    public void OneOfEachAncillaryGroupFitsButDuplicateGroupsAndWrongSizeFuelAreRejected()
    {
        using Fixture f = Prepare();
        f.Fit(SmallArmor);
        long shield = f.Fit(41481);
        Assert.Throws<InvalidDataException>(() => f.Fit(SmallArmor));
        Assert.Throws<InvalidDataException>(() => f.Fit(SmallShield));
        long fuel = f.Grant(Navy25, 20);
        Assert.Throws<InvalidDataException>(() => Ammo(f).Load(f.PilotId, f.ShipId, [shield], [fuel], f.StationId, f.Clock.Now));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(20, world.Set<OwnedItem>().Find(fuel)!.Quantity);
        Assert.Empty(world.Set<OwnedItem>().Where(i => i.LocationId == shield));
        Assert.Null(world.Set<ModuleCooldown>().Find(shield));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void EmptyRemoteShieldCostUsesEarnedEmissionSkill(int level)
    {
        using Fixture f = Prepare();
        long module = f.Fit(SmallShield);
        f.Train((int)RemoteRepairSkill.ShieldEmissionSystems, level);
        Space(f);
        var result = Start(f, module, DogmaEffect.AncillaryRemoteShieldBoost);
        Assert.Equal(135 * (1 - .05 * level), result.Cycle.Module.CapacitorNeed, 8);
        Assert.Equal(125, result.Cycle.Module.Amount);
        Assert.Null(result.Magazine);
    }

    [Fact]
    public void PaidArmorUsesCompletionDistanceAndRecipientImpedanceAfterThePasteBonus()
    {
        using Fixture f = Prepare();
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            int impedance = (int)DogmaAttributeId.RemoteRepairImpedance;
            string attributeName = nameof(DogmaAttributeId.RemoteRepairImpedance);
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO DogmaAttribute VALUES ({impedance},{attributeName})");
            catalog.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)DogmaAttributeId.RemoteRepairImpedance},0.5)"
            );
        }
        long module = f.Fit(SmallArmor);
        Load(f, module, Paste, 1);
        Space(f);
        var result = Start(f, module, DogmaEffect.AncillaryRemoteArmorRepair);
        var source = f.Db.Store.FindPilot(1000001)!;
        double radii;
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            radii = 2 * catalog.FittingType(601)!.Radius!.Value;
        }
        double distance = radii + result.Cycle.Module.Optimal + result.Cycle.Module.Falloff;
        f.Db.Store.SetPosition(Target(f), source.Position! + new SpacePosition(distance, 0, 0));
        f.Clock.Milliseconds += (long)result.Cycle.Module.DurationMilliseconds;
        Store(f).Complete(f.PilotId, module, f.Clock.Now);
        var after = Health(f);
        Assert.Equal(after.Defense.Armor / 2 + 37 * 3 * .5 * .5, after.Remaining.Armor, 8);
    }
}
