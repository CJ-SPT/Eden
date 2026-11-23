using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Content;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
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
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Propulsion;

public sealed class NullifierTests
{
    private const int Cheetah = 11182;
    private const int Ares = 11202;
    private const int Nullifier = 58919;
    private const int Higgs = 34266;
    private const int BurstJammer = 580;

    [Theory]
    [InlineData(58919, 10, 100, 50)]
    [InlineData(58966, 12, 130, 50)]
    [InlineData(58971, 12, 130, 35)]
    [InlineData(58972, 15, 150, 50)]
    public void PaidImmunityUsesEveryCatalogProfileAndExactDeadlines(int type, int duration, int delay, double cost)
    {
        using var fixture = InterdictionTests.Prepare(hull: Cheetah);
        long ship = fixture.ShipId;
        var baseAttributes = fixture.Fitting.ShipAttributes(fixture.PilotId);
        long module = fixture.Fit(type);
        int system = Place(fixture);
        using ResidentFitCache cache = new(new(fixture.Db.Path));
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        SeedProbe(live, fixture, system);
        var store = fixture.Db.Services.GetRequiredService<IPilotInterdiction>();
        var online = fixture.Fitting.ShipAttributes(fixture.PilotId);
        Assert.Equal(baseAttributes[DogmaAttributeId.MaxTargetRange] * 0.5, online[DogmaAttributeId.MaxTargetRange], 6);
        Assert.Equal(baseAttributes[DogmaAttributeId.ScanResolution] * 0.5, online[DogmaAttributeId.ScanResolution], 6);
        Assert.True(store.Resolve(ship, system, SpacePosition.Zero, new(1_000_000, 0, 0), fixture.Clock.Now).Blocked);
        double before = live.Economy.Inspect(world => ShipResources.Capacitor(world, ship, online, fixture.Clock.Now));
        var paid = store.ActivateNullifier(fixture.PilotId, ship, module, fixture.Clock.Now);
        Assert.Equal(before - cost, paid.Capacitor, 6);
        Assert.Equal(duration * 1000, paid.Duration);
        Assert.Equal(fixture.Clock.Now + TimeSpan.FromSeconds(duration).Ticks, paid.ExpiresAt);
        Assert.Equal(paid.ExpiresAt + TimeSpan.FromSeconds(delay).Ticks, paid.ReadyAt);
        Assert.False(store.Resolve(ship, system, SpacePosition.Zero, new(1_000_000, 0, 0), fixture.Clock.Now).Blocked);
        var destination = new SpacePosition(1_000_000, 0, 0);
        Assert.Equal(destination, store.Resolve(ship, system, new(-1_000_000, 0, 0), destination, fixture.Clock.Now).Destination);
        Assert.Throws<InvalidDataException>(() => store.ActivateNullifier(fixture.PilotId, ship, module, fixture.Clock.Now));
        using (var metrics = SessionPerformance.Begin("warmed nullifier reads", _ => { }, force: true)!)
        {
            for (int i = 0; i < 8; i++)
            {
                Assert.False(store.Resolve(ship, system, SpacePosition.Zero, destination, fixture.Clock.Now).Blocked);
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using (var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot()))
        {
            snapshot.Project();
            using WorldDbContext saved = new(snapshot.Database);
            Assert.Equal(paid.ExpiresAt, saved.Set<ActiveFittedModule>().Find(module)!.ExpiresAt);
            Assert.Equal(paid.ReadyAt, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
            Assert.Equal(paid.Capacitor, saved.Set<ShipEnergy>().Find(ship)!.Charge, 6);
        }
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(paid.ExpiresAt - fixture.Clock.Now - 1));
        Assert.False(store.Resolve(ship, system, SpacePosition.Zero, destination, fixture.Clock.Now).Blocked);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.True(store.Resolve(ship, system, SpacePosition.Zero, destination, fixture.Clock.Now).Blocked);
        Assert.Throws<InvalidDataException>(() => store.ActivateNullifier(fixture.PilotId, ship, module, fixture.Clock.Now));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(paid.ReadyAt - fixture.Clock.Now));
        Assert.True(store.ActivateNullifier(fixture.PilotId, ship, module, fixture.Clock.Now).ExpiresAt > paid.ExpiresAt);
    }

