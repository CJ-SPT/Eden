using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Repair;

public sealed class ArmorRepairTests
{
    [Fact]
    public void ExistingRepairerFixturesUseLowSlotsAndTheNativeBrainIncludesRepairSystems()
    {
        using Fixture f = new();
        using CatalogDbContext catalog = new(f.CatalogPath);
        var repairers = catalog.Set<ItemType>().Where(t => t.GroupId == ArmorRepairDefinitions.Group).ToArray();
        var supported = repairers.Select(t => FittingCatalog.Definition(catalog, t.Id)).OfType<ModuleFittingDefinition>().ToArray();
        Assert.Equal(13, supported.Length);
        Assert.All(supported, definition => Assert.Equal(ModuleRack.Low, definition.Rack));
        Assert.NotNull(FittingCatalog.Definition(catalog, 1183)); // Small Armor Repairer II.
        f.Train(ArmorRepairDefinitions.RepairSystemsSkill, 3);
        var brain = f.Db.Services.GetRequiredService<PilotBrain>().Read(f.PilotId, f.ShipId);
        Assert.Contains(
            brain.ShipEffects,
            effect => effect.Skills.Contains(ArmorRepairDefinitions.RepairSystemsSkill) && effect.Value == -15
        );
    }

    private static long Prepare(Fixture f)
    {
        long module = f.Fit(ArmorRepairDefinitions.SmallTechOne);
        f.Train(ArmorRepairDefinitions.RepairSystemsSkill, 3);
        using WorldDbContext world = new(f.Db.Path);
        var saved = world.Set<ShipHealthState>().Find(f.ShipId) ?? world.Add(new ShipHealthState { ShipId = f.ShipId }).Entity;
        saved.ShieldCharge = 25;
        saved.ArmorDamage = 100;
        saved.StructureDamage = 9;
        saved.UpdatedAt = f.Clock.Now;
        world.SaveChanges();
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        return module;
    }

    private static IPilotLocalRepairing Store(Fixture f) => f.Db.Services.GetRequiredService<IPilotLocalRepairing>();

    private static ShipHealth Health(Fixture f) => f.Db.Services.GetRequiredService<IPilotHealth>().Read(f.PilotId, f.Clock.Now);

