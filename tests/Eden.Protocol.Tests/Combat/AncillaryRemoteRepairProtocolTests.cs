using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Repair;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Repair;
using Eden.Protocol.Targeting;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class AncillaryRemoteRepairTests
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

    private static void Lock(Fixture f, IServiceProvider services, PilotCallContext source)
    {
        var targeting = services.GetRequiredService<TargetingHandler>();
        targeting.CallWire(source, "dogmaIM", "AddTarget", [Target(f)]);
        f.Clock.Seconds += 30;
        targeting.Advance(source);
        Assert.True(targeting.IsLocked(Target(f)));
    }

    [Theory]
    [InlineData(SmallShield, Navy25, "shipModuleAncillaryRemoteShieldBooster", DogmaEffect.AncillaryRemoteShieldBoost)]
    [InlineData(SmallArmor, Paste, "shipModuleAncillaryRemoteArmorRepairer", DogmaEffect.AncillaryRemoteArmorRepair)]
    public void NativePaidDepletionPublishesFuelEffectAndRecipientUpdatesBeforeRealCargoReload(
        int type,
        int fuel,
        string effect,
        DogmaEffect effectId
    )
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        var loaded = Load(f, module, fuel, 1);
        long cargo = f.Grant(fuel, 20, cargo: true);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var recipientScope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var target = Client(f, 1000002);
        Lock(f, scope.ServiceProvider, source);
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<RemoteRepairHandler>();
        var recipient = recipientScope.ServiceProvider.GetRequiredService<RemoteRepairHandler>();
        recipient.Enter(target);
        source.Notifications.Clear();
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Activate", [module, effect, Target(f), 3L]));
        var prime = handler.ActiveEffect(source.Pilot, module);
        Assert.NotNull(prime);
        Assert.Equal(effectId, prime.Effect);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == module);
        var dogma = scope.ServiceProvider.GetRequiredService<ShipDogmaBuilder>().PrimeModule(source.Pilot, item);
        Assert.Contains(dogma.ActiveEffects, e => e.Key == effectId);
        Assert.Contains(
            source.Notifications.OfType<ModuleAttributesChanged>().SelectMany(n => n.Changes),
            c => c.Attribute == DogmaAttributeId.Quantity && c.Item == MagazineWire.Key(loaded) && Convert.ToDouble(c.Value) == 0
        );
        target.Flight.RefreshPeers();
        Assert.Contains(
            target.Flight.Updates,
            u =>
                u.Command is DestinyCommand.TimedSpecialEffect fx
                && fx.ModuleId == module
                && fx.TargetId == Target(f)
                && fx.Graphic == RemoteRepairWire.Graphic(effectId)
                && fx.Start
        );
        Assert.Throws<InvalidDataException>(() => Ammo(f).Load(f.PilotId, f.ShipId, [module], [cargo], f.ShipId, f.Clock.Now));
        f.Clock.Milliseconds += (long)prime.DurationMilliseconds!.Value;
        handler.Advance(source);
        recipient.Advance(target);
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
        Assert.Single(source.Notifications.OfType<ShipEffectChanged>(), e => e.Effect.Effect == effectId && e.Active);
        Assert.Single(source.Notifications.OfType<ShipEffectChanged>(), e => e.Effect.Effect == effectId && !e.Active);
        Assert.Contains(
            target.Notifications.OfType<ModuleAttributesChanged>().SelectMany(n => n.Changes),
            c => c.Attribute == (type == SmallShield ? DogmaAttributeId.ShieldCharge : DogmaAttributeId.ArmorDamage)
        );
        var loader = scope.ServiceProvider.GetRequiredService<AmmunitionHandler>();
        Assert.IsType<PilotCallResult.Success>(loader.Call(source, "dogmaIM", "LoadAmmo", [f.ShipId, module, cargo, f.ShipId]));
        Assert.Equal(f.Clock.Now + TimeSpan.FromSeconds(60).Ticks, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).ReadyAt);
        Assert.IsType<PilotCallResult.Failure>(activation.Call(source, "dogmaIM", "Activate", [module, effect, Target(f), 0L]));
        f.Clock.Milliseconds += 60000;
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Activate", [module, effect, Target(f), 0L]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LockLossOrDisconnectCancelsPaidAncillaryArmorWithoutRefundOrLateRepair(bool disconnect)
    {
        using Fixture f = Prepare();
        long module = f.Fit(SmallArmor);
        Load(f, module, Paste, 1);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var target = Client(f, 1000002);
        Lock(f, scope.ServiceProvider, source);
        var handler = scope.ServiceProvider.GetRequiredService<RemoteRepairHandler>();
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        Assert.IsType<PilotCallResult.Success>(
            activation.Call(source, "dogmaIM", "Activate", [module, "shipModuleAncillaryRemoteArmorRepairer", Target(f), 3L])
        );
        double paidCapacitor = source
            .Notifications.OfType<ModuleAttributesChanged>()
            .SelectMany(n => n.Changes)
            .Last(c => c.Attribute == DogmaAttributeId.CapacitorCharge)
            .Value;
        if (disconnect)
        {
            handler.Cancel(source);
        }
        else
        {
            scope.ServiceProvider.GetRequiredService<TargetingHandler>().CallWire(source, "dogmaIM", "RemoveTarget", [Target(f)]);
            handler.Advance(source);
        }
        using (WorldDbContext paid = new(f.Db.Path))
        {
            Assert.Equal(paidCapacitor, paid.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        }
        f.Clock.Seconds += 30;
        Store(f).Complete(f.PilotId, module, f.Clock.Now);
        Assert.Equal(500000, Health(f).Defense.Armor - Health(f).Remaining.Armor);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<WorldRemoteRepair>());
        Assert.Empty(world.Set<OwnedItem>().Where(i => i.LocationId == module));
    }

    [Theory]
    [InlineData(SmallShield, "shipModuleAncillaryRemoteShieldBooster")]
    [InlineData(SmallArmor, "shipModuleAncillaryRemoteArmorRepairer")]
    public void EmptyRemoteAncillaryModeRepeatsUntilPaidStop(int type, string effect)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        Space(f);
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var target = Client(f, 1000002);
        Lock(f, scope.ServiceProvider, source);
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<RemoteRepairHandler>();
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Activate", [module, effect, Target(f), 2L]));
        var prime = handler.ActiveEffect(source.Pilot, module)!;
        f.Clock.Milliseconds += (long)prime.DurationMilliseconds!.Value;
        handler.Advance(source);
        Assert.NotNull(handler.ActiveEffect(source.Pilot, module));
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Deactivate", [module, effect]));
        Assert.NotNull(handler.ActiveEffect(source.Pilot, module));
        f.Clock.Milliseconds += (long)prime.DurationMilliseconds.Value;
        handler.Advance(source);
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
    }

    [Theory]
    [InlineData(SmallShield, Navy25, "shipModuleAncillaryRemoteShieldBooster")]
    [InlineData(SmallArmor, Paste, "shipModuleAncillaryRemoteArmorRepairer")]
    public void WarmRemoteAncillaryHandlersDoNotReadSqlOrRecalculateFits(int type, int fuel, string effect)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        Load(f, module, fuel);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var target = Client(f, 1000002);
        Lock(f, scope.ServiceProvider, source);
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<RemoteRepairHandler>();
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Activate", [module, effect, Target(f), 0L]));
        handler.Advance(source);
        using var metrics = SessionPerformance.Begin("warm remote ancillary handlers", _ => { }, force: true);
        for (int frame = 0; frame < 12; frame++)
        {
            f.Clock.Milliseconds += 50;
            handler.Advance(source);
        }
        Assert.Equal(0, metrics!.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }
}
