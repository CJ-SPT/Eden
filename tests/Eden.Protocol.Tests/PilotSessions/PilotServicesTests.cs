using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class PilotServicesTests
{
    [Fact]
    public async Task NativeStationGuestRequestReturnsBothDockedPilotsAndResolvesTheOtherName()
    {
        using TestWorld world = new();
        using var peerScope = world.Services.CreateScope();
        var peerServices = peerScope.ServiceProvider;
        var pilot = world.Store.FindPilot(1000001)!;
        var peer = world.Store.FindPilot(1000002)!;
        using PilotCallContext peerContext = new(
            1000002,
            peer.Account,
            [peer],
            peerServices.GetRequiredService<FlightSessionFactory>().Create,
            43,
            SignedSettings.Empty
        );
        peerContext.Select(peer);
        peerServices.GetRequiredService<PilotUpdateCoordinator>().Enter(peerContext);
        var packets = await ExchangeAsync(
            world,
            42,
            Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            Request("station", "GetGuests", []),
            Request("config", "GetMultiOwnersEx", [new List<object?> { peer.PilotId }])
        );
        Assert.DoesNotContain(packets, packet => packet.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
        var guests = Assert.IsType<List<object?>>(Result(packets[^2]));
        Assert.Equal(2, guests.Count);
        Assert.Equal(new object?[] { pilot.PilotId, (long)pilot.CorpId, null, null }, BlueRead.Tuple(guests[0], 4));
        Assert.Equal(new object?[] { peer.PilotId, (long)peer.CorpId, null, null }, BlueRead.Tuple(guests[1], 4));
        var owners = BlueRead.Tuple(Result(packets[^1]), 2);
        var owner = BlueRead.Tuple(Assert.Single(Assert.IsType<List<object?>>(owners[1])), 5);
        Assert.Equal(peer.PilotId, owner[0]);
        Assert.Equal(peer.Name, BlueMarshal.Text(owner[1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CharacterLogoffClearsSessionFieldsBeforeReplyAndAllowsAnotherOwnedPilotOnTheSameConnection(bool inSpace)
    {
        using TestWorld world = new();
        TestCatalog.AddSchool(new WorldDatabase(world.Path).CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        long other = world
            .Services.GetRequiredService<ICharacterCreation>()
            .Create(1000001, new("Second Pilot", 1, 1, 1, 64, [1], [2], 18));
        if (inSpace)
        {
            world.Store.SetPosition(1000000000001, new SpacePosition(1_000_000, 0, 0));
        }
        var packets = await ExchangeAsync(
            world,
            42,
            Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            Request("invbroker", "GetInventoryFromId", [1000000000001L]),
            Request("skillMgr2", "GetMySkillHandler", []),
            Request("userSvc", "UserLogOffCharacter", []),
            Request("charUnboundMgr", "GetCharacterSelectionData", []),
            Request(null, "List", [4L], "N=10001:42-2"),
            Request("charUnboundMgr", "SelectCharacterID", [other]),
            Request("userSvc", "UserLogOffCharacter", []),
            Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            Request("charMgr", "GetPublicInfo", [90000001L])
        );
        var changes = packets.Where(p => p.Name.EndsWith(".SessionChangeNotification", StringComparison.Ordinal)).ToArray();
        Assert.Equal(5, changes.Length);
        for (int index = 1; index <= 3; index += 2)
        {
            var state = BlueRead.Tuple(changes[index].State, 14);
            Assert.Equal(1000001L, state[3]);
            var fields = BlueRead.Fields(BlueRead.Tuple(BlueRead.Tuple(state[4], 3)[1], 2)[1]);
            Assert.Equal(new object?[] { index == 1 ? 90000001L : other, null }, fields["charid"]);
            Assert.All(fields.Values, value => Assert.Null(BlueRead.Tuple(value, 2)[1]));
            Assert.Contains("shipid", fields.Keys);
            Assert.Contains("locationid", fields.Keys);
            Assert.DoesNotContain("userid", fields.Keys);
            Assert.DoesNotContain("role", fields.Keys);
            int packetIndex = packets.IndexOf(changes[index]);
            Assert.EndsWith(".CallRsp", packets[packetIndex + 1].Name);
            Assert.True(Assert.IsType<bool>(Result(packets[packetIndex + 1])));
        }
        var error = Assert.Single(packets, p => p.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
        var errorBody = BlueRead.Tuple(BlueRead.Tuple(error.State, 14)[4], 3);
        Assert.Equal("Unsupported local call unknown binding.List", errorBody[2]);
        var publicInfo = BlueRead.Fields(Assert.IsType<BlueInstance>(Result(packets[^1])).State);
        Assert.Equal(90000001L, publicInfo["characterID"]);
    }

    [Fact]
    public async Task CharacterLogoffWithoutSelectionIsDeniedAndMalformedRequestsDoNotLogOffThePilot()
    {
        var packets = await ExchangeAsync(
            42,
            Request("userSvc", "UserLogOffCharacter", []),
            Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            Request("userSvc", "UserLogOffCharacter", [90000002L]),
            Request("charMgr", "GetPublicInfo", [90000001L])
        );
        Assert.False(Assert.IsType<bool>(Result(packets[0])));
        Assert.Single(packets, p => p.Name.EndsWith(".SessionChangeNotification", StringComparison.Ordinal));
        AssertUserError(packets[^2], "CustomInfo", new() { ["info"] = "Character logoff does not accept arguments." });
        Assert.Equal(90000001L, BlueRead.Fields(Assert.IsType<BlueInstance>(Result(packets[^1])).State)["characterID"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionExitRemovesStationPresenceBeforeTheConnectionScopeIsDisposed(bool writeFailure)
    {
        using TestWorld world = new();
        using var scope = world.Services.CreateScope();
        var services = scope.ServiceProvider;
        var pilot = world.Store.FindPilot(1000001)!;
        var guests = services.GetRequiredService<SharedStationGuests>();
        Queue<object?> requests = new([Request("charUnboundMgr", "SelectCharacterID", [pilot.PilotId])]);
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            services
                .GetRequiredService<IPilotSession>()
                .RunAsync(
                    () => requests.TryDequeue(out var request) ? Task.FromResult(request) : throw new EndOfStreamException(),
                    _ =>
                    {
                        Assert.Equal(pilot.PilotId, Assert.Single(guests.List(pilot)).PilotId);
                        return writeFailure ? throw new EndOfStreamException() : Task.CompletedTask;
                    },
                    _ => { },
                    1000001,
                    42,
                    SignedSettings.Empty,
                    CancellationToken.None
                )
        );
        Assert.Empty(guests.List(pilot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestedDisconnectLoggingReturnsNoneBeforeAndAfterSelectionAndToleratesRetries(bool selected)
    {
        using TestWorld world = new();
        var before = world.Store.FindPilot(1000001);
        List<BlueInstance> requests = [];
        if (selected)
        {
            requests.Add(Request("charUnboundMgr", "SelectCharacterID", [90000001L]));
        }
        requests.Add(Request("eventLog", "LogPlayerRequestedDisconnect", []));
        requests.Add(Request("eventLog", "LogPlayerRequestedDisconnect", []));
        requests.Add(Request("machoNet", "GetTime", []));
        var packets = await ExchangeAsync(world, 42, requests.ToArray());
        Assert.DoesNotContain(packets, packet => packet.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
        Assert.Equal(selected ? 5 : 3, packets.Count);
        var replies = packets.TakeLast(3).ToArray();
        Assert.All(replies, packet => Assert.EndsWith(".CallRsp", packet.Name));
        Assert.Null(Result(replies[0]));
        Assert.Null(Result(replies[1]));
        Assert.True(Assert.IsType<long>(Result(replies[2])) > 0);
        Assert.Equal(before, world.Store.FindPilot(1000001));
    }

    [Theory]
    [InlineData("LogPlayerRequestedDisconnect", true)]
    [InlineData("UnknownExitMethod", false)]
    public async Task ShutdownCompatibilityDoesNotAcceptMalformedOrUnknownCalls(string method, bool extraArgument)
    {
        var packet = Assert.Single(await ExchangeAsync("eventLog", method, extraArgument ? [90000001L] : [], 42));
        Assert.EndsWith(".ErrorResponse", packet.Name);
        var body = BlueRead.Tuple(BlueRead.Tuple(packet.State, 14)[4], 3);
        Assert.Equal(0, body[1]);
        Assert.Equal($"Unsupported local call eventLog.{method}", body[2]);
    }

    private sealed class RejectingHandler : ISelectedPilotCallHandler
    {
        public PilotCallResult Call(
            PilotCallContext context,
            string service,
            string method,
            object?[] args,
            Dictionary<string, object?>? keywords = null
        )
        {
            context.NotificationsBeforeFlight.Add(new AccountBalanceChanged(context.Pilot.PilotId, 1));
            context.Notifications.Add(new AccountBalanceChanged(context.Pilot.PilotId, 2));
            return new PilotCallResult.Failure("Rejected after notifications were queued.");
        }
    }

    [Fact]
    public async Task RejectedCallFlushesBothNotificationQueuesInOrder()
    {
        using TestWorld world = new();
        using var scope = world.Services.CreateScope();
        var pilot = world.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => new FlightSession(world.Store, selected, TimeProvider.System, new(), world.Objects),
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        List<object?> packets = [];
        PilotSessionOutput output = new(
            context,
            1000001,
            42,
            packet =>
            {
                packets.Add(packet);
                return Task.CompletedTask;
            },
            _ => { }
        );
        PilotSessionCalls calls = new(
            context,
            output,
            new PilotCallRouter([], [new RejectingHandler()]),
            scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>(),
            scope.ServiceProvider.GetRequiredService<SkillHandler>(),
            TimeProvider.System,
            _ => { },
            1000001,
            42
        );
        await calls.ProcessAsync(Request("test", "Reject", []));
        Assert.Equal(3, packets.Count);
        Assert.EndsWith(".ErrorResponse", Assert.IsType<BlueInstance>(packets[0]).Name);
        Assert.All(packets.Skip(1), packet => Assert.EndsWith(".Notification", Assert.IsType<BlueInstance>(packet).Name));
        Assert.Empty(context.NotificationsBeforeFlight);
        Assert.Empty(context.Notifications);
    }

    [Fact]
    public async Task FailedBoundMarketCheckReturnsAnErrorAndKeepsTheSessionUsable()
    {
        using TestWorld world = new();
        var packets = await ExchangeAsync(
            world,
            42,
            Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            Request(
                "corpStationMgr",
                "MachoBindObject",
                [
                    60003760L,
                    new object?[]
                    {
                        "DoStandingCheckForStationService",
                        new object?[] { (long)StationService.Market },
                        new Dictionary<string, object?>(),
                    },
                ]
            ),
            Request("account", "GetCashBalance", [0L])
        );
        Assert.EndsWith(".ErrorResponse", packets[^2].Name);
        AssertUserError(packets[^2], "CustomInfo", new() { ["info"] = "This station does not provide a market." });
        Assert.EndsWith(".CallRsp", packets[^1].Name);
        Assert.Equal(0d, Result(packets[^1]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task GameplayRejectionExplainsTheReasonAndKeepsTheModuleAndSessionIntact(bool bound, bool native)
    {
        using TestWorld world = new();
        const long ship = 1000000000001L;
        long module;
        using (WorldDbContext saved = new(world.Path))
        {
            var item = saved.Set<OwnedItem>().Single(i => i.OwnerId == 90000001 && i.TypeId == InventoryDefinitions.CivilianMinerType);
            module = item.Id;
            item.LocationId = ship;
            item.FlagId = (int)InventoryFlag.HighSlot0;
            item.Singleton = true;
            item.Online = true;
            saved.SaveChanges();
        }
        string method = native ? "Deactivate" : "TakeModuleOffline";
        object?[] args = native ? [module, InventoryWireDefinitions.OnlineEffectName] : [ship, module];
        var rejection = bound
            ? Request("dogmaIM", "MachoBindObject", [null, new object?[] { method, args, new Dictionary<string, object?>() }])
            : Request("dogmaIM", method, args);
        var packets = await ExchangeAsync(
            world,
            42,
            Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            Request("ship", "Undock", [ship]),
            rejection,
            Request("account", "GetCashBalance", [0L])
        );
        AssertUserError(packets[^2], "CustomInfo", new() { ["info"] = "Change module online state on your docked active ship." });
        Assert.EndsWith(".CallRsp", packets[^1].Name);
        Assert.Equal(0d, Result(packets[^1]));
        using WorldDbContext persisted = new(world.Path);
        var retained = persisted.Set<OwnedItem>().Find(module)!;
        Assert.Equal(ship, retained.LocationId);
        Assert.Equal((int)InventoryFlag.HighSlot0, retained.FlagId);
        Assert.True(retained.Online);
    }

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
        var packets = await ExchangeAsync(
            world,
            42,
            Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            Request("dogmaIM", "CreateNewbieShip", [1000000000001L, 60003760L])
        );
        Assert.Equal(
            new[]
            {
                "SessionChangeNotification",
                "CallRsp",
                "Notification",
                "Notification",
                "Notification",
                "Notification",
                "SessionChangeNotification",
                "CallRsp",
            },
            packets.Select(p => p.Name.Split('.')[^1])
        );
        var brainEnvelope = BlueRead.Tuple(packets[2].State, 14);
        var brainDestination = Assert.IsType<BlueInstance>(brainEnvelope[2]);
        Assert.Equal("OnServerBrainUpdated", BlueRead.Tuple(brainDestination.State, 4)[1]);
        foreach (var packet in packets.Skip(3).Take(3))
        {
            var envelope = BlueRead.Tuple(packet.State, 14);
            var destination = Assert.IsType<BlueInstance>(envelope[2]);
            Assert.Equal("OnItemChange", BlueRead.Tuple(destination.State, 4)[1]);
        }
        var sessionBody = BlueRead.Tuple(BlueRead.Tuple(packets[6].State, 14)[4], 3);
        var delta = BlueRead.Fields(BlueRead.Tuple(sessionBody[1], 2)[1]);
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
        var packets = await ExchangeAsync(
            world,
            42,
            Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            Request("invbroker", "GetInventoryFromId", [1000000000001L]),
            Request(null, "Add", [itemId, 60003760L], "N=10001:42-2", new() { ["flag"] = 5L })
        );
        var packet = packets[^1];
        AssertUserError(packet, "NotEnoughCargoSpace", new() { ["available"] = 125d, ["volume"] = 150d });
    }

    [Fact]
    public async Task ListsSameOwnedPilotOnFreshSessions()
    {
        foreach (long sessionId in new long[] { 41, 42 })
        {
            var packets = await ExchangeAsync("charUnboundMgr", "GetCharacterSelectionData", [], sessionId);
            var roster = BlueRead.Tuple(Result(packets.Single()), 4);
            var character = Assert.IsType<BlueInstance>(Assert.Single(Assert.IsType<List<object?>>(roster[2])));
            var fields = BlueRead.Fields(character.State);
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
        var envelope = BlueRead.Tuple(packets[0].State, 14);
        Assert.Equal(16, envelope[0]);
        var body = BlueRead.Tuple(envelope[4], 3);
        Assert.Equal(42L, body[0]);
        var delta = BlueRead.Tuple(body[1], 2);
        var changes = BlueRead.Fields(delta[1]);
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
        var packets = await ExchangeAsync(
            42,
            Request("charMgr", "GetPublicInfo", [90000001L]),
            Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            Request("charMgr", "GetPublicInfo", [90000002L]),
            Request("charMgr", "GetPublicInfo", [90000001L])
        );
        Assert.EndsWith(".ErrorResponse", packets[0].Name);
        Assert.EndsWith(".ErrorResponse", packets[3].Name);
        var fields = BlueRead.Fields(Assert.IsType<BlueInstance>(Result(packets[4])).State);
        Assert.Equal(90000001L, fields["characterID"]);
        Assert.Equal(19L, fields["schoolID"]);
        Assert.Equal(10L, fields["ancestryID"]);
        Assert.Equal(134000000000000000L, fields["createDateTime"]);
    }

    [Fact]
    public async Task UnknownMethodsReceiveAnError()
    {
        var packets = await ExchangeAsync("charUnboundMgr", "DeleteCharacter", [90000001], 42);
        var packet = Assert.Single(packets);
        Assert.EndsWith(".ErrorResponse", packet.Name);
        var body = BlueRead.Tuple(BlueRead.Tuple(packet.State, 14)[4], 3);
        Assert.Equal(0, body[1]);
        Assert.Equal("Unsupported local call charUnboundMgr.DeleteCharacter", body[2]);
    }

    [Fact]
    public async Task CreatorCanSelectItsNewCharacterByKeywordAndReadBackTheAvatar()
    {
        using TestWorld world = new();
        TestCatalog.AddSchool(new WorldDatabase(world.Path).CatalogPath, 22, 8, 7, 30005305, 60015161, 606);
        BlueInstance appearance = new(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["appearance"] = new PaperDollRow("eve.common.script.paperDoll.paperDollDefinitions.AppearanceRow", [1L]),
            }
        );
        BlueInstance portrait = new("utillib.KeyVal", new Dictionary<string, object?> { ["lightID"] = 15 });
        var replies = await ExchangeAsync(
            world,
            42,
            Request("charUnboundMgr", "CreateCharacterWithDoll", ["New Pilot", 8, 7, 1, 64, appearance, portrait, 22, null, null]),
            Request("charUnboundMgr", "SelectCharacterID", [], keywords: new() { ["charID"] = 90000003L, ["skipTutorial"] = true }),
            Request("paperDollServer", "GetPaperDollData", [90000003L]),
            Request("home_station", "get_home_station", [])
        );
        Assert.Equal(90000003L, Result(replies[0]));
        Assert.EndsWith(".SessionChangeNotification", replies[1].Name);
        Assert.Null(Result(replies[2]));
        Assert.Equal(BlueMarshal.Encode(appearance), BlueMarshal.Encode(Result(replies[3])));
        var home = BlueRead.Fields(Assert.IsType<BlueInstance>(Result(replies[4])).State);
        Assert.Equal(60015161L, home["id"]);
        Assert.Equal(30005305L, home["solar_system_id"]);
    }

    [Fact]
    public async Task HomeCloneTransferSendsInventoryAndStationSessionChangeBeforeItsSuccessfulReply()
    {
        using TestWorld world = new();
        Persistence.HomeStationTestData.Prepare(world);
        long previousShip = Persistence.HomeStationTestData.MakeActiveCapsule(world);
        var packets = await ExchangeAsync(
            world,
            42,
            Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            Request("home_station", "self_destruct_clone", []),
            Request("home_station", "get_home_station", [])
        );
        Assert.DoesNotContain(packets, packet => packet.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
        var transitions = packets.Where(packet => packet.Name.EndsWith(".SessionChangeNotification", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, transitions.Length);
        var body = BlueRead.Tuple(BlueRead.Tuple(transitions[1].State, 14)[4], 3);
        var delta = BlueRead.Fields(BlueRead.Tuple(body[1], 2)[1]);
        Assert.Equal(new object?[] { previousShip, world.Store.FindPilot(1000001)!.ShipId }, delta["shipid"]);
        Assert.Equal(new object?[] { Persistence.HomeStationTestData.Other, Persistence.HomeStationTestData.Origin }, delta["stationid"]);
        int transitionIndex = packets.IndexOf(transitions[1]);
        Assert.Equal(3, packets.Take(transitionIndex).Count(packet => packet.Name.EndsWith(".Notification", StringComparison.Ordinal)));
        Assert.EndsWith(".CallRsp", packets[transitionIndex + 1].Name);
        Assert.Null(Result(packets[transitionIndex + 1]));
        var home = BlueRead.Fields(Assert.IsType<BlueInstance>(Result(packets[^1])).State);
        Assert.Equal((long)Persistence.HomeStationTestData.Origin, home["id"]);
    }

    private static void AssertUserError(BlueInstance packet, string name, Dictionary<string, object?> values)
    {
        Assert.EndsWith(".ErrorResponse", packet.Name);
        var body = BlueRead.Tuple(BlueRead.Tuple(packet.State, 14)[4], 3);
        Assert.Equal(6, body[0]);
        Assert.Equal(2, body[1]);
        byte[] exception = BlueRead.Bytes(Assert.Single(BlueRead.Tuple(body[2], 1)));
        Assert.Equal(BlueMarshal.Encode(new BlueConstructor("exceptions.UserError", [name, values])), exception);
    }

    private static object? Result(BlueInstance packet)
    {
        var state = BlueRead.Tuple(packet.State, 14);
        return BlueMarshal.Decode(BlueRead.Bytes(BlueRead.Tuple(state[4], 1)[0]));
    }

    [Fact]
    public async Task RoutesBoundInventoryAndOrdersFlightNotifications()
    {
        const long ship = 1000000000001L;
        var packets = await ExchangeAsync(
            42,
            Request("ship", "Undock", [ship]),
            Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            Request("invbroker", "GetInventory", [10004L]),
            Request(null, "List", [4L], "N=10001:42-2"),
            Request("ship", "Undock", [ship]),
            Request("beyonce", "MachoBindObject", [30000142L, null])
        );
        Assert.Equal(
            new[]
            {
                "ErrorResponse",
                "SessionChangeNotification",
                "CallRsp",
                "CallRsp",
                "CallRsp",
                "SessionChangeNotification",
                "CallRsp",
                "CallRsp",
                "Notification",
            },
            packets.Select(packet => packet.Name.Split('.')[^1])
        );
        // DBRow uses a client-only constructor. Compare the actual bound List payload with the owned ship ID on the wire.
        var state = BlueRead.Tuple(packets[4].State, 14);
        var payload = BlueRead.Bytes(BlueRead.Tuple(state[4], 1)[0]);
        Assert.True(payload.AsSpan().IndexOf(BitConverter.GetBytes(ship)) >= 0);
        Assert.Null(Result(packets[6]));
        var notification = BlueRead.Tuple(packets[8].State, 14);
        var destination = Assert.IsType<BlueInstance>(notification[2]);
        Assert.Equal("DoDestinyUpdate", BlueRead.Tuple(destination.State, 4)[1]);
    }

    private static Task<List<BlueInstance>> ExchangeAsync(string service, string method, object?[] args, long sessionId) =>
        ExchangeAsync(sessionId, Request(service, method, args));

    private static BlueInstance Request(
        string? service,
        string method,
        object?[] args,
        object? objectId = null,
        Dictionary<string, object?>? keywords = null
    )
    {
        const string address = "carbon.common.script.net.machoNetAddress.MachoAddress";
        // ServiceCallGPCS adds this cache-version metadata even to uncached RPCs.
        Dictionary<string, object?> wireKeywords = keywords is null ? new() : new(keywords);
        wireKeywords.TryAdd("machoVersion", 1L);
        byte[] call = BlueMarshal.Encode(new object?[] { objectId, method, args, wireKeywords });
        return new(
            "carbon.common.script.net.machoNetPacket.CallReq",
            new object?[]
            {
                6L,
                new BlueInstance(address, new object?[] { 2L, 0L, 1L, null }),
                new BlueInstance(
                    address,
                    service is null ? new object?[] { 1L, (long)PilotLogin.NodeId, null, null } : new object?[] { 8L, service, null }
                ),
                null,
                new object?[] { new object?[] { 0L, call } },
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
            }
        );
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
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            session.RunAsync(
                () => input.TryDequeue(out object? packet) ? Task.FromResult(packet) : throw new EndOfStreamException(),
                packet =>
                {
                    output.Add(Assert.IsType<BlueInstance>(packet));
                    return Task.CompletedTask;
                },
                _ => { },
                1000001,
                sessionId,
                SignedSettings.Empty,
                CancellationToken.None
            )
        );
        return output;
    }
}
