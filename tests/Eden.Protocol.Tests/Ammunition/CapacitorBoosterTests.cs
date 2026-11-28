using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Thermal;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Ammunition;

public sealed class CapacitorBoosterTests
{
    private static IPilotAmmunition Ammo(Fixture f) => f.Db.Services.GetRequiredService<IPilotAmmunition>();

    private static IPilotCapacitorBoosting Boosting(Fixture f) => f.Db.Services.GetRequiredService<IPilotCapacitorBoosting>();

    private static long Prepare(Fixture f, int chargeType = 3554, int quantity = 9)
    {
        long module = f.Fit(CapacitorBoosterDefinitions.SmallTechOne);
        long stack = f.Grant(chargeType, quantity);
        _ = Ammo(f).Load(f.PilotId, f.ShipId, [module], [stack], f.StationId, f.Clock.Now);
        f.Clock.Milliseconds += 10000;
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        SetCharge(f, 25);
        return module;
    }

    private static void SetCharge(Fixture f, double charge)
    {
        using WorldDbContext world = new(f.Db.Path);
        var saved = world.Set<ShipEnergy>().Find(f.ShipId) ?? world.Add(new ShipEnergy { ShipId = f.ShipId }).Entity;
        saved.Charge = charge;
        saved.UpdatedAt = f.Clock.Now;
        world.SaveChanges();
    }

