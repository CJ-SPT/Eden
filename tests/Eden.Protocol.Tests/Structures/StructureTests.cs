using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Data.World.Structures;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Reprocessing;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.PilotSessions;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Clock = Eden.Protocol.Tests.Fixtures.TestClock;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureTests
{
    public static IEnumerable<object[]> Hulls =>
        new[]
        {
            35825,
            35826,
            35827,
            35835,
            35836,
            35841,
            35832,
            35833,
            35834,
            40340,
            47512,
            47513,
            47514,
            47515,
            47516,
            37534,
            35840,
            81826,
            81080,
            32458,
        }.Select(id => new object[] { id });

    internal sealed class Fixture : IDisposable
    {
        internal Clock Clock { get; } = new();
        internal TestWorld Db { get; }
        internal IPilotStructures Structures => Db.Services.GetRequiredService<IPilotStructures>();
        internal long PilotId => 90000001;
        internal long ShipId => Db.Store.FindPilot(1000001)!.ShipId;
        internal int CorporationId { get; }
        internal const int SystemId = 30001392;
        internal static readonly SpacePosition Point = new(10_000_000_000, 0, 0);

        internal Fixture(bool liveWorld = false)
        {
            Db = new(clock: Clock, liveWorld: liveWorld);
            var catalog = new WorldDatabase(Db.Path).CatalogPath;
            PassiveCatalogFixture.LoadStructures(catalog);
            TestCatalog.AddSchool(catalog, 18, 1, 1, SystemId, 60015159, 601);
            PlayerCorporationTests.Prepare(Db);
            CorporationId = Db
                .Services.GetRequiredService<IPlayerCorporations>()
                .Create(PilotId, PlayerCorporationTests.Details())
                .Corporation.Id;
            using WorldDbContext world = new(Db.Path);
            world.Set<SavedCorporation>().Find(CorporationId)!.CreatedAt = Clock.Now - 8 * TimeSpan.TicksPerDay;
            world.SaveChanges();
            Db.Store.SetPosition(ShipId, Point, SystemId);
        }

        internal long Item(int typeId, int quantity = 1)
        {
            using WorldDbContext world = new(Db.Path);
            long id = Eden.World.Inventory.InventoryPersistence.NextId(world);
            world.Add(
                new OwnedItem
                {
                    Id = id,
                    OwnerId = PilotId,
                    TypeId = typeId,
                    LocationId = ShipId,
                    FlagId = (int)InventoryFlag.Cargo,
                    Quantity = quantity,
                }
            );
            world.SaveChanges();
            return id;
        }

        internal StructureSnapshot Deploy(int typeId = 35832) =>
            Structures.Deploy(PilotId, new(Item(typeId), Point, 0, "Eden Home")).Structure;

        internal StructureSnapshot Online(int typeId = 35832)
        {
            var s = Deploy(typeId);
            Clock.AdvanceWallTime(TimeSpan.FromDays(1));
            s = Structures.Read(PilotId, s.Id)!;
            if (s.Profile.CoreTypeId is int core)
            {
                s = Structures.InstallCore(PilotId, s.Id, Item(core)).Structure;
            }
            Clock.AdvanceWallTime(TimeSpan.FromMinutes(15));
            return Structures.Read(PilotId, s.Id)!;
        }

        public void Dispose() => Db.Dispose();
    }

    [Theory]
    [MemberData(nameof(Hulls))]
    public void EveryPublishedUpwellHullDeploysWithNativeIdentityAndReopensWithoutDuplicatingInventory(int typeId)
    {
        using Fixture f = new();
        var structure = f.Deploy(typeId);
        Assert.True(structure.Id > StructureRules.FirstStructureId);
        Assert.NotEqual(f.ShipId, structure.Id);
        Assert.Equal(typeId, structure.Profile.TypeId);
        Assert.Equal(StructureState.Anchoring, structure.State);
        Assert.Equal(f.CorporationId, structure.CorporationId);
        Assert.Equal(Fixture.SystemId, structure.SystemId);
        Assert.Equal(Fixture.Point, structure.Position);
        Assert.Equal(f.Clock.Now + StructureRules.AnchorDuration(structure.Profile), structure.StateEndsAt);
        Assert.Single(f.Structures.List(f.PilotId));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(structure.Id, saved.Set<SavedStructure>().Single().Id);
        Assert.DoesNotContain(saved.Set<OwnedItem>(), i => i.TypeId == typeId && i.LocationId == f.ShipId);
        Assert.Throws<InvalidDataException>(() => f.Structures.Deploy(f.PilotId, new(0, Fixture.Point, 0, "Duplicate")));
        Assert.Single(saved.Set<SavedStructure>());
    }

    [Fact]
    public void CoreInstallationDockingAndPersonalHangarTransfersPersistAtTheStructureIdentity()
    {
        using Fixture f = new();
        var structure = f.Online();
        Assert.Equal(StructureState.ShieldVulnerable, structure.State);
        Assert.True(structure.HasCore);
        Assert.True(f.Structures.HasService(f.PilotId, structure.Id, StructureService.Docking));
        var docked = f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        Assert.True(docked.InStructure);
        Assert.Equal(structure.Id, docked.DockedLocationId);
        Assert.Equal(structure.Id, docked.LocationId);
        Assert.Equal(Fixture.SystemId, docked.SystemId);
        long item = f.Item(34, 50);
        var inventory = f.Db.Services.GetRequiredService<IPilotInventory>();
        var moved = Assert.Single(
            inventory.Move(f.PilotId, structure.Id, new([item], f.ShipId, (int)InventoryFlag.Hangar, 25)),
            c => c.Item.LocationId == structure.Id
        );
        Assert.Equal(structure.Id, moved.Item.LocationId);
        Assert.Equal(25, moved.Item.Quantity);
        Assert.Equal(25, inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == item).Quantity);
        var reopened = new Eden.World.Docking.SqliteWorld(new WorldDatabase(f.Db.Path), f.Clock).FindPilot(1000001)!;
        Assert.True(reopened.InStructure);
        Assert.Equal(structure.Id, reopened.DockedLocationId);
        Assert.Equal(25, Assert.Single(inventory.List(f.PilotId, structure.Id)).Quantity);
        Assert.Equal(structure.Id, f.Db.Services.GetRequiredService<IPilotFleet>().Ships(f.PilotId, structure.Id).Single().LocationId);
    }

    [Fact]
    public void CapsuleTransferUsesActualStructureInsteadOfLastNpcStation()
    {
        using Fixture f = new();
        var structure = f.Online();
        long originalShip = f.ShipId;
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        var fleet = f.Db.Services.GetRequiredService<IPilotFleet>();
        var capsule = fleet.Leave(f.PilotId, f.ShipId).Pilot;
        Assert.Equal(structure.Id, capsule.DockedLocationId);
        using (WorldDbContext world = new(f.Db.Path))
        {
            long oldStation = world.Ships.Find(capsule.ShipId)!.StationId;
            Assert.Throws<InvalidDataException>(() => fleet.RecoverCorvette(f.PilotId, capsule.ShipId, oldStation));
        }
        var home = f.Db.Services.GetRequiredService<IPilotHomeStation>();
        var transferred = home.SelfDestruct(f.PilotId, capsule.ShipId).Pilot;
        Assert.Equal(home.Read(f.PilotId).Id, transferred.StationId);
        Assert.False(transferred.InStructure);
        using WorldDbContext reopened = new(f.Db.Path);
        Assert.Equal(structure.Id, reopened.Set<Eden.Data.World.Characters.SavedHomeCloneTransfer>().Single().SourceStationId);
        Assert.Equal(structure.Id, reopened.Ships.Find(originalShip)!.StructureId);
    }

    [Fact]
    public async Task LiveCheckpointPreservesStructureDockedShipCoreFuelAndSavedDeadlines()
    {
        using Fixture f = new(liveWorld: true);
        var structure = f.Online();
        f.Structures.DepositFuel(f.PilotId, structure.Id, f.Item(4051, 100), 100);
        structure = f.Structures.Unanchor(f.PilotId, structure.Id).Structure;
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        await Eden.World.Runtime.LiveWorld.Find(f.Db.Path)!.FlushAsync();
        string copy = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "structure-checkpoint.sqlite");
        File.Copy(f.Db.Path, copy);
        using WorldDbContext reopened = new(copy);
        var saved = Assert.Single(reopened.Set<SavedStructure>());
        Assert.Equal(structure.Id, saved.Id);
        Assert.Equal(structure.UnanchorAt, saved.UnanchorAt);
        Assert.True(saved.HasCore);
        Assert.Equal(structure.Id, reopened.Ships.Find(f.ShipId)!.StructureId);
        Assert.Equal(
            100,
            reopened.Set<OwnedItem>().Single(i => i.LocationId == structure.Id && i.FlagId == StructureRules.FuelBayFlag).Quantity
        );
        Assert.Single(reopened.Set<OwnedItem>(), i => i.LocationId == structure.Id && i.FlagId == StructureRules.CoreRoomFlag);
    }

    [Fact]
    public void ExistingVersion135SaveAddsStructureColumnsAndPreservesShips()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldStructureAccess;
                DROP TABLE WorldStructureService;
                DROP TABLE WorldStructure;
                ALTER TABLE WorldShip DROP COLUMN StructureId;
                ALTER TABLE WorldIndustryJob DROP COLUMN SuspendedAt;
                PRAGMA user_version=135;
                """
            );
        }
        WorldInitialization.Initialize(new(f.Db.Path));
        using WorldDbContext reopened = new(f.Db.Path);
        Assert.Null(reopened.Ships.Find(f.ShipId)!.StructureId);
        Assert.Empty(reopened.Set<SavedStructure>());
        Assert.Equal(WorldMigration.CurrentVersion, reopened.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Fact]
    public void FuelDeadlinesChargeCompletedHoursOnceAndOfflineOnExhaustion()
    {
        using Fixture f = new();
        var structure = f.Online(35825);
        long module = f.Item(35878);
        f.Structures.FitService(f.PilotId, structure.Id, module, StructureRules.FirstServiceSlot);
        f.Structures.DepositFuel(f.PilotId, structure.Id, f.Item(4051, 5000), 5000);
        var online = f.Structures.SetServiceOnline(f.PilotId, structure.Id, module, true).Structure;
        Assert.Contains(StructureService.BasicManufacturing, online.Services);
        long next = Assert.Single(online.Modules).NextFuelAt!.Value;
        Assert.Equal(f.Clock.Now + TimeSpan.TicksPerHour, next);
        int afterOnline = f.Structures.Inventory(f.PilotId, structure.Id).Single(i => i.FlagId == StructureRules.FuelBayFlag).Quantity;
        Assert.Equal(4352, afterOnline);
        f.Clock.AdvanceWallTime(TimeSpan.FromHours(3));
        var advanced = f.Structures.Read(f.PilotId, structure.Id)!;
        int afterThree = f.Structures.Inventory(f.PilotId, structure.Id).Single(i => i.FlagId == StructureRules.FuelBayFlag).Quantity;
        Assert.Equal(afterOnline - 27, afterThree);
        Assert.Equal(next + 3 * TimeSpan.TicksPerHour, Assert.Single(advanced.Modules).NextFuelAt);
        f.Structures.Advance();
        Assert.Equal(
            afterThree,
            f.Structures.Inventory(f.PilotId, structure.Id).Single(i => i.FlagId == StructureRules.FuelBayFlag).Quantity
        );
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(365));
        var exhausted = f.Structures.Read(f.PilotId, structure.Id)!;
        Assert.False(Assert.Single(exhausted.Modules).Online);
        Assert.DoesNotContain(StructureService.BasicManufacturing, exhausted.Services);
        Assert.Null(Assert.Single(exhausted.Modules).NextFuelAt);
    }

    [Fact]
    public void UnimplementedServiceCannotConsumeFuelOrAdvertiseAvailability()
    {
        using Fixture f = new();
        var structure = f.Online(35833);
        long module = f.Item(35892);
        f.Structures.FitService(f.PilotId, structure.Id, module, StructureRules.FirstServiceSlot);
        f.Structures.DepositFuel(f.PilotId, structure.Id, f.Item(4051, 1000), 1000);
        Assert.Throws<InvalidDataException>(() => f.Structures.SetServiceOnline(f.PilotId, structure.Id, module, true));
        var unchanged = f.Structures.Read(f.PilotId, structure.Id)!;
        Assert.False(Assert.Single(unchanged.Modules).Online);
        Assert.Null(Assert.Single(unchanged.Modules).NextFuelAt);
        Assert.DoesNotContain(StructureService.Market, unchanged.Services);
        Assert.Equal(1000, f.Structures.Inventory(f.PilotId, structure.Id).Single(i => i.FlagId == StructureRules.FuelBayFlag).Quantity);
    }

    [Fact]
    public void LateDecommissioningSettlesFuelOnlyThroughItsSavedDeadlineBeforeOffliningServices()
    {
        using Fixture f = new();
        var structure = f.Online(35825);
        long module = f.Item(35878);
        f.Structures.FitService(f.PilotId, structure.Id, module, StructureRules.FirstServiceSlot);
        f.Structures.DepositFuel(f.PilotId, structure.Id, f.Item(4051, 5000), 5000);
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, module, true);
        f.Structures.Unanchor(f.PilotId, structure.Id);
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(8));
        var stopped = f.Structures.Read(f.PilotId, structure.Id)!;
        Assert.Equal(StructureState.Unanchored, stopped.State);
        Assert.False(Assert.Single(stopped.Modules).Online);
        Assert.Null(Assert.Single(stopped.Modules).NextFuelAt);
        Assert.Equal(2840, f.Structures.Inventory(f.PilotId, structure.Id).Single(i => i.FlagId == StructureRules.FuelBayFlag).Quantity);
        f.Structures.Advance();
        Assert.Equal(2840, f.Structures.Inventory(f.PilotId, structure.Id).Single(i => i.FlagId == StructureRules.FuelBayFlag).Quantity);
    }

    [Fact]
    public void ConfiguredSpeedsApplyToNewTimersWhileSavedDeadlinesRemainStable()
    {
        using Fixture f = new();
        Eden.Protocol.Tests.Persistence.WorldTuningTests.Activate(
            f.Db,
            s => s with { Structures = new(AnchoringSpeed: 2, OnliningSpeed: 3, UnanchoringSpeed: 4) }
        );
        var structure = f.Deploy();
        long deadline = f.Clock.Now + 12 * TimeSpan.TicksPerHour;
        Assert.Equal(deadline, structure.StateEndsAt);
        Eden.Protocol.Tests.Persistence.WorldTuningTests.Activate(f.Db, s => s with { Structures = new(100, 3, 4) });
        Assert.Equal(deadline, f.Structures.Read(f.PilotId, structure.Id)!.StateEndsAt);
        f.Clock.AdvanceWallTime(TimeSpan.FromHours(12));
        structure = f.Structures.Read(f.PilotId, structure.Id)!;
        Assert.Equal(StructureState.Onlining, structure.State);
        structure = f.Structures.InstallCore(f.PilotId, structure.Id, f.Item(structure.Profile.CoreTypeId!.Value)).Structure;
        Assert.Equal(f.Clock.Now + 5 * TimeSpan.TicksPerMinute, structure.StateEndsAt);
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(5));
        structure = f.Structures.Unanchor(f.PilotId, structure.Id).Structure;
        Assert.Equal(f.Clock.Now + 42 * TimeSpan.TicksPerHour, structure.UnanchorAt);
        Assert.Null(f.Structures.Unanchor(f.PilotId, structure.Id, cancel: true).Structure.UnanchorAt);
    }

    [Fact]
    public void RejectedManagementAndExplicitAccessDenialPreserveSourceInventory()
    {
        using Fixture f = new();
        var structure = f.Online();
        long fuel = f.Item(4051, 20);
        Assert.Throws<InvalidDataException>(() => f.Structures.DepositFuel(90000002, structure.Id, fuel, 20));
        Assert.Throws<InvalidDataException>(() => f.Structures.DepositFuel(f.PilotId, structure.Id, fuel, 21));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(20, world.Set<OwnedItem>().Find(fuel)!.Quantity);
        Assert.DoesNotContain(f.Structures.Inventory(f.PilotId, structure.Id), i => i.FlagId == StructureRules.FuelBayFlag);
        Assert.False(f.Structures.HasService(90000002, structure.Id, StructureService.Docking));
        f.Structures.SetAccess(f.PilotId, structure.Id, true, [new(90000002, false)]);
        Assert.False(f.Structures.HasService(90000002, structure.Id, StructureService.Docking));
        f.Structures.SetAccess(f.PilotId, structure.Id, true, []);
        Assert.True(f.Structures.HasService(90000002, structure.Id, StructureService.Docking));
        Assert.Equal(20, world.Set<OwnedItem>().Find(fuel)!.Quantity);
    }

    [Fact]
    public void WarmStructureAndEmptySystemContactsAvoidSqlAndReflectCommittedRenames()
    {
        using Fixture f = new(liveWorld: true);
        var structure = f.Online();
        var contacts = f.Db.Services.GetRequiredService<IStructureContacts>();
        var populated = contacts.Contacts(Fixture.SystemId);
        var empty = contacts.Contacts(0);
        Assert.Single(populated);
        Assert.Empty(empty);
        Assert.Equal(structure.Name, Assert.Single(f.Structures.Names([structure.Id, long.MaxValue])).Name);
        using (var metrics = SessionPerformance.Begin("warm structures", _ => { }, force: true)!)
        {
            for (int i = 0; i < 20; i++)
            {
                Assert.Same(populated, contacts.Contacts(Fixture.SystemId));
                Assert.Same(empty, contacts.Contacts(0));
                Assert.Equal(structure.Name, Assert.Single(f.Structures.Names([structure.Id, long.MaxValue])).Name);
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        f.Structures.Rename(f.PilotId, structure.Id, "Renamed home");
        Assert.Equal("Renamed home", Assert.Single(contacts.Contacts(Fixture.SystemId)).Name);
        Assert.Equal("Renamed home", Assert.Single(f.Structures.Names([structure.Id])).Name);
    }

    [Fact]
    public void NativeStructureDirectoryHangarAndUndockUseLongStructureIds()
    {
        using Fixture f = new();
        var structure = f.Online();
        var docked = f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        using var scope = f.Db.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
        using PilotCallContext context = new(1000001, docked.Account, [docked], factory.Create, 42, SignedSettings.Empty);
        context.Select(docked);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var has = Assert.IsType<PilotCallResult.Success>(
            router.Call(context, "structureSettings", "CharacterHasService", new object?[] { structure.Id, 1L }, _ => { })
        );
        Assert.True(Assert.IsType<bool>(has.Reply.ToBlue()));
        var hangar = Assert.IsType<PilotCallResult.Success>(
            router.Call(context, "structureHangarViewMgr", "GetMyHangarViewState", Array.Empty<object?>(), _ => { })
        );
        var native = Assert.IsType<BlueNewObject>(BlueMarshal.Decode(BlueMarshal.Encode(hangar.Reply)));
        Assert.Equal(BlueNewObject.StructureHangarClass, native.Name);
        var state = Assert.IsType<Dictionary<string, object?>>(native.State);
        Assert.Equal((long)StructureState.ShieldVulnerable, state["_operatingState"]);
        var directory = Assert.IsType<PilotCallResult.Success>(
            router.Call(context, "structureDirectory", "GetMyCharacterStructures", Array.Empty<object?>(), _ => { })
        );
        var entries = Assert.IsType<Dictionary<long, object?>>(BlueMarshal.Decode(BlueMarshal.Encode(directory.Reply)));
        Assert.Contains(structure.Id, entries.Keys);
        var names = Assert.IsType<PilotCallResult.Success>(
            router.Call(context, "config", "GetMultiLocationsEx", new object?[] { new List<object?> { structure.Id } }, _ => { })
        );
        var location = Assert.Single(Assert.IsType<Eden.Protocol.Docking.LocationTable>(names.Reply).Locations);
        Assert.Equal(structure.Id, location.Id);
        Assert.Equal(structure.Name, location.Name);
        Assert.Equal(Fixture.SystemId, location.SystemId);
        Assert.IsType<PilotCallResult.Success>(
            router.Call(context, "structureDocking", "Undock", new object?[] { structure.Id, docked.ShipId }, _ => { })
        );
        Assert.NotNull(context.Pilot.Position);
        Assert.Equal(Fixture.SystemId, context.Pilot.SystemId);
        Assert.False(context.Pilot.InStructure);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Null(world.Ships.Find(docked.ShipId)!.StructureId);
        Assert.NotNull(world.Positions.Find(docked.ShipId));
    }

    [Fact]
    public void OverdueDecommissioningRefreshesContactsAfterTheLastDockedShipLeaves()
    {
        using Fixture f = new(liveWorld: true);
        var structure = f.Online();
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        f.Structures.Unanchor(f.PilotId, structure.Id);
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(7));
        var contacts = f.Db.Services.GetRequiredService<IStructureContacts>();
        Assert.Equal(StructureState.ShieldVulnerable, Assert.Single(contacts.Contacts(Fixture.SystemId)).Structure!.State);
        f.Db.Store.SetPosition(f.ShipId, Fixture.Point, Fixture.SystemId);
        Assert.Equal(StructureState.Unanchored, Assert.Single(contacts.Contacts(Fixture.SystemId)).Structure!.State);
    }

    [Fact]
    public void VisitorCanMovePersonalItemsWithoutStructureManagementAuthority()
    {
        using Fixture f = new();
        var structure = f.Online();
        f.Structures.SetAccess(f.PilotId, structure.Id, true, []);
        var visitor = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(visitor.ShipId, Fixture.Point, Fixture.SystemId);
        visitor = f.Db.Services.GetRequiredService<IStationDocking>().Dock(visitor.PilotId, visitor.ShipId, structure.Id);
        long item = f.Item(34, 20);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var owned = world.Set<OwnedItem>().Find(item)!;
            owned.OwnerId = visitor.PilotId;
            owned.LocationId = structure.Id;
            owned.FlagId = (int)InventoryFlag.Hangar;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Structures.Inventory(visitor.PilotId, structure.Id));
        using var scope = f.Db.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
        using PilotCallContext context = new(1000002, visitor.Account, [visitor], factory.Create, 43, SignedSettings.Empty);
        context.Select(visitor);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Assert.IsType<PilotCallResult.Success>(router.Call(context, "@ship", "Add", new object?[] { item, structure.Id }, _ => { }));
        var cargo = Assert.Single(f.Db.Services.GetRequiredService<IPilotInventory>().List(visitor.PilotId, visitor.ShipId));
        Assert.Equal(item, cargo.Id);
        Assert.Equal(20, cargo.Quantity);
        Assert.Equal((int)InventoryFlag.Cargo, cargo.FlagId);
    }

    [Fact]
    public void NativeDeploymentValidatesProfileAndPersistsRequestedMetadata()
    {
        using Fixture f = new();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using var scope = f.Db.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
        using PilotCallContext context = new(1000001, pilot.Account, [pilot], factory.Create, 44, SignedSettings.Empty);
        context.Select(pilot);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        long item = f.Item(35832);
        object?[] request = [item, 0d, 0d, 1.5d, (long)f.CorporationId + 1, "New home", "Description", 4L, 12L, null];
        Assert.IsType<PilotCallResult.Failure>(router.Call(context, "structureDeployment", "Anchor", request, _ => { }));
        Assert.Empty(f.Structures.List(f.PilotId));
        Assert.Contains(f.Db.Services.GetRequiredService<IPilotInventory>().List(f.PilotId, f.ShipId), i => i.Id == item);
        request[4] = (long)f.CorporationId;
        Assert.IsType<PilotCallResult.Success>(router.Call(context, "structureDeployment", "Anchor", request, _ => { }));
        var deployed = Assert.Single(f.Structures.List(f.PilotId));
        Assert.Equal(Fixture.Point, deployed.Position);
        Assert.Equal("New home", deployed.Name);
        Assert.Equal("Description", deployed.Description);
        Assert.Equal(1.5, deployed.Yaw);
        Assert.Equal(4, deployed.ReinforceWeekday);
        Assert.Equal(12, deployed.ReinforceHour);
    }

    [Fact]
    public void ManufacturingUsesEngineeringBonusesAndPausesAtTheExactFuelDeadlineAcrossReopen()
    {
        using Fixture f = new();
        var structure = f.Online(35825);
        long module = f.Item(35878);
        f.Structures.FitService(f.PilotId, structure.Id, module, StructureRules.FirstServiceSlot);
        f.Structures.DepositFuel(f.PilotId, structure.Id, f.Item(4051, 648), 648);
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, module, true);
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        long blueprint = f.Item(1130);
        long tritanium = f.Item(34, 600);
        long pyerite = f.Item(35, 500);
        using (WorldDbContext world = new(f.Db.Path))
        {
            foreach (long id in new[] { blueprint, tritanium, pyerite })
            {
                var item = world.Set<OwnedItem>().Find(id)!;
                item.LocationId = structure.Id;
                item.FlagId = (int)InventoryFlag.Hangar;
            }
            world.Add(
                new PilotSkill
                {
                    PilotId = f.PilotId,
                    TypeId = 3380,
                    Points = 250,
                }
            );
            world.SaveChanges();
        }
        var industry = f.Db.Services.GetRequiredService<IPilotIndustry>();
        var facility = industry.Facility(f.PilotId, structure.Id);
        Assert.Equal(.85, facility.TimeMultiplier);
        Assert.Equal(.99, facility.MaterialMultiplier);
        var installed = industry.Install(
            f.PilotId,
            new(blueprint, 1130, structure.Id, 20, 215, 4896, new Dictionary<int, int> { [34] = 535, [35] = 416 })
        );
        long originalEnd = installed.Job.EndTime;
        long stoppedAt = f.Clock.Now + TimeSpan.TicksPerHour;
        f.Clock.AdvanceWallTime(TimeSpan.FromHours(3));
        var paused = industry.Job(f.PilotId, installed.Job.Id);
        Assert.Equal(IndustryJobStatus.Paused, paused.Status);
        Assert.Equal(stoppedAt, paused.SuspendedAt);
        Assert.Equal(originalEnd, paused.EndTime);
        Assert.Throws<InvalidDataException>(() => industry.Deliver(f.PilotId, paused.Id, Fixture.SystemId));
        using (WorldDbContext reopened = new(f.Db.Path))
        {
            Assert.Equal(stoppedAt, reopened.Set<Eden.Data.World.Industry.IndustryJob>().Find(paused.Id)!.SuspendedAt);
        }
        f.Structures.DepositFuel(f.PilotId, structure.Id, f.Item(4051, 1000), 1000);
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, module, true);
        var resumed = industry.Job(f.PilotId, paused.Id);
        Assert.Null(resumed.SuspendedAt);
        Assert.Equal(originalEnd + 2 * TimeSpan.TicksPerHour, resumed.EndTime);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1296));
        var delivered = industry.Deliver(f.PilotId, paused.Id, Fixture.SystemId);
        Assert.Equal(IndustryJobStatus.Delivered, delivered.Job.Status);
        Assert.Contains(delivered.Changes, c => c.Item.TypeId == 215 && c.Item.Quantity == 2000 && c.Item.LocationId == structure.Id);
        Assert.Empty(industry.Deliver(f.PilotId, paused.Id, Fixture.SystemId).Changes);
    }

    [Fact]
    public void RefineryReprocessingUsesOnlineServiceAndCatalogHullYield()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadMining(new WorldDatabase(f.Db.Path).CatalogPath);
        var structure = f.Online(35835);
        long module = f.Item(35899);
        f.Structures.FitService(f.PilotId, structure.Id, module, StructureRules.FirstServiceSlot);
        f.Structures.DepositFuel(f.PilotId, structure.Id, f.Item(4051, 1000), 1000);
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, module, true);
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        long ore = f.Item(1230, 1000);
        var reprocessing = f.Db.Services.GetRequiredService<IPilotReprocessing>();
        var quote = reprocessing.Quote(f.PilotId, structure.Id, f.ShipId, [ore]);
        Assert.Equal(.51, quote.StationEfficiency);
        Assert.Equal(0, quote.StationTax);
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, module, false);
        Assert.Throws<InvalidDataException>(() => reprocessing.Quote(f.PilotId, structure.Id, f.ShipId, [ore]));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(1000, world.Set<OwnedItem>().Find(ore)!.Quantity);
    }

    [Theory]
    [InlineData(35832)]
    [InlineData(35841)]
    [InlineData(81826)]
    public void DecommissioningRecoversOneHullWithoutDuplicatingIntegralEquipment(int typeId)
    {
        using Fixture f = new();
        using (Eden.World.Catalog.CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=1000000 WHERE ItemTypeId=601 AND AttributeId=38");
            catalog.Database.ExecuteSqlRaw("UPDATE ItemType SET Capacity=1000000 WHERE Id=601");
        }
        var structure = f.Online(typeId);
        f.Structures.Unanchor(f.PilotId, structure.Id);
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(7));
        Assert.Equal(StructureState.Unanchored, f.Structures.Read(f.PilotId, structure.Id)!.State);
        foreach (
            var integral in f
                .Structures.Inventory(f.PilotId, structure.Id)
                .Where(i => i.TypeId == structure.Profile.BuiltinCoreTypeId || i.TypeId == structure.Profile.BuiltinModuleTypeId)
        )
        {
            Assert.Throws<InvalidDataException>(() => f.Structures.Withdraw(f.PilotId, structure.Id, integral.Id, 1, toCargo: true));
            Assert.Contains(f.Structures.Inventory(f.PilotId, structure.Id), i => i.Id == integral.Id && i.Quantity == 1);
        }
        f.Structures.Recover(f.PilotId, structure.Id);
        Assert.Null(f.Structures.Read(f.PilotId, structure.Id));
        var items = f.Db.Services.GetRequiredService<IPilotInventory>().List(f.PilotId, f.ShipId);
        Assert.Equal(1, Assert.Single(items, i => i.TypeId == typeId).Quantity);
        if (structure.Profile.CoreTypeId is int core)
        {
            Assert.Equal(1, Assert.Single(items, i => i.TypeId == core).Quantity);
        }
        if (structure.Profile.BuiltinCoreTypeId is int integralCore)
        {
            Assert.DoesNotContain(items, i => i.TypeId == integralCore);
        }
        if (structure.Profile.BuiltinModuleTypeId is int integralModule)
        {
            Assert.DoesNotContain(items, i => i.TypeId == integralModule);
        }
        Assert.Throws<InvalidDataException>(() => f.Structures.Recover(f.PilotId, structure.Id));
        Assert.Equal(
            1,
            Assert.Single(f.Db.Services.GetRequiredService<IPilotInventory>().List(f.PilotId, f.ShipId), i => i.TypeId == typeId).Quantity
        );
    }
}
