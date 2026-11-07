using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Protocol.Corporations;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Corporations;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationStorageTests
{
    private const long Ceo = 90000001;
    private const long Member = 90000002;
    private const int StationId = 60003760;
    private const int FirstDivision = CorporationStorageRules.FirstFlag;
    private const int SecondDivision = FirstDivision + 1;
    private const long HullId = 1000000000099;

    private static ICorporationStorage Storage(TestWorld world) => world.Services.GetRequiredService<ICorporationStorage>();

    private static int Prepare(TestWorld world)
    {
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        world
            .Services.GetRequiredService<ICorporationWallet>()
            .Transfer(
                Ceo,
                new(false, CorporationWalletRules.MasterKey, corporation, CorporationWalletRules.MasterKey, 10000000, "Office budget")
            );
        return corporation;
    }

    private static CorporationOffice Rent(TestWorld world) => Storage(world).Rent(Ceo, Storage(world).Quote(Ceo));

    private static InventoryItem PersonalItem(TestWorld world, long owner = Ceo) =>
        world
            .Services.GetRequiredService<IPilotInventory>()
            .List(owner, StationId)
            .Single(i => i.TypeId == InventoryDefinitions.CivilianMinerType);

    private static void Access(TestWorld world, int corporation, CorporationRoleSet roles, int? baseId = null, long titles = 0)
    {
        using WorldDbContext saved = new(world.Path);
        var row =
            saved.Set<SavedCorporationMemberAccess>().Find(Member)
            ?? saved.Add(new SavedCorporationMemberAccess { PilotId = Member, CorporationId = corporation }).Entity;
        row.DataJson = JsonSerializer.Serialize(new CorporationMemberAccess(roles, BaseId: baseId, TitleMask: titles));
        saved.SaveChanges();
    }

    [Fact]
    public void RentalAndLocalNativeInventoryTransfersChargeOnceAndConserveOwnership()
    {
        using TestWorld world = new();
        int corporation = Prepare(world);
        var storage = Storage(world);
        long balance = world.Services.GetRequiredService<ICorporationWallet>().Balance(Ceo, CorporationWalletRules.MasterKey);
        var item = PersonalItem(world);
        using CorporationTestClient ceo = new(world, 1000001);
        var bind = ceo.CallService(
            "officeManager",
            "MachoBindObject",
            [(long)StationId, new object?[] { "GetPriceQuote", new object?[] { (long)corporation } }]
        );
        var bound = Assert.IsType<BoundPilotReply>(Assert.IsType<PilotCallResult.Success>(bind).Reply);
        Assert.Equal("officeManager", ceo.Context.ResolveReference(bound.Reference.Id));
        double quote = Assert.IsType<double>(BlueMarshal.Decode(BlueMarshal.Encode(bound.Reply)));
        Assert.Equal(CorporationStorageRules.DefaultRentalFeeCents / 100d, quote);
        Assert.IsType<PilotCallResult.Success>(ceo.CallService("officeManager", "RentOffice", [quote]));
        var office = Assert.Single(storage.Offices(Ceo));
        var rental = Assert.Single(ceo.Context.Notifications.OfType<CorporationOfficeRentalChanged>());
        Assert.Equal(new object?[] { corporation, office.Id }, rental.Arguments);
        Assert.IsType<PilotCallResult.Failure>(ceo.CallService("officeManager", "RentOffice", [quote]));
        Assert.Equal(
            balance - office.FeeCents,
            world.Services.GetRequiredService<ICorporationWallet>().Balance(Ceo, CorporationWalletRules.MasterKey)
        );
        Assert.Single(
            world.Services.GetRequiredService<ICorporationWallet>().Journal(Ceo, true, CorporationWalletRules.MasterKey),
            e => e.EntryType == WalletEntryType.OfficeRentalFee
        );
        var rows = Assert.IsType<List<object?>>(Decode(ceo, "officeManager", "GetMyCorporationsOffices", []));
        var row = BlueRead.Fields(Assert.IsType<BlueInstance>(Assert.Single(rows)).State);
        Assert.Equal(office.Id, row["officeID"]);
        Assert.Equal((long)StationId, row["stationID"]);
        Assert.Equal(office.SystemId, Convert.ToInt32(row["solarsystemID"]));
        var reference = Assert.IsType<BlueObjectReference>(
            Assert.IsType<PilotCallResult.Success>(ceo.CallService("invbroker", "GetInventoryFromId", [office.Id, 0L])).Reply.ToBlue()
        );
        string service = ceo.Context.ResolveReference(reference.Id)!;
        Assert.Equal($"{CorporationStorageHandler.InventoryPrefix}{office.Id}", service);
        Assert.IsType<BlueConstructor>(
            Assert.IsType<PilotCallResult.Success>(ceo.CallService(service, "GetSelfInvItem", [])).Reply is InventoryRow officeRow
                ? officeRow.ToBlue()
                : null
        );
        Assert.IsType<PilotCallResult.Success>(
            ceo.CallService(service, "Add", [item.Id, (long)StationId], new() { ["flag"] = (long)FirstDivision })
        );
        var nativeList = Assert.IsType<PilotCallResult.Success>(ceo.CallService(service, "List", [], new() { ["flag"] = null })).Reply;
        Assert.Single(Assert.IsType<BlueList<InventoryRow>>(nativeList).Items);
        Assert.NotEmpty(BlueMarshal.Encode(nativeList));
        var listed = Assert.Single(storage.List(Ceo, office.Id, FirstDivision));
        Assert.Equal(corporation, listed.OwnerId);
        Assert.Equal(office.Id, listed.LocationId);
        Assert.Equal(FirstDivision, listed.FlagId);
        Assert.DoesNotContain(world.Services.GetRequiredService<IPilotInventory>().List(Ceo, StationId), i => i.Id == item.Id);
        Assert.IsType<PilotCallResult.Success>(ceo.CallService("@hangar", "Add", [item.Id, office.Id]));
        Assert.Equal(item, PersonalItem(world));
        Assert.Empty(storage.List(Ceo, office.Id, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedStacksAndFittedInactiveShipsSurviveReopenAndCanBeWithdrawnByAnotherPilot(bool liveWorld)
    {
        using TestWorld world = new(liveWorld: liveWorld);
        int corporation = Prepare(world);
        var storage = Storage(world);
        var office = Rent(world);
        var item = PersonalItem(world);
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Set<OwnedItem>().Find(item.Id)!.Quantity = 20;
            saved.Add(
                new Ship
                {
                    Id = HullId,
                    OwnerId = Ceo,
                    TypeId = 601,
                    StationId = StationId,
                    Name = "Shared replacement",
                }
            );
            saved.Add(
                new OwnedItem
                {
                    Id = HullId + 1,
                    OwnerId = Ceo,
                    TypeId = InventoryDefinitions.CivilianMinerType,
                    LocationId = HullId,
                    FlagId = (int)InventoryFlag.HighSlot0,
                    Quantity = 1,
                    Singleton = true,
                }
            );
            saved.SaveChanges();
        }
        var split = storage.Move(Ceo, office.Id, new([item.Id], StationId, FirstDivision, 12));
        Assert.Equal(2, split.Count);
        Assert.Equal(8, PersonalItem(world).Quantity);
        storage.Move(Ceo, office.Id, new([HullId], StationId, SecondDivision, null));
        Assert.Equal(2, storage.List(Ceo, office.Id, null).Count);
        Assert.DoesNotContain(world.Services.GetRequiredService<IPilotFleet>().Ships(Ceo, StationId), i => i.Id == HullId);
        Access(
            world,
            corporation,
            new(
                RolesAtHQ: (long)(CorporationRole.HangarCanQuery1 | CorporationRole.HangarCanTake1)
                    | ((long)(CorporationRole.HangarCanQuery1 | CorporationRole.HangarCanTake1) << 1)
            )
        );
        long rentedId = office.Id;
        world.Services.Dispose();
        WorldInitialization.Initialize(new(world.Path));
        using var reopened = ServerServices.Build(new(world.Path), liveWorld: liveWorld);
        var persisted = reopened.GetRequiredService<ICorporationStorage>();
        Assert.Equal(office, Assert.Single(persisted.Offices(Member)));
        var shared = persisted.List(Member, rentedId, FirstDivision);
        Assert.Equal(12, Assert.Single(shared).Quantity);
        persisted.Move(Member, StationId, new([shared[0].Id], rentedId, (int)InventoryFlag.Hangar, 5));
        persisted.Move(Member, StationId, new([HullId], rentedId, (int)InventoryFlag.Hangar, null));
        Assert.Equal(7, Assert.Single(persisted.List(Member, rentedId, FirstDivision)).Quantity);
        using WorldDbContext check = new(world.Path);
        Assert.Equal(Member, check.Ships.Find(HullId)!.OwnerId);
        Assert.Null(check.Ships.Find(HullId)!.StoredInId);
        Assert.Equal(Member, check.Set<OwnedItem>().Find(HullId + 1)!.OwnerId);
        Assert.Equal(
            5,
            reopened
                .GetRequiredService<IPilotInventory>()
                .List(Member, StationId)
                .Single(i => i.Id != PersonalItemId(check, Member) && i.TypeId == item.TypeId)
                .Quantity
        );
        Assert.Equal(0, check.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_foreign_key_check").Single());
    }

    private static long PersonalItemId(WorldDbContext saved, long owner) =>
        saved
            .Set<OwnedItem>()
            .Where(i => i.OwnerId == owner && i.LocationId == StationId && i.TypeId == InventoryDefinitions.CivilianMinerType)
            .OrderBy(i => i.Id)
            .Select(i => i.Id)
            .First();

    [Fact]
    public void QueryAndTakeRightsAreIndependentAndRevokedCachedReferencesFailImmediately()
    {
        using TestWorld world = new();
        int corporation = Prepare(world);
        var storage = Storage(world);
        var office = Rent(world);
        var item = PersonalItem(world);
        storage.Move(Ceo, office.Id, new([item.Id], StationId, FirstDivision, null));
        using CorporationTestClient member = new(world, 1000002);
        string service = $"{CorporationStorageHandler.InventoryPrefix}{office.Id}";
        Assert.Empty(storage.List(Member, office.Id, null));
        Assert.IsType<PilotCallResult.Failure>(member.CallService(service, "List", [(long)FirstDivision]));
        Access(world, corporation, new(RolesAtHQ: (long)CorporationRole.HangarCanQuery1));
        Assert.Single(storage.List(Member, office.Id, FirstDivision));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("@hangar", "Add", [item.Id, office.Id]));
        Access(world, corporation, new(RolesAtHQ: (long)CorporationRole.HangarCanTake1));
        Assert.Throws<InvalidDataException>(() => storage.List(Member, office.Id, FirstDivision));
        Assert.Throws<InvalidDataException>(() => storage.Move(Member, StationId, new([item.Id], office.Id, 4, null)));
        Access(world, corporation, new(RolesAtHQ: (long)(CorporationRole.HangarCanTake1 | CorporationRole.HangarCanQuery1)));
        Assert.Single(InventoryRows(member, service, [(long)FirstDivision]));
        Assert.IsType<PilotCallResult.Failure>(member.CallService(service, "List", [(long)SecondDivision]));
        Access(world, corporation, new());
        Assert.IsType<PilotCallResult.Failure>(member.CallService(service, "List", [(long)FirstDivision]));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("@hangar", "Add", [item.Id, office.Id]));
        world.Services.GetRequiredService<ICorporationMembership>().Remove(Member, Member);
        Assert.IsType<PilotCallResult.Failure>(member.CallService(service, "GetSelfInvItem", []));
        Assert.Equal(corporation, Assert.Single(storage.List(Ceo, office.Id, FirstDivision)).OwnerId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void DivisionRolesRespectHeadquartersBaseOtherAndGlobalRights(int division)
    {
        int flag = FirstDivision + division;
        long query = (long)CorporationRole.HangarCanQuery1 << division;
        long take = (long)CorporationRole.HangarCanTake1 << division;
        CorporationMemberAccess At(CorporationRoleSet roles, int? baseId = null) => new(roles, BaseId: baseId);
        Assert.True(CorporationStorageRules.CanQuery(At(new(RolesAtHQ: query)), StationId, StationId, flag));
        Assert.False(CorporationStorageRules.CanTake(At(new(RolesAtHQ: query)), StationId, StationId, flag));
        Assert.False(CorporationStorageRules.CanQuery(At(new(RolesAtBase: query), StationId), StationId, StationId, flag));
        Assert.True(CorporationStorageRules.CanTake(At(new(RolesAtBase: take), StationId), StationId, StationId + 1, flag));
        Assert.True(CorporationStorageRules.CanQuery(At(new(RolesAtOther: query)), StationId, StationId + 1, flag));
        Assert.False(CorporationStorageRules.CanQuery(At(new(RolesAtHQ: query)), StationId, StationId + 1, flag));
        Assert.True(CorporationStorageRules.CanQuery(At(new(query)), StationId, StationId + 1, flag));
        Assert.True(CorporationStorageRules.CanTake(At(new((long)CorporationRole.Director)), StationId, StationId + 1, flag));
        Assert.False(CorporationStorageRules.CanQuery(At(new(query)), StationId, StationId, FirstDivision - 1));
    }

    [Fact]
    public void MembersMayDepositWithoutQueryRightsAndOnlyAuthorizedLocalObserversReceiveChanges()
    {
        using TestWorld world = new();
        int corporation = Prepare(world);
        var storage = Storage(world);
        var office = Rent(world);
        var ceoItem = PersonalItem(world);
        var memberItem = PersonalItem(world, Member);
        using CorporationTestClient ceo = new(world, 1000001);
        using CorporationTestClient member = new(world, 1000002);
        storage.Move(Member, office.Id, new([memberItem.Id], StationId, FirstDivision, null));
        Assert.Empty(storage.List(Member, office.Id, null));
        Assert.Single(storage.List(Ceo, office.Id, FirstDivision));
        ceo.Advance();
        Assert.Contains(ceo.Context.Notifications.OfType<ItemChanged>(), n => n.Item.Id == memberItem.Id);
        int hiddenCount = member.Context.Notifications.OfType<ItemChanged>().Count();
        storage.Move(Ceo, office.Id, new([ceoItem.Id], StationId, FirstDivision, null));
        member.Advance();
        Assert.Equal(hiddenCount, member.Context.Notifications.OfType<ItemChanged>().Count());
        Access(world, corporation, new(RolesAtHQ: (long)CorporationRole.HangarCanQuery1));
        storage.Move(Ceo, office.Id, new([ceoItem.Id], office.Id, SecondDivision, null));
        member.Advance();
        var removal = Assert.Single(member.Context.Notifications.OfType<ItemChanged>());
        Assert.Equal(ceoItem.Id, removal.Item.Id);
        Assert.Equal((long)InventoryLocation.Graveyard, removal.Item.LocationId);
        Assert.DoesNotContain(member.Context.Notifications.OfType<ItemChanged>(), n => n.Item.FlagId == SecondDivision);
        member.Advance();
        Assert.Single(member.Context.Notifications.OfType<ItemChanged>());
    }

    [Fact]
    public void RejectedBatchesCargoOverflowsActiveShipsAndRemoteMovesLeaveAssetsAndEventsUnchanged()
    {
        using TestWorld world = new();
        Prepare(world);
        var storage = Storage(world);
        var office = Rent(world);
        var item = PersonalItem(world);
        var foreign = PersonalItem(world, Member);
        long active = world.Store.FindPilot(1000001)!.ShipId;
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Set<OwnedItem>().Find(item.Id)!.Quantity = 1000;
            saved.SaveChanges();
        }
        string Snapshot()
        {
            using WorldDbContext saved = new(world.Path);
            return JsonSerializer.Serialize(
                new
                {
                    Items = saved.Set<OwnedItem>().OrderBy(i => i.Id).ToArray(),
                    Ships = saved.Ships.OrderBy(s => s.Id).ToArray(),
                    Events = saved.Set<SavedCorporationChange>().Count(),
                }
            );
        }
        string before = Snapshot();
        void Reject(long destination, InventoryMoveRequest request)
        {
            Assert.Throws<InvalidDataException>(() => storage.Move(Ceo, destination, request));
            Assert.Equal(before, Snapshot());
        }
        Reject(office.Id, new([item.Id, foreign.Id], StationId, FirstDivision, null));
        Reject(office.Id, new([item.Id, item.Id], StationId, FirstDivision, null));
        Reject(office.Id, new([item.Id], StationId + 1, FirstDivision, null));
        Reject(office.Id, new([item.Id], StationId, FirstDivision, 1001));
        Reject(office.Id, new([item.Id], StationId, FirstDivision, 0));
        Reject(office.Id, new([item.Id], StationId, (int)InventoryFlag.Hangar, null));
        Reject(office.Id, new([active], StationId, FirstDivision, null));
        storage.Move(Ceo, office.Id, new([item.Id], StationId, FirstDivision, null));
        before = Snapshot();
        Assert.Throws<CargoCapacityException>(() => storage.Move(Ceo, active, new([item.Id], office.Id, 5, null)));
        Assert.Equal(before, Snapshot());
        world.Store.SetPosition(active, SpacePosition.Zero);
        before = Snapshot();
        Reject(StationId, new([item.Id], office.Id, 4, null));
    }

    [Fact]
    public void RentalRequiresAuthorityFundsMatchingQuoteAndCapacityAndNonemptyOfficeCannotBeUnrented()
    {
        using TestWorld world = new();
        int corporation = Prepare(world);
        var storage = Storage(world);
        long quote = storage.Quote(Ceo);
        Assert.Throws<InvalidDataException>(() => storage.Rent(Member, quote));
        Assert.Throws<InvalidDataException>(() => storage.Rent(Ceo, quote - 1));
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Set<SavedCorporation>().Find(corporation)!.BalanceCents = 0;
            saved.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => storage.Rent(Ceo, quote));
        Assert.Empty(storage.Offices(Ceo));
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Set<SavedCorporation>().Find(corporation)!.BalanceCents = quote * 3;
            saved.Set<SavedCorporationOfficeSettings>().Find(1)!.OfficeSlots = 1;
            saved.SaveChanges();
        }
        var office = storage.Rent(Ceo, quote);
        Assert.Equal(0, storage.EmptyOffices(Ceo));
        Assert.Equal(corporation, Assert.Single(storage.Tenants(Member)));
        var item = PersonalItem(world);
        storage.Move(Ceo, office.Id, new([item.Id], StationId, FirstDivision, null));
        Assert.Throws<InvalidDataException>(() => storage.Unrent(Ceo));
        Assert.Throws<InvalidDataException>(() => storage.Unrent(Member));
        storage.Move(Ceo, StationId, new([item.Id], office.Id, 4, null));
        storage.Unrent(Ceo);
        Assert.Empty(storage.Offices(Ceo));
        Assert.Throws<InvalidDataException>(() => storage.List(Ceo, office.Id, null));
        var replacement = storage.Rent(Ceo, quote);
        Assert.True(replacement.Id > office.Id);
        Assert.Equal(quote, world.Services.GetRequiredService<ICorporationWallet>().Balance(Ceo, CorporationWalletRules.MasterKey));
    }

    [Fact]
    public void NativeBindingsRejectRemoteAndMalformedCallsAndTitlesGrantOnlyTheirActualDivision()
    {
        using TestWorld world = new();
        int corporation = Prepare(world);
        var office = Rent(world);
        var item = PersonalItem(world);
        Storage(world).Move(Ceo, office.Id, new([item.Id], StationId, FirstDivision, null));
        world
            .Services.GetRequiredService<ICorporationRoles>()
            .UpdateTitles(
                Ceo,
                [new(1, "Storekeeper", new(RolesAtHQ: (long)(CorporationRole.HangarCanQuery1 | CorporationRole.HangarCanTake1)))]
            );
        Access(world, corporation, new(), titles: 1);
        using CorporationTestClient member = new(world, 1000002);
        string service = $"{CorporationStorageHandler.InventoryPrefix}{office.Id}";
        Assert.Single(Storage(world).List(Member, office.Id, FirstDivision));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("officeManager", "MachoBindObject", [(long)StationId + 1, null]));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("officeManager", "MachoResolveObject", [(long)StationId + 1]));
        Assert.IsType<PilotCallResult.Failure>(member.CallService(service, "List", [long.MaxValue]));
        Assert.IsType<PilotCallResult.Failure>(member.CallService(service, "Add", [item.Id, office.Id], new() { ["flag"] = 4L }));
        Assert.IsType<PilotCallResult.Failure>(
            member.CallService(
                service,
                "ListByFlags",
                [],
                new()
                {
                    ["flags"] = new List<object?> { (long)FirstDivision, (long)SecondDivision },
                }
            )
        );
        Assert.Single(InventoryRows(member, service, [(long)FirstDivision]));
        Assert.IsType<PilotCallResult.Success>(member.CallService("@hangar", "Add", [item.Id, office.Id]));
        Assert.Contains(world.Services.GetRequiredService<IPilotInventory>().List(Member, StationId), i => i.Id == item.Id);
        Assert.Empty(Storage(world).List(Ceo, office.Id, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeSplitAndStackPreserveQuantitiesAndAssetReportsRespectQueryPermissions(bool liveWorld)
    {
        using TestWorld world = new(liveWorld: liveWorld);
        int corporation = Prepare(world);
        var storage = Storage(world);
        var office = Rent(world);
        var item = PersonalItem(world);
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Set<OwnedItem>().Find(item.Id)!.Quantity = 10;
            saved.SaveChanges();
        }
        storage.Move(Ceo, office.Id, new([item.Id], StationId, FirstDivision, null));
        using CorporationTestClient ceo = new(world, 1000001);
        using CorporationTestClient member = new(world, 1000002);
        string service = $"{CorporationStorageHandler.InventoryPrefix}{office.Id}";
        Assert.IsType<PilotCallResult.Success>(
            ceo.CallService("invbroker", "SplitStack", [(long)StationId, item.Id, 4L, (long)corporation])
        );
        Assert.Equal(new[] { 4, 6 }, storage.List(Ceo, office.Id, FirstDivision).Select(i => i.Quantity).Order());
        Assert.IsType<PilotCallResult.Failure>(
            member.CallService("invbroker", "SplitStack", [(long)StationId, item.Id, 2L, (long)corporation])
        );
        Assert.Empty(storage.Assets(Member, corporation, StationId));
        Access(world, corporation, new(RolesAtHQ: (long)CorporationRole.HangarCanQuery1));
        Assert.Equal(2, storage.Assets(Member, corporation, StationId).Count);
        Assert.IsType<PilotCallResult.Failure>(member.CallService(service, "StackAll", [], new() { ["flag"] = (long)FirstDivision }));
        var report = Assert.IsType<BlueInstance>(Decode(member, "corpmgr", "GetAssetInventory", [(long)corporation, "offices"]));
        var fields = BlueRead.Fields(report.State);
        Assert.Equal(
            new object?[] { (long)StationId, (long)office.SystemId, 2L },
            Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(fields["lines"])))
        );
        Assert.IsType<PilotCallResult.Failure>(
            member.CallService("corpmgr", "GetAssetInventoryForLocation", [(long)corporation + 1, (long)StationId, "offices"])
        );
        Assert.IsType<PilotCallResult.Success>(ceo.CallService(service, "StackAll", [], new() { ["flag"] = (long)FirstDivision }));
        Assert.Equal(10, Assert.Single(storage.List(Ceo, office.Id, FirstDivision)).Quantity);
        Assert.Contains(ceo.Context.Notifications.OfType<ItemChanged>(), n => n.Item.LocationId == (long)InventoryLocation.Graveyard);
    }

    [Fact]
    public async Task ConcurrentWithdrawalsSettleOneOwnerAndOneNotificationBatch()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = Prepare(world);
        var storage = Storage(world);
        var office = Rent(world);
        var item = PersonalItem(world);
        storage.Move(Ceo, office.Id, new([item.Id], StationId, FirstDivision, null));
        Access(world, corporation, new(RolesAtHQ: (long)(CorporationRole.HangarCanQuery1 | CorporationRole.HangarCanTake1)));
        int before;
        using (WorldDbContext saved = new(world.Path))
        {
            before = saved.Set<SavedCorporationChange>().Count();
        }
        bool Withdraw(long pilot)
        {
            try
            {
                storage.Move(pilot, StationId, new([item.Id], office.Id, 4, null));
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }
        var results = await Task.WhenAll(Task.Run(() => Withdraw(Ceo)), Task.Run(() => Withdraw(Member)));
        Assert.Single(results, result => result);
        Assert.Empty(storage.List(Ceo, office.Id, null));
        using WorldDbContext check = new(world.Path);
        var settled = check.Set<OwnedItem>().Find(item.Id)!;
        Assert.Contains(settled.OwnerId, new[] { Ceo, Member });
        Assert.Equal(StationId, settled.LocationId);
        Assert.Equal(1, settled.Quantity);
        Assert.Equal(before + 2, check.Set<SavedCorporationChange>().Count());
    }

    [Fact]
    public void Version96UpgradePreservesExistingAssetsAndSavedOfficePolicyAcrossRepeatedStartup()
    {
        using TestWorld world = new();
        Prepare(world);
        var item = PersonalItem(world);
        world.Services.Dispose();
        using (WorldDbContext old = new(world.Path))
        {
            old.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldCorporationOffice;
                DROP TABLE WorldCorporationOfficeSettings;
                ALTER TABLE WorldCorporationChange DROP COLUMN StorageJson;
                PRAGMA user_version=96;
                """
            );
        }
        WorldInitialization.Initialize(new(world.Path));
        using (WorldDbContext upgraded = new(world.Path))
        {
            Assert.Equal(WorldMigration.CurrentVersion, upgraded.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
            Assert.Equal(item.Quantity, upgraded.Set<OwnedItem>().Find(item.Id)!.Quantity);
            Assert.Equal(
                CorporationStorageRules.DefaultRentalFeeCents,
                upgraded.Set<SavedCorporationOfficeSettings>().Find(1)!.RentalFeeCents
            );
            upgraded.Set<SavedCorporationOfficeSettings>().Find(1)!.RentalFeeCents = 12345;
            upgraded.Set<SavedCorporationOfficeSettings>().Find(1)!.OfficeSlots = 2;
            upgraded.SaveChanges();
        }
        WorldInitialization.Initialize(new(world.Path));
        using var reopened = ServerServices.Build(new(world.Path));
        Assert.Equal(12345, reopened.GetRequiredService<ICorporationStorage>().Quote(Ceo));
        Assert.Equal(2, reopened.GetRequiredService<ICorporationStorage>().EmptyOffices(Ceo));
    }

    [Fact]
    public void LateBatchRejectionRollsBackAlreadyTransferredHullAndItsContents()
    {
        using TestWorld world = new(liveWorld: true);
        Prepare(world);
        var storage = Storage(world);
        var office = Rent(world);
        using (WorldDbContext saved = new(world.Path))
        {
            saved.AddRange(
                new Ship
                {
                    Id = HullId,
                    OwnerId = Ceo,
                    TypeId = 601,
                    StationId = StationId,
                },
                new Ship
                {
                    Id = HullId + 1,
                    OwnerId = Ceo,
                    TypeId = 601,
                    StationId = StationId,
                },
                new OwnedItem
                {
                    Id = HullId + 2,
                    OwnerId = Ceo,
                    TypeId = InventoryDefinitions.CivilianMinerType,
                    LocationId = HullId,
                    FlagId = (int)InventoryFlag.HighSlot0,
                    Quantity = 1,
                    Singleton = true,
                },
                new OwnedItem
                {
                    Id = HullId + 3,
                    OwnerId = Ceo,
                    TypeId = Eden.Gameplay.Contracts.CourierRules.PackageType,
                    LocationId = HullId + 1,
                    FlagId = (int)InventoryFlag.Cargo,
                    Quantity = 1,
                    Singleton = true,
                }
            );
            saved.SaveChanges();
        }
        int events;
        using (WorldDbContext saved = new(world.Path))
        {
            events = saved.Set<SavedCorporationChange>().Count();
        }
        Assert.Throws<InvalidDataException>(() => storage.Move(Ceo, office.Id, new([HullId, HullId + 1], StationId, FirstDivision, null)));
        using WorldDbContext check = new(world.Path);
        Assert.Equal(Ceo, check.Ships.Find(HullId)!.OwnerId);
        Assert.Null(check.Ships.Find(HullId)!.StoredInId);
        Assert.Equal(Ceo, check.Set<OwnedItem>().Find(HullId + 2)!.OwnerId);
        Assert.Equal(events, check.Set<SavedCorporationChange>().Count());
        Assert.Empty(storage.List(Ceo, office.Id, null));
    }

    [Fact]
    public void StationCapacityIsSharedAcrossCorporationsAndChangingStationInvalidatesInventoryAccess()
    {
        using TestWorld world = new();
        int corporation = Prepare(world);
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Set<SavedCorporationOfficeSettings>().Find(1)!.OfficeSlots = 1;
            saved.SaveChanges();
        }
        var storage = Storage(world);
        var office = Rent(world);
        world.Services.GetRequiredService<ICorporationMembership>().Remove(Member, Member);
        PlayerCorporationTests.Prepare(world, Member);
        int second = world
            .Services.GetRequiredService<IPlayerCorporations>()
            .Create(Member, PlayerCorporationTests.Details("Other Corporation", "OTHER"))
            .Corporation.Id;
        world
            .Services.GetRequiredService<ICorporationWallet>()
            .Transfer(
                Member,
                new(false, CorporationWalletRules.MasterKey, second, CorporationWalletRules.MasterKey, 10000000, "Office budget")
            );
        Assert.Throws<InvalidDataException>(() => storage.Rent(Member, storage.Quote(Member)));
        Assert.Empty(storage.Offices(Member));
        Assert.Equal(corporation, Assert.Single(storage.Tenants(Member)));
        long active = world.Store.FindPilot(1000001)!.ShipId;
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Ships.Find(active)!.StationId = 60015159;
            saved.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => storage.List(Ceo, office.Id, null));
        Assert.Throws<InvalidDataException>(() => storage.Office(Ceo, office.Id));
        Assert.Empty(storage.Assets(Ceo, corporation, StationId));
    }

    private static IReadOnlyList<InventoryRow> InventoryRows(CorporationTestClient client, string service, object?[] args)
    {
        var reply = Assert.IsType<PilotCallResult.Success>(client.CallService(service, "List", args)).Reply;
        // The receive codec intentionally rejects outbound DBRow reductions.
        Assert.NotEmpty(BlueMarshal.Encode(reply));
        return Assert.IsType<BlueList<InventoryRow>>(reply).Items;
    }

    private static object? Decode(CorporationTestClient client, string service, string method, object?[] args) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(client.CallService(service, method, args)).Reply));
}
