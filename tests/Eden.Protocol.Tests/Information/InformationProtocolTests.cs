using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Information;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Structures;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Information;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.Protocol.Tests.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Information;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Information;

public sealed partial class InformationTests
{
    private static PilotCallContext Client(Fixture f, long account)
    {
        var pilot = f.Db.Store.FindPilot(account)!;
        var space = f.Db.Services.GetRequiredService<SharedSpace>();
        PilotCallContext context = new(
            account,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, space, f.Db.Objects, wrecks: f.Db.Services.GetRequiredService<IWrecks>()),
            account,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        return context;
    }

    private static void Lock(Fixture f, PilotCallContext source, TargetingHandler targeting, long target)
    {
        _ = targeting.Call(source, "dogmaIM", "AddTarget", [target]);
        f.Clock.Seconds += 30;
        targeting.Advance(source);
        Assert.True(targeting.IsLocked(target));
    }

    [Theory]
    [InlineData(442, "cargoScan", "OnCargoScanComplete", 2)]
    [InlineData(443, "shipScan", "OnShipScanCompleted", 4)]
    public void NativeScannerDispatchPublishesThePinnedReplyExactlyOnce(int type, string effect, string notification, int arity)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        Space(f);
        using var source = Client(f, 1000001);
        using var recipient = Client(f, 1000002);
        using var scope = f.Db.Services.CreateScope();
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<InformationHandler>();
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        Lock(f, source, targeting, Target(f));
        source.Notifications.Clear();
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Activate", [module, effect, Target(f), 99L]));
        var active = handler.ActiveEffect(source.Pilot, module);
        Assert.NotNull(active);
        Assert.Equal(0, active.Repeats);
        handler.Advance(source);
        Assert.DoesNotContain(source.Notifications, n => n.Name == notification);
        f.Clock.Milliseconds += (long)active.DurationMilliseconds!.Value;
        handler.Advance(source);
        handler.Advance(source);
        var result = Assert.Single(source.Notifications, n => n.Name == notification);
        Assert.Equal(arity, result.Arguments.Length);
        Assert.Equal(Target(f), result.Arguments[0]);
        Assert.IsType<List<object?>>(result.Arguments[^1]);
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
        Assert.DoesNotContain(recipient.Notifications, n => n.Name == notification);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<SavedInformationCycle>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CargoScanImmunityIsEnforcedBeforePaymentAndAtDisclosure(bool immuneAtActivation)
    {
        const int crane = 12729;
        using Fixture f = Prepare();
        PassiveCatalogFixture.LoadTechTwoHulls(f.CatalogPath, crane);
        long cargoScanner = f.Fit(442);
        long shipScanner = f.Fit(443);
        long ship = f.ShipId;
        long target = Target(f);
        Space(f);
        if (!immuneAtActivation)
        {
            SetResistance(0);
        }
        WorldDatabase database = new(f.Db.Path);
        using LiveWorld live = new(database, f.Clock, _ => { });
        using (WorldActionScope setup = new(database, new(ships: [target])))
        {
            setup.World.Ships.Find(target)!.TypeId = crane;
            setup.Commit();
        }
        using var source = Client(f, 1000001);
        using var recipient = Client(f, 1000002);
        using var scope = f.Db.Services.CreateScope();
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<InformationHandler>();
        Lock(f, source, targeting, target);

        double? capacitorBefore = live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(ship)?.Charge);
        var activation = handler.CallModule(source, "Activate", [cargoScanner, "cargoScan", target, 0L]);
        if (immuneAtActivation)
        {
            Assert.Contains("immune to cargo scanning", Assert.IsType<PilotCallResult.Failure>(activation).Message);
            Assert.Equal(capacitorBefore, live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(ship)?.Charge));
            Assert.Null(live.Economy.Inspect(world => world.Set<ModuleCooldown>().Find(cargoScanner)));
            Assert.IsType<PilotCallResult.Success>(handler.CallModule(source, "Activate", [shipScanner, "shipScan", target, 0L]));
            f.Clock.Milliseconds += (long)handler.ActiveEffect(source.Pilot, shipScanner)!.DurationMilliseconds!.Value;
            handler.Advance(source);
            Assert.Single(source.Notifications, notification => notification.Name == "OnShipScanCompleted");
        }
        else
        {
            Assert.IsType<PilotCallResult.Success>(activation);
            double paid = live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(ship)!.Charge);
            long deadline = live.Economy.Inspect(world => world.Set<ModuleCooldown>().Find(cargoScanner)!.ReadyAt);
            SetResistance(1);
            f.Clock.Milliseconds += (long)handler.ActiveEffect(source.Pilot, cargoScanner)!.DurationMilliseconds!.Value;
            handler.Advance(source);
            Assert.Equal(paid, live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(ship)!.Charge));
            Assert.Equal(deadline, live.Economy.Inspect(world => world.Set<ModuleCooldown>().Find(cargoScanner)!.ReadyAt));
        }
        Assert.Null(handler.ActiveEffect(source.Pilot, cargoScanner));
        Assert.Null(live.Economy.Inspect(world => world.Set<SavedInformationCycle>().Find(cargoScanner)));
        Assert.DoesNotContain(source.Notifications, notification => notification.Name == "OnCargoScanComplete");
        Assert.DoesNotContain(recipient.Notifications, notification => notification.Name == "OnCargoScanComplete");

        void SetResistance(double value)
        {
            using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
            int attributeId = (int)DogmaAttributeId.CargoScanResistance;
            Assert.Equal(
                1,
                catalog.Database.ExecuteSql(
                    $"UPDATE TypeAttribute SET Value={value} WHERE ItemTypeId={crane} AND AttributeId={attributeId}"
                )
            );
        }
    }

    [Fact]
    public void NativeShipScannerReturnsCompleteUpwellFitThroughFalloffAndCancelsAfterDestruction()
    {
        const long structure = StructureRules.FirstStructureId + 51;
        const int astrahus = 35832;
        const int launcher = 35922;
        const int reprocessing = 35899;
        const int tritanium = 34;
        using Fixture f = Prepare();
        PassiveCatalogFixture.LoadStructures(f.CatalogPath);
        int shipType = f.Db.Store.FindPilot(1000001)!.ShipTypeId;
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            int targetRange = (int)DogmaAttributeId.MaxTargetRange;
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES ({shipType},{targetRange},150000)");
        }
        long scanner = f.Fit(443);
        long owner = f.Db.Store.FindPilot(1000002)!.PilotId;
        PlayerCorporationTests.Prepare(f.Db, owner);
        WorldDatabase database = new(f.Db.Path);
        using LiveWorld live = new(database, f.Clock, _ => { });
        int corporation = f
            .Db.Services.GetRequiredService<IPlayerCorporations>()
            .Create(owner, PlayerCorporationTests.Details())
            .Corporation.Id;
        Space(f);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        SpacePosition position = pilot.Position! + new SpacePosition(110000, 0, 0);
        using (WorldActionScope setup = new(database, new(systems: [pilot.SystemId])))
        {
            setup.World.Add(
                new SavedStructure
                {
                    Id = structure,
                    TypeId = astrahus,
                    Name = "Private Astrahus",
                    CorporationId = corporation,
                    SystemId = pilot.SystemId,
                    X = position.X,
                    Y = position.Y,
                    Z = position.Z,
                    State = StructureState.ShieldVulnerable,
                    HasCore = true,
                    StateStartedAt = f.Clock.Now,
                }
            );
            SetItem(7000000000001, launcher, (int)InventoryFlag.HighSlot0);
            SetItem(7000000000002, launcher, (int)InventoryFlag.HighSlot1);
            SetItem(7000000000003, reprocessing, StructureRules.FirstServiceSlot);
            SetItem(7000000000004, tritanium, (int)InventoryFlag.Hangar);
            setup.World.Add(
                new SavedStructureService
                {
                    ItemId = 7000000000003,
                    StructureId = structure,
                    Slot = StructureRules.FirstServiceSlot,
                }
            );
            StructureEnergy.Write(setup.World, structure, 12345, f.Clock.Now);
            setup.Commit();

            void SetItem(long id, int type, int flag) =>
                setup.World.Add(
                    new OwnedItem
                    {
                        Id = id,
                        OwnerId = corporation,
                        LocationId = structure,
                        TypeId = type,
                        FlagId = flag,
                        Quantity = 1,
                        Singleton = true,
                    }
                );
        }
        using var source = Client(f, 1000001);
        using var recipient = Client(f, 1000002);
        using var services = f.Db.Services.CreateScope();
        var targeting = services.ServiceProvider.GetRequiredService<TargetingHandler>();
        var store = new PilotInformationStore(database, shipScanRandom: new ShipScanRolls());
        using var handler = new InformationHandler(
            store,
            targeting,
            f.Clock,
            services.ServiceProvider.GetRequiredService<SupportModuleWire>()
        );
        Lock(f, source, targeting, structure);
        StartScan();
        handler.Advance(source);
        Assert.DoesNotContain(source.Notifications, item => item.Name == "OnShipScanCompleted");
        AdvanceToDeadline();
        var result = Assert.Single(source.Notifications.OfType<ShipScanCompleted>());
        Assert.Equal(structure, result.Scan.ShipId);
        Assert.Equal(200000, result.Scan.CapacitorCapacity);
        Assert.InRange(result.Scan.CapacitorCharge, 12345, 50000);
        Assert.Equal(new ScanItem[] { new(launcher, 2), new(reprocessing, 1) }, result.Scan.Hardware);
        Assert.DoesNotContain(recipient.Notifications, item => item.Name == "OnShipScanCompleted");

        StartScan();
        using (WorldActionScope destroy = new(database, new(systems: [pilot.SystemId])))
        {
            destroy.World.Set<SavedStructure>().Find(structure)!.State = StructureState.Destroyed;
            destroy.Commit();
        }
        AdvanceToDeadline();
        Assert.Single(source.Notifications.OfType<ShipScanCompleted>());
        Assert.Null(handler.ActiveEffect(source.Pilot, scanner));
        Assert.NotNull(live.Economy.Inspect(world => world.Set<ModuleCooldown>().Find(scanner)));

        void StartScan() =>
            Assert.IsType<PilotCallResult.Success>(handler.CallModule(source, "Activate", [scanner, "shipScan", structure, 0L]));

        void AdvanceToDeadline()
        {
            f.Clock.Milliseconds += (long)Math.Ceiling(handler.ActiveEffect(source.Pilot, scanner)!.DurationMilliseconds!.Value);
            handler.Advance(source);
        }
    }

    [Fact]
    public void NativeCargoScannerRevealsOneContainerLayerWithoutChangingContents()
    {
        const int auditContainer = 17363;
        const int standardContainer = 3297;
        const int tritanium = 34;
        const int veldspar = 1230;
        const long outer = 7000000000001;
        const long recovered = 7000000000002;
        const long inner = 7000000000003;
        const long hidden = 7000000000004;
        using Fixture f = Prepare();
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql(
                $"""
                INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES (448,'Audit Log Secure Container',2);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Capacity) VALUES
                    ({auditContainer},'Small Audit Log Secure Container',448,120),({standardContainer},'Small Standard Container',12,120);
                """
            );
        }
        long scanner = f.Fit(442);
        Space(f);
        var target = f.Db.Store.FindPilot(1000002)!;
        WorldDatabase database = new(f.Db.Path);
        using LiveWorld live = new(database, f.Clock, _ => { });
        using (WorldActionScope setup = new(database, new(ships: [target.ShipId])))
        {
            Add(outer, auditContainer, target.ShipId, InventoryFlag.Cargo, 1, singleton: true);
            Add(recovered, tritanium, outer, InventoryFlag.Locked, 25);
            Add(inner, standardContainer, outer, InventoryFlag.Unlocked, 1, singleton: true);
            Add(hidden, veldspar, inner, InventoryFlag.None, 71);
            setup.World.Add(new SavedCargoContainer { ItemId = outer, GeneralPassword = "fixture" });
            setup.Commit();

            void Add(long id, int type, long location, InventoryFlag flag, int quantity, bool singleton = false) =>
                setup.World.Add(
                    new OwnedItem
                    {
                        Id = id,
                        TypeId = type,
                        OwnerId = target.PilotId,
                        LocationId = location,
                        FlagId = (int)flag,
                        Quantity = quantity,
                        Singleton = singleton,
                    }
                );
        }
        using var source = Client(f, 1000001);
        using var recipient = Client(f, 1000002);
        using var services = f.Db.Services.CreateScope();
        var targeting = services.ServiceProvider.GetRequiredService<TargetingHandler>();
        var handler = services.ServiceProvider.GetRequiredService<InformationHandler>();
        Lock(f, source, targeting, target.ShipId);
        Assert.IsType<PilotCallResult.Success>(handler.CallModule(source, "Activate", [scanner, "cargoScan", target.ShipId, 0L]));
        Assert.DoesNotContain(source.Notifications, item => item.Name == "OnCargoScanComplete");
        f.Clock.Milliseconds += (long)Math.Ceiling(handler.ActiveEffect(source.Pilot, scanner)!.DurationMilliseconds!.Value);
        handler.Advance(source);
        handler.Advance(source);

        var cargo = Assert.Single(source.Notifications.OfType<CargoScanCompleted>()).Scan.Cargo;
        Assert.Contains(new ScanItem(tritanium, 25), cargo);
        Assert.Contains(new ScanItem(standardContainer, 1), cargo);
        Assert.DoesNotContain(cargo, item => item.TypeId == auditContainer || item == new ScanItem(veldspar, 71));
        Assert.DoesNotContain(recipient.Notifications, item => item.Name == "OnCargoScanComplete");
        Assert.Equal((int)InventoryFlag.Locked, live.Economy.Inspect(world => world.Set<OwnedItem>().Find(recovered)!.FlagId));
        Assert.Equal(inner, live.Economy.Inspect(world => world.Set<OwnedItem>().Find(hidden)!.LocationId));
        Assert.Equal("fixture", live.Economy.Inspect(world => world.Set<SavedCargoContainer>().Find(outer)!.GeneralPassword));
    }

    [Fact]
    public void LockLossCancelsAPaidScanWithoutResultsOrRefund()
    {
        using Fixture f = Prepare();
        long module = f.Fit(442);
        Space(f);
        using var source = Client(f, 1000001);
        using var recipient = Client(f, 1000002);
        using var scope = f.Db.Services.CreateScope();
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<InformationHandler>();
        Lock(f, source, targeting, Target(f));
        Assert.IsType<PilotCallResult.Success>(handler.CallModule(source, "Activate", [module, "cargoScan", Target(f), 0L]));
        double paid;
        using (WorldDbContext world = new(f.Db.Path))
        {
            paid = world.Set<Eden.Data.World.Fittings.ShipEnergy>().Find(f.ShipId)!.Charge;
        }
        _ = targeting.Call(source, "dogmaIM", "RemoveTarget", [Target(f)]);
        handler.Advance(source);
        f.Clock.Seconds += 10;
        handler.Advance(source);
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
        Assert.DoesNotContain(source.Notifications, n => n.Name == "OnCargoScanComplete");
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(paid, verify.Set<Eden.Data.World.Fittings.ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.NotNull(verify.Set<ModuleCooldown>().Find(module));
    }

    [Fact]
    public void PassiveTargetingRetainsTheSourceLockWithoutPublishingATargetedMarker()
    {
        using Fixture f = Prepare();
        long module = f.Fit(581);
        Space(f);
        using var source = Client(f, 1000001);
        using var recipient = Client(f, 1000002);
        using var scope = f.Db.Services.CreateScope();
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<InformationHandler>();
        Assert.IsType<PilotCallResult.Success>(handler.CallModule(source, "Activate", [module, "targetPassively", Target(f), 0L]));
        f.Clock.Seconds += 30;
        targeting.Advance(source);
        Assert.True(targeting.IsLocked(Target(f)));
        Assert.True(f.Db.Services.GetRequiredService<SharedTargetLocks>().Contains(f.ShipId, Target(f)));
        Assert.Empty(f.Db.Services.GetRequiredService<SharedTargetLocks>().Targeters(Target(f)));
        Assert.Contains(source.Notifications.OfType<TargetChanged>(), n => n.Change == TargetChange.Added);
    }

    [Fact]
    public void ResidentTractorFramesDoNotOpenSqlAndCheckpointTheSameMovedPosition()
    {
        using Fixture f = Prepare();
        long module = f.Fit(24348);
        Space(f);
        long wreck = Wreck(f, 10000);
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var activation = Store(f).Begin(f.PilotId, module, DogmaEffect.TractorBeam, wreck, new HashSet<long> { wreck }, f.Clock.Now);
            var wrecks = f.Db.Services.GetRequiredService<IWrecks>();
            _ = wrecks.List(30000142, f.Clock.Now);
            using (var metrics = SessionPerformance.Begin("warm tractors", _ => { }, force: true))
            {
                for (int i = 0; i < 10; i++)
                {
                    f.Clock.Milliseconds += 100;
                    Store(f).AdvanceTractor(activation.Cycle, f.Clock.Now);
                    _ = wrecks.List(30000142, f.Clock.Now);
                }
                Assert.Equal(0, metrics!.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            double expected = f.Db.Store.FindPilot(1000001)!.Position!.X + 9500;
            Assert.Equal(expected, Assert.Single(wrecks.List(30000142, f.Clock.Now)).Position.X, 5);
            using var snapshot = live.CaptureSnapshot();
            Assert.NotNull(snapshot);
            _ = snapshot.Project();
        }
        WorldInitialization.Initialize(new(f.Db.Path));
        using WorldDbContext reopened = new(f.Db.Path);
        double origin = f.Db.Store.FindPilot(1000001)!.Position!.X;
        Assert.Equal(origin + 9500, reopened.Set<WorldWreck>().Find(wreck)!.X, 5);
        Assert.Empty(reopened.Set<SavedInformationCycle>());
    }
}
