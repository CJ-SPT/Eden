using Eden.Data.World.Inventory;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationHangarStartupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CeoAndDirectorCanReadAllNativeHangarsIncludingGoalsAndBindCorporateDeliveries(bool director)
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        world.Services.GetRequiredService<ICorporationWallet>().Transfer(90000001, new(false, 1000, corporation, 1000, 10000000, "Office"));
        var storage = world.Services.GetRequiredService<ICorporationStorage>();
        var office = storage.Rent(90000001, storage.Quote(90000001));
        if (director)
        {
            using CorporationTestClient ceo = new(world, 1000001);
            Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(90000002, new((long)CorporationRole.Director))));
        }
        long goalItem;
        long deliveryItem;
        using (var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None))
        {
            goalItem = action.NextItemId();
            deliveryItem = action.NextItemId();
            action.Add(
                new OwnedItem
                {
                    Id = goalItem,
                    OwnerId = corporation,
                    TypeId = InventoryDefinitions.CivilianMinerType,
                    LocationId = office.Id,
                    FlagId = CorporationStorageRules.GoalDeliveriesFlag,
                    Quantity = 3,
                }
            );
            action.Add(
                new OwnedItem
                {
                    Id = deliveryItem,
                    OwnerId = corporation,
                    TypeId = InventoryDefinitions.CivilianMinerType,
                    LocationId = office.StationId,
                    FlagId = CorporationStorageRules.MarketDeliveriesFlag,
                    Quantity = 2,
                }
            );
            action.Commit();
        }
        long actor = director ? 90000002 : 90000001;
        using CorporationTestClient client = new(world, director ? 1000002 : 1000001);
        var flags = Enumerable
            .Range(CorporationStorageRules.FirstFlag, CorporationStorageRules.DivisionCount)
            .Append(CorporationStorageRules.GoalDeliveriesFlag)
            .Select(flag => (object?)(long)flag)
            .ToList();
        string officeService = CorporationStorageHandler.InventoryPrefix + office.Id;
        var listed = Assert.IsType<PilotCallResult.Success>(
            client.CallService(officeService, "ListByFlags", [], new() { ["flags"] = flags })
        );
        Assert.NotEmpty(BlueMarshal.Encode(listed.Reply));
        Assert.Equal(goalItem, Assert.Single(storage.List(actor, office.Id, CorporationStorageRules.GoalDeliveriesFlag)).Id);
        Assert.IsType<PilotCallResult.Success>(
            client.CallService(officeService, "List", [], new() { ["flag"] = (long)CorporationStorageRules.GoalDeliveriesFlag })
        );
        var bound = Assert.IsType<PilotCallResult.Success>(
            client.CallService(
                "invbroker",
                "MachoBindObject",
                [
                    new object?[] { (long)office.StationId, 15L },
                    new object?[] { "GetInventory", new object?[] { (long)CorporationStorageRules.MarketDeliveriesContainer, null } },
                ]
            )
        );
        Assert.NotEmpty(BlueMarshal.Encode(bound.Reply));
        Assert.IsType<PilotCallResult.Success>(client.CallService("@corporationDeliveries", "List", [], new() { ["flag"] = null }));
        Assert.Equal(deliveryItem, Assert.Single(storage.Deliveries(actor)).Id);
        Assert.Throws<InvalidDataException>(() => storage.List(actor, office.Id, 999));
    }

    [Fact]
    public void OrdinaryMembersCannotReadGoalsOrCorporateDeliveriesAndRevocationIsImmediate()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        world.Services.GetRequiredService<ICorporationWallet>().Transfer(90000001, new(false, 1000, corporation, 1000, 10000000, "Office"));
        var storage = world.Services.GetRequiredService<ICorporationStorage>();
        var office = storage.Rent(90000001, storage.Quote(90000001));
        Assert.Throws<InvalidDataException>(() => storage.List(90000002, office.Id, CorporationStorageRules.GoalDeliveriesFlag));
        Assert.Throws<InvalidDataException>(() => storage.Deliveries(90000002));
        using CorporationTestClient ceo = new(world, 1000001);
        Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(90000002, new((long)CorporationRole.Director))));
        Assert.Empty(storage.List(90000002, office.Id, CorporationStorageRules.GoalDeliveriesFlag));
        Assert.Empty(storage.Deliveries(90000002));
        Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(90000002, new())));
        Assert.Throws<InvalidDataException>(() => storage.List(90000002, office.Id, CorporationStorageRules.GoalDeliveriesFlag));
        Assert.Throws<InvalidDataException>(() => storage.Deliveries(90000002));
    }
}
