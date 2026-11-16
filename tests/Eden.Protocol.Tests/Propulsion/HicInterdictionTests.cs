using Eden.Data.Catalog;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Propulsion;

public sealed class HicInterdictionTests
{
    private const int Broadsword = 12013;
    private const int Generator = 28654;

    [Fact]
    public void MovingFieldFollowsItsHullBlocksAssistanceAndExpiresExactly()
    {
        using var fixture = InterdictionTests.Prepare(hull: Broadsword);
        long ship = fixture.ShipId;
        long module = fixture.Fit(Generator);
        int system = fixture.Db.Store.FindPilot(1000001)!.SystemId;
        fixture.Db.Store.SetPosition(ship, SpacePosition.Zero, system);
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var store = fixture.Db.Services.GetRequiredService<IPilotInterdiction>();
        double signature = fixture.Fitting.ShipAttributes(fixture.PilotId)[DogmaAttributeId.SignatureRadius];
        var paid = store.ActivateGenerator(
            fixture.PilotId,
            ship,
            module,
            DogmaEffect.WarpDisruptSphere,
            null,
            new HashSet<long>(),
            fixture.Clock.Now
        );
        Assert.Equal(30000, paid.Duration);
        Assert.Equal(16000, paid.Range);
        Assert.Equal(signature * 1.5, fixture.Fitting.ShipAttributes(fixture.PilotId)[DogmaAttributeId.SignatureRadius], 6);
        Assert.Equal(1, fixture.Fitting.ShipAttributes(fixture.PilotId)[DogmaAttributeId.DisallowAssistance]);
        Assert.True(store.Resolve(ship, system, SpacePosition.Zero, new(1_000_000, 0, 0), fixture.Clock.Now).Blocked);
        fixture.Db.Store.SetPosition(ship, new(50000, 0, 0), system);
        Assert.False(store.Resolve(ship, system, SpacePosition.Zero, new(1_000_000, 0, 0), fixture.Clock.Now).Blocked);
        Assert.True(store.Resolve(ship, system, new(50000, 0, 0), new(1_000_000, 0, 0), fixture.Clock.Now).Blocked);
        using (var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot()))
        {
            snapshot.Project();
            using WorldDbContext saved = new(snapshot.Database);
            var field = Assert.Single(saved.Set<SavedInterdictionField>());
            Assert.Equal(paid.ExpiresAt, field.ExpiresAt);
            Assert.Equal(InterdictionKind.HeavyField, field.Kind);
        }
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(paid.ExpiresAt - fixture.Clock.Now - 1));
        Assert.True(store.Active(ship, module, fixture.Clock.Now));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.False(store.Active(ship, module, fixture.Clock.Now));
        Assert.False(store.Resolve(ship, system, new(50000, 0, 0), new(1_000_000, 0, 0), fixture.Clock.Now).Blocked);
        Assert.Equal(signature, fixture.Fitting.ShipAttributes(fixture.PilotId)[DogmaAttributeId.SignatureRadius]);
        Assert.Equal(0, fixture.Fitting.ShipAttributes(fixture.PilotId).GetValueOrDefault(DogmaAttributeId.DisallowAssistance));
    }

    [Theory]
    [InlineData(InterdictionRules.FocusedDisruptionScript, DogmaEffect.FocusedWarpDisruption, 24000, 0)]
    [InlineData(InterdictionRules.FocusedScramblingScript, DogmaEffect.FocusedWarpScrambling, 12800, 1)]
    public void FocusedScriptsUseCatalogModifiersAndLockedTargetAndExpire(int script, DogmaEffect effect, double range, double mwdBlock)
    {
        using var fixture = InterdictionTests.Prepare(0.1, Broadsword);
        long ship = fixture.ShipId;
        long module = fixture.Fit(Generator);
        InterdictionTests.Load(fixture, module, script);
        var victim = fixture.Db.Store.FindPilot(1000002)!;
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Pilots.Find(fixture.PilotId)!.SafetyLevel = ShipSafetyLevel.Disabled;
            world.SaveChanges();
        }
        fixture.Db.Store.SetPosition(ship, SpacePosition.Zero, victim.SystemId);
        fixture.Db.Store.SetPosition(victim.ShipId, new(1000, 0, 0), victim.SystemId);
        using ResidentFitCache cache = new(new(fixture.Db.Path));
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var store = fixture.Db.Services.GetRequiredService<IPilotInterdiction>();
        var tackle = fixture.Db.Services.GetRequiredService<IPilotTackle>();
        TackleTarget target = new(victim.ShipId, TackleTargetKind.Ship);
        Assert.Throws<InvalidDataException>(() =>
            store.ActivateGenerator(fixture.PilotId, ship, module, effect, target, new HashSet<long>(), fixture.Clock.Now)
        );
        Assert.Empty(tackle.Incoming(victim.ShipId));
        double signature = fixture.Fitting.ShipAttributes(fixture.PilotId)[DogmaAttributeId.SignatureRadius];
        var paid = store.ActivateGenerator(
            fixture.PilotId,
            ship,
            module,
            effect,
            target,
            new HashSet<long> { target.Id },
            fixture.Clock.Now
        );
        Assert.Equal(6000, paid.Duration, 6);
        Assert.Equal(range, paid.Range, 6);
        var incoming = Assert.Single(tackle.Incoming(victim.ShipId));
        Assert.Equal(100, incoming.Module.Strength);
        Assert.Equal(mwdBlock, incoming.Module.MwdBlock);
        Assert.True(tackle.ShipAttributes(victim.PilotId)[DogmaAttributeId.WarpScrambleStatus] > 0);
        Assert.Equal(signature, fixture.Fitting.ShipAttributes(fixture.PilotId)[DogmaAttributeId.SignatureRadius]);
        using (var metrics = SessionPerformance.Begin("focused incoming reads", _ => { }, force: true)!)
        {
            for (int i = 0; i < 8; i++)
            {
                Assert.Single(tackle.Incoming(victim.ShipId));
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(paid.ExpiresAt - fixture.Clock.Now));
        Assert.False(store.Active(ship, module, fixture.Clock.Now));
        Assert.Empty(tackle.Incoming(victim.ShipId));
        Assert.True(tackle.ShipAttributes(victim.PilotId).GetValueOrDefault(DogmaAttributeId.WarpScrambleStatus) <= 0);
        Assert.Equal(
            1,
            Assert
                .Single(fixture.Db.Services.GetRequiredService<Eden.Gameplay.Ammunition.IPilotAmmunition>().Read(fixture.PilotId, ship))
                .Quantity
        );
    }

    [Fact]
    public void NativeFocusedRouteSendsIncomingEwarAndClearsAtExpiry()
    {
        using var fixture = InterdictionTests.Prepare(hull: Broadsword);
        long module = fixture.Fit(Generator);
        InterdictionTests.Load(fixture, module, InterdictionRules.FocusedDisruptionScript);
        var victim = fixture.Db.Store.FindPilot(1000002)!;
        fixture.Db.Store.SetPosition(fixture.ShipId, SpacePosition.Zero, victim.SystemId);
        fixture.Db.Store.SetPosition(victim.ShipId, new(1000, 0, 0), victim.SystemId);
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        victim = fixture.Db.Store.FindPilot(1000002)!;
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        using var scope = fixture.Db.Services.CreateScope();
        using var recipientScope = fixture.Db.Services.CreateScope();
        using PilotCallContext source = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create(selected),
            1,
            SignedSettings.Empty
        );
        using PilotCallContext recipient = new(
            1000002,
            victim.Account,
            [victim],
            selected => recipientScope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create(selected),
            2,
            SignedSettings.Empty
        );
        source.Select(pilot);
        recipient.Select(victim);
        source.Flight.Snapshot();
        recipient.Flight.Snapshot();
        source.Flight.RefreshPeers();
        var targeting = scope.ServiceProvider.GetRequiredService<Eden.Protocol.Targeting.TargetingHandler>();
        _ = targeting.CallWire(source, "dogmaIM", "AddTarget", [victim.ShipId]);
        fixture.Clock.Seconds += 30;
        targeting.Advance(source);
        Assert.True(targeting.IsLocked(victim.ShipId));
        var route = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        Assert.Same(
            PilotCallResult.None,
            route.Call(source, "dogmaIM", "Activate", [module, "shipModuleFocusedWarpDisruptionScript", victim.ShipId, 0L])
        );
        recipientScope.ServiceProvider.GetRequiredService<TackleHandler>().Advance(recipient);
        Assert.Contains(recipient.Notifications, notification => notification is TackleEwarChanged { Active: true });
        Assert.IsType<PilotCallResult.Failure>(recipient.Flight.Execute(new FlightCommand.WarpToPoint(1, new(1_000_000, 0, 0), 0)));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(6));
        scope.ServiceProvider.GetRequiredService<InterdictionHandler>().Advance(source);
        recipientScope.ServiceProvider.GetRequiredService<TackleHandler>().Advance(recipient);
        Assert.Contains(recipient.Notifications, notification => notification is TackleEwarChanged { Active: false });
    }

    [Fact]
    public void GeneratorRulesRejectInvalidTypedInputs()
    {
        Dictionary<DogmaAttributeId, double> attributes = new()
        {
            [DogmaAttributeId.Duration] = 30000,
            [DogmaAttributeId.CapacitorNeed] = 150,
            [DogmaAttributeId.WarpScrambleRange] = 16000,
            [DogmaAttributeId.WarpScrambleStrength] = 100,
        };
        Assert.Equal(new(30000, 150, 16000, 100), InterdictionGenerator.Define(attributes));
        foreach (var attribute in attributes.Keys.ToArray())
        {
            var invalid = attributes.ToDictionary();
            invalid[attribute] = double.NaN;
            Assert.Throws<InvalidDataException>(() => InterdictionGenerator.Define(invalid));
        }
        Assert.Throws<OverflowException>(() => InterdictionRules.Deadline(long.MaxValue, 30000));
    }

    [Fact]
    public void EveryPublishedGeneratorAndBothScriptsHaveExplicitAdmission()
    {
        using var fixture = InterdictionTests.Prepare(hull: Broadsword);
        using CatalogDbContext catalog = new(fixture.CatalogPath);
        var generators = catalog.Set<ItemType>().Where(type => type.GroupId == InterdictionRules.GeneratorGroup).ToArray();
        Assert.Equal(8, generators.Length);
        Assert.All(generators, type => Assert.NotNull(FittingCatalog.Definition(catalog, type.Id)));
        foreach (int type in new[] { InterdictionRules.FocusedDisruptionScript, InterdictionRules.FocusedScramblingScript })
        {
            Assert.True(ScriptCatalog.Supported(catalog, type, InterdictionRules.ScriptGroup));
        }
    }

    [Fact]
    public void NativeFieldDeactivateFinishesThePaidCycleAndPublishesRange()
    {
        using var fixture = InterdictionTests.Prepare(hull: Broadsword);
        long ship = fixture.ShipId;
        long module = fixture.Fit(Generator);
        int system = fixture.Db.Store.FindPilot(1000001)!.SystemId;
        fixture.Db.Store.SetPosition(ship, SpacePosition.Zero, system);
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        using var scope = fixture.Db.Services.CreateScope();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create(selected),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        var route = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<InterdictionHandler>();
        Assert.Same(PilotCallResult.None, route.Call(context, "dogmaIM", "Activate", [module, "warpDisruptSphere", null, 1L]));
        Assert.Contains(
            context.Flight.Updates,
            update =>
                update.Command
                    is DestinyCommand.TimedSpecialEffect
                    {
                        Graphic: "effects.WarpDisruptFieldGenerating",
                        Start: true,
                        GraphicInfo: { } info
                    }
                && Equals(info["range"], 16000d)
        );
        Assert.Same(PilotCallResult.None, route.Call(context, "dogmaIM", "Deactivate", [module, "warpDisruptSphere"]));
        Assert.NotNull(handler.ActiveEffect(pilot, module));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(30));
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(pilot, module));
    }
}