    [Fact]
    public void DisconnectAndReopenedCheckpointPreservePaymentAndReactivationDeadline()
    {
        using var fixture = InterdictionTests.Prepare(hull: Cheetah);
        long ship = fixture.ShipId;
        long module = fixture.Fit(Nullifier);
        int system = Place(fixture);
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        SeedProbe(live, fixture, system);
        var store = fixture.Db.Services.GetRequiredService<IPilotInterdiction>();
        var paid = store.ActivateNullifier(fixture.PilotId, ship, module, fixture.Clock.Now);
        store.Stop(fixture.PilotId, ship, module, fixture.Clock.Now);
        Assert.True(store.Resolve(ship, system, SpacePosition.Zero, new(1_000_000, 0, 0), fixture.Clock.Now).Blocked);
        string reopenedPath = Path.Combine(Path.GetDirectoryName(fixture.Db.Path)!, "nullifier-reopened.sqlite");
        using (var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot()))
        {
            snapshot.Project();
            using WorldDbContext saved = new(snapshot.Database);
            Assert.Null(saved.Set<ActiveFittedModule>().Find(module));
            Assert.Equal(paid.ReadyAt, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
            Assert.Equal(paid.Capacitor, saved.Set<ShipEnergy>().Find(ship)!.Charge, 6);
            using var output = WorldConnections.FileConnection(reopenedPath);
            output.Open();
            snapshot.Database.BackupDatabase(output);
        }
        using LiveWorld reopened = new(new(reopenedPath), fixture.Clock, _ => { });
        var reopenedStore = new Eden.World.Propulsion.PilotInterdictionStore(new(reopenedPath));
        Assert.True(reopenedStore.Resolve(ship, system, SpacePosition.Zero, new(1_000_000, 0, 0), fixture.Clock.Now).Blocked);
        Assert.Throws<InvalidDataException>(() => reopenedStore.ActivateNullifier(fixture.PilotId, ship, module, fixture.Clock.Now));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(paid.ReadyAt - fixture.Clock.Now));
        var attributes = fixture.Fitting.ShipAttributes(fixture.PilotId);
        double recharged = reopened.Economy.Inspect(world => ShipResources.Capacitor(world, ship, attributes, fixture.Clock.Now));
        Assert.Equal(recharged - 50, reopenedStore.ActivateNullifier(fixture.PilotId, ship, module, fixture.Clock.Now).Capacitor, 6);
    }

    [Fact]
    public void CoverageRecognizesNullifiersAndCompletedInterdictorRoles()
    {
        using var fixture = InterdictionTests.Prepare(hull: Cheetah);
        using CatalogDbContext catalog = new(fixture.CatalogPath);
        var entries = ShipContentAudit.Read(catalog).Entries;
        foreach (int type in new[] { 58919, 58966, 58971, 58972 })
        {
            var entry = Assert.Single(entries, entry => entry.TypeId == type);
            Assert.True(entry.Admitted);
            Assert.Contains(entry.Checks, check => check.Operation == ContentOperation.Operation && check.Implemented);
        }
        foreach (int type in new[] { 22456, 12013 })
        {
            var entry = Assert.Single(entries, entry => entry.TypeId == type);
            Assert.Contains(entry.Checks, check => check.Role == HullCapability.Interdiction && check.Implemented);
        }
    }

    [Fact]
    public void FleetInterceptorUsesItsOwnDurationDelayAndPenaltyBonuses()
    {
        using var fixture = InterdictionTests.Prepare(hull: Ares);
        long ship = fixture.ShipId;
        var original = fixture.Fitting.ShipAttributes(fixture.PilotId);
        long module = fixture.Fit(Nullifier);
        Place(fixture);
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var current = fixture.Fitting.ShipAttributes(fixture.PilotId);
        Assert.Equal(original[DogmaAttributeId.MaxTargetRange] * 0.9, current[DogmaAttributeId.MaxTargetRange], 6);
        Assert.Equal(original[DogmaAttributeId.ScanResolution] * 0.9, current[DogmaAttributeId.ScanResolution], 6);
        var paid = fixture
            .Db.Services.GetRequiredService<IPilotInterdiction>()
            .ActivateNullifier(fixture.PilotId, ship, module, fixture.Clock.Now);
        Assert.Equal(20000, paid.Duration);
        Assert.Equal(paid.ExpiresAt + TimeSpan.FromSeconds(20).Ticks, paid.ReadyAt);
    }

    [Theory]
    [InlineData(Higgs)]
    [InlineData(BurstJammer)]
    public void IncompatibleFittedGearSuppressesImmunity(int type)
    {
        using var fixture = InterdictionTests.Prepare(hull: Cheetah);
        long ship = fixture.ShipId;
        long module = fixture.Fit(Nullifier);
        if (type == Higgs)
        {
            fixture.Fit(type, online: false);
        }
        else
        {
            // Imported/offline fitted burst jammers still suppress nullification; burst activation remains a separate feature.
            long item = fixture.Grant(type);
            using WorldDbContext world = new(fixture.Db.Path);
            var saved = world.Set<OwnedItem>().Find(item)!;
            saved.Singleton = true;
            saved.FlagId = (int)InventoryFlag.HighSlot1;
            saved.LocationId = ship;
            world.SaveChanges();
        }
        int system = Place(fixture);
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        SeedProbe(live, fixture, system);
        var store = fixture.Db.Services.GetRequiredService<IPilotInterdiction>();
        store.ActivateNullifier(fixture.PilotId, ship, module, fixture.Clock.Now);
        Assert.True(store.Resolve(ship, system, SpacePosition.Zero, new(1_000_000, 0, 0), fixture.Clock.Now).Blocked);
        Assert.Equal(0, fixture.Fitting.ShipAttributes(fixture.PilotId)[DogmaAttributeId.WarpBubbleImmune]);
    }

    [Fact]
    public void WrongHullAndDuplicateFittingPreserveOwnedItems()
    {
        using var rejected = InterdictionTests.Prepare();
        rejected.Train(3455, 5);
        long item = rejected.Grant(Nullifier);
        Assert.Throws<InvalidDataException>(() =>
            rejected.Fitting.Fit(rejected.PilotId, [item], rejected.StationId, (int)InventoryFlag.AutoFit)
        );
        Assert.Contains(rejected.Inventory.List(rejected.PilotId, rejected.StationId), row => row.Id == item && row.Quantity == 1);
        using var fixture = InterdictionTests.Prepare(hull: Cheetah);
        fixture.Fit(Nullifier);
        long second = fixture.Grant(Nullifier);
        Assert.Throws<InvalidDataException>(() =>
            fixture.Fitting.Fit(fixture.PilotId, [second], fixture.StationId, (int)InventoryFlag.AutoFit)
        );
        Assert.Contains(fixture.Inventory.List(fixture.PilotId, fixture.StationId), row => row.Id == second && row.Quantity == 1);
    }

    [Fact]
    public void InsufficientCapacitorRejectsWithoutBuyingImmunityOrCooldown()
    {
        using var fixture = InterdictionTests.Prepare(hull: Cheetah);
        long ship = fixture.ShipId;
        long module = fixture.Fit(Nullifier);
        int system = Place(fixture);
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        SeedProbe(live, fixture, system);
        using (var transaction = live.Economy.Begin(new(ships: [ship])))
        {
            var energy = transaction.Set<ShipEnergy>().Find(ship) ?? transaction.Add(new ShipEnergy { ShipId = ship }).Entity;
            energy.Charge = 0;
            energy.UpdatedAt = fixture.Clock.Now;
            transaction.Commit();
        }
        var store = fixture.Db.Services.GetRequiredService<IPilotInterdiction>();
        Assert.Throws<InvalidDataException>(() => store.ActivateNullifier(fixture.PilotId, ship, module, fixture.Clock.Now));
        Assert.True(store.Resolve(ship, system, SpacePosition.Zero, new(1_000_000, 0, 0), fixture.Clock.Now).Blocked);
        using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        snapshot.Project();
        using WorldDbContext saved = new(snapshot.Database);
        Assert.Null(saved.Set<ActiveFittedModule>().Find(module));
        Assert.Null(saved.Set<ModuleCooldown>().Find(module));
        Assert.Equal(0, saved.Set<ShipEnergy>().Find(ship)!.Charge);
    }

    [Fact]
    public void ShuttleInnateImmunityRequiresNoNullifier()
    {
        using var fixture = InterdictionTests.Prepare(hull: 672);
        long ship = fixture.ShipId;
        int system = Place(fixture);
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        SeedProbe(live, fixture, system);
        Assert.False(
            fixture
                .Db.Services.GetRequiredService<IPilotInterdiction>()
                .Resolve(ship, system, SpacePosition.Zero, new(1_000_000, 0, 0), fixture.Clock.Now)
                .Blocked
        );
    }

    [Fact]
    public void NativeNullifierActivatesWhileCloakedAndStopsAnUnfinishedWarpAtExpiry()
    {
        using var fixture = InterdictionTests.Prepare(hull: Cheetah);
        PassiveCatalogFixture.LoadSpatialModules(fixture.CatalogPath);
        long ship = fixture.ShipId;
        long module = fixture.Fit(Nullifier);
        long cloak = fixture.Fit(11578);
        var docked = fixture.Db.Store.FindPilot(1000001)!;
        int system = docked.SystemId;
        SpacePosition origin = new(docked.X + docked.Flight.StationRadius + 50000, docked.Y, docked.Z);
        fixture.Db.Store.SetPosition(ship, origin, system);
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        SeedProbe(live, fixture, system, origin + new SpacePosition(5000, 0, 0));
        using var scope = fixture.Db.Services.CreateScope();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected =>
                new(
                    fixture.Db.Store,
                    selected,
                    fixture.Clock,
                    fixture.Db.Services.GetRequiredService<SharedSpace>(),
                    [],
                    interdiction: fixture.Db.Services.GetRequiredService<IPilotInterdiction>()
                ),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        var route = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<InterdictionHandler>();
        Assert.Same(PilotCallResult.None, route.Call(context, "dogmaIM", "Activate", [cloak, "cloakingWarpSafe", null, 0L]));
        Assert.True(context.Flight.Cloaked);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(6));
        Assert.Same(
            PilotCallResult.None,
            route.Call(context, "dogmaIM", "Activate", [module, InterdictionHandler.NullifierEffectName, null, 1L])
        );
        Assert.True(context.Flight.Cloaked);
        Assert.Contains(
            context.Notifications,
            notification =>
                notification is ModuleAttributesChanged changed
                && changed.Changes.Any(change =>
                    change.Attribute == DogmaAttributeId.NextActivationTime && change.Value > fixture.Clock.Now
                )
        );
        Assert.Same(
            PilotCallResult.None,
            context.Flight.Execute(new FlightCommand.WarpToPoint(1, origin + new SpacePosition(1_000_000, 0, 0), 0))
        );
        Assert.NotNull(handler.ActiveEffect(pilot, module));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(10));
        using (live.UseSystem(system))
        {
            handler.Advance(context);
        }
        Assert.Null(handler.ActiveEffect(pilot, module));
        Assert.Contains(context.Flight.Updates, update => update.Command is DestinyCommand.Stop);
        Assert.True(context.Flight.Cloaked);
        Assert.Same(PilotCallResult.None, route.Call(context, "dogmaIM", "Deactivate", [cloak, "cloakingWarpSafe"]));
    }

    [Theory]
    [InlineData(DogmaEffect.FocusedWarpDisruption)]
    [InlineData(DogmaEffect.FocusedWarpScrambling)]
    public void NullificationDoesNotDefeatFocusedTackle(DogmaEffect effect)
    {
        using var fixture = InterdictionTests.Prepare(hull: Cheetah);
        long ship = fixture.ShipId;
        long module = fixture.Fit(Nullifier);
        int system = Place(fixture);
        var attacker = fixture.Db.Store.FindPilot(1000002)!;
        fixture.Db.Store.SetPosition(attacker.ShipId, new(1000, 0, 0), system);
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        SeedProbe(live, fixture, system);
        using (var transaction = live.Economy.Begin(new(ships: [ship, attacker.ShipId])))
        {
            const long sourceModule = 2_000_099_000_001;
            transaction.Add(
                new OwnedItem
                {
                    Id = sourceModule,
                    OwnerId = attacker.PilotId,
                    TypeId = 28654,
                    LocationId = attacker.ShipId,
                    Quantity = 1,
                    Singleton = true,
                    Online = true,
                    FlagId = (int)InventoryFlag.HighSlot0,
                }
            );
            transaction.Add(
                new WorldTackle
                {
                    ModuleId = sourceModule,
                    PilotId = attacker.PilotId,
                    SourceShipId = attacker.ShipId,
                    TypeId = 28654,
                    SystemId = system,
                    TargetId = ship,
                    TargetKind = TackleTargetKind.Ship,
                    Effect = effect,
                    Strength = 100,
                    MwdBlock = effect == DogmaEffect.FocusedWarpScrambling ? 1 : 0,
                    StartedAt = fixture.Clock.Now,
                    ExpiresAt = fixture.Clock.Now + TimeSpan.FromSeconds(6).Ticks,
                }
            );
            transaction.Commit();
        }
        var store = fixture.Db.Services.GetRequiredService<IPilotInterdiction>();
        store.ActivateNullifier(fixture.PilotId, ship, module, fixture.Clock.Now);
        Assert.False(store.Resolve(ship, system, SpacePosition.Zero, new(1_000_000, 0, 0), fixture.Clock.Now).Blocked);
        var tackle = fixture.Db.Services.GetRequiredService<IPilotTackle>();
        Assert.Equal(100, Assert.Single(tackle.Incoming(ship)).Module.Strength);
        Assert.True(tackle.ShipAttributes(fixture.PilotId)[DogmaAttributeId.WarpScrambleStatus] > 0);
    }

    [Fact]
    public void NullifierRulesRejectInvalidTypedProfiles()
    {
        Dictionary<DogmaAttributeId, double> attributes = new()
        {
            [DogmaAttributeId.SignatureSuppressionDuration] = 10000,
            [DogmaAttributeId.CapacitorNeed] = 50,
            [DogmaAttributeId.ModuleReactivationDelay] = 100000,
        };
        Assert.Equal(new(10000, 50, 100000), InterdictionNullifier.Define(attributes));
        foreach (var attribute in attributes.Keys.ToArray())
        {
            var invalid = attributes.ToDictionary();
            invalid[attribute] = double.PositiveInfinity;
            Assert.Throws<InvalidDataException>(() => InterdictionNullifier.Define(invalid));
        }
    }

    internal static int Place(Fixture fixture)
    {
        int system = fixture.Db.Store.FindPilot(1000001)!.SystemId;
        fixture.Db.Store.SetPosition(fixture.ShipId, SpacePosition.Zero, system);
        return system;
    }

    internal static void SeedProbe(LiveWorld live, Fixture fixture, int system, SpacePosition? center = null)
    {
        using var transaction = live.Economy.Begin(new(systems: [system]));
        center ??= new(5000, 0, 0);
        transaction.Add(
            new SavedInterdictionField
            {
                Id = InterdictionRules.FirstProbeId,
                TypeId = InterdictionTests.Probe,
                SystemId = system,
                X = center.X,
                Y = center.Y,
                Z = center.Z,
                Radius = 20000,
                HitPoints = 1000,
                ExpiresAt = fixture.Clock.Now + TimeSpan.FromMinutes(10).Ticks,
            }
        );
        transaction.Commit();
    }
}