    private static PilotCallContext Client(Fixture f)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => new(f.Db.Store, selected, f.Clock, new(), [], health: f.Db.Services.GetRequiredService<IPilotHealth>()),
            10,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        return context;
    }

    [Fact]
    public void PaysAtStartRepairsOnlyAtEndAndUsesRepairSystemsDurationOnce()
    {
        using Fixture f = new();
        long module = Prepare(f);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == module);
        var attributes = f.Fitting.ModuleAttributes(f.PilotId, item);
        Assert.Equal(5100, attributes[DogmaAttributeId.Duration]);
        Assert.Equal(69, attributes[DogmaAttributeId.ArmorRepairAmount]);
        double capacitor = f.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.PilotId, f.Clock.Now);
        var paid = Store(f).Start(f.PilotId, module, f.Clock.Now);
        Assert.Equal(capacitor - 40, paid.Capacitor);
        Assert.Equal(f.Clock.Now + 5100 * TimeSpan.TicksPerMillisecond, paid.Schedule.CompletesAt);
        Assert.Equal(100, Health(f).Defense.Armor - Health(f).Remaining.Armor);
        f.Clock.Milliseconds += 5099;
        Assert.Equal(100, Health(f).Defense.Armor - Health(f).Remaining.Armor);
        f.Clock.Milliseconds++;
        var completed = Health(f);
        Assert.Equal(31, completed.Defense.Armor - completed.Remaining.Armor, 8);
        Assert.Equal(9, completed.Defense.Structure - completed.Remaining.Structure, 8);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<PendingLocalRepair>());
        Assert.Equal(paid.Schedule.CompletesAt, world.Set<ModuleCooldown>().Single().ReadyAt);
        f.Clock.Milliseconds += 600000;
        Assert.Equal(31, Health(f).Defense.Armor - Health(f).Remaining.Armor, 8);
    }

    [Fact]
    public void ReconnectRestoresOnePaidCycleWithoutPayingAgainOrAutorepeating()
    {
        using Fixture f = new();
        long module = Prepare(f);
        var paid = Store(f).Start(f.PilotId, module, f.Clock.Now);
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Throws<InvalidDataException>(() => Store(f).Start(f.PilotId, module, f.Clock.Now));
        using var context = Client(f);
        LocalRepairHandler restored = new(f.Clock, Store(f));
        restored.Enter(context);
        var effect = restored.ActiveEffect(context.Pilot, module)!;
        Assert.Equal(paid.Schedule.StartedAt, effect.StartTime);
        Assert.Equal(0, effect.Repeats);
        f.Clock.Milliseconds += 5100;
        restored.Advance(context);
        Assert.Null(restored.ActiveEffect(context.Pilot, module));
        Assert.Equal(31, Health(f).Defense.Armor - Health(f).Remaining.Armor, 8);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<PendingLocalRepair>());
        Assert.Equal(paid.Schedule.StartedAt, world.Set<ShipEnergy>().Single().UpdatedAt);
    }

    [Fact]
    public void DockingRetainsOnlyThePaidCompletionAndResourceReadsSettleItOnce()
    {
        using Fixture f = new();
        long module = Prepare(f);
        using var context = Client(f);
        LocalRepairHandler handler = new(f.Clock, Store(f));
        _ = handler.Call(context, "dogmaIM", "Activate", [module, ArmorRepairWireDefinitions.EffectName, null, 100L]);
        handler.Cancel(context);
        f.Db.Store.SetPosition(f.ShipId, null);
        f.Clock.Milliseconds += 5100;
        _ = WorldInitialization.Open(f.Db.Path);
        var resources = f.Db.Services.GetRequiredService<IPilotShipResources>().Read(f.PilotId, f.ShipId, f.Clock.Now);
        Assert.Equal(31, resources.Health.Defense.Armor - resources.Health.Remaining.Armor, 8);
        f.Clock.Milliseconds += 600000;
        Assert.Equal(31, Health(f).Defense.Armor - Health(f).Remaining.Armor, 8);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<PendingLocalRepair>());
        Assert.Equal(f.Clock.Now - 605100 * TimeSpan.TicksPerMillisecond, world.Set<ShipEnergy>().Single().UpdatedAt);
    }

    [Fact]
    public void RepeatRetainsHudStateAndManualStopFinishesThePaidRepair()
    {
        using Fixture f = new();
        long module = Prepare(f);
        using var context = Client(f);
        LocalRepairHandler handler = new(f.Clock, Store(f));
        Assert.IsType<PilotCallResult.Success>(
            handler.Call(context, "dogmaIM", "Activate", [module, ArmorRepairWireDefinitions.EffectName, null, 100L])
        );
        context.Notifications.Clear();
        f.Clock.Milliseconds += 5100;
        handler.Advance(context);
        Assert.Empty(context.Notifications.OfType<ShipEffectChanged>());
        Assert.Equal(31, Health(f).Defense.Armor - Health(f).Remaining.Armor, 8);
        Assert.IsType<PilotCallResult.Success>(
            handler.Call(context, "dogmaIM", "Deactivate", [module, ArmorRepairWireDefinitions.EffectName])
        );
        f.Clock.Milliseconds += 5099;
        handler.Advance(context);
        Assert.NotNull(handler.ActiveEffect(context.Pilot, module));
        f.Clock.Milliseconds++;
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(context.Pilot, module));
        Assert.Equal(0, Health(f).Defense.Armor - Health(f).Remaining.Armor, 8);
        Assert.False(Assert.Single(context.Notifications.OfType<ShipEffectChanged>()).Start);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("docked")]
    [InlineData("foreign")]
    [InlineData("skill")]
    [InlineData("capacitor")]
    [InlineData("duration")]
    [InlineData("amount")]
    [InlineData("destroyed")]
    public void RejectedStartsDoNotPayOrScheduleARepair(string reason)
    {
        using Fixture f = new();
        long module = Prepare(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            if (reason == "offline")
            {
                world.Set<OwnedItem>().Find(module)!.Online = false;
            }
            if (reason == "foreign")
            {
                world.Set<OwnedItem>().Find(module)!.OwnerId = 90000002;
            }
            if (reason == "skill")
            {
                world.Set<PilotSkill>().Find(f.PilotId, ArmorRepairDefinitions.RepairSystemsSkill)!.Points = 0;
            }
            if (reason == "destroyed")
            {
                world.Set<ShipHealthState>().Find(f.ShipId)!.StructureDamage = 1000000;
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
        if (reason == "docked")
        {
            f.Db.Store.SetPosition(f.ShipId, null);
        }
        if (reason is "duration" or "amount")
        {
            using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
            int attribute = (int)(reason == "duration" ? DogmaAttributeId.Duration : DogmaAttributeId.ArmorRepairAmount);
            catalog.Database.ExecuteSql(
                $"UPDATE TypeAttribute SET Value=0 WHERE ItemTypeId={ArmorRepairDefinitions.SmallTechOne} AND AttributeId={attribute}"
            );
        }
        Assert.Throws<InvalidDataException>(() => Store(f).Start(f.PilotId, module, f.Clock.Now));
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
    public void TwoPaidRepairersCompleteOnceAndNeverResurrectADestroyedHull()
    {
        using Fixture f = new();
        long first = Prepare(f);
        f.Db.Store.SetPosition(f.ShipId, null);
        long second = f.Fit(ArmorRepairDefinitions.SmallTechOne);
        f.Train(ArmorRepairDefinitions.RepairSystemsSkill, 3);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        Store(f).Start(f.PilotId, first, f.Clock.Now);
        Store(f).Start(f.PilotId, second, f.Clock.Now);
        f.Clock.Milliseconds += 5100;
        Assert.Equal(0, Health(f).Defense.Armor - Health(f).Remaining.Armor, 8);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<ShipHealthState>().Find(f.ShipId)!.ArmorDamage = 100;
            world.SaveChanges();
        }
        Store(f).Start(f.PilotId, first, f.Clock.Now);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<ShipHealthState>().Find(f.ShipId)!.StructureDamage = 1000000;
            world.SaveChanges();
        }
        f.Clock.Milliseconds += 5100;
        Assert.True(Health(f).Remaining.Destroyed);
        Assert.Equal(100, Health(f).Defense.Armor - Health(f).Remaining.Armor);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<PendingLocalRepair>());
    }

    [Fact]
    public void VersionTwentyEightMigrationPreservesItemsAndPaidCooldownsWithoutInventingRepairs()
    {
        using Fixture f = new();
        long module = Prepare(f);
        var items = f.Inventory.List(f.PilotId, f.ShipId);
        long deadline = f.Clock.Now + TimeSpan.TicksPerMinute;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new ModuleCooldown { ModuleId = module, ReadyAt = deadline });
            world.SaveChanges();
            world.Database.ExecuteSqlRaw(
                "ALTER TABLE WorldArmorRepair DROP COLUMN Layer; "
                    + "ALTER TABLE WorldItem DROP COLUMN CrystalDamage; DROP TABLE WorldActiveModule; "
                    + "DROP TABLE WorldArmorRepair; PRAGMA user_version=28"
            );
        }
        _ = WorldInitialization.Open(f.Db.Path);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Empty(saved.Set<PendingLocalRepair>());
        Assert.Equal(deadline, saved.Set<ModuleCooldown>().Single().ReadyAt);
        Assert.Equal(items, f.Inventory.List(f.PilotId, f.ShipId));
        using CatalogDbContext catalog = new(f.CatalogPath);
        Assert.NotNull(FittingCatalog.Definition(catalog, 1183));
    }
}
