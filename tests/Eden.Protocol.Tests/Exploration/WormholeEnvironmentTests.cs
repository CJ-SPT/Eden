using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Exploration;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Exploration;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Exploration;

public sealed class WormholeEnvironmentTests
{
    [Fact]
    public void NativeEffectSnapshotAndNotificationsRemainConsistentAcrossArrivalAndExit()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadWormholes(f.CatalogPath);
        long ship = f.ShipId;
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var environments = f.Db.Services.GetRequiredService<IWormholeEnvironments>();
        using CatalogDbContext catalog = new(f.CatalogPath);
        int system = checked((int)catalog.Records(CatalogData.Sde.MapSecondarySuns).Values.First().SolarSystemID!.Value);
        f.Db.Store.SetPosition(ship, SpacePosition.Zero, system);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            f.Db.Store.ReadAccount(1000001),
            [pilot],
            selected => new FlightSession(f.Db.Store, selected, f.Clock, new(), []),
            1,
            new([], [])
        );
        context.Select(pilot);
        WormholeEnvironmentHandler handler = new(environments);
        var effects = handler.Read(pilot);
        Assert.NotEmpty(effects);
        handler.Advance(context);
        Assert.Empty(context.Notifications);
        Assert.Same(effects, handler.Read(pilot));
        var wire = Assert.IsType<BlueDictionary>(new WormholeEffectsWire(effects).ToBlue());
        Assert.Single(wire.Entries);
        var key = Assert.IsType<object?[]>(wire.Entries.Single().Key);
        Assert.Equal(effects[0].SourceItemId, key[0]);
        Assert.Equal(effects[0].SourceTypeId, key[1]);
        Assert.IsType<BlueConstructor>(wire.Entries.Single().Value);

        var outside = pilot with { Position = null };
        Assert.Empty(handler.Read(outside));
        context.ChangeShip(outside);
        handler.Advance(context);
        Assert.Equal(effects.Count, context.Notifications.Count);
        Assert.All(context.Notifications, notification => Assert.IsType<WormholeEffectStopped>(notification));
        context.Notifications.Clear();
        handler.Advance(context);
        Assert.Empty(context.Notifications);
        context.ChangeShip(pilot);
        handler.Advance(context);
        Assert.Equal(effects.Count, context.Notifications.Count);
        Assert.All(context.Notifications, notification => Assert.IsType<WormholeEffectStarted>(notification));
    }

    [Fact]
    public void TypedSystemModifiersSelectHullModulesAndOwnerAmmunitionWithoutLeaking()
    {
        WormholeEnvironment environment = new(
            30847,
            [
                new(ModifierSelector.Item, null, DogmaAttributeId.MaxTargetRange, DogmaOperation.PostMultiply, .85),
                new(ModifierSelector.RequiredSkill, 3300, DogmaAttributeId.DamageMultiplier, DogmaOperation.PostMultiply, 1.3),
                new(ModifierSelector.OwnerRequiredSkill, 3319, DogmaAttributeId.KineticDamage, DogmaOperation.PostMultiply, 1.3),
                new(ModifierSelector.Location, null, DogmaAttributeId.HeatDamage, DogmaOperation.PostMultiply, 1.15),
            ]
        );
        WormholeEffectTarget[] modules = [new(2, 53, new HashSet<int> { 3300 }), new(3, 40, new HashSet<int> { 3416 })];
        WormholeEffectTarget[] owners = [new(4, 386, new HashSet<int> { 3319 }), new(5, 100, new HashSet<int> { 3436 })];
        var modifiers = WormholeEnvironmentRules.Modifiers(environment, 1, modules, modules.Concat(owners).ToArray(), owners).ToArray();
        Assert.Equal(5, modifiers.Length);
        Assert.Single(modifiers, m => m.TargetId == 1 && m.Attribute == DogmaAttributeId.MaxTargetRange);
        Assert.Single(modifiers, m => m.TargetId == 2 && m.Attribute == DogmaAttributeId.DamageMultiplier);
        Assert.Single(modifiers, m => m.TargetId == 4 && m.Attribute == DogmaAttributeId.KineticDamage);
        Assert.DoesNotContain(modifiers, m => m.TargetId == 5);
        Assert.All(modifiers, m => Assert.True(m.ExemptFromStacking));
    }

    [Fact]
    public async Task EveryNativeEnvironmentChangesResidentFitsAndRestoresOnExitAndRestart()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadWormholes(f.CatalogPath);
        long ship = f.ShipId;
        int system;
        double expectedShield;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var baseline = f.Fitting.ShipAttributes(f.PilotId);
            using CatalogDbContext catalog = new(f.CatalogPath);
            var systems = catalog
                .Records(CatalogData.Sde.MapSecondarySuns)
                .Values.Select(s => checked((int)s.SolarSystemID!.Value))
                .ToArray();
            Assert.Equal(36, systems.Length);
            foreach (int remote in systems)
            {
                var environment = WormholeEnvironmentStore.At(catalog, remote)!;
                Assert.NotEmpty(environment.Modifiers);
                f.Db.Store.SetPosition(ship, SpacePosition.Zero, remote);
                var actual = f.Fitting.ShipAttributes(f.PilotId);
                foreach (var modifier in environment.Modifiers.Where(m => m.Selector == ModifierSelector.Item))
                {
                    double multiplier = modifier.Operation == DogmaOperation.PostPercent ? 1 + modifier.Value / 100 : modifier.Value;
                    double expected = baseline[modifier.Attribute] * multiplier;
                    var definition = catalog.Read(CatalogData.Client.Dogmaattributes, (long)modifier.Attribute)!;
                    if (definition.MaxAttributeID is long maximum)
                    {
                        double bound = actual.GetValueOrDefault(
                            (DogmaAttributeId)maximum,
                            catalog.Read(CatalogData.Client.Dogmaattributes, maximum)!.DefaultValue ?? double.PositiveInfinity
                        );
                        expected = Math.Min(expected, bound);
                    }
                    Assert.Equal(expected, actual[modifier.Attribute], 6);
                }
                f.Db.Store.SetPosition(ship, SpacePosition.Zero, 30000144);
                Assert.Equal(
                    baseline[DogmaAttributeId.ShieldCapacity],
                    f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ShieldCapacity]
                );
            }
            system = systems.First(remote => WormholeEnvironmentStore.At(catalog, remote)!.TypeId == 30844);
            f.Db.Store.SetPosition(ship, SpacePosition.Zero, system);
            expectedShield = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ShieldCapacity];
            using (var measured = SessionPerformance.Begin("resident wormhole environment", _ => { }, force: true)!)
            {
                f.Db.Store.SetPosition(ship, new(1, 2, 3), system);
                Assert.Equal(expectedShield, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ShieldCapacity]);
                Assert.Equal(0, measured.Commands);
                Assert.Equal(0, measured.Connections);
                Assert.Equal(0, measured.FittingCalculations);
            }
            await live.FlushAsync();
        }
        using (LiveWorld restarted = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            Assert.Equal(system, f.Db.Store.FindPilot(1000001)!.SystemId);
            Assert.Equal(expectedShield, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ShieldCapacity]);
        }
    }
}
