using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Repair;

public sealed class HardenerTests
{
    private static IPilotHardening Store(Fixture f) => f.Db.Services.GetRequiredService<IPilotHardening>();

    private static PilotCallContext Client(Fixture f, SharedSpace? space = null)
    {
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected =>
                new(f.Db.Store, selected, f.Clock, space ?? new(), f.Db.Objects, health: f.Db.Services.GetRequiredService<IPilotHealth>()),
            10,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        return context;
    }

    private static HardenerActivation Activate(Fixture f, long module, DogmaEffect effect = DogmaEffect.ActiveShieldResistance) =>
        Store(f).Activate(f.PilotId, module, effect, f.Clock.Now);

    [Fact]
    public void OrdinaryTechOneProfilesAreCompleteAndTechTwoRemainsExcluded()
    {
        using Fixture f = new();
        using CatalogDbContext catalog = new(f.CatalogPath);
        var types = catalog.Set<ItemType>().Where(t => t.GroupId == 77 || t.GroupId == 328).ToArray();
        var supported = types.Where(t => FittingCatalog.Definition(catalog, t.Id) is not null).ToArray();
        Assert.Equal(15, supported.Count(t => t.GroupId == HardenerDefinitions.ShieldGroup));
        Assert.Equal(12, supported.Count(t => t.GroupId == HardenerDefinitions.ArmorGroup));
        Assert.All(
            supported,
            t => Assert.Equal(t.GroupId == 77 ? ModuleRack.Medium : ModuleRack.Low, FittingCatalog.Definition(catalog, t.Id)!.Rack)
        );
        Assert.Null(FittingCatalog.Definition(catalog, 2301));
        Assert.Null(FittingCatalog.Definition(catalog, 11642));
    }

