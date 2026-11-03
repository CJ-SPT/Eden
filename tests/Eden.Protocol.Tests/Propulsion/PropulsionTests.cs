using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Propulsion;

public sealed class PropulsionTests
{
    private sealed class Clock : TimeProvider
    {
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
        public long Milliseconds { get; set; }
        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Milliseconds;

        public override DateTimeOffset GetUtcNow() => _start.AddMilliseconds(Milliseconds);

        public long Now => GetUtcNow().UtcDateTime.ToFileTimeUtc();
    }

    private sealed class Fixture : IDisposable
    {
        public TestWorld Db { get; } = new();
        public Clock Clock { get; } = new();
        public IPilotPropulsion Store => Db.Services.GetRequiredService<IPilotPropulsion>();
        public IPilotFitting Fitting => Db.Services.GetRequiredService<IPilotFitting>();
        public DockedPilot Pilot => Db.Store.FindPilot(1000001)!;
        public long Module { get; }

        public Fixture()
        {
            using (CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT INTO ItemGroup VALUES (46,'Afterburner',7);
                    INSERT INTO ItemType(Id,Name,GroupId) VALUES (439,'1MN Afterburner I',46),(3449,'Navigation',257),
                        (3450,'Afterburner',257),(3451,'Fuel Conservation',257),(3452,'Acceleration Control',257);
                    INSERT INTO DogmaAttribute VALUES (6,'capacitorNeed'),(11,'powerOutput'),(13,'medSlots'),(14,'hiSlots'),
                        (20,'speedFactor'),(30,'power'),(48,'cpuOutput'),(50,'cpu'),(66,'durationBonus'),(73,'duration'),
                        (182,'requiredSkill1'),(277,'requiredSkill1Level'),(315,'velocityBonus'),(317,'capNeedBonus'),
                        (318,'speedFBonus'),(567,'speedBoostFactor'),(763,'maxGroupActive'),(796,'massAddition');
                    INSERT INTO TypeAttribute VALUES (601,11,23),(601,13,2),(601,14,2),(601,48,135),
                        (439,6,20),(439,20,115),(439,30,10),(439,50,15),(439,73,10000),(439,182,3450),(439,277,1),
                        (439,567,1500000),(439,763,1),(439,796,500000),(3449,275,1),(3449,315,5),
                        (3450,275,1),(3450,66,-5),(3450,317,-10),(3451,275,2),(3451,317,-10),(3452,275,4),(3452,318,5);
                    INSERT INTO SourceRecord VALUES ('client/types','439','{{"volume":5}}');
                    """
                );
            }
            using (WorldDbContext world = new(Db.Path))
            {
                world.Add(
                    new PilotSkill
                    {
                        PilotId = Pilot.PilotId,
                        TypeId = (int)NavigationSkill.Navigation,
                        Points = 8000,
                    }
                );
                world.Add(
                    new PilotSkill
                    {
                        PilotId = Pilot.PilotId,
                        TypeId = (int)NavigationSkill.Afterburner,
                        Points = 8000,
                    }
                );
                Module = PilotInventoryStore.NextId(world);
                world.Add(
                    new OwnedItem
                    {
                        Id = Module,
                        OwnerId = Pilot.PilotId,
                        TypeId = 439,
                        LocationId = Pilot.StationId,
                        FlagId = (int)InventoryFlag.Hangar,
                        Quantity = 1,
                    }
                );
                world.SaveChanges();
            }
            Fitting.Fit(Pilot.PilotId, [Module], Pilot.StationId, (int)InventoryFlag.AutoFit);
            Fitting.SetOnline(Pilot.PilotId, Pilot.ShipId, Module, true);
            Db.Store.SetPosition(Pilot.ShipId, new(1_000_000, 0, 0));
        }

        public PilotCallContext Client(SharedSpace? space = null, IReadOnlyList<StaticSpaceObject>? objects = null)
        {
            var pilot = Pilot;
            PilotCallContext context = new(
                1000001,
                pilot.Account,
                [pilot],
                p => new(Db.Store, p, Clock, space ?? new(), objects ?? []),
                7,
                SignedSettings.Empty
            );
            context.Select(pilot);
            context.Flight.Snapshot();
            context.Flight.Updates.Clear();
            return context;
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public void EarnedNavigationAndModuleSkillsAgreeWithNativeBrainAndMassDependentFlight()
    {
        using Fixture f = new();
        Assert.Equal(339.25, f.Pilot.Flight.MaxSpeed);
        Assert.Equal(339.25, f.Fitting.ShipAttributes(f.Pilot.PilotId)[DogmaAttributeId.MaxVelocity]);
        var cycle = f.Store.Activate(f.Pilot.PilotId, f.Module, f.Clock.Now);
        Assert.Equal(8500, cycle.Module.DurationMilliseconds);
        Assert.Equal(14, cycle.Module.CapacitorNeed);
        Assert.Equal(116, cycle.Capacitor);
        var boosted = cycle.Module.Apply(f.Pilot.Flight);
        Assert.Equal(1_663_000, boosted.Mass);
        Assert.Equal(339.25 * (1 + 1.15 * 1_500_000 / 1_663_000), boosted.MaxSpeed, 3);
        var brain = f.Db.Services.GetRequiredService<PilotBrain>().Read(f.Pilot.PilotId, f.Pilot.ShipId);
        var effects = Assert
            .IsType<List<object?>>(WireAssertions.Materialize(new BlueList<BrainEffect>(brain.ShipEffects)))
            .Cast<BlueNewObject>()
            .Select(e => (Dictionary<string, object?>)e.State!)
            .ToArray();
        Assert.Contains(effects, e => Equals(e["modifierType"], "M") && Equals(e["value"], 15d) && Equals(e["toItemID"], f.Pilot.ShipId));
        Assert.Contains(effects, e => Equals(e["modifierType"], "LRS") && Equals(e["value"], -30d));
        Assert.Contains(effects, e => Equals(e["toAttribID"], (int)DogmaAttributeId.Duration) && Equals(e["value"], -15d));
        Assert.NotEmpty(BlueMarshal.Encode(brain));
    }

    [Fact]
    public void FuelConservationAndAccelerationControlUseCatalogBonuses()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new PilotSkill
                {
                    PilotId = f.Pilot.PilotId,
                    TypeId = (int)NavigationSkill.FuelConservation,
                    Points = 16000,
                }
            );
            world.Add(
                new PilotSkill
                {
                    PilotId = f.Pilot.PilotId,
                    TypeId = (int)NavigationSkill.AccelerationControl,
                    Points = 32000,
                }
            );
            world.SaveChanges();
        }
        var cycle = f.Store.Activate(f.Pilot.PilotId, f.Module, f.Clock.Now);
        Assert.Equal(9.8, cycle.Module.CapacitorNeed, 8);
        Assert.Equal(132.25, cycle.Module.SpeedFactor, 8);
    }

    [Fact]
    public async Task CompetingCyclesPayOnceAndCooldownSurvivesRestart()
    {
        using Fixture f = new();
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        try
                        {
                            return f.Store.Activate(f.Pilot.PilotId, f.Module, f.Clock.Now);
                        }
                        catch (InvalidDataException)
                        {
                            return null;
                        }
                    })
                )
        );
        Assert.Single(results, r => r is not null);
        _ = new SqliteWorld(f.Db.Path);
        Assert.Throws<InvalidDataException>(() => f.Store.Activate(f.Pilot.PilotId, f.Module, f.Clock.Now));
        Assert.Equal(
            116,
            f.Db.Services.GetRequiredService<Eden.Gameplay.Ships.IPilotShipResources>().Capacitor(f.Pilot.PilotId, f.Clock.Now)
        );
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(f.Clock.Now + 8500 * TimeSpan.TicksPerMillisecond, saved.Set<ModuleCooldown>().Single().ReadyAt);
        Assert.Equal(339.25, f.Pilot.Flight.MaxSpeed);
    }

    [Fact]
    public void WrongOwnerSkillsOfflineDockedAndInsufficientCapacitorCannotAddSpeedOrPay()
    {
        using Fixture f = new();
        Assert.Throws<InvalidDataException>(() => f.Store.Activate(90000002, f.Module, f.Clock.Now));
        f.Db.Store.SetPosition(f.Pilot.ShipId, null);
        Assert.Throws<InvalidDataException>(() => f.Store.Activate(f.Pilot.PilotId, f.Module, f.Clock.Now));
        f.Fitting.SetOnline(f.Pilot.PilotId, f.Pilot.ShipId, f.Module, false);
        f.Db.Store.SetPosition(f.Pilot.ShipId, SpacePosition.Zero);
        Assert.Throws<InvalidDataException>(() => f.Store.Activate(f.Pilot.PilotId, f.Module, f.Clock.Now));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(f.Module)!.Online = true;
            world.Set<PilotSkill>().Find(f.Pilot.PilotId, (int)NavigationSkill.Afterburner)!.Points = 0;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Store.Activate(f.Pilot.PilotId, f.Module, f.Clock.Now));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotSkill>().Find(f.Pilot.PilotId, (int)NavigationSkill.Afterburner)!.Points = 8000;
            world.Add(
                new ShipEnergy
                {
                    ShipId = f.Pilot.ShipId,
                    Charge = 0,
                    UpdatedAt = f.Clock.Now,
                }
            );
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Store.Activate(f.Pilot.PilotId, f.Module, f.Clock.Now));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ModuleCooldown>());
        Assert.Equal(0, saved.Set<ShipEnergy>().Single().Charge);
    }

    [Fact]
    public void OnlyOneAfterburnerRunsAndCpuPowerAndRackLimitsStillApply()
    {
        using Fixture f = new();
        f.Db.Store.SetPosition(f.Pilot.ShipId, null);
        var inventory = f.Db.Services.GetRequiredService<IPilotInventory>();
        inventory.Move(f.Pilot.PilotId, [f.Module], f.Pilot.ShipId, f.Pilot.StationId, (int)InventoryFlag.Hangar, null);
        Assert.Throws<InvalidDataException>(() =>
            f.Fitting.Fit(f.Pilot.PilotId, [f.Module], f.Pilot.StationId, (int)InventoryFlag.HighSlot0)
        );
        f.Fitting.Fit(f.Pilot.PilotId, [f.Module], f.Pilot.StationId, (int)InventoryFlag.AutoFit);
        f.Fitting.SetOnline(f.Pilot.PilotId, f.Pilot.ShipId, f.Module, true);
        long second = f.Module + 1;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new OwnedItem
                {
                    Id = second,
                    OwnerId = f.Pilot.PilotId,
                    TypeId = 439,
                    LocationId = f.Pilot.StationId,
                    FlagId = 4,
                    Quantity = 1,
                }
            );
            world.SaveChanges();
        }
        f.Fitting.Fit(f.Pilot.PilotId, [second], f.Pilot.StationId, (int)InventoryFlag.AutoFit);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=29 WHERE ItemTypeId=601 AND AttributeId=48");
        }
        Assert.Throws<InvalidDataException>(() => f.Fitting.SetOnline(f.Pilot.PilotId, f.Pilot.ShipId, second, true));
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=135 WHERE ItemTypeId=601 AND AttributeId=48");
        }
        f.Fitting.SetOnline(f.Pilot.PilotId, f.Pilot.ShipId, second, true);
        f.Db.Store.SetPosition(f.Pilot.ShipId, SpacePosition.Zero);
        f.Store.Activate(f.Pilot.PilotId, f.Module, f.Clock.Now);
        Assert.Throws<InvalidDataException>(() => f.Store.Activate(f.Pilot.PilotId, second, f.Clock.Now));
    }

    [Fact]
    public void ManualStopFinishesPaidCycleAndSpeedChangesPreserveVelocityForNearbyPilots()
    {
        using Fixture f = new();
        SharedSpace space = new();
        using var context = f.Client(space);
        var wing = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(wing.ShipId, new(1_000_100, 0, 0));
        using FlightSession observer = new(f.Db.Store, f.Db.Store.FindPilot(1000002)!, f.Clock, space, []);
        observer.Snapshot();
        observer.Updates.Clear();
        PropulsionHandler handler = new(f.Clock, f.Store);
        Assert.Null(context.Flight.CallWire("beyonce", "CmdGotoDirection", [1d, 0d, 0d]));
        f.Clock.Milliseconds = 1000;
        context.Flight.Checkpoint();
        var velocity = context.Flight.Velocity;
        Assert.Null(handler.CallWire(context, "dogmaIM", "Activate", [f.Module, PropulsionWireDefinitions.EffectName, null, 100L]));
        Assert.Equal(velocity, context.Flight.Velocity);
        Assert.True(context.Pilot.Flight.MaxSpeed > 690);
        observer.RefreshPeers();
        var actions = observer.WireUpdates().Cast<object?[]>().Select(u => (object?[])u[1]!).ToArray();
        Assert.Contains(actions, a => Equals(a[0], "SetMaxSpeed"));
        Assert.Contains(actions, a => Equals(a[0], "SetBallMass"));
        Assert.Contains(
            actions,
            a => Equals(a[0], "OnSpecialFX") && Equals(((object?[])a[1]!)[5], PropulsionWireDefinitions.EffectGraphic)
        );
        Assert.DoesNotContain(actions, a => Equals(a[0], "SetState"));
        Assert.Null(handler.CallWire(context, "dogmaIM", "Deactivate", [f.Module, PropulsionWireDefinitions.EffectName]));
        Assert.NotNull(handler.ActiveEffect(context.Pilot, f.Module));
        f.Clock.Milliseconds = 9500;
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(context.Pilot, f.Module));
        Assert.Equal(339.25, context.Pilot.Flight.MaxSpeed);
        Assert.Equal(1_163_000, context.Pilot.Flight.Mass);
        Assert.True(context.Flight.Velocity.Length > velocity.Length);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Single(saved.Set<ModuleCooldown>());
        Assert.NotEmpty(BlueMarshal.Encode(context.Notifications.Select(n => (object?)n.Arguments).ToList()));
    }

    [Fact]
    public void RepeatedCyclesDoNotStackAndUnpaidNextCycleRemovesAllModifiers()
    {
        using Fixture f = new();
        using var context = f.Client();
        PropulsionHandler handler = new(f.Clock, f.Store);
        object? Activate() =>
            handler.CallWire(context, "dogmaIM", "Activate", [f.Module, PropulsionWireDefinitions.EffectName, null, 100L]);
        Assert.Null(Activate());
        double speed = context.Pilot.Flight.MaxSpeed;
        Assert.Null(Activate());
        context.Flight.Updates.Clear();
        f.Clock.Milliseconds = 60_000;
        handler.Advance(context);
        Assert.Equal(speed, context.Pilot.Flight.MaxSpeed);
        var actions = context.Flight.WireUpdates().Cast<object?[]>().Select(u => (object?[])u[1]!).ToArray();
        Assert.DoesNotContain(actions, a => Equals(a[0], "SetMaxSpeed") || Equals(a[0], "SetBallMass"));
        using (WorldDbContext world = new(f.Db.Path))
        {
            var energy = world.Set<ShipEnergy>().Single();
            energy.Charge = 0;
            energy.UpdatedAt = f.Clock.Now + 8500 * TimeSpan.TicksPerMillisecond;
            world.SaveChanges();
        }
        f.Clock.Milliseconds += 8500;
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(context.Pilot, f.Module));
        Assert.Equal(339.25, context.Pilot.Flight.MaxSpeed);
        Assert.Equal(1_163_000, context.Pilot.Flight.Mass);
    }

    [Fact]
    public void InvalidTargetsAndHullReplacementNeverTransferTheEffect()
    {
        using Fixture f = new();
        using var context = f.Client();
        PropulsionHandler handler = new(f.Clock, f.Store);
        Assert.IsType<PilotCallResult.Failure>(
            handler.CallWire(context, "dogmaIM", "Activate", [f.Module, PropulsionWireDefinitions.EffectName, 999L, 0L])
        );
        Assert.Null(handler.CallWire(context, "dogmaIM", "Activate", [f.Module, PropulsionWireDefinitions.EffectName, null, 0L]));
        handler.Cancel(context);
        Assert.Null(handler.ActiveEffect(context.Pilot, f.Module));
        Assert.Equal(339.25, context.Pilot.Flight.MaxSpeed);
        var capsule = context.Pilot with { ShipId = 999, Flight = context.Pilot.Flight with { MaxSpeed = 100, Mass = 32_000 } };
        context.Flight.ReplaceDestroyedShip(capsule);
        handler.Advance(context);
        Assert.Equal(capsule.Flight, context.Pilot.Flight);
    }

    [Fact]
    public void WarpCannotStartANewCycleAndDockingImmediatelyRestoresBaseFlight()
    {
        using Fixture f = new();
        var destination = new StaticSpaceObject(999, 52673, 8, 2, 0, new(1_000_000_000, 0, 0), 1000);
        using (var context = f.Client(objects: [destination]))
        {
            PropulsionHandler handler = new(f.Clock, f.Store);
            Assert.Null(handler.CallWire(context, "dogmaIM", "Activate", [f.Module, PropulsionWireDefinitions.EffectName, null, 100L]));
            Assert.Null(context.Flight.CallWire("beyonce", "CmdWarpToStuff", ["item", destination.Id]));
            Assert.IsType<PilotCallResult.Failure>(
                handler.CallWire(context, "dogmaIM", "Activate", [f.Module, PropulsionWireDefinitions.EffectName, null, 100L])
            );
            f.Clock.Milliseconds = 9000;
            handler.Advance(context);
            Assert.Null(handler.ActiveEffect(context.Pilot, f.Module));
            Assert.Equal(339.25, context.Pilot.Flight.MaxSpeed);
        }
        var pilot = f.Pilot;
        f.Db.Store.SetPosition(pilot.ShipId, new(pilot.X + pilot.Flight.StationRadius + 500, pilot.Y, pilot.Z));
        using var docked = f.Client(objects: f.Db.Objects);
        PropulsionHandler active = new(f.Clock, f.Store);
        Assert.Null(active.CallWire(docked, "dogmaIM", "Activate", [f.Module, PropulsionWireDefinitions.EffectName, null, 100L]));
        Assert.Null(docked.Flight.CallWire("beyonce", "CmdDock", [(long)pilot.StationId, pilot.ShipId]));
        active.Advance(docked);
        Assert.Null(active.ActiveEffect(docked.Pilot, f.Module));
        Assert.Null(docked.Pilot.Position);
        Assert.Equal(339.25, docked.Pilot.Flight.MaxSpeed);
        Assert.Equal(1_163_000, docked.Pilot.Flight.Mass);
    }

    [Fact]
    public void NavigationRefreshChangesBaselineWithoutStackingAnActiveAfterburner()
    {
        using Fixture f = new();
        using var context = f.Client();
        PropulsionHandler handler = new(f.Clock, f.Store);
        Assert.Null(handler.CallWire(context, "dogmaIM", "Activate", [f.Module, PropulsionWireDefinitions.EffectName, null, 100L]));
        double before = context.Pilot.Flight.MaxSpeed;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotSkill>().Find(f.Pilot.PilotId, (int)NavigationSkill.Navigation)!.Points = 45255;
            world.SaveChanges();
        }
        handler.RefreshNavigation(context);
        double after = context.Pilot.Flight.MaxSpeed;
        Assert.Equal(before * 1.2 / 1.15, after, 3);
        handler.RefreshNavigation(context);
        Assert.Equal(after, context.Pilot.Flight.MaxSpeed);
        handler.Cancel(context);
        Assert.Equal(354, context.Pilot.Flight.MaxSpeed);
    }
}
