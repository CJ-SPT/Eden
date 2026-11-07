using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Repair;
using Eden.Data.World.Skills;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Repair;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class RemoteRepairTests
{
    [Theory]
    [InlineData(3986)]
    [InlineData(4296)]
    [InlineData(4299)]
    [InlineData(27904)]
    [InlineData(27930)]
    [InlineData(27932)]
    [InlineData(27934)]
    [InlineData(41475)]
    public void EveryStaticHullRepairerPaysEarnedCostAndHealsOnlyOnceAtItsDeadline(int type)
    {
        using Fixture f = Prepare();
        using CatalogDbContext catalog = new(f.CatalogPath);
        Assert.True(RemoteRepairCatalog.Supported(catalog, type, RemoteRepairRules.HullGroup));
        Assert.NotNull(FittingCatalog.Definition(catalog, type));
        // Prepare explicitly raises corvette fitting budgets; capital hull admission is separate.
        long module = f.Fit(type);
        Space(f);
        Damage(f);
        var before = Health(f);
        var raw = catalog.FittingAttributes(type);
        var started = Start(f, module, DogmaEffect.RemoteHullRepair);
        bool capital = type is 27934 or 41475;
        Assert.Equal(raw[DogmaAttributeId.CapacitorNeed] * .75 * (capital ? .75 : 1), started.Cycle.Module.CapacitorNeed, 7);
        Assert.Equal(raw[DogmaAttributeId.Duration], started.Cycle.Module.DurationMilliseconds);
        Assert.Equal(raw[DogmaAttributeId.HullRepairAmount], started.Cycle.Module.Amount);
        Assert.Equal(RemoteRepairKind.Hull, started.Cycle.Module.Kind);
        Assert.Equal(before.Remaining.Structure, Health(f).Remaining.Structure);
        f.Clock.Milliseconds += (long)started.Cycle.Module.DurationMilliseconds - 1;
        Store(f).Complete(f.PilotId, module, f.Clock.Now);
        Assert.Equal(before.Remaining.Structure, Health(f).Remaining.Structure);
        f.Clock.Milliseconds++;
        Store(f).Complete(f.PilotId, module, f.Clock.Now);
        double expected = Math.Min(before.Defense.Structure, before.Remaining.Structure + raw[DogmaAttributeId.HullRepairAmount]);
        Assert.Equal(expected, Health(f).Remaining.Structure, 7);
        Assert.Equal(before.Remaining.Armor, Health(f).Remaining.Armor);
        Store(f).Complete(f.PilotId, module, f.Clock.Now);
        using WorldDbContext reopened = new(f.Db.Path);
        var saved = reopened.Set<ShipHealthState>().Find(Target(f))!;
        Assert.Equal(before.Defense.Structure - expected, saved.StructureDamage, 7);
        Assert.Equal(100, saved.ArmorDamage);
        Assert.Equal(started.Capacitor, reopened.Set<ShipEnergy>().Find(f.ShipId)!.Charge, 7);
        Assert.Equal(started.Cycle.CompletesAt, reopened.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Empty(reopened.Set<WorldRemoteRepair>());
    }

    [Theory]
    [InlineData(27932, 1, 0, .95)]
    [InlineData(27932, 3, 0, .85)]
    [InlineData(27932, 5, 0, .75)]
    [InlineData(27934, 5, 1, .7125)]
    [InlineData(27934, 5, 3, .6375)]
    [InlineData(27934, 5, 5, .5625)]
    public void HullRepairSkillsMultiplyOnlyTheirRequiredModuleSelectors(int type, int ordinary, int capital, double factor)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        f.Train((int)RemoteRepairSkill.RemoteHullRepairSystems, ordinary);
        f.Train((int)RemoteRepairSkill.CapitalRemoteHullRepairSystems, capital);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == module);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var raw = catalog.FittingAttributes(type);
        var actual = f.Fitting.ModuleAttributes(f.PilotId, item);
        Assert.Equal(raw[DogmaAttributeId.CapacitorNeed] * factor, actual[DogmaAttributeId.CapacitorNeed], 7);
        Assert.Equal(raw[DogmaAttributeId.HullRepairAmount], actual[DogmaAttributeId.HullRepairAmount]);
        Assert.Equal(raw[DogmaAttributeId.Duration], actual[DogmaAttributeId.Duration]);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("offline")]
    [InlineData("skill")]
    [InlineData("capacitor")]
    [InlineData("different-system")]
    [InlineData("destroyed")]
    [InlineData("self")]
    [InlineData("non-pilot")]
    [InlineData("missing-lock")]
    [InlineData("wrong-effect")]
    [InlineData("range")]
    [InlineData("disallow-assistance")]
    public void HullRepairRejectionsPreserveHealthCapacitorCooldownAndTimers(string rejection)
    {
        using Fixture f = Prepare();
        long module = f.Fit(27932);
        Space(f);
        Damage(f);
        if (rejection == "range")
        {
            var source = f.Db.Store.FindPilot(1000001)!;
            f.Db.Store.SetPosition(Target(f), source.Position! + new SpacePosition(100000, 0, 0));
        }
        if (rejection == "disallow-assistance")
        {
            int targetType = f.Db.Store.FindPilot(1000002)!.ShipTypeId;
            using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
            int attribute = (int)DogmaAttributeId.DisallowAssistance;
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO DogmaAttribute VALUES ({attribute},{attribute.ToString()})");
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES ({targetType},{attribute},{1})");
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            switch (rejection)
            {
                case "owner":
                    world.Set<OwnedItem>().Find(module)!.OwnerId = f.Db.Store.FindPilot(1000002)!.PilotId;
                    break;
                case "offline":
                    world.Set<OwnedItem>().Find(module)!.Online = false;
                    break;
                case "skill":
                    world.Set<PilotSkill>().Find(f.PilotId, (int)RemoteRepairSkill.RemoteHullRepairSystems)!.Points = 0;
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
        long target = rejection switch
        {
            "self" => f.ShipId,
            "non-pilot" => long.MaxValue,
            _ => Target(f),
        };
        var effect = rejection == "wrong-effect" ? DogmaEffect.RemoteArmorRepair : DogmaEffect.RemoteHullRepair;
        using WorldDbContext before = new(f.Db.Path);
        double? capacitor = before.Set<ShipEnergy>().Find(f.ShipId)?.Charge;
        double structure = before.Set<ShipHealthState>().Find(Target(f))!.StructureDamage;
        Assert.Throws<InvalidDataException>(() =>
            Store(f).Start(f.PilotId, module, effect, target, rejection == "missing-lock" ? [] : new HashSet<long> { target }, f.Clock.Now)
        );
        using WorldDbContext after = new(f.Db.Path);
        Assert.Equal(capacitor, after.Set<ShipEnergy>().Find(f.ShipId)?.Charge);
        Assert.Equal(structure, after.Set<ShipHealthState>().Find(Target(f))!.StructureDamage);
        Assert.Equal(100, after.Set<ShipHealthState>().Find(Target(f))!.ArmorDamage);
        Assert.Empty(after.Set<WorldRemoteRepair>());
        Assert.Null(after.Set<ModuleCooldown>().Find(module));
        Assert.Empty(after.Set<SavedCrimewatch>());
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("source-travel")]
    [InlineData("target-travel")]
    [InlineData("destroyed-target")]
    public void HullRepairCompletionRevalidatesParticipantsWithoutRefundOrHealing(string change)
    {
        using Fixture f = Prepare();
        long module = f.Fit(27932);
        Space(f);
        Damage(f);
        var cycle = Start(f, module, DogmaEffect.RemoteHullRepair);
        using (WorldDbContext world = new(f.Db.Path))
        {
            switch (change)
            {
                case "offline":
                    world.Set<OwnedItem>().Find(module)!.Online = false;
                    break;
                case "source-travel":
                    world.Positions.Find(f.ShipId)!.SystemId = 30000144;
                    break;
                case "target-travel":
                    world.Positions.Find(Target(f))!.SystemId = 30000144;
                    break;
                case "destroyed-target":
                    world.Set<ShipHealthState>().Find(Target(f))!.StructureDamage = 1000000;
                    break;
            }
            world.SaveChanges();
        }
        f.Clock.Seconds += 6;
        Assert.Throws<InvalidDataException>(() => Store(f).Complete(f.PilotId, module, f.Clock.Now));
        Store(f).Stop(f.PilotId, module);
        Store(f).Complete(f.PilotId, module, f.Clock.Now);
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(change == "destroyed-target" ? 1000000 : 50, verify.Set<ShipHealthState>().Find(Target(f))!.StructureDamage);
        Assert.Equal(cycle.Capacitor, verify.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Equal(cycle.Cycle.CompletesAt, verify.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Empty(verify.Set<WorldRemoteRepair>());
    }

    [Fact]
    public async Task CompletedHullRepairCheckpointsAndRestartCancelsTheNextPaidCycle()
    {
        using Fixture f = Prepare();
        long module = f.Fit(27932);
        Space(f);
        Damage(f);
        RemoteRepairActivation pending;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            _ = Health(f);
            Start(f, module, DogmaEffect.RemoteHullRepair);
            f.Clock.Seconds += 6;
            Store(f).Complete(f.PilotId, module, f.Clock.Now);
            Assert.Equal(2, Health(f).Defense.Structure - Health(f).Remaining.Structure, 7);
            pending = Start(f, module, DogmaEffect.RemoteHullRepair);
            await live.FlushAsync();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        f.Clock.Seconds += 30;
        Store(f).Complete(f.PilotId, module, f.Clock.Now);
        using WorldDbContext reopened = new(f.Db.Path);
        Assert.Equal(2, reopened.Set<ShipHealthState>().Find(Target(f))!.StructureDamage, 7);
        Assert.Equal(pending.Capacitor, reopened.Set<ShipEnergy>().Find(f.ShipId)!.Charge, 7);
        Assert.Equal(pending.Cycle.CompletesAt, reopened.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Empty(reopened.Set<WorldRemoteRepair>());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, .5)]
    [InlineData(2, .0625)]
    public void HullRepairUsesEdgeDistanceFalloffAndTargetImpedanceAtCompletion(int falloffs, double fraction)
    {
        using Fixture f = Prepare();
        long module = f.Fit(27932);
        Space(f);
        Damage(f);
        var source = f.Db.Store.FindPilot(1000001)!;
        var target = f.Db.Store.FindPilot(1000002)!;
        double edgeDistance;
        double radii;
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            int targetType = target.ShipTypeId;
            int impedanceAttribute = (int)DogmaAttributeId.RemoteRepairImpedance;
            catalog.Database.ExecuteSql(
                $"INSERT OR IGNORE INTO DogmaAttribute VALUES ({impedanceAttribute},{impedanceAttribute.ToString()})"
            );
            catalog.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO TypeAttribute VALUES ({targetType},{(int)DogmaAttributeId.RemoteRepairImpedance},{.5})"
            );
            var raw = catalog.FittingAttributes(27932);
            edgeDistance = raw[DogmaAttributeId.MaxRange] + falloffs * raw[DogmaAttributeId.FalloffEffectiveness];
            radii = (catalog.FittingType(601)!.Radius ?? 0) + (catalog.FittingType(targetType)!.Radius ?? 0);
        }
        f.Db.Store.SetPosition(target.ShipId, source.Position! + new SpacePosition(edgeDistance + radii, 0, 0));
        var before = Health(f);
        Start(f, module, DogmaEffect.RemoteHullRepair);
        f.Clock.Seconds += 6;
        Store(f).Complete(f.PilotId, module, f.Clock.Now);
        Assert.Equal(before.Remaining.Structure + 48 * fraction * .5, Health(f).Remaining.Structure, 7);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(1000)]
    public void HullRepairClipsOnlyStructureAndCannotReviveDestroyedShips(double amount)
    {
        ShipHealth health = new(1, new(100, 200, 300, 0, new(1, 1, 1, 1), new(1, 1, 1, 1), new(1, 1, 1, 1)), new(10, 20, 30));
        var repaired = RemoteRepairRules.Apply(health, RemoteRepairKind.Hull, amount);
        Assert.Equal(health.Remaining with { Structure = Math.Min(300, 30 + amount) }, repaired.Remaining);
        Assert.Throws<InvalidDataException>(() =>
            RemoteRepairRules.Apply(health with { Remaining = health.Remaining with { Structure = 0 } }, RemoteRepairKind.Hull, amount)
        );
    }
}
