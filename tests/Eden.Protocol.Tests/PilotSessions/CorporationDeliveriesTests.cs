using Eden.Data.World.Inventory;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationDeliveriesTests
{
    private const long Ceo = 90000001;
    private const long Member = 90000002;
    private const int Flag = CorporationStorageRules.MarketDeliveriesFlag;

    [Fact]
    public void NativeMarketDeliveryTransfersPartialAndWholeStacksAndNotifiesTheirOwners()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        Assign(world, CorporationRole.Trader);
        using CorporationTestClient actor = new(world, 1000002);
        using CorporationTestClient recipient = new(world, 1000001);
        int station = actor.Context.Pilot.StationId;
        var live = world.Services.GetRequiredService<LiveWorld>();
        long item = Grant(live, corporation, station, Flag, 7);
        Assert.Single(world.Services.GetRequiredService<ICorporationStorage>().Deliveries(Member));
        Assert.Same(PilotCallResult.None, Deliver(actor, Ceo, station, [item], 3, corporation));
        var removed = Assert.Single(actor.Context.Notifications.OfType<ItemChanged>());
        Assert.Equal(item, removed.Item.Id);
        Assert.Equal(corporation, removed.Item.OwnerId);
        recipient.Advance();
        var received = Assert.Single(recipient.Context.Notifications.OfType<ItemChanged>());
        Assert.Equal(Ceo, received.Item.OwnerId);
        Assert.Equal(station, received.Item.LocationId);
        Assert.Equal((int)InventoryFlag.Hangar, received.Item.FlagId);
        Assert.NotEqual(item, received.Item.Id);
        var split = Read(live, received.Item.Id);
        Assert.Equal(3, split.Quantity);
        Assert.Equal(.5, split.VolumeOverride);
        Assert.Equal(.125, split.CrystalDamage);
        Assert.Equal(4, Read(live, item).Quantity);
        recipient.Advance();
        Assert.Single(recipient.Context.Notifications.OfType<ItemChanged>());

        Assert.Same(PilotCallResult.None, Deliver(actor, Member, station, [item], null, corporation));
        var whole = Read(live, item);
        Assert.Equal(Member, whole.OwnerId);
        Assert.Equal(station, whole.LocationId);
        Assert.Equal((int)InventoryFlag.Hangar, whole.FlagId);
        Assert.Equal(4, whole.Quantity);
        Assert.Empty(world.Services.GetRequiredService<ICorporationStorage>().Deliveries(Member));
        Assert.IsType<PilotCallResult.Failure>(Deliver(actor, Member, station, [item], null, corporation));
    }

    [Fact]
    public void NativeMarketDeliveryRejectsWrongStationSourceRecipientAndRoleWithoutPartialTransfer()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        Assign(world, CorporationRole.JuniorAccountant);
        using CorporationTestClient actor = new(world, 1000002);
        int station = actor.Context.Pilot.StationId;
        var live = world.Services.GetRequiredService<LiveWorld>();
        long item = Grant(live, corporation, station, Flag, 7);
        long otherFlag = Grant(live, corporation, station, CorporationStorageRules.GoalDeliveriesFlag, 2);
        long otherStation = Grant(live, corporation, station + 1, Flag, 1);
        Assert.Single(world.Services.GetRequiredService<ICorporationStorage>().Deliveries(Member));
        Assert.IsType<PilotCallResult.Failure>(Deliver(actor, Member, station, [item], null, corporation));
        Assign(world, CorporationRole.Trader);
        Assert.IsType<PilotCallResult.Failure>(Deliver(actor, Member, station + 1, [otherStation], null, corporation));
        Assert.IsType<PilotCallResult.Failure>(Deliver(actor, Member, station, [item], null, corporation + 1));
        Assert.IsType<PilotCallResult.Failure>(Deliver(actor, Member + 10, station, [item], null, corporation));
        Assert.IsType<PilotCallResult.Failure>(
            Deliver(actor, Member, station, [otherFlag], null, corporation, CorporationStorageRules.GoalDeliveriesFlag)
        );
        Assert.IsType<PilotCallResult.Failure>(Deliver(actor, Member, station, [item, otherFlag], null, corporation));
        Assert.IsType<PilotCallResult.Failure>(Deliver(actor, Member, station, [item, otherStation], null, corporation));
        Assert.IsType<PilotCallResult.Failure>(Deliver(actor, Member, station, [item], 8, corporation));
        Assert.IsType<PilotCallResult.Failure>(Deliver(actor, Member, station, [item, item], null, corporation));
        Assert.Empty(actor.Context.Notifications.OfType<ItemChanged>());
        Assert.Equal(corporation, Read(live, item).OwnerId);
        Assert.Equal(7, Read(live, item).Quantity);
        Assign(world, CorporationRole.NoRole);
        Assert.IsType<PilotCallResult.Failure>(Deliver(actor, Member, station, [item], null, corporation));
        Assert.Equal(corporation, Read(live, item).OwnerId);
    }

    private static PilotCallResult Deliver(
        CorporationTestClient actor,
        long target,
        int station,
        IReadOnlyList<long> items,
        long? quantity,
        int corporation,
        int flag = Flag
    ) =>
        actor.CallService(
            "invbroker",
            "DeliverToCorpMember",
            [target, (long)station, items.Cast<object?>().ToList(), quantity, (long)corporation, (long)flag]
        );

    private static void Assign(TestWorld world, CorporationRole role) =>
        world
            .Services.GetRequiredService<ICorporationRoles>()
            .UpdateMembers(Ceo, [new(Member, null, null, null, new((long)role, 0, 0, 0, 0, 0, 0, 0), null, 0)]);

    private static long Grant(LiveWorld live, int corporation, int station, int flag, int quantity)
    {
        using var world = live.Economy.Begin(TransactionParticipants.None);
        var item = world
            .Add(
                new OwnedItem
                {
                    Id = world.NextItemId(),
                    TypeId = InventoryDefinitions.CivilianMinerType,
                    OwnerId = corporation,
                    LocationId = station,
                    FlagId = flag,
                    Quantity = quantity,
                    VolumeOverride = .5,
                    CrystalDamage = .125,
                }
            )
            .Entity;
        world.Commit();
        return item.Id;
    }

    private static OwnedItem Read(LiveWorld live, long item) => live.Economy.Inspect(world => world.Set<OwnedItem>().Find(item)!);
}
