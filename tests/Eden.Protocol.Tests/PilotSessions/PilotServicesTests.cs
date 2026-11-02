using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class PilotServicesTests
{
    [Fact]
    public async Task RecoveryAnnouncesTheNewHullBeforeTheHangarProcessesItsShipChange()
    {
        using TestWorld world = new();
        Persistence.PilotFleetTests.AddVenture(world);
        using (CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO TypeAttribute VALUES (601,14,2)");
        }
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Ships.Single(s => s.Id == 1000000000001L).TypeId = (int)StarterShipType.Venture;
            saved.SaveChanges();
        }
        var packets = await ExchangeAsync(world, 42,
            Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            Request("dogmaIM", "CreateNewbieShip", [1000000000001L, 60003760L]));
        Assert.Equal(new[] { "SessionChangeNotification", "CallRsp", "Notification", "Notification", "Notification",
            "SessionChangeNotification", "CallRsp" }, packets.Select(p => p.Name.Split('.')[^1]));
        foreach (var packet in packets.Skip(2).Take(3))
        {
            var envelope = PilotLogin.Tuple(packet.State, 14);
            var destination = Assert.IsType<BlueInstance>(envelope[2]);
            Assert.Equal("OnItemChange", PilotLogin.Tuple(destination.State, 4)[1]);
        }
        var sessionBody = PilotLogin.Tuple(PilotLogin.Tuple(packets[5].State, 14)[4], 3);
        var delta = PilotLogin.Fields(PilotLogin.Tuple(sessionBody[1], 2)[1]);
        Assert.Equal(new object?[] { 1000000000001L, world.Store.FindPilot(1000001)!.ShipId }, delta["shipid"]);
        Assert.Null(Result(packets[^1]));
    }

    [Fact]
    public async Task CargoCapacityErrorWrapsAUserErrorForTheClientsQuantityRetry()
    {
        using TestWorld world = new();
        long itemId;
        using (WorldDbContext saved = new(world.Path))
        {
            var item = saved.Set<OwnedItem>().Single(i => i.OwnerId == 90000001 && i.TypeId == InventoryDefinitions.CivilianMinerType);
            item.Quantity = 30;
            itemId = item.Id;
            saved.SaveChanges();
        }
        var packets = await ExchangeAsync(world, 42,
            Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            Request("invbroker", "GetInventoryFromId", [1000000000001L]),
            Request(null, "Add", [itemId, 60003760L], "N=10001:42-2", new() { ["flag"] = 5L }));
        var packet = packets[^1];
        Assert.EndsWith(".ErrorResponse", packet.Name);
        var body = PilotLogin.Tuple(PilotLogin.Tuple(packet.State, 14)[4], 3);
        Assert.Equal(6, body[0]);
        Assert.Equal(2, body[1]);
        byte[] exception = PilotLogin.Bytes(Assert.Single(PilotLogin.Tuple(body[2], 1)));
        Assert.Equal(BlueMarshal.Encode(new BlueConstructor("exceptions.UserError", ["NotEnoughCargoSpace",
            new Dictionary<string, object?> { ["available"] = 125d, ["volume"] = 150d }])), exception);
    }

    [Fact]
    public async Task ListsSameOwnedPilotOnFreshSessions()
    {
        foreach (long sessionId in new long[] { 41, 42 })
        {
            var packets = await ExchangeAsync("charUnboundMgr", "GetCharacterSelectionData", [], sessionId);
            var roster = PilotLogin.Tuple(Result(packets.Single()), 4);
            var character = Assert.IsType<BlueInstance>(Assert.Single(Assert.IsType<List<object?>>(roster[2])));
            var fields = PilotLogin.Fields(character.State);
            Assert.Equal(90000001L, fields["characterID"]);
            Assert.Equal("Eden Pilot", BlueMarshal.Text(fields["characterName"]));
            Assert.Equal(601L, fields["shipTypeID"]);
            Assert.Equal(8000L, fields["skillPoints"]);
        }
    }

    [Theory]
    [InlineData(90000001, true)]
    [InlineData(90000002, false)]
    public async Task AcceptsOnlyOwnedCharacter(int characterId, bool expected)
    {
        var packets = await ExchangeAsync("charUnboundMgr", "SelectCharacterID", [characterId, null, false], 42);
        if (!expected)
        {
            Assert.EndsWith(".ErrorResponse", Assert.Single(packets).Name);
            return;
        }

        Assert.Equal(2, packets.Count);
        Assert.EndsWith(".SessionChangeNotification", packets[0].Name);
        var envelope = PilotLogin.Tuple(packets[0].State, 14);
        Assert.Equal(16, envelope[0]);
        var body = PilotLogin.Tuple(envelope[4], 3);
        Assert.Equal(42L, body[0]);
        var delta = PilotLogin.Tuple(body[1], 2);
        var changes = PilotLogin.Fields(delta[1]);
        Assert.Equal(new object?[] { null, 90000001L }, changes["charid"]);
        Assert.Equal(new object?[] { null, 1000000000001L }, changes["shipid"]);
        Assert.Equal(new object?[] { null, 60003760 }, changes["stationid"]);
        Assert.Equal(new object?[] { null, 30000142 }, changes["solarsystemid2"]);
        Assert.DoesNotContain("solarsystemid", changes.Keys);
        Assert.EndsWith(".CallRsp", packets[1].Name);
    }

    [Fact]
    public async Task SheetReadsOnlyTheSelectedPilotsProfile()
    {
        var packets = await ExchangeAsync(42,
            Request("charMgr", "GetPublicInfo", [90000001L]),
            Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            Request("charMgr", "GetPublicInfo", [90000002L]),
            Request("charMgr", "GetPublicInfo", [90000001L]));
        Assert.EndsWith(".ErrorResponse", packets[0].Name);
        Assert.EndsWith(".ErrorResponse", packets[3].Name);
        var fields = PilotLogin.Fields(Assert.IsType<BlueInstance>(Result(packets[4])).State);
        Assert.Equal(90000001L, fields["characterID"]);
        Assert.Equal(19L, fields["schoolID"]);
        Assert.Equal(10L, fields["ancestryID"]);
        Assert.Equal(134000000000000000L, fields["createDateTime"]);
    }

    [Fact]
    public async Task UnknownMethodsReceiveAnError()
    {
        var packets = await ExchangeAsync("charUnboundMgr", "DeleteCharacter", [90000001], 42);
        Assert.EndsWith(".ErrorResponse", packets.Single().Name);
    }

    [Fact]
    public async Task CreatorCanSelectItsNewCharacterByKeywordAndReadBackTheAvatar()
    {
        using TestWorld world = new();
        TestCatalog.AddSchool(new WorldDatabase(world.Path).CatalogPath, 22, 8, 7, 30005305, 60015161, 606);
        BlueInstance appearance = new("utillib.KeyVal", new Dictionary<string, object?>
        {
            ["appearance"] = new PaperDollRow("eve.common.script.paperDoll.paperDollDefinitions.AppearanceRow", [1L]),
        });
        BlueInstance portrait = new("utillib.KeyVal", new Dictionary<string, object?> { ["lightID"] = 15 });
        var replies = await ExchangeAsync(world, 42,
            Request("charUnboundMgr", "CreateCharacterWithDoll", ["New Pilot", 8, 7, 1, 64, appearance, portrait, 22, null, null]),
            Request("charUnboundMgr", "SelectCharacterID", [], keywords: new() { ["charID"] = 90000003L, ["skipTutorial"] = true }),
            Request("paperDollServer", "GetPaperDollData", [90000003L]),
            Request("home_station", "get_home_station", []));
        Assert.Equal(90000003L, Result(replies[0]));
        Assert.EndsWith(".SessionChangeNotification", replies[1].Name);
        Assert.Null(Result(replies[2]));
        Assert.Equal(BlueMarshal.Encode(appearance), BlueMarshal.Encode(Result(replies[3])));
        var home = PilotLogin.Fields(Assert.IsType<BlueInstance>(Result(replies[4])).State);
        Assert.Equal(60015161L, home["id"]);
        Assert.Equal(30005305L, home["solar_system_id"]);
    }

    private static object? Result(BlueInstance packet)
    {
        var state = PilotLogin.Tuple(packet.State, 14);
        return BlueMarshal.Decode(PilotLogin.Bytes(PilotLogin.Tuple(state[4], 1)[0]));
    }

    [Fact]
    public async Task RoutesBoundInventoryAndOrdersFlightNotifications()
    {
        const long ship = 1000000000001L;
        var packets = await ExchangeAsync(42,
            Request("ship", "Undock", [ship]),
            Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            Request("invbroker", "GetInventory", [10004L]),
            Request(null, "List", [4L], "N=10001:42-2"),
            Request("ship", "Undock", [ship]),
            Request("beyonce", "MachoBindObject", [30000142L, null]));
        Assert.Equal(new[] { "ErrorResponse", "SessionChangeNotification", "CallRsp", "CallRsp", "CallRsp",
            "SessionChangeNotification", "CallRsp", "CallRsp", "Notification" }, packets.Select(packet => packet.Name.Split('.')[^1]));
        // DBRow uses a client-only constructor. Compare the actual bound List payload with the owned ship ID on the wire.
        var state = PilotLogin.Tuple(packets[4].State, 14);
        var payload = PilotLogin.Bytes(PilotLogin.Tuple(state[4], 1)[0]);
        Assert.True(payload.AsSpan().IndexOf(BitConverter.GetBytes(ship)) >= 0);
        Assert.Null(Result(packets[6]));
        var notification = PilotLogin.Tuple(packets[8].State, 14);
        var destination = Assert.IsType<BlueInstance>(notification[2]);
        Assert.Equal("DoDestinyUpdate", PilotLogin.Tuple(destination.State, 4)[1]);
    }

    private static Task<List<BlueInstance>> ExchangeAsync(string service, string method, object?[] args, long sessionId)
        => ExchangeAsync(sessionId, Request(service, method, args));

    private static BlueInstance Request(string? service, string method, object?[] args, object? objectId = null, Dictionary<string, object?>? keywords = null)
    {
        const string address = "carbon.common.script.net.machoNetAddress.MachoAddress";
        byte[] call = BlueMarshal.Encode(new object?[] { objectId, method, args, keywords ?? new Dictionary<string, object?>() });
        return new("carbon.common.script.net.machoNetPacket.CallReq", new object?[]
        {
            6L, new BlueInstance(address, new object?[] { 2L, 0L, 1L, null }),
            new BlueInstance(address, service is null ? new object?[] { 1L, (long)PilotLogin.NodeId, null, null } : new object?[] { 8L, service, null }), null,
            new object?[] { new object?[] { 0L, call } }, null, null, null, null, null, null, null, null, null,
        });
    }

    private static async Task<List<BlueInstance>> ExchangeAsync(long sessionId, params BlueInstance[] requests)
    {
        using TestWorld world = new();
        return await ExchangeAsync(world, sessionId, requests);
    }

    private static async Task<List<BlueInstance>> ExchangeAsync(TestWorld world, long sessionId, params BlueInstance[] requests)
    {
        using var scope = world.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IPilotSession>();
        Queue<object?> input = new(requests);
        List<BlueInstance> output = [];
        await Assert.ThrowsAsync<EndOfStreamException>(() => session.RunAsync(
            () => input.TryDequeue(out object? packet) ? Task.FromResult(packet) : throw new EndOfStreamException(),
            packet =>
            {
                output.Add(Assert.IsType<BlueInstance>(packet));
                return Task.CompletedTask;
            },
            _ => { }, 1000001, sessionId, [], CancellationToken.None));
        return output;
    }
}
