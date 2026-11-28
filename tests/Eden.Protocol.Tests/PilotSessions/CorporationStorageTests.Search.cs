using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Corporations;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed partial class CorporationStorageTests
{
    [Fact]
    public async Task NativeOfficeAssetSearchFiltersResidentStacksAndStoredShipsAcrossCheckpoint()
    {
        const int otherStation = 60015159;
        const int otherSystem = 30001392;
        const int shipType = 601;
        using TestWorld world = new(liveWorld: true);
        TestCatalog.AddSchool(new WorldDatabase(world.Path).CatalogPath, 18, 1, 1, otherSystem, otherStation, shipType);
        int corporation = Prepare(world);
        var office = Rent(world);
        var item = PersonalItem(world);
        var live = world.Services.GetRequiredService<LiveWorld>();
        using (var action = live.Economy.Begin())
        {
            action.Set<OwnedItem>().Find(item.Id)!.Quantity = 12;
            action.Add(
                new OwnedItem
                {
                    Id = action.NextItemId(),
                    TypeId = item.TypeId,
                    OwnerId = corporation,
                    LocationId = office.Id,
                    FlagId = SecondDivision,
                    Quantity = 8,
                }
            );
            long remoteOffice = office.Id + 1;
            action.Add(
                new SavedCorporationOffice
                {
                    Id = remoteOffice,
                    CorporationId = corporation,
                    StationId = otherStation,
                    SystemId = otherSystem,
                    RentedAt = office.RentedAt,
                    FeeCents = office.FeeCents,
                }
            );
            action.Add(
                new Ship
                {
                    Id = action.NextItemId(),
                    TypeId = shipType,
                    OwnerId = corporation,
                    StoredInId = remoteOffice,
                    StorageFlagId = FirstDivision,
                    StationId = otherStation,
                    Name = "Stored corporation ship",
                }
            );
            action.Commit();
        }
        Storage(world).Move(Ceo, office.Id, new([item.Id], StationId, FirstDivision, null));

        using (CorporationTestClient ceo = new(world, 1000001))
        {
            Assert.Equal(new[] { StationId, otherStation }, Search(ceo));
            int shipStation;
            using (var performance = SessionPerformance.Begin("warm corporation office search", _ => { }, force: true)!)
            {
                Assert.Equal(new[] { StationId }, Search(ceo, item.CategoryId, item.GroupId, item.TypeId, 12));
                Assert.Equal(new[] { StationId }, Search(ceo, category: item.CategoryId, group: item.GroupId));
                Assert.Empty(Search(ceo, group: int.MaxValue, type: item.TypeId));
                Assert.Empty(Search(ceo, type: item.TypeId, minimumQuantity: 13));
                shipStation = Assert.Single(Search(ceo, type: shipType, minimumQuantity: 1));
                Assert.Equal(otherStation, shipStation);
                Assert.Empty(Search(ceo, type: shipType, minimumQuantity: 2));
                Assert.Equal(0, performance.Commands);
                Assert.Equal(0, performance.Connections);
            }
            var ship = Assert.Single(Storage(world).Assets(Ceo, corporation, shipStation));
            Assert.True(ship.Singleton);
            var row = InventoryRow.From(ship);
            Assert.Equal(InventoryWireDefinitions.AssembledQuantity, row.Quantity);
            AssertOfficeAssetDetails(ceo, corporation, shipStation, [row]);
        }
        await live.FlushAsync();
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), liveWorld: true);
        var storage = reopened.GetRequiredService<ICorporationStorage>();
        Assert.Equal(new[] { StationId }, storage.SearchOfficeAssets(Ceo, new(item.CategoryId, item.GroupId, item.TypeId, 12)));
        Assert.Equal(new[] { otherStation }, storage.SearchOfficeAssets(Ceo, new(0, 0, shipType, 1)));
    }

    [Fact]
    public void NativeOfficeAssetSearchRequiresCurrentFinancialAuthorityAndPermitsOpeningResults()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = Prepare(world);
        var storage = Storage(world);
        var office = Rent(world);
        var item = PersonalItem(world);
        storage.Move(Ceo, office.Id, new([item.Id], StationId, SecondDivision, null));
        using CorporationTestClient member = new(world, 1000002);
        object?[] arguments = ["offices", null, null, (long)item.TypeId, null];
        var live = world.Services.GetRequiredService<LiveWorld>();
        long deliveryId;
        using (var action = live.Economy.Begin())
        {
            deliveryId = action.NextItemId();
            action.Add(
                new OwnedItem
                {
                    Id = deliveryId,
                    TypeId = item.TypeId,
                    OwnerId = corporation,
                    LocationId = office.Id,
                    FlagId = CorporationStorageRules.GoalDeliveriesFlag,
                    Quantity = 9,
                }
            );
            action.Commit();
        }
        SetRoles(new(RolesAtHQ: (long)CorporationRole.HangarCanQuery1));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("corpmgr", "SearchAssets", arguments));
        AssertOfficeAssetDetails(member, corporation, StationId, []);

        SetRoles(new((long)CorporationRole.JuniorAccountant));
        int match = Assert.Single(Search(member, type: item.TypeId));
        Assert.Equal(StationId, match);
        Assert.Empty(Search(member, type: item.TypeId, minimumQuantity: 9));
        var ordinary = InventoryRow.From(item with { OwnerId = corporation, LocationId = office.Id, FlagId = SecondDivision });
        AssertOfficeAssetDetails(member, corporation, match, [ordinary]);
        Assert.IsType<PilotCallResult.Failure>(
            member.CallService("corpmgr", "SearchAssets", arguments, new() { ["corporationID"] = (long)corporation + 1 })
        );
        Assert.IsType<PilotCallResult.Failure>(member.CallService("corpmgr", "SearchAssets", ["offices", -1L, null, null, null]));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("corpmgr", "SearchAssets", ["offices", null, null, null, -1L]));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("corpmgr", "SearchAssets", ["impounded", null, null, null, null]));

        SetRoles(new((long)(CorporationRole.JuniorAccountant | CorporationRole.DeliveriesHangarQuery)));
        Assert.Equal(new[] { StationId }, Search(member, type: item.TypeId, minimumQuantity: 9));
        var delivery = ordinary with { Id = deliveryId, FlagId = CorporationStorageRules.GoalDeliveriesFlag, Quantity = 9 };
        AssertOfficeAssetDetails(member, corporation, StationId, new[] { ordinary, delivery }.OrderBy(row => row.Id).ToArray());

        SetRoles(new());
        Assert.IsType<PilotCallResult.Failure>(member.CallService("corpmgr", "SearchAssets", arguments));
        AssertOfficeAssetDetails(member, corporation, StationId, []);

        void SetRoles(CorporationRoleSet roles)
        {
            using var action = live.Economy.Begin(new(pilots: [Member]));
            var row =
                action.Set<SavedCorporationMemberAccess>().Find(Member)
                ?? action.Add(new SavedCorporationMemberAccess { PilotId = Member, CorporationId = corporation }).Entity;
            row.DataJson = JsonSerializer.Serialize(new CorporationMemberAccess(roles));
            action.Commit();
        }
    }

    private static void AssertOfficeAssetDetails(
        CorporationTestClient client,
        int corporation,
        int station,
        IReadOnlyList<InventoryRow> items
    )
    {
        var reply = Assert.IsType<PilotCallResult.Success>(
            client.CallService("corpmgr", "GetAssetInventoryForLocation", [(long)corporation, (long)station, "offices"])
        );
        var descriptor = InventoryWire.Descriptor();
        var header = Assert.IsType<BlueRowDescriptor>(BlueMarshal.Decode(BlueMarshal.Encode(descriptor)));
        Assert.True(header.InventoryVirtuals);
        var expected = new BlueConstructor("carbon.common.script.sys.crowset.CRowset", [descriptor, items.Cast<object?>().ToList()]);
        Assert.Equal(BlueMarshal.Encode(expected), BlueMarshal.Encode(reply.Reply));
    }

    private static int[] Search(
        CorporationTestClient client,
        int? category = null,
        int? group = null,
        int? type = null,
        long? minimumQuantity = null
    )
    {
        var reply = Assert.IsType<PilotCallResult.Success>(
            client.CallService("corpmgr", "SearchAssets", ["offices", (long?)category, (long?)group, (long?)type, minimumQuantity])
        );
        var model = Assert.IsType<CorporationOfficeAssetSearchReply>(reply.Reply);
        var descriptor = BlueRows.Descriptor([("locationID", BlueColumnType.Int64)]);
        var expected = new BlueConstructor(
            "carbon.common.script.sys.crowset.CRowset",
            [
                descriptor,
                model.Stations.Select(id => (object?)new BlueConstructor("blue.DBRow", [descriptor, new object?[] { (long)id }])).ToList(),
            ]
        );
        Assert.Equal(BlueMarshal.Encode(expected), BlueMarshal.Encode(reply.Reply));
        return model.Stations.ToArray();
    }
}
