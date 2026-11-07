using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Repair;
using Eden.Data.World.Skills;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Repair;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Repair;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class RemoteRepairTests
{
    private static Fixture Prepare()
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadRemoteRepairs(f.CatalogPath);
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
            }
        )
        {
            double value = attribute == DogmaAttributeId.HighSlots ? 8 : 1000000;
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)attribute},{value})");
        }
        return f;
    }

    private static long Target(Fixture f) => f.Db.Store.FindPilot(1000002)!.ShipId;

    private static PilotRemoteRepairStore Store(Fixture f) => new(new(f.Db.Path), f.Db.Services.GetRequiredService<IRemoteRepairEvents>());

    private static void Space(Fixture f)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        Eden.Gameplay.Flight.SpacePosition origin = new(pilot.X + pilot.Flight.StationRadius + 50000, pilot.Y, pilot.Z);
        f.Db.Store.SetPosition(f.ShipId, origin);
        f.Db.Store.SetPosition(Target(f), origin + new Eden.Gameplay.Flight.SpacePosition(1000, 0, 0));
    }

    private static ShipHealth Health(Fixture f) =>
        f.Db.Services.GetRequiredService<IPilotHealth>().Read(f.Db.Store.FindPilot(1000002)!.PilotId, f.Clock.Now);

    private static void Damage(Fixture f)
    {
        using WorldDbContext world = new(f.Db.Path);
        world.Add(
            new ShipHealthState
            {
                ShipId = Target(f),
                ShieldCharge = 0,
                ArmorDamage = 100,
                UpdatedAt = f.Clock.Now,
            }
        );
        world.SaveChanges();
    }

    private static RemoteRepairActivation Start(Fixture f, long module, DogmaEffect effect) =>
        Store(f).Start(f.PilotId, module, effect, Target(f), new HashSet<long> { Target(f) }, f.Clock.Now);

    [Fact]
    public void AllUnrestrictedCatalogProfilesAreAdmittedAndAbyssalProfilesAreRejected()
    {
        using Fixture f = Prepare();
        using CatalogDbContext catalog = new(f.CatalogPath);
        var types = catalog
            .Set<ItemType>()
            .Where(t => t.GroupId == RemoteRepairRules.ShieldGroup || t.GroupId == RemoteRepairRules.ArmorGroup)
            .ToArray();
        int shield = 0;
        int armor = 0;
        foreach (var type in types)
        {
            bool abyssal = catalog.Require(CatalogData.Client.Types, (long)type.Id).MetaGroupID == 15;
            Assert.Equal(!abyssal, RemoteRepairCatalog.Supported(catalog, type.Id, type.GroupId));
            Assert.Equal(!abyssal, FittingCatalog.Definition(catalog, type.Id) is not null);
            if (!abyssal)
            {
                if (type.GroupId == RemoteRepairRules.ShieldGroup)
                {
                    shield++;
                }
                else
                {
                    armor++;
                }
            }
        }
        Assert.Equal(44, shield);
        Assert.Equal(45, armor);
    }

    [Theory]
    [InlineData(3586, DogmaEffect.RemoteShieldBoost)]
    [InlineData(3588, DogmaEffect.RemoteShieldBoost)]
    [InlineData(19141, DogmaEffect.RemoteShieldBoost)]
    [InlineData(11355, DogmaEffect.RemoteArmorRepair)]
    [InlineData(26913, DogmaEffect.RemoteArmorRepair)]
    [InlineData(18987, DogmaEffect.RemoteArmorRepair)]
    public void CatalogPaymentAndRepairTimingAreExactAndCompletionsCannotReplay(int type, DogmaEffect effect)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        Space(f);
        Damage(f);
        var before = Health(f);
        var result = Start(f, module, effect);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var attributes = catalog.FittingAttributes(type);
        Assert.Equal(attributes[DogmaAttributeId.CapacitorNeed] * .75, result.Cycle.Module.CapacitorNeed, 7);
        Assert.Equal(attributes[DogmaAttributeId.Duration], result.Cycle.Module.DurationMilliseconds);
        var immediate = Health(f);
        if (effect == DogmaEffect.RemoteShieldBoost)
        {
            Assert.Equal(Math.Min(before.Defense.Shield, result.Cycle.Module.Amount), immediate.Remaining.Shield, 6);
            Assert.Equal(before.Remaining.Armor, immediate.Remaining.Armor);
        }
        else
        {
            Assert.Equal(before.Remaining.Armor, immediate.Remaining.Armor);
        }
        f.Clock.Seconds += (long)Math.Ceiling(result.Cycle.Module.DurationMilliseconds / 1000);
        Store(f).Complete(f.PilotId, module, f.Clock.Now);
        double armor = Health(f).Remaining.Armor;
        if (effect == DogmaEffect.RemoteArmorRepair)
        {
            Assert.Equal(Math.Min(before.Defense.Armor, before.Remaining.Armor + result.Cycle.Module.Amount), armor, 6);
        }
        Store(f).Complete(f.PilotId, module, f.Clock.Now);
        Assert.Equal(armor, Health(f).Remaining.Armor);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<WorldRemoteRepair>());
        Assert.Equal(result.Cycle.CompletesAt, world.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }

    [Fact]
    public void MissingLockAndUnsafeAssistanceRejectWithoutPaymentOrHealing()
    {
        using Fixture f = Prepare();
        long module = f.Fit(3586);
        Space(f);
        Damage(f);
        Assert.Throws<InvalidDataException>(() =>
            Store(f).Start(f.PilotId, module, DogmaEffect.RemoteShieldBoost, Target(f), new HashSet<long>(), f.Clock.Now)
        );
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "UPDATE SourceRecord SET Data='{{\"securityStatus\":1}}' WHERE Dataset='client/systems' AND Key='30000142'"
            );
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            var state = CrimewatchPersistence.State(world, f.Db.Store.FindPilot(1000002)!.PilotId);
            state.SuspectUntil = f.Clock.Now + TimeSpan.FromMinutes(15).Ticks;
            world.SaveChanges();
        }
        var before = Health(f);
        Assert.Throws<InvalidDataException>(() => Start(f, module, DogmaEffect.RemoteShieldBoost));
        Assert.Equal(before, Health(f));
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Empty(verify.Set<WorldRemoteRepair>());
        Assert.Null(verify.Set<ModuleCooldown>().Find(module));
        Assert.Null(verify.Set<ShipEnergy>().Find(f.ShipId));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5000, 1)]
    [InlineData(15000, .5)]
    [InlineData(25000, .0625)]
    public void FalloffAndTargetImpedanceScaleRepairAmount(double distance, double fraction)
    {
        RemoteRepairModule module = new(1, 3586, RemoteRepairKind.Shield, DogmaEffect.RemoteShieldBoost, 5000, 20, 100, 5000, 10000);
        Assert.Equal(100 * fraction, RemoteRepairRules.Amount(module, distance, 1), 9);
        Assert.Equal(50 * fraction, RemoteRepairRules.Amount(module, distance, .5), 9);
        Assert.Equal(0, RemoteRepairRules.Amount(module, distance, 0));
    }

    [Fact]
    public void RestartCancelsPendingArmorWithoutRefundOrOfflineRepair()
    {
        using Fixture f = Prepare();
        long module = f.Fit(11355);
        Space(f);
        Damage(f);
        var before = Health(f);
        var cycle = Start(f, module, DogmaEffect.RemoteArmorRepair);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        f.Clock.Seconds += 30;
        Store(f).Complete(f.PilotId, module, f.Clock.Now);
        Assert.Equal(before.Remaining.Armor, Health(f).Remaining.Armor);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<WorldRemoteRepair>());
        Assert.Equal(cycle.Capacitor, world.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Equal(cycle.Cycle.CompletesAt, world.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Equal(WorldMigration.CurrentVersion, world.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("offline")]
    [InlineData("skill")]
    [InlineData("capacitor")]
    [InlineData("different-system")]
    [InlineData("destroyed")]
    public void RejectionsLeaveHealthPaymentCooldownAndTimersUnchanged(string rejection)
    {
        using Fixture f = Prepare();
        long module = f.Fit(3586);
        Space(f);
        Damage(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            switch (rejection)
            {
                case "owner":
                    world.Set<Eden.Data.World.Inventory.OwnedItem>().Find(module)!.OwnerId = f.Db.Store.FindPilot(1000002)!.PilotId;
                    break;
                case "offline":
                    world.Set<Eden.Data.World.Inventory.OwnedItem>().Find(module)!.Online = false;
                    break;
                case "skill":
                    world.Set<Eden.Data.World.Skills.PilotSkill>().Find(f.PilotId, (int)RemoteRepairSkill.ShieldEmissionSystems)!.Points =
                        0;
                    break;
                case "capacitor":
                    world.Add(
                        new ShipEnergy
                        {
                            ShipId = f.ShipId,
                            Charge = 0,
                            UpdatedAt = f.Clock.Now,
                        }
                    );
                    break;
                case "different-system":
                    world.Positions.Find(Target(f))!.SystemId = 30000144;
                    break;
                case "destroyed":
                    world.Set<ShipHealthState>().Find(Target(f))!.StructureDamage = 1000000;
                    break;
            }
            world.SaveChanges();
        }
        using WorldDbContext before = new(f.Db.Path);
        double? capacitor = before.Set<ShipEnergy>().Find(f.ShipId)?.Charge;
        double armor = before.Set<ShipHealthState>().Find(Target(f))!.ArmorDamage;
        Assert.Throws<InvalidDataException>(() => Start(f, module, DogmaEffect.RemoteShieldBoost));
        using WorldDbContext after = new(f.Db.Path);
        Assert.Equal(capacitor, after.Set<ShipEnergy>().Find(f.ShipId)?.Charge);
        Assert.Equal(armor, after.Set<ShipHealthState>().Find(Target(f))!.ArmorDamage);
        Assert.Empty(after.Set<WorldRemoteRepair>());
        Assert.Null(after.Set<ModuleCooldown>().Find(module));
        Assert.Empty(after.Set<SavedCrimewatch>());
    }

    [Fact]
    public void AssistanceInheritsRemainingDeadlinesWithoutMutualRepairKeepingTimersAlive()
    {
        long now = TimeSpan.FromHours(1).Ticks;
        var target = CrimewatchTimers.Empty with
        {
            Weapons = new(0, now + TimeSpan.FromSeconds(10).Ticks),
            Pvp = new(0, now + TimeSpan.FromMinutes(2).Ticks),
        };
        var decision = AssistanceRules.Decide(new(0, ShipSafetyLevel.Full, false, CrimewatchFlag.None, false, false));
        var helper = AssistanceRules.Apply(CrimewatchTimers.Empty, target, decision, now, now + TimeSpan.FromSeconds(5).Ticks);
        Assert.Equal(target.Weapons, helper.Weapons);
        Assert.Equal(target.Pvp, helper.Pvp);
        Assert.Equal(target, AssistanceRules.Apply(target, helper, decision, now, now + TimeSpan.FromSeconds(20).Ticks));
        Assert.Equal(
            CrimewatchFlag.Suspect,
            AssistanceRules.Decide(new(.1, ShipSafetyLevel.Partial, false, CrimewatchFlag.Suspect, false, false)).Flag
        );
        Assert.Throws<InvalidDataException>(() =>
            AssistanceRules.Decide(new(.1, ShipSafetyLevel.Full, false, CrimewatchFlag.Suspect, false, false))
        );
        Assert.Equal(
            CrimewatchFlag.None,
            AssistanceRules.Decide(new(.1, ShipSafetyLevel.Full, false, CrimewatchFlag.None, true, false)).Flag
        );
        Assert.Equal(
            CrimewatchFlag.Suspect,
            AssistanceRules.Decide(new(.1, ShipSafetyLevel.Partial, false, CrimewatchFlag.None, true, false, true)).Flag
        );
    }

    [Fact]
    public void OspreyWorkSkillsHullBonusesAndMediumFittingAdjustmentApplyOnce()
    {
        using Fixture f = Prepare();
        long module = f.Fit(3596);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == module);
        f.Train(3334, 4);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(f.ShipId)!.TypeId = 620;
            world.SaveChanges();
        }
        using CatalogDbContext catalog = new(f.CatalogPath);
        var raw = catalog.FittingAttributes(3596);
        var actual = f.Fitting.ModuleAttributes(f.PilotId, item);
        Assert.Equal(raw[DogmaAttributeId.CapacitorNeed] * .75 * .8, actual[DogmaAttributeId.CapacitorNeed], 6);
        Assert.Equal(raw[DogmaAttributeId.ShieldBonus] * 1.5, actual[DogmaAttributeId.ShieldBonus], 6);
        Assert.Equal(raw[DogmaAttributeId.MaxRange] * 5.3, actual[DogmaAttributeId.MaxRange], 6);
        Assert.Equal(raw[DogmaAttributeId.FalloffEffectiveness] * 5.3, actual[DogmaAttributeId.FalloffEffectiveness], 6);
        Assert.Equal(raw[DogmaAttributeId.Power], actual[DogmaAttributeId.Power]);
        Assert.Contains(f.Fitting.SkillBonuses(f.PilotId), b => b.SkillId == (int)RemoteRepairSkill.ShieldEmissionSystems);
    }

    [Theory]
    [InlineData(582)]
    [InlineData(590)]
    [InlineData(625)]
    public void LogisticsFrigatesAndArmorCruiserApplyTheirEarnedAndRoleBonuses(int hull)
    {
        using Fixture f = Prepare();
        Eden.Protocol.Tests.Persistence.SmallHullBonusTests.VerifyBonuses(f, hull);
    }
}
