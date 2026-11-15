using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationHeadquartersTests
{
    private const long Ceo = 90000001;
    private const long Member = 90000002;
    private const int Station = 60003760;
    private const int OriginalHeadquarters = 60004588;

    [Fact]
    public void NativeEmbeddedHeadquartersCallPublishesLocationAndPersistsOnce()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        world.Services.GetRequiredService<ICorporationWallet>().Transfer(Ceo, new(false, 1000, corporation, 1000, 10000000, "Office"));
        var storage = world.Services.GetRequiredService<ICorporationStorage>();
        storage.Rent(Ceo, storage.Quote(Ceo));
        using (var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None))
        {
            action.Set<SavedCorporation>().Find(corporation)!.StationId = OriginalHeadquarters;
            action.Commit();
        }
        using (CorporationTestClient ceo = new(world, 1000001))
        using (CorporationTestClient member = new(world, 1000002))
        {
            var result = Assert.IsType<PilotCallResult.Success>(
                ceo.CallService(
                    "corpStationMgr",
                    "MachoBindObject",
                    [(long)Station, new object?[] { "MoveCorpHQHere", Array.Empty<object?>() }]
                )
            );
            var bound = BlueRead.Tuple(result.Reply.ToBlue(), 2);
            Assert.IsType<BlueObjectReference>(bound[0]);
            Assert.Null(Assert.IsAssignableFrom<IBlueWritable>(bound[1]).ToBlue());
            Assert.NotEmpty(BlueMarshal.Encode(result.Reply));
            Assert.Equal(Station, world.Services.GetRequiredService<IPlayerCorporations>().Read(corporation)!.StationId);
            member.Context.Notifications.Clear();
            member.Advance();
            var notification = Assert.Single(member.Context.Notifications.OfType<CorporationDetailsChanged>());
            var fields = Assert.IsType<Dictionary<string, object?>>(notification.Arguments[1]);
            Assert.Equal(new object?[] { OriginalHeadquarters, Station }, Assert.IsType<object?[]>(fields["stationID"]));
            long head = world.Services.GetRequiredService<ICorporationMembership>().Head(Ceo);
            Assert.Null(Assert.IsType<PilotCallResult.Success>(ceo.CallService("corpStationMgr", "MoveCorpHQHere", [])).Reply.ToBlue());
            Assert.Equal(head, world.Services.GetRequiredService<ICorporationMembership>().Head(Ceo));
        }
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), liveWorld: true);
        Assert.Equal(Station, reopened.GetRequiredService<IPlayerCorporations>().Read(corporation)!.StationId);
    }

    [Fact]
    public void HeadquartersChangeRejectsUnauthorizedUndockedAndMissingOfficeWithoutChanges()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var headquarters = world.Services.GetRequiredService<ICorporationHeadquarters>();
        Assert.Throws<InvalidDataException>(() => headquarters.MoveHere(Ceo));
        Assert.Throws<InvalidDataException>(() => headquarters.MoveHere(Member));
        world.Services.GetRequiredService<ICorporationWallet>().Transfer(Ceo, new(false, 1000, corporation, 1000, 10000000, "Office"));
        var storage = world.Services.GetRequiredService<ICorporationStorage>();
        storage.Rent(Ceo, storage.Quote(Ceo));
        using CorporationTestClient ceo = new(world, 1000001);
        Assert.IsType<PilotCallResult.Failure>(ceo.CallService("corpStationMgr", "MoveCorpHQHere", [(long)Station]));
        Assert.IsType<PilotCallResult.Failure>(ceo.CallService("corpStationMgr", "MoveCorpHQHere", [], new() { ["stationID"] = Station }));
        world.Store.SetPosition(1000000000001, new(1, 2, 3));
        Assert.Throws<InvalidDataException>(() => headquarters.MoveHere(Ceo));
        Assert.Equal(Station, world.Services.GetRequiredService<IPlayerCorporations>().Read(corporation)!.StationId);
        ceo.Context.LogOff();
        Assert.Same(PilotCallResult.Unsupported, ceo.CallService("corpStationMgr", "MoveCorpHQHere", []));
    }
}
