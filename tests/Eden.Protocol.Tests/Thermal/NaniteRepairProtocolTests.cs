using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Repair;
using Eden.World.Combat;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Thermal;

public sealed partial class PaidNaniteRepairTests
{
    private static PilotCallContext Client(Fixture f)
    {
        var pilot = f.Fit.Db.Store.FindPilot(1000001)!;
        PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Fit.Db.Store, p, f.Fit.Clock, new SharedSpace(), f.Fit.Db.Objects),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        context.Flight.Updates.Clear();
        return context;
    }

    private static object? Reply(PilotCallResult result) => Assert.IsType<PilotCallResult.Success>(result).Reply.ToBlue();

    private static void DamageNotice(PilotCallContext context, long module, double damage)
    {
        var change = Assert.Single(
            context.Notifications.OfType<ModuleAttributesChanged>().SelectMany(n => n.Changes),
            c => c.Attribute == DogmaAttributeId.Damage
        );
        Assert.Equal(new DogmaItemKey.Item(module), change.Item);
        Assert.Equal(DogmaAttributeId.Damage, change.Attribute);
        Assert.Equal(damage, change.Value);
    }

    [Fact]
    public void NativePartialStopPublishesTheReturnedPasteStackOnce()
    {
        using Fixture f = new(live: true, paste: 200);
        SetRepairBasePrice(f, 2600000);
        using var scope = f.Fit.Db.Services.CreateScope();
        using var context = Client(f);
        var handler = scope.ServiceProvider.GetRequiredService<NaniteRepairHandler>();
        Assert.Equal(true, Reply(handler.Call(context, "dogmaIM", "InitiateModuleRepair", [f.Module])));
        context.Notifications.Clear();
        f.At(f.Now + TimeSpan.TicksPerMinute / 2);
        Assert.Null(Reply(handler.Call(context, "dogmaIM", "StopModuleRepair", [f.Module])));
        Assert.Equal(175, f.Paste());
        Assert.Single(context.Notifications.OfType<ItemChanged>());
        context.Notifications.Clear();
        Assert.Null(Reply(handler.Call(context, "dogmaIM", "StopModuleRepair", [f.Module])));
        Assert.Empty(context.Notifications);
    }

    [Fact]
    public void NativeSingleRepairRoutesOnceSpendsCargoAndPublishesElapsedDamageOnStop()
    {
        using Fixture f = new(live: true);
        using var scope = f.Fit.Db.Services.CreateScope();
        using var context = Client(f);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var handler = scope.ServiceProvider.GetRequiredService<NaniteRepairHandler>();
        Assert.Equal(true, Reply(router.Call(context, "dogmaIM", "InitiateModuleRepair", new object?[] { f.Module }, _ => { })));
        Assert.True(handler.IsMonitoring);
        Assert.Equal(49, f.Paste());
        Assert.Single(context.Notifications.OfType<ItemChanged>());
        Assert.Equal(false, Reply(handler.Call(context, "dogmaIM", "InitiateModuleRepair", [f.Module])));
        Assert.Equal(49, f.Paste());
        context.Notifications.Clear();
        f.At(f.Now + TimeSpan.TicksPerMinute);
        Assert.Null(Reply(handler.Call(context, "dogmaIM", "StopModuleRepair", [f.Module])));
        DamageNotice(context, f.Module, 10);
        Assert.False(handler.IsMonitoring);
        Assert.Equal(10, f.DamageLeft(f.Module));
        Assert.Empty(f.Repairs.Read(f.Pilot, f.Ship, f.Now));
        context.Notifications.Clear();
        Assert.Null(Reply(handler.Call(context, "dogmaIM", "StopModuleRepair", [f.Module])));
        Assert.Empty(context.Notifications);
    }

    [Fact]
    public void NativeManyReturnsATupleOfThreeListsWithPartialFundingAndRejections()
    {
        using Fixture f = new(live: true, paste: 1, second: true);
        f.Damage(f.Second, 20);
        using var scope = f.Fit.Db.Services.CreateScope();
        using var context = Client(f);
        var handler = scope.ServiceProvider.GetRequiredService<NaniteRepairHandler>();
        long unknown = long.MaxValue;
        var tuple = Assert.IsType<object?[]>(
            Reply(handler.Call(context, "dogmaIM", "InitiateModuleRepairMany", [new List<object?> { f.Module, f.Second, unknown }]))
        );
        Assert.Equal(3, tuple.Length);
        Assert.Equal(new object?[] { f.Module }, Assert.IsType<List<object?>>(tuple[0]));
        Assert.Equal(new object?[] { f.Second }, Assert.IsType<List<object?>>(tuple[1]));
        Assert.Equal(new object?[] { unknown }, Assert.IsType<List<object?>>(tuple[2]));
        Assert.Equal(0, f.Paste());
        var changed = Assert.Single(context.Notifications.OfType<ItemChanged>());
        Assert.True(changed.Previous.Count > 0);
        Assert.Single(f.Repairs.Read(f.Pilot, f.Ship, f.Now));
        Assert.Equal(20, f.DamageLeft(f.Second));
    }

    [Fact]
    public void NativeSingleInsufficientPasteReturnsTheBankCatchableNamedErrorWithoutSpending()
    {
        using Fixture f = new(paste: 0);
        using var scope = f.Fit.Db.Services.CreateScope();
        using var context = Client(f);
        var handler = scope.ServiceProvider.GetRequiredService<NaniteRepairHandler>();
        var result = Assert.IsType<PilotCallResult.NamedFailure>(handler.Call(context, "dogmaIM", "InitiateModuleRepair", [f.Module]));
        Assert.Equal("NotEnoughRepairMaterialToFinishAllRepairs", result.Name);
        Assert.Empty(context.Notifications);
        Assert.False(handler.IsMonitoring);
        Assert.Equal(20, f.DamageLeft(f.Module));
        Assert.Empty(f.Repairs.Read(f.Pilot, f.Ship, f.Now));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("negative")]
    [InlineData("oversized")]
    public void NativeManyRejectsInvalidSelectionsBeforePayment(string variant)
    {
        using Fixture f = new();
        using var scope = f.Fit.Db.Services.CreateScope();
        using var context = Client(f);
        var handler = scope.ServiceProvider.GetRequiredService<NaniteRepairHandler>();
        List<object?> ids = variant switch
        {
            "duplicate" => [f.Module, f.Module],
            "negative" => [f.Module, -1L],
            "oversized" => Enumerable.Range(1, 25).Select(i => (object?)(long)i).ToList(),
            _ => [],
        };
        Assert.IsType<PilotCallResult.Failure>(handler.Call(context, "dogmaIM", "InitiateModuleRepairMany", [ids]));
        Assert.Equal(50, f.Paste());
        Assert.Empty(context.Notifications);
        Assert.Empty(f.Repairs.Read(f.Pilot, f.Ship, f.Now));
    }

    [Fact]
    public void NativeRepairCompletionNotifiesOnceAndUnchangedActiveFramesUseNoSqlOrFitting()
    {
        using Fixture f = new(live: true);
        using var scope = f.Fit.Db.Services.CreateScope();
        using var context = Client(f);
        var handler = scope.ServiceProvider.GetRequiredService<NaniteRepairHandler>();
        Assert.Equal(true, Reply(handler.Call(context, "dogmaIM", "InitiateModuleRepair", [f.Module])));
        var schedule = Assert.Single(f.Repairs.Read(f.Pilot, f.Ship, f.Now));
        handler.Advance(context);
        context.Notifications.Clear();
        using (var metrics = SessionPerformance.Begin("native nanite frames", _ => { }, force: true)!)
        {
            for (int i = 0; i < 100; i++)
            {
                f.At(f.Now + TimeSpan.TicksPerMillisecond);
                handler.Advance(context);
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        Assert.Empty(context.Notifications);
        f.At(schedule.Key.CompletesAt);
        scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>().Advance(context);
        DamageNotice(context, f.Module, 0);
        Assert.False(handler.IsMonitoring);
        context.Notifications.Clear();
        handler.Advance(context);
        Assert.Empty(context.Notifications);
        Assert.Equal(0, f.DamageLeft(f.Module));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoordinatorSettlesRepairBeforeTravelOrDisconnect(bool disconnect)
    {
        using Fixture f = new(live: true);
        using var scope = f.Fit.Db.Services.CreateScope();
        using var context = Client(f);
        var handler = scope.ServiceProvider.GetRequiredService<NaniteRepairHandler>();
        var updates = scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>();
        Assert.Equal(true, Reply(handler.Call(context, "dogmaIM", "InitiateModuleRepair", [f.Module])));
        f.At(f.Now + TimeSpan.TicksPerMinute);
        context.Notifications.Clear();
        if (disconnect)
        {
            updates.Leave(context);
        }
        else
        {
            updates.PrepareTranslocation(context);
        }
        Assert.False(handler.IsMonitoring);
        Assert.Equal(10, f.DamageLeft(f.Module));
        Assert.Empty(f.Repairs.Read(f.Pilot, f.Ship, f.Now));
        Assert.Contains(
            context.Notifications.OfType<ModuleAttributesChanged>().SelectMany(n => n.Changes),
            c => c.Item == new DogmaItemKey.Item(f.Module) && c.Attribute == DogmaAttributeId.Damage && c.Value == 10
        );
        Assert.Equal(49, f.Paste());
    }

    [Fact]
    public void EnteringANewNativeSessionSettlesAnOrphanedRepairWithoutReplayingPayment()
    {
        using Fixture f = new(live: true);
        var schedule = f.Start();
        f.At(schedule.Key.StartedAt + TimeSpan.TicksPerMinute);
        using var scope = f.Fit.Db.Services.CreateScope();
        using var context = Client(f);
        var handler = scope.ServiceProvider.GetRequiredService<NaniteRepairHandler>();
        handler.Enter(context);
        DamageNotice(context, f.Module, 10);
        Assert.Equal(10, f.DamageLeft(f.Module));
        Assert.Equal(49, f.Paste());
        Assert.False(handler.IsMonitoring);
        Assert.Empty(f.Repairs.Read(f.Pilot, f.Ship, f.Now));
    }

    [Fact]
    public void NativeWeaponBankRequestsRepairOnlyTheirExplicitMemberAndChargeEachOnce()
    {
        const int Gun = 561;
        using Fixture f = new(live: true, second: true, type: Gun);
        f.Damage(f.Second, 20);
        new PilotWeaponBankStore(new(f.Fit.Db.Path), f.Fit.Clock).Change(f.Pilot, f.Ship, WeaponBankOperation.Link, f.Module, f.Second);
        using var scope = f.Fit.Db.Services.CreateScope();
        using var context = Client(f);
        var handler = scope.ServiceProvider.GetRequiredService<NaniteRepairHandler>();
        Assert.Equal(true, Reply(handler.Call(context, "dogmaIM", "InitiateModuleRepair", [f.Module])));
        Assert.Equal(f.Module, Assert.Single(f.Repairs.Read(f.Pilot, f.Ship, f.Now)).Key.ModuleId);
        long afterMaster = f.Paste();
        Assert.True(afterMaster < 50);
        Assert.Equal(true, Reply(handler.Call(context, "dogmaIM", "InitiateModuleRepair", [f.Second])));
        Assert.Equal(new[] { f.Module, f.Second }.Order(), f.Repairs.Read(f.Pilot, f.Ship, f.Now).Select(r => r.Key.ModuleId).Order());
        Assert.Equal(2 * afterMaster - 50, f.Paste());
        long paid = f.Paste();
        Assert.Equal(false, Reply(handler.Call(context, "dogmaIM", "InitiateModuleRepair", [f.Second])));
        Assert.Equal(paid, f.Paste());
    }

    [Fact]
    public void AStillRunningControllerRejectsRepairAtItsPaidDeadlineBeforeTheNextRepeat()
    {
        using Fixture f = new(live: true);
        using var scope = f.Fit.Db.Services.CreateScope();
        using var context = Client(f);
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var shields = scope.ServiceProvider.GetRequiredService<ShieldBoostHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<NaniteRepairHandler>();
        Assert.IsType<PilotCallResult.Success>(activation.Call(context, "dogmaIM", "Activate", [f.Module, "shieldBoosting", null, 2L]));
        var effect = Assert.IsType<DogmaActiveEffect>(shields.ActiveEffect(context.Pilot, f.Module));
        f.Fit.Clock.Milliseconds += (long)effect.DurationMilliseconds!.Value;
        Assert.Equal(false, Reply(handler.Call(context, "dogmaIM", "InitiateModuleRepair", [f.Module])));
        Assert.Equal(50, f.Paste());
        Assert.Empty(f.Repairs.Read(f.Pilot, f.Ship, f.Now));
        shields.Advance(context);
        Assert.NotNull(shields.ActiveEffect(context.Pilot, f.Module));
        shields.Cancel(context);
        f.Fit.Clock.Milliseconds += (long)effect.DurationMilliseconds.Value;
        Assert.Equal(true, Reply(handler.Call(context, "dogmaIM", "InitiateModuleRepair", [f.Module])));
    }

    [Fact]
    public void DockingClearsNativeMonitoringAndPublishesOnlyTheEarnedRepair()
    {
        using Fixture f = new(live: true);
        using var scope = f.Fit.Db.Services.CreateScope();
        using var context = Client(f);
        var handler = scope.ServiceProvider.GetRequiredService<NaniteRepairHandler>();
        Assert.Equal(true, Reply(handler.Call(context, "dogmaIM", "InitiateModuleRepair", [f.Module])));
        f.At(f.Now + TimeSpan.TicksPerMinute);
        f.Fit.Db.Store.SetPosition(f.Ship, null);
        context.Flight.Relocate(f.Fit.Db.Store.FindPilot(1000001)!, []);
        context.Notifications.Clear();
        handler.Advance(context);
        Assert.False(handler.IsMonitoring);
        Assert.Equal(10, f.DamageLeft(f.Module));
        Assert.Empty(f.Repairs.Read(f.Pilot, f.Ship, f.Now));
        DamageNotice(context, f.Module, 10);
        Assert.IsType<PilotCallResult.Failure>(handler.Call(context, "dogmaIM", "InitiateModuleRepair", [f.Module]));
        Assert.Equal(49, f.Paste());
    }

    [Fact]
    public void GateInitiationSettlesRepairBeforeTheSystemTransferAndRejectsNewWork()
    {
        using Fixture f = new(live: true);
        var gate = f.Fit.Db.Objects.Single(o => o.DestinationGateId.HasValue);
        f.Fit.Db.Store.SetPosition(f.Ship, gate.Position);
        using var scope = f.Fit.Db.Services.CreateScope();
        using var context = Client(f);
        var handler = scope.ServiceProvider.GetRequiredService<NaniteRepairHandler>();
        Assert.Equal(true, Reply(handler.Call(context, "dogmaIM", "InitiateModuleRepair", [f.Module])));
        f.At(f.Now + TimeSpan.TicksPerMinute);
        int source = context.Pilot.SystemId;
        Assert.Same(
            PilotCallResult.None,
            context.Flight.StartJump(
                new FlightCommand.Jump(gate.Id, gate.DestinationGateId!.Value, f.Ship),
                f.Fit.Db.Services.GetRequiredService<ISystemObjects>()
            )
        );
        context.Notifications.Clear();
        handler.Advance(context);
        Assert.Equal(source, context.Pilot.SystemId);
        Assert.False(handler.IsMonitoring);
        Assert.Equal(10, f.DamageLeft(f.Module));
        DamageNotice(context, f.Module, 10);
        Assert.IsType<PilotCallResult.Failure>(handler.Call(context, "dogmaIM", "InitiateModuleRepair", [f.Module]));
        Assert.Empty(f.Repairs.Read(f.Pilot, f.Ship, f.Now));
        Assert.Equal(49, f.Paste());
    }
}