    [Theory]
    [InlineData(2293, DogmaEffect.ActiveShieldResistance, DogmaAttributeId.ShieldEmResonance, 20, 10000)]
    [InlineData(11301, DogmaEffect.ActiveArmorResistance, DogmaAttributeId.ArmorEmResonance, 30, 20000)]
    public void PaidActivationChangesRealDefenseWithoutRepairingHealth(
        int typeId,
        DogmaEffect effect,
        DogmaAttributeId resistance,
        double cost,
        double duration
    )
    {
        using Fixture f = new();
        long module = f.Fit(typeId);
        var before = f.Fitting.ShipAttributes(f.PilotId);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        double capacitor = f.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.PilotId, f.Clock.Now);
        var paid = Activate(f, module, effect);
        Assert.Equal(capacitor - cost, paid.Capacitor, 8);
        Assert.Equal(duration, paid.Module.DurationMilliseconds);
        Assert.Equal(before[resistance] * 0.5, paid.ShipAttributes[resistance], 8);
        Assert.Throws<InvalidDataException>(() => Activate(f, module, effect));
        var health = f.Db.Services.GetRequiredService<IPilotHealth>().Read(f.PilotId, f.Clock.Now);
        Assert.Equal(health.Defense.Undamaged, health.Remaining);
        bool armor = effect == DogmaEffect.ActiveArmorResistance;
        var remaining = armor ? health.Remaining with { Shield = 0 } : health.Remaining;
        var incoming = CombatDamage.Apply(remaining, health.Defense, new(10, 0, 0, 0));
        Assert.Equal(10 * (armor ? health.Defense.ArmorResonance.Em : health.Defense.ShieldResonance.Em), incoming.Applied, 8);
        var restored = Store(f).Stop(f.PilotId, f.ShipId, module);
        Assert.Equal(before[resistance], restored[resistance], 8);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ActiveHardener>());
        Assert.Equal(capacitor - cost, saved.Set<ShipEnergy>().Single().Charge, 8);
        Assert.Equal(f.Clock.Now + duration * TimeSpan.TicksPerMillisecond, saved.Set<ModuleCooldown>().Single().ReadyAt);
    }

    [Fact]
    public void TwoShieldHardenersHaveDiminishingReturnsAndNoPassiveCompensationBonus()
    {
        using Fixture f = new();
        long first = f.Fit(2293);
        long second = f.Fit(2293);
        f.Train(12365, 5); // EM Shield Compensation applies to passive amplifiers.
        double basis = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ShieldEmResonance];
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        _ = Activate(f, first);
        var two = Activate(f, second);
        Assert.Equal(basis * 0.5 * (1 - 0.5 * Math.Exp(-Math.Pow(1 / 2.67, 2))), two.ShipAttributes[DogmaAttributeId.ShieldEmResonance], 8);
        var one = Store(f).Stop(f.PilotId, f.ShipId, first);
        Assert.Equal(basis * 0.5, one[DogmaAttributeId.ShieldEmResonance], 8);
    }

    [Theory]
    [InlineData(1197, true)]
    [InlineData(2046, false)]
    public void ArmorHardenersSharePassiveStackingButDamageControlIsExempt(int passiveType, bool penalized)
    {
        using Fixture f = new();
        long module = f.Fit(11301);
        double basis = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ArmorEmResonance];
        _ = f.Fit(passiveType);
        double passive = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ArmorEmResonance] / basis;
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        var paid = Activate(f, module, DogmaEffect.ActiveArmorResistance);
        double expected = penalized ? 1 + (passive - 1) * Math.Exp(-Math.Pow(1 / 2.67, 2)) : passive;
        Assert.Equal(basis * 0.5 * expected, paid.ShipAttributes[DogmaAttributeId.ArmorEmResonance], 8);
    }

    [Fact]
    public void RepeatsRetainOneBonusAndHudStateWhileManualStopFinishesThePaidCycle()
    {
        using Fixture f = new();
        long module = f.Fit(2293);
        double basis = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ShieldEmResonance];
        using var context = Client(f);
        using HardenerHandler handler = new(f.Clock, Store(f));
        Assert.IsType<PilotCallResult.Success>(
            handler.Call(context, "dogmaIM", "Activate", [module, HardenerWireDefinitions.ShieldEffectName, null, 100L])
        );
        using (WorldDbContext saved = new(f.Db.Path))
        {
            double charge = saved.Set<ShipEnergy>().Single().Charge;
            _ = handler.Call(context, "dogmaIM", "Activate", [module, HardenerWireDefinitions.ShieldEffectName, null, 100L]);
            Assert.Equal(charge, saved.Set<ShipEnergy>().AsNoTracking().Single().Charge);
        }
        context.Notifications.Clear();
        f.Clock.Milliseconds += 10000;
        handler.Advance(context);
        Assert.Empty(context.Notifications.OfType<ShipEffectChanged>());
        Assert.Equal(basis * 0.5, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ShieldEmResonance], 8);
        _ = handler.Call(context, "dogmaIM", "Deactivate", [module, HardenerWireDefinitions.ShieldEffectName]);
        f.Clock.Milliseconds += 9999;
        handler.Advance(context);
        Assert.NotNull(handler.ActiveEffect(context.Pilot, module));
        f.Clock.Milliseconds++;
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(context.Pilot, module));
        Assert.Equal(basis, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ShieldEmResonance], 8);
        Assert.Single(context.Notifications.OfType<ShipEffectChanged>());
    }

    [Fact]
    public void UnpaidRepeatAndDisconnectRemoveResistanceWithoutRefundingCapacitorOrCooldown()
    {
        using Fixture f = new();
        long module = f.Fit(2293);
        double basis = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ShieldEmResonance];
        using var context = Client(f);
        using HardenerHandler handler = new(f.Clock, Store(f));
        _ = handler.Call(context, "dogmaIM", "Activate", [module, HardenerWireDefinitions.ShieldEffectName, null, 100L]);
        using (WorldDbContext saved = new(f.Db.Path))
        {
            saved.Set<ShipEnergy>().Single().Charge = 0;
            saved.Set<ShipEnergy>().Single().UpdatedAt = f.Clock.Now + 10000 * TimeSpan.TicksPerMillisecond;
            saved.SaveChanges();
        }
        f.Clock.Milliseconds += 10000;
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(context.Pilot, module));
        Assert.Equal(basis, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ShieldEmResonance], 8);
        using (WorldDbContext saved = new(f.Db.Path))
        {
            saved.Set<ShipEnergy>().Single().Charge = 100;
            saved.SaveChanges();
        }
        _ = handler.Call(context, "dogmaIM", "Activate", [module, HardenerWireDefinitions.ShieldEffectName, null, 100L]);
        handler.Dispose();
        Assert.Equal(basis, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ShieldEmResonance], 8);
        using WorldDbContext check = new(f.Db.Path);
        Assert.Empty(check.Set<ActiveHardener>());
        Assert.Equal(80, check.Set<ShipEnergy>().Single().Charge, 8);
        Assert.True(check.Set<ModuleCooldown>().Single().ReadyAt > f.Clock.Now);
    }

    [Fact]
    public void RestartClearsOrphanedActivationButKeepsItsPaymentAndCooldown()
    {
        using Fixture f = new();
        long module = f.Fit(2293);
        double basis = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ShieldEmResonance];
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        var paid = Activate(f, module);
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Equal(basis, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ShieldEmResonance], 8);
        Assert.Throws<InvalidDataException>(() => Activate(f, module));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ActiveHardener>());
        Assert.Equal(paid.Capacitor, saved.Set<ShipEnergy>().Single().Charge);
        Assert.True(saved.Set<ModuleCooldown>().Single().ReadyAt > f.Clock.Now);
    }

    [Fact]
    public void HardenersAreWarpSafeButTheHullLossTransitionCancelsTheirBonus()
    {
        using Fixture f = new();
        long module = f.Fit(2293);
        using var context = Client(f);
        using HardenerHandler handler = new(f.Clock, Store(f));
        Assert.Null(context.Flight.CallWire("beyonce", "CmdWarpToStuff", ["item", (long)context.Pilot.StationId]));
        Assert.False(context.Flight.CanTarget);
        Assert.IsType<PilotCallResult.Success>(
            handler.Call(context, "dogmaIM", "Activate", [module, HardenerWireDefinitions.ShieldEffectName, null, 100L])
        );
        context.Flight.BeginShipLoss();
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(context.Pilot, module));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ActiveHardener>());
    }

    [Fact]
    public void VersionTwentyNineMigrationRetainsEquipmentAndCooldownsWithoutCreatingAnActivation()
    {
        using Fixture f = new();
        long module = f.Fit(2293);
        long deadline = f.Clock.Now + TimeSpan.TicksPerMinute;
        var before = f.Inventory.List(f.PilotId, f.ShipId);
        using (WorldDbContext saved = new(f.Db.Path))
        {
            saved.Add(new ModuleCooldown { ModuleId = module, ReadyAt = deadline });
            saved.SaveChanges();
            saved.Database.ExecuteSqlRaw("DROP TABLE WorldActiveHardener; PRAGMA user_version=29");
        }
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Equal(before, f.Inventory.List(f.PilotId, f.ShipId));
        using WorldDbContext check = new(f.Db.Path);
        Assert.Empty(check.Set<ActiveHardener>());
        Assert.Equal(deadline, check.Set<ModuleCooldown>().Single().ReadyAt);
        Assert.Equal(WorldMigration.CurrentVersion, check.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("docked")]
    [InlineData("foreign")]
    [InlineData("skill")]
    [InlineData("slot")]
    [InlineData("effect")]
    [InlineData("duration")]
    public void InvalidStartsNeverPayOrGrantResistance(string reason)
    {
        using Fixture f = new();
        long module = f.Fit(2293);
        f.Db.Store.SetPosition(f.ShipId, reason == "docked" ? null : SpacePosition.Zero);
        using (WorldDbContext saved = new(f.Db.Path))
        {
            var item = saved.Set<OwnedItem>().Find(module)!;
            if (reason == "offline")
            {
                item.Online = false;
            }
            if (reason == "foreign")
            {
                item.OwnerId = 90000002;
            }
            if (reason == "slot")
            {
                item.FlagId = (int)InventoryFlag.LowSlot0;
            }
            if (reason == "skill")
            {
                saved.Set<PilotSkill>().Find(f.PilotId, 3420)!.Points = 0;
            }
            saved.SaveChanges();
        }
        if (reason == "duration")
        {
            using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=0 WHERE ItemTypeId=2293 AND AttributeId=73");
        }
        var effect = reason == "effect" ? DogmaEffect.ActiveArmorResistance : DogmaEffect.ActiveShieldResistance;
        Assert.Throws<InvalidDataException>(() => Activate(f, module, effect));
        using WorldDbContext check = new(f.Db.Path);
        Assert.Empty(check.Set<ActiveHardener>());
        Assert.Empty(check.Set<ShipEnergy>());
        Assert.Empty(check.Set<ModuleCooldown>());
    }

    [Fact]
    public void RouterPrimesTheNativeActiveEffectAndPeersReceiveItsGraphic()
    {
        using Fixture f = new();
        long module = f.Fit(2293);
        SharedSpace space = new();
        using var context = Client(f, space);
        var wing = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(wing.ShipId, new(100, 0, 0));
        using FlightSession observer = new(f.Db.Store, f.Db.Store.FindPilot(1000002)!, f.Clock, space, []);
        observer.Snapshot();
        observer.Updates.Clear();
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Assert.Null(
            router.CallWire(
                context,
                "dogmaIM",
                "Activate",
                new object?[] { module, HardenerWireDefinitions.ShieldEffectName, null, 0L },
                _ => { }
            )
        );
        var info = Assert.IsType<PilotCallResult.Success>(router.Call(context, "dogmaIM", "GetAllInfo", Array.Empty<object?>(), _ => { }));
        Assert.NotEmpty(BlueMarshal.Encode(info.Reply));
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<HardenerHandler>().ActiveEffect(context.Pilot, module));
        observer.RefreshPeers();
        var actions = observer.WireUpdates().Cast<object?[]>().Select(u => (object?[])u[1]!).ToArray();
        Assert.Contains(
            actions,
            a =>
                Equals(a[0], "OnSpecialFX")
                && Equals(((object?[])a[1]!)[5], HardenerWireDefinitions.Graphic(DogmaEffect.ActiveShieldResistance))
        );
    }
}
