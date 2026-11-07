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
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Repair;
using Eden.Protocol.Skills;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Repair;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Repair;

public sealed class HullRepairTests
{
    private const int RepairSystems = 3393;
    private const int CapitalRepairSystems = 21803;
    private const double Budget = 1_000_000;
    private const double Damage = 10_000;

    private static Fixture WithTiers(params int[] types)
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadHullRepairTiers(
            f.CatalogPath,
            types.Length == 0 ? null : types.Concat(new[] { 3392, RepairSystems, CapitalRepairSystems }).ToHashSet()
        );
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        // Explicit test-only budgets permit medium/large/capital profiles on
        // the fixture Ibis without claiming they fit a normal corvette.
        foreach (
            var attribute in new[]
            {
                DogmaAttributeId.CpuOutput,
                DogmaAttributeId.PowerOutput,
                DogmaAttributeId.CapacitorCapacity,
                DogmaAttributeId.StructureHitPoints,
            }
        )
        {
            catalog
                .Set<TypeAttribute>()
                .Where(a => a.ItemTypeId == 601 && a.AttributeId == (int)attribute)
                .ExecuteUpdate(set => set.SetProperty(a => a.Value, Budget));
        }
        return f;
    }

    private static IPilotLocalRepairing Store(Fixture f) => f.Db.Services.GetRequiredService<IPilotLocalRepairing>();

    private static ShipHealth Health(Fixture f) => f.Db.Services.GetRequiredService<IPilotHealth>().Read(f.PilotId, f.Clock.Now);

    private static long Prepare(Fixture f, int type = HullRepairDefinitions.SmallTechOne, double damage = Damage)
    {
        long module = f.Fit(type);
        using WorldDbContext world = new(f.Db.Path);
        world.Add(
            new ShipHealthState
            {
                ShipId = f.ShipId,
                ShieldCharge = 25,
                ArmorDamage = 100,
                StructureDamage = damage,
                UpdatedAt = f.Clock.Now,
            }
        );
        world.SaveChanges();
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        return module;
    }

    private static PilotCallContext Client(Fixture f)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new(), [], health: f.Db.Services.GetRequiredService<IPilotHealth>()),
            10,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        return context;
    }

    [Fact]
    public void All25PublishedProfilesUseMediumSlotsAndTheirCompleteNativeEffects()
    {
        using Fixture f = WithTiers();
        using CatalogDbContext catalog = new(f.CatalogPath);
        var types = catalog.Set<ItemType>().Where(t => t.GroupId == HullRepairDefinitions.Group).ToArray();
        Assert.Equal(25, types.Length);
        Assert.All(
            types,
            type =>
            {
                Assert.True(HullRepairCatalog.Supported(catalog, type.Id, type.GroupId), type.Name);
                var definition = FittingCatalog.Definition(catalog, type.Id)!;
                Assert.Equal(ModuleRack.Medium, definition.Rack);
                Assert.False(definition.Turret);
                Assert.False(definition.Launcher);
                Assert.Equal(DogmaEffect.StructureRepair, FittingCatalog.DefaultEffect(catalog, type.Id));
            }
        );
        var tiers = types
            .Select(t => catalog.Set<SourceRecord>().Find("client/types", PassiveCatalog.Key(t.Id))!.Read<ClientTypes>().MetaGroupID)
            .ToHashSet();
        Assert.Equal(new long[] { 1, 2, 3 }, tiers.Select(t => t!.Value).OrderBy(t => t));
    }

    [Theory]
    [InlineData(524, 25, 25500, 30, 3)]
    [InlineData(2355, 30, 20400, 30, 3)]
    [InlineData(5693, 30, 20400, 30, 3)]
    [InlineData(5743, 28, 21675, 30, 3)]
    [InlineData(21504, 45, 17000, 30, 3)]
    [InlineData(3653, 50, 25500, 60, 3)]
    [InlineData(3655, 60, 20400, 60, 3)]
    [InlineData(21506, 90, 17000, 60, 3)]
    [InlineData(3663, 100, 25500, 120, 3)]
    [InlineData(3665, 120, 20400, 120, 3)]
    [InlineData(21508, 180, 17000, 120, 3)]
    [InlineData(41511, 1800, 36000, 720, 5)]
    [InlineData(41512, 1980, 36000, 576, 5)]
    [InlineData(41513, 1980, 36000, 720, 5)]
    [InlineData(41514, 2160, 36000, 864, 5)]
    public void TiersPayAtStartAndRepairOnlyHullAtTheSkilledCycleEnd(int type, double amount, double duration, double cost, int level)
    {
        using Fixture f = WithTiers(type);
        long module = Prepare(f, type);
        f.Train(RepairSystems, level);
        f.Train(CapitalRepairSystems, 4);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == module);
        var attributes = f.Fitting.ModuleAttributes(f.PilotId, item);
        Assert.Equal(duration, attributes[DogmaAttributeId.Duration], 8);
        var paid = Store(f).Start(f.PilotId, module, f.Clock.Now, RepairLayer.Hull);
        Assert.Equal(RepairLayer.Hull, paid.Schedule.Layer);
        Assert.Equal(duration, paid.Schedule.DurationMilliseconds);
        Assert.Equal(Budget - cost, paid.Capacitor);
        Assert.Equal(Damage, Health(f).Defense.Structure - Health(f).Remaining.Structure, 8);
        f.Clock.Milliseconds += (long)duration - 1;
        Assert.Equal(Damage, Health(f).Defense.Structure - Health(f).Remaining.Structure, 8);
        f.Clock.Milliseconds++;
        var complete = Store(f).Complete(f.PilotId, f.ShipId, f.Clock.Now);
        Assert.Equal(Damage - amount, complete.Defense.Structure - complete.Remaining.Structure, 8);
        Assert.Equal(100, complete.Defense.Armor - complete.Remaining.Armor, 8);
        Assert.Equal(complete.Remaining.Structure, Store(f).Complete(f.PilotId, f.ShipId, f.Clock.Now).Remaining.Structure);
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Equal(Damage - amount, Health(f).Defense.Structure - Health(f).Remaining.Structure, 8);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<PendingLocalRepair>());
        Assert.Equal(paid.Schedule.StartedAt, saved.Set<ShipEnergy>().Single().UpdatedAt);
    }

    [Fact]
    public void RepeatKeepsHudStateManualStopFinishesPaymentAndNativeEffectsUseStructureRepair()
    {
        using Fixture f = WithTiers(524);
        long module = Prepare(f);
        f.Train(RepairSystems, 3);
        using var context = Client(f);
        LocalRepairHandler handler = new(f.Clock, Store(f));
        Assert.IsType<PilotCallResult.Success>(
            handler.Call(context, "dogmaIM", "Activate", [module, LocalRepairWireDefinitions.HullEffectName, null, 100L])
        );
        Assert.Equal(DogmaEffect.StructureRepair, handler.ActiveEffect(context.Pilot, module)!.Effect);
        Assert.IsType<PilotCallResult.Failure>(
            handler.Call(context, "dogmaIM", "Activate", [module, ArmorRepairWireDefinitions.EffectName, null, 100L])
        );
        Assert.IsType<PilotCallResult.Failure>(
            handler.Call(context, "dogmaIM", "Deactivate", [module, ArmorRepairWireDefinitions.EffectName])
        );
        context.Notifications.Clear();
        f.Clock.Milliseconds += 25500;
        handler.Advance(context);
        Assert.Empty(context.Notifications.OfType<ShipEffectChanged>());
        Assert.Equal(Damage - 25, Health(f).Defense.Structure - Health(f).Remaining.Structure, 8);
        Assert.Contains(
            context.Notifications.OfType<ModuleAttributesChanged>().SelectMany(n => n.Changes),
            c => c.Attribute == DogmaAttributeId.Damage && c.Value == Damage - 25
        );
        _ = handler.Call(context, "dogmaIM", "Deactivate", [module, LocalRepairWireDefinitions.HullEffectName]);
        f.Clock.Milliseconds += 25500;
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(context.Pilot, module));
        Assert.False(Assert.Single(context.Notifications.OfType<ShipEffectChanged>()).Start);
        Assert.Equal(Damage - 50, Health(f).Defense.Structure - Health(f).Remaining.Structure, 8);
        List<DestinyCommand> commands = [];
        FlightEffects effects = new(commands.Add);
        effects.LocalRepairEffect(f.ShipId, new(module, 524, f.Clock.Now, 25500, RepairLayer.Hull), true);
        Assert.Equal("effects.StructureRepair", Assert.IsType<DestinyCommand.TimedSpecialEffect>(Assert.Single(commands)).Graphic);
    }

    [Fact]
    public void ReconnectRestoresOnePaidHullCycleAndDockingDoesNotBuyFurtherCycles()
    {
        using Fixture f = WithTiers(524);
        long module = Prepare(f);
        f.Train(RepairSystems, 3);
        var paid = Store(f).Start(f.PilotId, module, f.Clock.Now, RepairLayer.Hull);
        _ = WorldInitialization.Open(f.Db.Path);
        using var context = Client(f);
        LocalRepairHandler handler = new(f.Clock, Store(f));
        handler.Enter(context);
        var effect = handler.ActiveEffect(context.Pilot, module)!;
        Assert.Equal(DogmaEffect.StructureRepair, effect.Effect);
        Assert.Equal(paid.Schedule.StartedAt, effect.StartTime);
        Assert.Equal(0, effect.Repeats);
        handler.Cancel(context);
        f.Db.Store.SetPosition(f.ShipId, null);
        f.Clock.Milliseconds += 25500;
        Assert.Equal(Damage - 25, Health(f).Defense.Structure - Health(f).Remaining.Structure, 8);
        f.Clock.Milliseconds += 600000;
        Assert.Equal(Damage - 25, Health(f).Defense.Structure - Health(f).Remaining.Structure, 8);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<PendingLocalRepair>());
        Assert.Equal(paid.Schedule.StartedAt, saved.Set<ShipEnergy>().Single().UpdatedAt);
    }

    [Fact]
    public void IndependentArmorAndHullSchedulesNeverRepairTheOtherLayer()
    {
        using Fixture f = WithTiers(524);
        long hull = Prepare(f);
        f.Db.Store.SetPosition(f.ShipId, null);
        long armor = f.Fit(523);
        f.Train(RepairSystems, 3);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        Store(f).Start(f.PilotId, armor, f.Clock.Now);
        Store(f).Start(f.PilotId, hull, f.Clock.Now, RepairLayer.Hull);
        f.Clock.Milliseconds += 5100;
        Assert.Equal(31, Health(f).Defense.Armor - Health(f).Remaining.Armor, 8);
        Assert.Equal(Damage, Health(f).Defense.Structure - Health(f).Remaining.Structure, 8);
        f.Clock.Milliseconds += 20400;
        Assert.Equal(31, Health(f).Defense.Armor - Health(f).Remaining.Armor, 8);
        Assert.Equal(Damage - 25, Health(f).Defense.Structure - Health(f).Remaining.Structure, 8);
    }

    [Fact]
    public void NativeCapitalHullPrimeAndBrainMatchBothEarnedDurationBonuses()
    {
        using Fixture f = WithTiers(41511);
        long module = Prepare(f, 41511);
        f.Train(RepairSystems, 5);
        f.Train(CapitalRepairSystems, 4);
        Store(f).Start(f.PilotId, module, f.Clock.Now, RepairLayer.Hull);
        using var scope = f.Db.Services.CreateScope();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == module);
        var prime = scope.ServiceProvider.GetRequiredService<ShipDogmaBuilder>().PrimeModule(pilot, item);
        Assert.Equal(36000, prime.Attributes[DogmaAttributeId.Duration], 8);
        Assert.Equal(1800, prime.Attributes[DogmaAttributeId.HullRepairAmount]);
        Assert.Contains(DogmaEffect.StructureRepair, prime.ActiveEffects.Keys);
        var brain = scope.ServiceProvider.GetRequiredService<PilotBrain>().Read(f.PilotId, f.ShipId);
        Assert.Contains(brain.ShipEffects, e => e.Skills.Contains(RepairSystems) && e.Value == -25);
        Assert.Contains(brain.ShipEffects, e => e.Skills.Contains(CapitalRepairSystems) && e.Value == -20);
    }

    [Fact]
    public void HullSettlementDoesNotRewriteSavedArmorDamageAboveItsCurrentMaximum()
    {
        using Fixture f = WithTiers(524);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        long module = Prepare(f);
        f.Train(RepairSystems, 3);
        Store(f).Start(f.PilotId, module, f.Clock.Now, RepairLayer.Hull);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<ShipHealthState>().Single().ArmorDamage = 1000;
            world.SaveChanges();
        }
        f.Clock.Milliseconds += 25500;
        _ = Health(f);
        using WorldDbContext saved = new(f.Db.Path);
        var health = saved.Set<ShipHealthState>().Single();
        Assert.Equal(Damage - 25, health.StructureDamage);
        Assert.Equal(1000, health.ArmorDamage);
    }

    [Fact]
    public void TwoHullRepairersAddIndependentlyClipToMissingHullAndCannotResurrect()
    {
        using Fixture f = WithTiers(524);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        long first = Prepare(f, damage: 60);
        f.Db.Store.SetPosition(f.ShipId, null);
        long second = f.Fit(524);
        f.Train(RepairSystems, 3);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        Store(f).Start(f.PilotId, first, f.Clock.Now, RepairLayer.Hull);
        Store(f).Start(f.PilotId, second, f.Clock.Now, RepairLayer.Hull);
        f.Clock.Milliseconds += 25500;
        Assert.Equal(10, Health(f).Defense.Structure - Health(f).Remaining.Structure, 8);
        Store(f).Start(f.PilotId, first, f.Clock.Now, RepairLayer.Hull);
        f.Clock.Milliseconds += 25500;
        Assert.Equal(0, Health(f).Defense.Structure - Health(f).Remaining.Structure, 8);
        Store(f).Start(f.PilotId, first, f.Clock.Now, RepairLayer.Hull);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<ShipHealthState>().Single().StructureDamage = Budget * 2;
            world.SaveChanges();
        }
        f.Clock.Milliseconds += 25500;
        Assert.True(Health(f).Remaining.Destroyed);
        Assert.Equal(0, Health(f).Remaining.Structure);
        Assert.Throws<InvalidDataException>(() => Store(f).Start(f.PilotId, first, f.Clock.Now, RepairLayer.Hull));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<PendingLocalRepair>());
        Assert.Equal(Budget * 2, saved.Set<ShipHealthState>().Single().StructureDamage);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("foreign")]
    [InlineData("rack")]
    [InlineData("skill")]
    [InlineData("capacitor")]
    [InlineData("wrong-effect")]
    [InlineData("restriction")]
    [InlineData("mutation")]
    [InlineData("extra-effect")]
    [InlineData("duration")]
    public void RejectedActivationsRetainResourcesAndDoNotScheduleRepairs(string reason)
    {
        using Fixture f = WithTiers(524);
        long module = Prepare(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var item = world.Set<OwnedItem>().Find(module)!;
            if (reason == "offline")
            {
                item.Online = false;
            }
            if (reason == "foreign")
            {
                item.OwnerId = 90000002;
            }
            if (reason == "rack")
            {
                item.FlagId = (int)InventoryFlag.LowSlot0;
            }
            if (reason == "skill")
            {
                world.Set<PilotSkill>().Find(f.PilotId, RepairSystems)!.Points = 0;
            }
            if (reason == "capacitor")
            {
                world.Add(
                    new ShipEnergy
                    {
                        ShipId = f.ShipId,
                        Charge = 0,
                        UpdatedAt = f.Clock.Now,
                    }
                );
            }
            world.SaveChanges();
        }
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            if (reason == "restriction")
            {
                catalog.Database.ExecuteSqlRaw("INSERT INTO TypeAttribute VALUES (524,1298,1)");
            }
            if (reason == "duration")
            {
                catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=0 WHERE ItemTypeId=524 AND AttributeId=73");
            }
            if (reason == "mutation")
            {
                catalog.Database.ExecuteSqlRaw(
                    "UPDATE SourceRecord SET Data=json_set(Data,'$.metaGroupID',15) WHERE Dataset='client/types' AND Key='524'"
                );
            }
            if (reason == "extra-effect")
            {
                catalog.Database.ExecuteSqlRaw(
                    "UPDATE SourceRecord SET Data=json_insert(Data,'$.dogmaEffects[#]',json_object('effectID',27)) "
                        + "WHERE Dataset='client/typedogma' AND Key='524'"
                );
            }
        }
        Assert.Throws<InvalidDataException>(() =>
            Store(f).Start(f.PilotId, module, f.Clock.Now, reason == "wrong-effect" ? RepairLayer.Armor : RepairLayer.Hull)
        );
        using WorldDbContext check = new(f.Db.Path);
        Assert.Empty(check.Set<PendingLocalRepair>());
        Assert.Empty(check.Set<ModuleCooldown>());
        if (reason != "capacitor")
        {
            Assert.Empty(check.Set<ShipEnergy>());
        }
        else
        {
            Assert.Equal(0, check.Set<ShipEnergy>().Single().Charge);
        }
    }

    [Fact]
    public void ATransferredRepairerSettlesItsPreviousShipsPaidCycleWithoutHealingItsNewShip()
    {
        using Fixture f = WithTiers(524);
        long originalShip = f.ShipId;
        long module = Prepare(f);
        f.Train(RepairSystems, 3);
        Store(f).Start(f.PilotId, module, f.Clock.Now, RepairLayer.Hull);
        f.Db.Store.SetPosition(f.ShipId, null);
        f.Inventory.Move(f.PilotId, f.StationId, new([module], f.ShipId, (int)InventoryFlag.Hangar, null));
        const long NextShip = 2_000_000_099_999;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Add(
                new()
                {
                    Id = NextShip,
                    OwnerId = f.PilotId,
                    TypeId = 601,
                    StationId = checked((int)f.StationId),
                    Name = "Second Ibis",
                }
            );
            world.Pilots.Single(p => p.Id == f.PilotId).ActiveShipId = NextShip;
            world.Add(
                new ShipHealthState
                {
                    ShipId = NextShip,
                    StructureDamage = 100,
                    UpdatedAt = f.Clock.Now,
                }
            );
            world.SaveChanges();
        }
        f.Fitting.Fit(f.PilotId, [module], f.StationId, (int)InventoryFlag.AutoFit);
        f.Fitting.SetOnline(f.PilotId, NextShip, module, true);
        f.Db.Store.SetPosition(NextShip, SpacePosition.Zero);
        Assert.Throws<InvalidDataException>(() => Store(f).Start(f.PilotId, module, f.Clock.Now, RepairLayer.Hull));
        f.Clock.Milliseconds += 25500;
        Store(f).Start(f.PilotId, module, f.Clock.Now, RepairLayer.Hull);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(Damage - 25, saved.Set<ShipHealthState>().Find(originalShip)!.StructureDamage);
        Assert.Equal(100, saved.Set<ShipHealthState>().Find(NextShip)!.StructureDamage);
        Assert.Equal(NextShip, saved.Set<PendingLocalRepair>().Single().ShipId);
    }

    [Theory]
    [InlineData(36000.00000000001, 360000000)]
    [InlineData(.1234, 1234)]
    [InlineData(.12345, 1235)]
    public void PaidDeadlineAvoidsBinaryNoiseAndRetainsFractionalMillisecondPrecision(double duration, long ticks)
    {
        LocalRepairer repairer = new(1, 524, duration, 25, 30);
        Assert.Equal(1000 + ticks, LocalRepairRules.CompletesAt(1000, repairer));
    }

    [Fact]
    public void Version32MigrationPreservesAnAlreadyPaidArmorRepairAsArmor()
    {
        using Fixture f = WithTiers(524);
        long hull = Prepare(f);
        f.Db.Store.SetPosition(f.ShipId, null);
        long armor = f.Fit(523);
        f.Train(RepairSystems, 3);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        var paid = Store(f).Start(f.PilotId, armor, f.Clock.Now);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                TestWorld.DropSafetySchema + "ALTER TABLE WorldArmorRepair DROP COLUMN Layer; PRAGMA user_version=32"
            );
        }
        _ = WorldInitialization.Open(f.Db.Path);
        _ = WorldInitialization.Open(f.Db.Path);
        using (WorldDbContext saved = new(f.Db.Path))
        {
            var repair = Assert.Single(saved.Set<PendingLocalRepair>());
            Assert.Equal(RepairLayer.Armor, repair.Layer);
            Assert.Equal(paid.Schedule.CompletesAt, repair.CompletesAt);
            Assert.Equal(69, repair.Amount);
            Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        }
        f.Clock.Milliseconds += 5100;
        Assert.Equal(31, Health(f).Defense.Armor - Health(f).Remaining.Armor, 8);
        Assert.Equal(Damage, Health(f).Defense.Structure - Health(f).Remaining.Structure, 8);
    }
}
