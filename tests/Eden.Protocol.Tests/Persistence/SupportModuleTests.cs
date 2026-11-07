using System.Text.Json.Nodes;
using Eden.Data.Catalog;
using Eden.Data.Combat;
using Eden.Data.Fittings;
using Eden.Data.Inventory;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Repair;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Persistence;

public sealed class SupportModuleTests
{
    private static Fixture Prepare(params int[] types)
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadSupportTiers(f.CatalogPath, types.Concat(new[] { 601, 3317, 3428, 12441 }).ToHashSet());
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        foreach (var attribute in new[] { DogmaAttributeId.CpuOutput, DogmaAttributeId.PowerOutput, DogmaAttributeId.MediumSlots })
        {
            double value = attribute == DogmaAttributeId.MediumSlots ? 8 : 100000;
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)attribute},{value})");
        }
        return f;
    }

    private static IPilotSupportModules Store(Fixture f) => f.Db.Services.GetRequiredService<IPilotSupportModules>();

    private static IPilotAmmunition Ammo(Fixture f) => f.Db.Services.GetRequiredService<IPilotAmmunition>();

    private static InventoryItem Item(Fixture f, long module) => f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == module);

    private static void Space(Fixture f)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        // Avoid physical collision correction at the star's origin or inside a station.
        f.Db.Store.SetPosition(f.ShipId, new(pilot.X + pilot.Flight.StationRadius + 50000, pilot.Y, pilot.Z));
    }

    private static SupportActivation Activate(Fixture f, long module) =>
        Store(f).Activate(f.PilotId, module, SupportModuleDefinitions.Effect(Item(f, module).GroupId)!.Value, f.Clock.Now);

    private static AmmunitionTransfer Load(Fixture f, long module, int script, int quantity = 3)
    {
        long stack = f.Grant(script, quantity);
        var transfer = Ammo(f).Load(f.PilotId, f.ShipId, [module], [stack], f.StationId, f.Clock.Now);
        f.Clock.Milliseconds++;
        return transfer;
    }

    [Fact]
    public void AllThirtyTwoModulesAndSevenScriptsHaveCompletePublishedProfiles()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadSupportTiers(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var modules = catalog.Set<ItemType>().AsEnumerable().Where(t => SupportModuleDefinitions.Effect(t.GroupId) is not null).ToArray();
        Assert.Equal(32, modules.Length);
        Assert.Equal(11, modules.Count(t => t.GroupId == SupportModuleDefinitions.TrackingComputerGroup));
        Assert.Equal(16, modules.Count(t => t.GroupId == SupportModuleDefinitions.SensorBoosterGroup));
        Assert.Equal(5, modules.Count(t => t.GroupId == SupportModuleDefinitions.GuidanceComputerGroup));
        Assert.All(
            modules,
            t =>
            {
                Assert.True(SupportModuleCatalog.Supported(catalog, t.Id, t.GroupId), t.Name);
                Assert.Equal(ModuleRack.Medium, FittingCatalog.Definition(catalog, t.Id)!.Rack);
            }
        );
        var scripts = catalog.Set<ItemType>().AsEnumerable().Where(t => SupportModuleDefinitions.IsScript(t.GroupId)).ToArray();
        Assert.Equal(7, scripts.Length);
        Assert.All(scripts, t => Assert.True(ScriptCatalog.Supported(catalog, t.Id, t.GroupId), t.Name));
    }

    [Theory]
    [InlineData(1977, 7, 351, 5)]
    [InlineData(1978, 10, 351, 7.5)]
    [InlineData(15290, 14, 351, 9.9)]
    [InlineData(22929, 4, 351, 7.5)]
    [InlineData(35788, 7, 547, 4)]
    [InlineData(35790, 7, 547, 5.5)]
    [InlineData(94063, 7, 547, 6.5)]
    [InlineData(1973, 10, 309, 25)]
    [InlineData(1952, 12, 309, 30)]
    [InlineData(41195, 10, 309, 39)]
    public void TiersPayTheirOwnCycleAndKeepBonusesInactiveUntilStarted(int type, double cost, int source, double bonus)
    {
        using Fixture f = Prepare(type);
        long module = f.Fit(type);
        Assert.Equal(bonus, f.Fitting.ModuleAttributes(f.PilotId, Item(f, module))[(DogmaAttributeId)source], 8);
        var before = f.Fitting.ShipAttributes(f.PilotId);
        Space(f);
        var activated = Activate(f, module);
        Assert.Equal(cost, activated.Module.CapacitorNeed);
        Assert.Equal(10000, activated.Module.DurationMilliseconds);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(f.Clock.Now + 10000 * TimeSpan.TicksPerMillisecond, world.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Single(world.Set<ActiveFittedModule>());
        if (source == (int)DogmaAttributeId.TargetRangeBonus)
        {
            Assert.Equal(
                before[DogmaAttributeId.MaxTargetRange] * (1 + bonus / 100),
                f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxTargetRange],
                7
            );
        }
        Store(f).Stop(f.PilotId, f.ShipId, module);
        Assert.Equal(before[DogmaAttributeId.MaxTargetRange], f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxTargetRange], 7);
        Assert.Throws<InvalidDataException>(() => Activate(f, module));
    }

    [Theory]
    [InlineData(1977, 28999, 351, 10, 767, 0)]
    [InlineData(1977, 29001, 767, 20, 351, 0)]
    [InlineData(1973, 29009, 309, 50, 566, 0)]
    [InlineData(1973, 29011, 566, 50, 309, 0)]
    [InlineData(1973, 41155, 1027, 80, 309, 0)]
    [InlineData(35788, 35794, 547, 8, 848, 0)]
    [InlineData(35788, 35795, 848, -11, 547, 0)]
    public void ScriptsDoubleSelectedBonusesAndSuppressAlternativesWithoutBeingConsumed(
        int type,
        int script,
        int improved,
        double expected,
        int suppressed,
        double zero
    )
    {
        using Fixture f = Prepare(type, script);
        long module = f.Fit(type);
        var transfer = Load(f, module, script);
        Assert.Equal(.01, transfer.ReloadMilliseconds);
        Assert.Equal(1, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
        var attributes = f.Fitting.ModuleAttributes(f.PilotId, Item(f, module));
        Assert.Equal(expected, attributes[(DogmaAttributeId)improved], 7);
        Assert.Equal(zero, attributes[(DogmaAttributeId)suppressed], 7);
        Space(f);
        Activate(f, module);
        Store(f).Stop(f.PilotId, f.ShipId, module);
        f.Clock.Milliseconds += 10000;
        Activate(f, module);
        Assert.Equal(1, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(3, world.Set<OwnedItem>().Where(i => i.TypeId == script && i.OwnerId == f.PilotId).Sum(i => i.Quantity));
    }

    [Fact]
    public void TrackingComputersStackWithEachOtherAndAffectOnlyGunneryEquipment()
    {
        using Fixture f = Prepare(1978, 15290, 28999);
        long gun = f.Fit(561);
        long miner = f.Fit(3651);
        long techTwo = f.Fit(1978);
        long officer = f.Fit(15290);
        Load(f, techTwo, 28999);
        var gunBefore = f.Fitting.ModuleAttributes(f.PilotId, Item(f, gun));
        var minerBefore = f.Fitting.ModuleAttributes(f.PilotId, Item(f, miner));
        Space(f);
        Activate(f, techTwo);
        Activate(f, officer);
        const double SecondWeight = .8691199808003975;
        var after = f.Fitting.ModuleAttributes(f.PilotId, Item(f, gun));
        Assert.Equal(gunBefore[DogmaAttributeId.MaxRange] * 1.15 * (1 + .099 * SecondWeight), after[DogmaAttributeId.MaxRange], 7);
        Assert.Equal(gunBefore[DogmaAttributeId.TrackingSpeed] * 1.21, after[DogmaAttributeId.TrackingSpeed], 7);
        Assert.Equal(
            minerBefore[DogmaAttributeId.MaxRange],
            f.Fitting.ModuleAttributes(f.PilotId, Item(f, miner))[DogmaAttributeId.MaxRange]
        );
        Store(f).Stop(f.PilotId, f.ShipId, techTwo);
        Assert.Equal(
            gunBefore[DogmaAttributeId.MaxRange] * 1.099,
            f.Fitting.ModuleAttributes(f.PilotId, Item(f, gun))[DogmaAttributeId.MaxRange],
            7
        );
    }

    [Fact]
    public void GuidanceChangesLoadedMissileMotorsAndApplicationWithIndependentStackingSequences()
    {
        using Fixture f = Prepare(35788, 35790, 35794);
        PassiveCatalogFixture.LoadMissileTiers(f.CatalogPath, MissileDefinitions.Skills.Concat(new[] { 499, 212 }).ToHashSet());
        long launcher = f.Fit(499);
        long first = f.Fit(35788);
        long second = f.Fit(35790);
        long missiles = f.Grant(212, 5);
        Ammo(f).Load(f.PilotId, f.ShipId, [launcher], [missiles], f.StationId, f.Clock.Now);
        Load(f, first, 35794);
        f.Clock.Milliseconds += 10000;
        var before = Ammo(f).ChargeAttributes(f.PilotId, f.ShipId, launcher);
        Space(f);
        Activate(f, first);
        Activate(f, second);
        var after = Ammo(f).ChargeAttributes(f.PilotId, f.ShipId, launcher);
        const double SecondWeight = .8691199808003975;
        Assert.Equal(before[DogmaAttributeId.MaxVelocity] * 1.08 * (1 + .055 * SecondWeight), after[DogmaAttributeId.MaxVelocity], 7);
        Assert.Equal(
            before[DogmaAttributeId.MissileFlightTime] * 1.08 * (1 + .055 * SecondWeight),
            after[DogmaAttributeId.MissileFlightTime],
            7
        );
        Assert.Equal(before[DogmaAttributeId.ExplosionRadius] * .9175, after[DogmaAttributeId.ExplosionRadius], 7);
        Assert.Equal(before[DogmaAttributeId.ExplosionVelocity] * 1.0825, after[DogmaAttributeId.ExplosionVelocity], 7);
        Store(f).Stop(f.PilotId, f.ShipId, first);
        Assert.Equal(
            before[DogmaAttributeId.MaxVelocity] * 1.055,
            Ammo(f).ChargeAttributes(f.PilotId, f.ShipId, launcher)[DogmaAttributeId.MaxVelocity],
            7
        );
    }

    [Fact]
    public void ScriptSwapsConserveStacksAndCannotChangeAnActiveModuleEvenAfterItsDeadline()
    {
        using Fixture f = Prepare(1977, 28999, 29001);
        long module = f.Fit(1977);
        Load(f, module, 28999);
        long replacement = f.Grant(29001, 2, cargo: true);
        Space(f);
        Activate(f, module);
        f.Clock.Milliseconds += 10001;
        Assert.Throws<InvalidDataException>(() => Ammo(f).Load(f.PilotId, f.ShipId, [module], [replacement], f.ShipId, f.Clock.Now));
        Assert.Throws<InvalidDataException>(() =>
            Ammo(f).Unload(f.PilotId, f.ShipId, [module], f.ShipId, (int)InventoryFlag.Cargo, null, f.Clock.Now)
        );
        Store(f).Stop(f.PilotId, f.ShipId, module);
        Ammo(f).Load(f.PilotId, f.ShipId, [module], [replacement], f.ShipId, f.Clock.Now);
        f.Clock.Milliseconds++;
        Assert.Equal(29001, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).TypeId);
        WorldInitialization.Open(f.Db.Path);
        Ammo(f).Unload(f.PilotId, f.ShipId, [module], f.ShipId, (int)InventoryFlag.Cargo, null, f.Clock.Now);
        Assert.Empty(Ammo(f).Read(f.PilotId, f.ShipId));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(3, world.Set<OwnedItem>().Where(i => i.TypeId == 28999 && i.OwnerId == f.PilotId).Sum(i => i.Quantity));
        Assert.Equal(2, world.Set<OwnedItem>().Where(i => i.TypeId == 29001 && i.OwnerId == f.PilotId).Sum(i => i.Quantity));
    }

    [Theory]
    [InlineData("family")]
    [InlineData("size")]
    [InlineData("effect")]
    public void InvalidScriptsPreserveTheExistingMagazineAndDeadline(string reason)
    {
        using Fixture f = Prepare(1977, 28999, 29001, 35794);
        long module = f.Fit(1977);
        Load(f, module, 28999);
        long stack = f.Grant(reason == "family" ? 35794 : 29001);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            if (reason == "size")
            {
                catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=2 WHERE ItemTypeId=29001 AND AttributeId=128");
            }
            if (reason == "effect")
            {
                var record = catalog.Set<SourceRecord>().Find("client/typedogma", "29001")!;
                record.Data = record.Data.Replace("3599", "3600", StringComparison.Ordinal);
                catalog.Update(record);
                catalog.SaveChanges();
            }
        }
        var before = Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId));
        Assert.Throws<InvalidDataException>(() => Ammo(f).Load(f.PilotId, f.ShipId, [module], [stack], f.StationId, f.Clock.Now));
        Assert.Equal(before, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)));
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("foreign")]
    [InlineData("docked")]
    [InlineData("skill")]
    [InlineData("capacitor")]
    [InlineData("effect")]
    public void InvalidActivationsNeverPayOrInstallABonus(string reason)
    {
        using Fixture f = Prepare(1977);
        long module = f.Fit(1977);
        if (reason != "docked")
        {
            Space(f);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            if (reason == "offline")
            {
                world.Set<OwnedItem>().Find(module)!.Online = false;
            }
            if (reason == "foreign")
            {
                world.Set<OwnedItem>().Find(module)!.OwnerId++;
            }
            if (reason == "capacitor")
            {
                world
                    .Set<ShipEnergy>()
                    .Add(
                        new()
                        {
                            ShipId = f.ShipId,
                            Charge = 0,
                            UpdatedAt = f.Clock.Now,
                        }
                    );
            }
            world.SaveChanges();
        }
        if (reason == "skill")
        {
            f.Train(3317, 0);
        }
        Assert.Throws<InvalidDataException>(() =>
            Store(f).Activate(f.PilotId, module, reason == "effect" ? DogmaEffect.SensorBooster : DogmaEffect.TrackingComputer, f.Clock.Now)
        );
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ActiveFittedModule>());
        Assert.Null(saved.Set<ModuleCooldown>().Find(module));
    }

    [Theory]
    [InlineData("restriction")]
    [InlineData("mutation")]
    [InlineData("default")]
    [InlineData("modifier")]
    public void UnsupportedModuleProfilesCannotFitOrCreateActiveBonuses(string reason)
    {
        using Fixture f = Prepare(1977);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            if (reason == "restriction")
            {
                catalog.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO TypeAttribute VALUES (1977,1302,601)");
            }
            if (reason == "mutation")
            {
                var type = catalog.Set<SourceRecord>().Find("client/types", "1977")!;
                var data = JsonNode.Parse(type.Data)!;
                data["metaGroupID"] = 15;
                type.Data = data.ToJsonString();
                catalog.Update(type);
            }
            if (reason == "default")
            {
                var type = catalog.Set<SourceRecord>().Find("client/typedogma", "1977")!;
                var data = JsonNode.Parse(type.Data)!;
                foreach (var effect in data["dogmaEffects"]!.AsArray())
                {
                    effect!["isDefault"] = 0;
                }
                type.Data = data.ToJsonString();
                catalog.Update(type);
            }
            if (reason == "modifier")
            {
                var effect = catalog.Set<SourceRecord>().Find("client/dogmaeffects", "4559")!;
                effect.Data = effect.Data.Replace("LocationRequiredSkillModifier", "UnknownModifier", StringComparison.Ordinal);
                catalog.Update(effect);
            }
            catalog.SaveChanges();
            Assert.Null(FittingCatalog.Definition(catalog, 1977));
        }
        long module = f.Grant(1977);
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.PilotId, [module], f.StationId, (int)InventoryFlag.AutoFit));
        Assert.Contains(f.Inventory.List(f.PilotId, f.StationId), i => i.Id == module);
    }

    [Fact]
    public void RestartRetainsTheScriptAndPaymentButRemovesTheActiveBonus()
    {
        using Fixture f = Prepare(1973, 29009);
        long module = f.Fit(1973);
        Load(f, module, 29009);
        double before = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxTargetRange];
        Space(f);
        var activation = Activate(f, module);
        Assert.Equal(before * 1.5, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxTargetRange], 7);
        var magazine = Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId));
        WorldInitialization.Open(f.Db.Path);
        Assert.Equal(before, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxTargetRange], 7);
        Assert.Equal(magazine, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)));
        Assert.Throws<InvalidDataException>(() => Activate(f, module));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<ActiveFittedModule>());
        Assert.Equal(activation.Capacitor, world.Set<ShipEnergy>().Find(f.ShipId)!.Charge, 7);
    }

    [Fact]
    public void ProductionDispatchKeepsOneRepeatRingAndPaidStopRemovesExtendedTargetLocks()
    {
        using Fixture f = Prepare(1973, 29009);
        long module = f.Fit(1973);
        Load(f, module, 29009);
        Space(f);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        double range = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxTargetRange];
        var wing = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(wing.ShipId, pilot.Position! + new SpacePosition(range * 1.2, 0, 0));
        wing = f.Db.Store.FindPilot(1000002)!;
        using var scope = f.Db.Services.CreateScope();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create(p),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        using FlightSession observer = new(f.Db.Store, wing, f.Clock, f.Db.Services.GetRequiredService<SharedSpace>(), []);
        observer.Snapshot();
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        Assert.Equal(
            "UnableToTarget",
            Assert.IsType<PilotCallResult.NamedFailure>(targeting.Call(context, "dogmaIM", "AddTarget", [wing.ShipId])).Name
        );
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var controller = scope.ServiceProvider.GetRequiredService<SupportModuleHandler>();
        context.Notifications.Clear();
        object? ActivateWire() =>
            activation.CallWire(context, "dogmaIM", "Activate", [module, "sensorBoosterActivePercentage", null, 100L]);
        Assert.Null(ActivateWire());
        Assert.IsType<PilotCallResult.Failure>(
            controller.Call(context, "dogmaIM", "Activate", [module, "gunneryMaxRangeFalloffTrackingSpeedBonus", null, 100L])
        );
        using (WorldDbContext world = new(f.Db.Path))
        {
            double paid = world.Set<ShipEnergy>().Find(f.ShipId)!.Charge;
            Assert.Null(ActivateWire());
            Assert.Equal(paid, world.Set<ShipEnergy>().AsNoTracking().Single(e => e.ShipId == f.ShipId).Charge);
        }
        Assert.Single(context.Notifications, n => n.Name == "OnGodmaShipEffect");
        Assert.NotNull(controller.ActiveEffect(context.Pilot, module));
        Assert.Equal(range * 1.5, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxTargetRange], 7);
        Assert.Contains(
            context.Notifications.OfType<ModuleAttributesChanged>().SelectMany(n => n.Changes),
            c =>
                c.Item == new DogmaItemKey.Item(pilot.ShipId)
                && c.Attribute == DogmaAttributeId.MaxTargetRange
                && Math.Abs(c.Value - range * 1.5) < .00001
        );
        var locking = targeting.Call(context, "dogmaIM", "AddTarget", [wing.ShipId]);
        Assert.True(locking is PilotCallResult.Success, locking.ToString());
        f.Clock.Milliseconds += 5000;
        targeting.Advance(context);
        Assert.True(targeting.IsLocked(wing.ShipId));
        observer.RefreshPeers();
        Assert.Contains(
            observer.Updates,
            u =>
                u.Command is DestinyCommand.TimedSpecialEffect fx
                && fx.ModuleId == module
                && fx.Graphic == "effects.ElectronicAttributeModifyActivate"
                && fx.Start
        );
        observer.Updates.Clear();
        observer.Snapshot();
        Assert.Contains(
            observer.Updates,
            u =>
                u.Command is DestinyCommand.TimedSpecialEffect fx
                && fx.ModuleId == module
                && fx.Graphic == "effects.ElectronicAttributeModifyActivate"
                && fx.Start
        );
        f.Clock.Milliseconds += 5000;
        controller.Advance(context);
        Assert.Single(context.Notifications, n => n.Name == "OnGodmaShipEffect");
        Assert.Null(activation.CallWire(context, "dogmaIM", "Deactivate", [module, "sensorBoosterActivePercentage"]));
        Assert.Equal(range * 1.5, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxTargetRange], 7);
        f.Clock.Milliseconds += 10000;
        controller.Advance(context);
        targeting.Advance(context);
        Assert.False(targeting.IsLocked(wing.ShipId));
        Assert.Null(controller.ActiveEffect(context.Pilot, module));
        Assert.Equal(2, context.Notifications.Count(n => n.Name == "OnGodmaShipEffect"));
        Assert.Equal(range, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxTargetRange], 7);
        Assert.Equal(1, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
    }

    [Fact]
    public void AnUnpaidRepeatRemovesTheBonusAndDisconnectRetainsOnlyItsPaidDeadline()
    {
        using Fixture f = Prepare(1973);
        long module = f.Fit(1973);
        double before = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxTargetRange];
        Space(f);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using var scope = f.Db.Services.CreateScope();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create(p),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        var controller = scope.ServiceProvider.GetRequiredService<SupportModuleHandler>();
        Assert.Same(
            PilotCallResult.None,
            controller.Call(context, "dogmaIM", "Activate", [module, "sensorBoosterActivePercentage", null, 100L])
        );
        f.Clock.Milliseconds += 10000;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var energy = world.Set<ShipEnergy>().Find(f.ShipId)!;
            energy.Charge = 0;
            energy.UpdatedAt = f.Clock.Now;
            world.SaveChanges();
        }
        controller.Advance(context);
        Assert.Null(controller.ActiveEffect(context.Pilot, module));
        Assert.Equal(before, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxTargetRange], 7);
        f.Clock.Milliseconds += 100000;
        Assert.Same(
            PilotCallResult.None,
            controller.Call(context, "dogmaIM", "Activate", [module, "sensorBoosterActivePercentage", null, 100L])
        );
        controller.Dispose();
        Assert.Equal(before, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxTargetRange], 7);
        Assert.Throws<InvalidDataException>(() => Activate(f, module));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ActiveFittedModule>());
        Assert.Equal(f.Clock.Now + 10000 * TimeSpan.TicksPerMillisecond, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActiveAndPassiveTrackingEquipmentShareDiminishingReturnsInEitherActivationOrder(bool reverse)
    {
        using Fixture f = Prepare(1977, 1978);
        long gun = f.Fit(561);
        var before = f.Fitting.ModuleAttributes(f.PilotId, Item(f, gun));
        f.Fit(1998);
        long one = f.Fit(1977);
        long two = f.Fit(1978);
        Space(f);
        Activate(f, reverse ? two : one);
        Activate(f, reverse ? one : two);
        const double SecondWeight = .8691199808003975;
        const double ThirdWeight = .570583143510673;
        var after = f.Fitting.ModuleAttributes(f.PilotId, Item(f, gun));
        Assert.Equal(
            before[DogmaAttributeId.MaxRange] * 1.075 * (1 + .0725 * SecondWeight) * (1 + .05 * ThirdWeight),
            after[DogmaAttributeId.MaxRange],
            7
        );
        Assert.Equal(
            before[DogmaAttributeId.TrackingSpeed] * 1.15 * (1 + .10 * SecondWeight) * (1 + .07 * ThirdWeight),
            after[DogmaAttributeId.TrackingSpeed],
            7
        );
    }

    [Theory]
    [InlineData(1977, 28999, "gunneryMaxRangeFalloffTrackingSpeedBonus", "effects.TurretWeaponRangeTrackingSpeedMultiplyActivate")]
    [InlineData(35788, 35795, "missileGuidanceComputerBonus4", "effects.TurretWeaponRangeTrackingSpeedMultiplyActivate")]
    public void ComputerDispatchPrimesEffectiveScriptBonusesAndRetainsOneHudEffect(int type, int script, string effect, string graphic)
    {
        using Fixture f = Prepare(type, script);
        long module = f.Fit(type);
        Load(f, module, script);
        Space(f);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using var scope = f.Db.Services.CreateScope();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create(p),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        Assert.Null(activation.CallWire(context, "dogmaIM", "Activate", [module, effect, null, 100L]));
        var prime = scope.ServiceProvider.GetRequiredService<ShipDogmaBuilder>().PrimeModule(context.Pilot, Item(f, module));
        Assert.Contains(prime.ActiveEffects, e => e.Key == SupportModuleDefinitions.Effect(Item(f, module).GroupId));
        Assert.Contains(
            context.Flight.Updates,
            u => u.Command is DestinyCommand.TimedSpecialEffect fx && fx.ModuleId == module && fx.Graphic == graphic && fx.Start
        );
        f.Clock.Milliseconds += 10000;
        scope.ServiceProvider.GetRequiredService<SupportModuleHandler>().Advance(context);
        Assert.Single(context.Notifications, n => n.Name == "OnGodmaShipEffect");
        var attributes = f.Fitting.ModuleAttributes(f.PilotId, Item(f, module));
        Assert.Equal(
            type == 1977 ? 10 : -11,
            attributes[type == 1977 ? DogmaAttributeId.OptimalRangeBonus : DogmaAttributeId.ExplosionRadiusBonus],
            7
        );
    }
}