    private static PilotCallContext Client(Fixture f)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => new(f.Db.Store, selected, f.Clock, new(), f.Db.Objects),
            10,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        return context;
    }

    [Fact]
    public void ExistingTechOneAndTechTwoBoosterFixturesUseMediumSlots()
    {
        using Fixture f = new();
        using CatalogDbContext catalog = new(f.CatalogPath);
        var types = catalog.Set<ItemType>().Where(t => t.GroupId == CapacitorBoosterDefinitions.Group).ToArray();
        var supported = types.Select(t => FittingCatalog.Definition(catalog, t.Id)).OfType<ModuleFittingDefinition>().ToArray();
        Assert.Equal(9, supported.Length);
        Assert.All(supported, definition => Assert.Equal(ModuleRack.Medium, definition.Rack));
        Assert.NotNull(FittingCatalog.Definition(catalog, 3568));
    }

    [Theory]
    [InlineData(263, 12)]
    [InlineData(3554, 3)]
    [InlineData(11285, 1)]
    [InlineData(32006, 1)]
    public void MediumSlotMagazinesUseChargeVolumeRatherThanWeaponChargeSize(int chargeType, int expected)
    {
        using Fixture f = new();
        long module = f.Fit(CapacitorBoosterDefinitions.SmallTechOne);
        long stack = f.Grant(chargeType, 20);
        var result = Ammo(f).Load(f.PilotId, f.ShipId, [module], [stack], f.StationId, f.Clock.Now);
        var magazine = Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId));
        Assert.Equal((int)InventoryFlag.MediumSlot0, magazine.FlagId);
        Assert.Equal(expected, magazine.Quantity);
        Assert.Equal(f.Clock.Now + 10000 * TimeSpan.TicksPerMillisecond, magazine.ReadyAt);
        Assert.Equal(10000, result.ReloadMilliseconds);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == module);
        Assert.Equal(magazine.ReadyAt, f.Fitting.ModuleAttributes(f.PilotId, item)[DogmaAttributeId.NextActivationTime]);
        Assert.Throws<InvalidDataException>(() => Boosting(f).Inject(f.PilotId, module, f.Clock.Now));
        f.Clock.Milliseconds += 10000;
        var unloaded = Ammo(f).Unload(f.PilotId, f.ShipId, [module], f.StationId, (int)InventoryFlag.Hangar, null, f.Clock.Now);
        Assert.Equal(expected, Assert.Single(unloaded.Items).Item.Quantity);
        Assert.Empty(Ammo(f).Read(f.PilotId, f.ShipId));
    }

    [Fact]
    public void MixedModuleLoadRejectsAtomicallyAndOversizedChargesNeverLeaveTheHangar()
    {
        using Fixture f = new();
        long module = f.Fit(3566);
        long gun = f.Fit(561);
        long stack = f.Grant(3554, 9);
        Assert.Throws<InvalidDataException>(() => Ammo(f).Load(f.PilotId, f.ShipId, [module, gun], [stack], f.StationId, f.Clock.Now));
        long oversized = f.Grant(11289, 2);
        Assert.Throws<InvalidDataException>(() => Ammo(f).Load(f.PilotId, f.ShipId, [module], [oversized], f.StationId, f.Clock.Now));
        Assert.Empty(Ammo(f).Read(f.PilotId, f.ShipId));
        Assert.Equal(9, f.Inventory.List(f.PilotId, f.StationId).Single(i => i.Id == stack).Quantity);
        Assert.Equal(2, f.Inventory.List(f.PilotId, f.StationId).Single(i => i.Id == oversized).Quantity);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<ModuleCooldown>());
    }

    [Fact]
    public void InjectionConsumesOneChargeImmediatelyAndClampsWithoutCreatingCapacitor()
    {
        using Fixture f = new();
        long module = Prepare(f);
        var paid = Boosting(f).Inject(f.PilotId, module, f.Clock.Now);
        Assert.Equal(125, paid.Capacitor, 8);
        Assert.Equal(2, paid.Magazine.Quantity);
        Assert.Equal(15000, paid.Module.DurationMilliseconds);
        Assert.Throws<InvalidDataException>(() => Boosting(f).Inject(f.PilotId, module, f.Clock.Now));
        f.Clock.Milliseconds += 15000;
        var next = Boosting(f).Inject(f.PilotId, module, f.Clock.Now);
        Assert.Equal(f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.CapacitorCapacity], next.Capacitor, 8);
        Assert.Equal(1, next.Magazine.Quantity);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(next.Capacitor, world.Set<ShipEnergy>().Single().Charge, 8);
        Assert.Equal(f.Clock.Now + 15000 * TimeSpan.TicksPerMillisecond, world.Set<ModuleCooldown>().Single().ReadyAt);
    }

    [Fact]
    public void RepeatsRetainHudStateThenStopWhenTheLastChargeIsGone()
    {
        using Fixture f = new();
        long module = Prepare(f);
        using var context = Client(f);
        CapacitorBoosterHandler handler = new(f.Clock, Boosting(f));
        Assert.IsType<PilotCallResult.Success>(
            handler.Call(context, "dogmaIM", "Activate", [module, CapacitorBoosterHandler.EffectName, null, 100L])
        );
        _ = handler.Call(context, "dogmaIM", "Activate", [module, CapacitorBoosterHandler.EffectName, null, 100L]);
        Assert.Equal(2, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
        context.Notifications.Clear();
        f.Clock.Milliseconds += 15000;
        handler.Advance(context);
        Assert.Equal(1, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
        Assert.Empty(context.Notifications.OfType<ShipEffectChanged>());
        f.Clock.Milliseconds += 15000;
        handler.Advance(context);
        Assert.Empty(Ammo(f).Read(f.PilotId, f.ShipId));
        Assert.NotNull(handler.ActiveEffect(context.Pilot, module));
        f.Clock.Milliseconds += 15000;
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(context.Pilot, module));
        Assert.Single(context.Notifications.OfType<ShipEffectChanged>());
        Assert.Contains(
            context.Notifications.OfType<ModuleAttributesChanged>().SelectMany(n => n.Changes),
            change => change.Attribute == DogmaAttributeId.Quantity && change.Value == 0
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeBoosterReloadKeepsTheChargeHiddenUntilItsPaidWindowCompletes(bool reconnect)
    {
        using Fixture f = new();
        const int charge = CapacitorBoosterDefinitions.ChargeOneHundred;
        long module = Prepare(f, charge, 1);
        long spare = f.Grant(charge, 9, cargo: true);
        using var context = Client(f);
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var handler = scope.ServiceProvider.GetRequiredService<CapacitorBoosterHandler>();
        Assert.Same(
            PilotCallResult.None,
            router.Call(context, "dogmaIM", "Activate", new object?[] { module, CapacitorBoosterHandler.EffectName, null, 99L }, _ => { })
        );
        Assert.Empty(Ammo(f).Read(f.PilotId, f.ShipId));
        Assert.NotNull(handler.ActiveEffect(context.Pilot, module));
        f.Clock.Milliseconds += 15000;
        handler.Advance(context);
        Assert.False(context.Notifications.OfType<ShipEffectChanged>().Last().Active);

        context.Notifications.Clear();
        Assert.Same(
            PilotCallResult.None,
            router.Call(context, "dogmaIM", "LoadAmmo", new object?[] { f.ShipId, module, spare, f.ShipId }, _ => { })
        );
        var reloading = Assert.Single(context.Notifications.OfType<ChargeLoading>());
        Assert.Equal(module, Assert.Single(Assert.IsType<List<object?>>(reloading.Arguments[0])));
        Assert.Equal(charge, reloading.Arguments[1]);
        Assert.Equal(10000d, reloading.Arguments[2]);
        var started = Assert.Single(context.Notifications.OfType<ModuleAttributesChanged>());
        var ready = Assert.Single(started.Changes, change => change.Attribute == DogmaAttributeId.NextActivationTime);
        Assert.Equal((double)(f.Clock.Now + 10000 * TimeSpan.TicksPerMillisecond), ready.Value);
        Assert.True(context.Notifications.IndexOf(started) < context.Notifications.IndexOf(reloading));
        Assert.Empty(context.Notifications.OfType<GodmaItemPrimed>());
        Assert.Equal(3, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
        Assert.Throws<InvalidDataException>(() => Boosting(f).Inject(f.PilotId, module, f.Clock.Now));

        using var reopened = f.Db.Services.CreateScope();
        var services = reconnect ? reopened.ServiceProvider : scope.ServiceProvider;
        var dogma = services.GetRequiredService<ShipDogmaBuilder>();
        var pending = dogma.AllInfo(context.Pilot);
        Assert.Empty(pending.Charges);
        Assert.Empty(pending.ShipState.Quantities);
        Assert.Equal(ready.Value, pending.ShipInfo[module].Attributes[DogmaAttributeId.NextActivationTime]);
        handler = services.GetRequiredService<CapacitorBoosterHandler>();
        context.Notifications.Clear();
        f.Clock.Milliseconds += 9999;
        handler.Advance(context);
        Assert.Empty(context.Notifications);
        f.Clock.Milliseconds++;
        handler.Advance(context);

        var prime = Assert.Single(context.Notifications.OfType<GodmaItemPrimed>());
        Assert.Equal(3d, prime.Item.Attributes[DogmaAttributeId.Quantity]);
        Assert.Contains(
            context.Notifications.OfType<ModuleAttributesChanged>().SelectMany(update => update.Changes),
            change => change.Attribute == DogmaAttributeId.NextActivationTime && change.Value == 0
        );
        Assert.Empty(context.Notifications.OfType<ChargeLoading>());
        context.Notifications.Clear();
        handler.Advance(context);
        Assert.Empty(context.Notifications);
        Assert.Same(
            PilotCallResult.None,
            handler.Call(context, "dogmaIM", "Activate", [module, CapacitorBoosterHandler.EffectName, null, 0L])
        );
        Assert.Equal(2, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
    }

    [Fact]
    public void NativeBoosterCyclePublishesThePaidDurationWhenOverloadChanges()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadActiveTiers(
            f.CatalogPath,
            new HashSet<int> { CapacitorBoosterDefinitions.SmallTechOne, CapacitorBoosterDefinitions.ChargeOneHundred }
        );
        PassiveCatalogFixture.LoadOverload(f.CatalogPath);
        f.Train(OverloadRules.ThermodynamicsSkill, 3);
        long module = Prepare(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var context = Client(f);
        using var scope = f.Db.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<CapacitorBoosterHandler>();
        var overloading = f.Db.Services.GetRequiredService<IPilotOverloading>();
        int effect = Assert
            .Single(
                f.Db.Services.GetRequiredService<IPilotModuleConditions>().Read(f.PilotId, f.ShipId),
                condition => condition.Condition.ItemId == module
            )
            .OverloadEffect;
        Assert.Same(
            PilotCallResult.None,
            handler.Call(context, "dogmaIM", "Activate", [module, CapacitorBoosterHandler.EffectName, null, 2L])
        );
        var started = Assert.Single(context.Notifications.OfType<ShipEffectChanged>());
        double cold = started.Effect.DurationMilliseconds!.Value;
        Assert.Equal(cold, PaidDuration());
        Assert.IsType<ModuleAttributesChanged>(context.Notifications[0]);
        overloading.Set(new(f.PilotId, f.ShipId, module, effect, OverloadScope.Module, true, f.Clock.Now));

        context.Notifications.Clear();
        f.Clock.Milliseconds += (long)cold;
        handler.Advance(context);
        double hot = PaidDuration();
        Assert.True(hot < cold);
        Assert.Equal(hot, handler.ActiveEffect(context.Pilot, module)!.DurationMilliseconds);
        Assert.Empty(context.Notifications.OfType<ShipEffectChanged>());
        var dogma = scope.ServiceProvider.GetRequiredService<ShipDogmaBuilder>();
        var fitted = Assert.Single(f.Inventory.List(f.PilotId, f.ShipId), item => item.Id == module);
        Assert.Equal(hot, dogma.PrimeModule(context.Pilot, fitted).Attributes[DogmaAttributeId.Duration]);

        overloading.Set(new(f.PilotId, f.ShipId, module, effect, OverloadScope.Module, false, f.Clock.Now));
        context.Notifications.Clear();
        f.Clock.Milliseconds += (long)Math.Ceiling(hot);
        handler.Advance(context);
        Assert.Equal(cold, PaidDuration());
        Assert.Empty(context.Notifications.OfType<ShipEffectChanged>());
        Assert.Empty(Ammo(f).Read(f.PilotId, f.ShipId));

        double PaidDuration() =>
            Assert
                .Single(
                    context.Notifications.OfType<ModuleAttributesChanged>().SelectMany(update => update.Changes),
                    change => change.Item == new DogmaItemKey.Item(module) && change.Attribute == DogmaAttributeId.Duration
                )
                .Value;
    }

    [Fact]
    public void ManualStopAndRestartNeverInjectOrConsumeAdditionalCharges()
    {
        using Fixture f = new();
        long module = Prepare(f);
        using var context = Client(f);
        CapacitorBoosterHandler handler = new(f.Clock, Boosting(f));
        _ = handler.Call(context, "dogmaIM", "Activate", [module, CapacitorBoosterHandler.EffectName, null, 100L]);
        _ = handler.Call(context, "dogmaIM", "Deactivate", [module, CapacitorBoosterHandler.EffectName]);
        f.Clock.Milliseconds += 15000;
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(context.Pilot, module));
        _ = WorldInitialization.Open(f.Db.Path);
        f.Clock.Milliseconds += 60000;
        Assert.Equal(2, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(125, world.Set<ShipEnergy>().Single().Charge, 8);
    }

    [Fact]
    public void ReloadSurvivesRestartAndUnfittingReturnsLoadedChargesWithTheModule()
    {
        using Fixture f = new();
        long module = f.Fit(3566);
        long stack = f.Grant(3554, 9);
        _ = Ammo(f).Load(f.PilotId, f.ShipId, [module], [stack], f.StationId, f.Clock.Now);
        long deadline = Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).ReadyAt;
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Equal(deadline, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).ReadyAt);
        Assert.Throws<InvalidDataException>(() =>
            Ammo(f).Unload(f.PilotId, f.ShipId, [module], f.StationId, (int)InventoryFlag.Hangar, null, f.Clock.Now)
        );
        f.Clock.Milliseconds += 10000;
        var returned = f.Inventory.Move(f.PilotId, [module], f.ShipId, f.StationId, (int)InventoryFlag.Hangar, null);
        Assert.Contains(returned, change => change.Item.Id == module && !change.Item.Online);
        Assert.Contains(returned, change => change.Item.TypeId == 3554 && change.Item.Quantity == 3);
        Assert.Equal(9, f.Inventory.List(f.PilotId, f.StationId).Where(i => i.TypeId == 3554).Sum(i => i.Quantity));
        Assert.Empty(Ammo(f).Read(f.PilotId, f.ShipId));
    }

    [Fact]
    public void WarpSafeActivationIsCancelledAtHullLossWithoutAnotherInjection()
    {
        using Fixture f = new();
        long module = Prepare(f);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        f.Db.Store.SetPosition(f.ShipId, new(pilot.X + 1000000, pilot.Y, pilot.Z));
        using var context = Client(f);
        Assert.Null(context.Flight.CallWire("beyonce", "CmdWarpToStuff", ["item", (long)context.Pilot.StationId]));
        f.Clock.Seconds += 10;
        context.Flight.Checkpoint();
        Assert.True(context.Flight.InWarp);
        Assert.False(context.Flight.CanTarget);
        CapacitorBoosterHandler handler = new(f.Clock, Boosting(f));
        Assert.IsType<PilotCallResult.Success>(
            handler.Call(context, "dogmaIM", "Activate", [module, CapacitorBoosterHandler.EffectName, null, 100L])
        );
        context.Flight.BeginShipLoss();
        f.Clock.Milliseconds += 15000;
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(context.Pilot, module));
        Assert.Equal(2, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("docked")]
    [InlineData("foreign")]
    [InlineData("skill")]
    [InlineData("empty")]
    [InlineData("invalid")]
    public void RejectedInjectionNeverChangesChargeOrMagazine(string reason)
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
                world.Set<PilotSkill>().Find(f.PilotId, 3417)!.Points = 0;
            }
            if (reason == "empty")
            {
                world.Remove(world.Set<OwnedItem>().Single(i => i.LocationId == module));
            }
            world.SaveChanges();
        }
        if (reason == "docked")
        {
            f.Db.Store.SetPosition(f.ShipId, null);
        }
        if (reason == "invalid")
        {
            using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=0 WHERE ItemTypeId=3554 AND AttributeId=67");
        }
        Assert.Throws<InvalidDataException>(() => Boosting(f).Inject(f.PilotId, module, f.Clock.Now));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(25, saved.Set<ShipEnergy>().Single().Charge, 8);
        if (reason != "empty")
        {
            Assert.Equal(3, saved.Set<OwnedItem>().Single(i => i.LocationId == module).Quantity);
        }
        Assert.Equal(ModuleCooldownKind.Reload, saved.Set<ModuleCooldown>().Single().Kind);
    }

    [Fact]
    public void NativePrimingIncludesTheMediumSlotMagazineAndItsBoosterEffect()
    {
        using Fixture f = new();
        long module = Prepare(f);
        using var context = Client(f);
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Assert.Null(
            router.CallWire(
                context,
                "dogmaIM",
                "Activate",
                new object?[] { module, CapacitorBoosterHandler.EffectName, null, 0L },
                _ => { }
            )
        );
        var response = Assert.IsType<PilotCallResult.Success>(
            router.Call(context, "dogmaIM", "GetAllInfo", Array.Empty<object?>(), _ => { })
        );
        Assert.NotEmpty(BlueMarshal.Encode(response.Reply));
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CapacitorBoosterHandler>().ActiveEffect(context.Pilot, module));
        Assert.Equal((int)InventoryFlag.MediumSlot0, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).FlagId);
    }
}
