using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Repair;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Repair;

public sealed partial class AncillaryRepairTests
{
    private static PilotCallContext Client(Fixture f, long account)
    {
        var pilot = f.Db.Store.FindPilot(account)!;
        var shared = f.Db.Services.GetRequiredService<SharedSpace>();
        PilotCallContext context = new(
            account,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, shared, f.Db.Objects, health: f.Db.Services.GetRequiredService<IPilotHealth>()),
            account,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        context.Flight.Updates.Clear();
        return context;
    }

    [Theory]
    [InlineData(SmallShield, Navy25, "fueledShieldBoosting", DogmaEffect.AncillaryShieldBoosting, "effects.ShieldBoosting")]
    [InlineData(SmallArmor, Paste, "fueledArmorRepair", DogmaEffect.AncillaryArmorRepair, "effects.ArmorRepair")]
    public void NativeDepletionFinishesThePaidCycleThenPermitsARealClientReload(
        int type,
        int fuel,
        string effect,
        DogmaEffect effectId,
        string graphic
    )
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        var loaded = Load(f, module, fuel, 1);
        SpaceAndDamage(f);
        var sourcePilot = f.Db.Store.FindPilot(1000001)!;
        var wing = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(wing.ShipId, sourcePilot.Position! + new SpacePosition(1000, 0, 0));
        long cargo = f.Grant(fuel, 20, cargo: true);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var context = Client(f, 1000001);
        using var observer = Client(f, 1000002);
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var shields = scope.ServiceProvider.GetRequiredService<ShieldBoostHandler>();
        var armor = scope.ServiceProvider.GetRequiredService<LocalRepairHandler>();
        context.Notifications.Clear();
        Assert.IsType<PilotCallResult.Success>(activation.Call(context, "dogmaIM", "Activate", [module, effect, null, 3L]));
        var prime = type == SmallShield ? shields.ActiveEffect(context.Pilot, module) : armor.ActiveEffect(context.Pilot, module);
        Assert.NotNull(prime);
        Assert.Equal(effectId, prime.Effect);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == module);
        var dogma = scope.ServiceProvider.GetRequiredService<ShipDogmaBuilder>().PrimeModule(context.Pilot, item);
        Assert.Contains(dogma.ActiveEffects, entry => entry.Key == effectId);
        Assert.Contains(
            context.Notifications.OfType<ModuleAttributesChanged>().SelectMany(n => n.Changes),
            c => c.Attribute == DogmaAttributeId.Quantity && c.Item == MagazineWire.Key(loaded) && Convert.ToDouble(c.Value) == 0
        );
        observer.Flight.RefreshPeers();
        Assert.Contains(
            observer.Flight.Updates,
            u => u.Command is DestinyCommand.TimedSpecialEffect fx && fx.ModuleId == module && fx.Graphic == graphic && fx.Start
        );
        f.Clock.Milliseconds += (long)prime.DurationMilliseconds!.Value;
        shields.Advance(context);
        armor.Advance(context);
        Assert.Null(type == SmallShield ? shields.ActiveEffect(context.Pilot, module) : armor.ActiveEffect(context.Pilot, module));
        Assert.Single(context.Notifications.OfType<ShipEffectChanged>(), e => e.Effect.Effect == effectId && e.Active);
        Assert.Single(context.Notifications.OfType<ShipEffectChanged>(), e => e.Effect.Effect == effectId && !e.Active);
        Assert.Empty(Ammo(f).Read(f.PilotId, f.ShipId));
        var handler = scope.ServiceProvider.GetRequiredService<AmmunitionHandler>();
        Assert.IsType<PilotCallResult.Success>(handler.Call(context, "dogmaIM", "LoadAmmo", [f.ShipId, module, cargo, f.ShipId]));
        long ready = Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).ReadyAt;
        Assert.Equal(f.Clock.Now + TimeSpan.FromSeconds(60).Ticks, ready);
        Assert.IsType<PilotCallResult.Failure>(activation.Call(context, "dogmaIM", "Activate", [module, effect, null, 0L]));
        f.Clock.Milliseconds += 60000;
        Assert.IsType<PilotCallResult.Success>(activation.Call(context, "dogmaIM", "Activate", [module, effect, null, 0L]));
    }

    [Theory]
    [InlineData(SmallShield, "fueledShieldBoosting")]
    [InlineData(SmallArmor, "fueledArmorRepair")]
    public void EmptyAncillaryActivationCanRepeatInItsUnfueledModeAndManualStopFinishesThePaidInterval(int type, string effect)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        SpaceAndDamage(f);
        using var scope = f.Db.Services.CreateScope();
        using var context = Client(f, 1000001);
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var shields = scope.ServiceProvider.GetRequiredService<ShieldBoostHandler>();
        var armor = scope.ServiceProvider.GetRequiredService<LocalRepairHandler>();
        Assert.IsType<PilotCallResult.Success>(activation.Call(context, "dogmaIM", "Activate", [module, effect, null, 3L]));
        var prime = type == SmallShield ? shields.ActiveEffect(context.Pilot, module) : armor.ActiveEffect(context.Pilot, module);
        f.Clock.Milliseconds += (long)prime!.DurationMilliseconds!.Value;
        shields.Advance(context);
        armor.Advance(context);
        Assert.NotNull(type == SmallShield ? shields.ActiveEffect(context.Pilot, module) : armor.ActiveEffect(context.Pilot, module));
        Assert.IsType<PilotCallResult.Success>(activation.Call(context, "dogmaIM", "Deactivate", [module, effect]));
        f.Clock.Milliseconds += (long)prime.DurationMilliseconds.Value;
        shields.Advance(context);
        armor.Advance(context);
        Assert.Null(type == SmallShield ? shields.ActiveEffect(context.Pilot, module) : armor.ActiveEffect(context.Pilot, module));
    }

    [Theory]
    [InlineData(SmallShield, Navy25, "fueledShieldBoosting")]
    [InlineData(SmallArmor, Paste, "fueledArmorRepair")]
    public void WarmAncillaryHandlerFramesDoNotReadSqlOpenConnectionsOrRecalculateFits(int type, int fuel, string effect)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        Load(f, module, fuel);
        SpaceAndDamage(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var context = Client(f, 1000001);
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var shields = scope.ServiceProvider.GetRequiredService<ShieldBoostHandler>();
        var armor = scope.ServiceProvider.GetRequiredService<LocalRepairHandler>();
        Assert.IsType<PilotCallResult.Success>(activation.Call(context, "dogmaIM", "Activate", [module, effect, null, 0L]));
        shields.Advance(context);
        armor.Advance(context);
        using var metrics = SessionPerformance.Begin("warm ancillary repair handlers", _ => { }, force: true);
        for (int frame = 0; frame < 12; frame++)
        {
            f.Clock.Milliseconds += 50;
            shields.Advance(context);
            armor.Advance(context);
        }
        Assert.Equal(0, metrics!.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }
}
