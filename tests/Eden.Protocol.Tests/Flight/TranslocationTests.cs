using System.Net;
using System.Text.Json;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Chat;
using Eden.Protocol.Tests.Combat;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.Persistence;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server.Chat;
using Eden.Server.Launcher;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed class TranslocationTests
{
    private const int OtherSystem = 30000144;
    private const int SourceMoon = 40009087;
    private const int OtherGate = 50002185;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedNativeTranslocationsKeepChatConnectedUntilActualPilotLogoff(bool inSpace)
    {
        using Fixture f = new(inSpace: inSpace);
        f.Select();
        if (inSpace)
        {
            f.Context.Flight.Snapshot();
        }
        var runtime = f.Db.Services.GetRequiredService<ChatRuntime>();
        var certificate = f.Db.Services.GetRequiredService<ChatCertificate>();
        await using var server = f.Db.Services.GetRequiredService<ChatServer>();
        await server.StartAsync(new(IPAddress.Loopback, 0));
        var token = Assert.IsType<PilotCallResult.Success>(
            f.Router.Call(f.Context, "chatAuthenticationService", "GetAuthenticationToken", Array.Empty<object?>(), _ => { })
        );
        await using var client = await XmppChatTests.NativeClient.ConnectAsync(
            server.Endpoint!,
            certificate.Certificate,
            f.Context.Pilot.PilotId,
            BlueMarshal.Text(token.Reply.ToBlue())
        );
        string local = "local_" + f.Context.Pilot.SystemId;
        string corporation = "corp_" + f.Context.Pilot.CorpId;
        async Task JoinAsync(string room)
        {
            await client.JoinAsync(room, room);
            await client.UntilAsync(e => e.Name.LocalName == "message" && e.Elements().Any(child => child.Name.LocalName == "subject"));
        }
        async Task MessageAsync(string room, string id)
        {
            f.Clock.Seconds++;
            await client.MessageAsync(room, id, "Connection retained");
            var message = (await client.UntilAsync(e => (string?)e.Attribute("id") == id))[^1];
            Assert.True((string?)message.Attribute("type") == "groupchat", message.ToString());
            Assert.Equal("Connection retained", Assert.Single(message.Elements()).Value);
        }
        await JoinAsync(local);
        await JoinAsync(corporation);
        int cycle = 0;
        foreach (
            int target in new[] { SourceMoon, HomeStationTestData.Origin, OtherSystem, HomeStationTestData.School, OtherGate, SourceMoon }
        )
        {
            Assert.Equal(PilotCallResult.None, f.Call($"/tr me {target}"));
            await MessageAsync(corporation, "departure-" + cycle);
            if (f.Context.Flight.Translocating)
            {
                if (!f.Context.Flight.InBallpark)
                {
                    f.Context.Flight.Snapshot();
                }
                f.Clock.Seconds += FlightSession.TranslocationDurationTicks;
                await f.TickAsync([]);
                Assert.False(f.Context.Flight.Translocating);
            }
            string destination = "local_" + f.Context.Pilot.SystemId;
            if (local != destination)
            {
                await client.MessageAsync(local, "old-local-" + cycle, "No longer authorized");
                var rejected = (await client.UntilAsync(e => (string?)e.Attribute("id") == "old-local-" + cycle))[^1];
                Assert.Equal("error", (string?)rejected.Attribute("type"));
                local = destination;
                await JoinAsync(local);
            }
            await MessageAsync(local, "local-" + cycle);
            await MessageAsync(corporation, "arrival-" + cycle);
            if (f.Context.Pilot.Position is not null && !f.Context.Flight.InBallpark)
            {
                f.Context.Flight.Snapshot();
            }
            cycle++;
        }
        var logoff = Assert.IsType<PilotCallResult.Success>(
            f.Router.Call(f.Context, "userSvc", "UserLogOffCharacter", Array.Empty<object?>(), _ => { })
        );
        Assert.Equal(true, logoff.Reply.ToBlue());
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            while (true)
            {
                var departed = await client.ReadAsync();
                Assert.Equal("presence", departed.Name.LocalName);
                Assert.Equal("unavailable", (string?)departed.Attribute("type"));
            }
        });
        Assert.Null(runtime.User(HomeStationTestData.PilotId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranslocationIncludesDestinationNpcsInFirstStateBeforeCombat(bool stationOrigin)
    {
        using Fixture f = new(inSpace: !stationOrigin);
        PrepareBelts(f.Db);
        f.Select();
        if (!stationOrigin)
        {
            f.Context.Flight.Snapshot();
        }
        Assert.Equal(PilotCallResult.None, f.Call("/tr me 40009232"));
        if (!f.Context.Flight.InBallpark)
        {
            f.Context.Flight.Snapshot();
        }
        List<object?> output = [];
        await f.TickAsync(output);
        f.Clock.Seconds += FlightSession.TranslocationDurationTicks;
        await f.TickAsync(output);
        Assert.Equal(OtherSystem, f.Context.Pilot.SystemId);
        Assert.False(f.Context.Flight.InBallpark);
        Assert.Empty(f.Context.Flight.VisibleNpcs);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Empty(world.Set<NpcAggression>());
            Assert.Empty(world.Set<ShipLoss>());
        }
        f.Context.Flight.Snapshot();
        var state = Assert.IsType<DestinyCommand.SetState>(f.Context.Flight.Updates[0].Command);
        var npc = Assert.Single(f.Context.Flight.VisibleNpcs);
        var slim = Assert.Single(state.Snapshot.Slims.OfType<NpcSlimItem>());
        Assert.Equal(npc.Id, slim.Npc.Id);
        Assert.Equal(npc.TypeId, slim.Npc.TypeId);
        Assert.True(state.Snapshot.Damage.ContainsKey(npc.Id));
        Assert.NotEmpty(BlueMarshal.Encode(state.Snapshot));
        Assert.DoesNotContain(f.Context.Flight.Updates, u => u.Command is DestinyCommand.AddBalls);
        f.Clock.Seconds += FlightSession.ArrivalDurationTicks - 1;
        await f.TickAsync(output);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Empty(world.Set<NpcAggression>());
        }
        f.Clock.Seconds++;
        await f.TickAsync(output);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Contains(saved.Set<NpcAggression>(), a => a.NpcId == npc.Id);
        Assert.Empty(saved.Set<ShipLoss>());
    }

    [Fact]
    public async Task SameSystemBeltTransfersRemoveSourceNpcsAndAddDestinationNpcsOnTheBoundPark()
    {
        using Fixture f = new(inSpace: true);
        PrepareBelts(f.Db);
        f.Select();
        var flight = f.Context.Flight;
        flight.Snapshot();
        Assert.Equal(PilotCallResult.None, f.Call("/tr me 40009232"));
        f.Clock.Seconds += FlightSession.TranslocationDurationTicks;
        await f.TickAsync([]);
        flight.Snapshot();
        long previous = Assert.Single(flight.VisibleNpcs).Id;
        var binding = f.Context.Reference("beyonce", PilotBindingLifetime.Location);
        foreach (int belt in new[] { 40009245, 40009232, 40009245 })
        {
            flight.Updates.Clear();
            Assert.Equal(PilotCallResult.None, f.Call($"/tr me {belt}"));
            Assert.True(flight.InBallpark);
            Assert.Equal("beyonce", f.Context.ResolveReference(binding.Id));
            Assert.DoesNotContain(flight.Updates, u => u.Command is DestinyCommand.SetState);
            var removed = Assert.Single(flight.Updates, u => u.Command is DestinyCommand.RemoveBall { Id: var id } && id == previous);
            var added = Assert.Single(
                flight.Updates,
                u => u.Command is DestinyCommand.AddBalls a && a.Addition.Slims.OfType<NpcSlimItem>().Any()
            );
            Assert.True(flight.Updates.IndexOf(removed) < flight.Updates.IndexOf(added));
            var chunk = ((DestinyCommand.AddBalls)added.Command).Addition;
            var npc = Assert.Single(chunk.Slims.OfType<NpcSlimItem>()).Npc;
            Assert.NotEqual(previous, npc.Id);
            Assert.Equal(Assert.Single(flight.VisibleNpcs).Id, npc.Id);
            Assert.Contains(npc.Id, chunk.Damage.Keys);
            var wire = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(added.Command)), 2);
            Assert.Equal("AddBalls", BlueMarshal.Text(wire[0]));
            var addition = BlueRead.Tuple(BlueRead.Tuple(wire[1], 1)[0], 3);
            var slim = Assert.IsType<BlueInstance>(Assert.Single(Assert.IsType<List<object?>>(addition[1])));
            Assert.Equal(npc.Id, BlueRead.Fields(slim.State)["itemID"]);
            using BinaryReader ball = new(new MemoryStream(BlueRead.Bytes(addition[0])));
            Assert.Equal(0, ball.ReadByte());
            Assert.Equal(added.Stamp, ball.ReadInt32());
            Assert.Equal(npc.Id, ball.ReadInt64());
            flight.RefreshNpcs();
            Assert.Single(flight.Updates, u => u.Command is DestinyCommand.AddBalls a && a.Addition.Slims.OfType<NpcSlimItem>().Any());
            previous = npc.Id;
        }
        f.Clock.Seconds += FlightSession.ArrivalDurationTicks - 1;
        await f.TickAsync([]);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<NpcAggression>());
        f.Clock.Seconds++;
        await f.TickAsync([]);
        Assert.Contains(world.Set<NpcAggression>(), a => a.NpcId == previous);
    }

    private static void PrepareBelts(TestWorld db)
    {
        NpcEncounterTests.Prepare(db);
        using (Eden.Server.Catalog.CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            foreach (int belt in new[] { 40009232, 40009245 })
            {
                catalog.Add(
                    new Eden.Server.Catalog.SourceRecord
                    {
                        Dataset = "client/asteroidcountbytypeidbeltid",
                        Key = belt.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                        Data = "{}",
                    }
                );
            }
            catalog.SaveChanges();
        }
    }

    [Theory]
    [InlineData(OtherSystem, false)]
    [InlineData(OtherGate, false)]
    [InlineData(OtherSystem, true)]
    [InlineData(OtherGate, true)]
    public async Task UnboundAndStationOriginsLoadSourceSpaceBeforeStartingTheCrossSystemCameraHandoff(int target, bool inSpace)
    {
        using Fixture f = new(inSpace: inSpace);
        f.Select();
        var original = f.Context.Pilot;
        Assert.Equal(PilotCallResult.None, f.Call($"/tr me {target}"));
        Assert.NotNull(f.Context.Pilot.Position);
        Assert.Equal(original.SystemId, f.Context.Pilot.SystemId);
        Assert.True(f.Context.Flight.Translocating);
        Assert.False(f.Context.Flight.InBallpark);
        f.Clock.Seconds += 10;
        List<object?> packets = [];
        await f.TickAsync(packets);
        Assert.Equal(original.SystemId, f.Context.Pilot.SystemId);
        Assert.DoesNotContain(f.Context.Flight.Updates, u => u.Command is DestinyCommand.TranslocationOut);
        f.Context.Flight.Snapshot();
        Assert.IsType<DestinyCommand.SetState>(f.Context.Flight.Updates[0].Command);
        Assert.Single(f.Context.Flight.Updates, u => u.Command is DestinyCommand.TranslocationOut);
        await f.TickAsync(packets);
        f.Clock.Seconds += FlightSession.TranslocationDurationTicks;
        await f.TickAsync(packets);
        Assert.Equal(OtherSystem, f.Context.Pilot.SystemId);
        Assert.False(f.Context.Flight.InBallpark);
        f.Context.Flight.Snapshot();
        Assert.IsType<DestinyCommand.SetState>(f.Context.Flight.Updates[0].Command);
        Assert.Contains(f.Context.Flight.Updates, u => u.Command is DestinyCommand.SpecialEffect { Graphic: "effects.JumpDriveIn" });
        Assert.Equal(original.ShipId, f.Context.Pilot.ShipId);
        Assert.Equal(1, TaskTrackingTestData.Count(f.Db, TaskEvents.Undocked));
        Assert.Equal(0, TaskTrackingTestData.Count(f.Db, TaskEvents.Jumped));
    }

    [Fact]
    public void DisconnectBeforeTransferKeepsTheSourceLocation()
    {
        using Fixture f = new(inSpace: true);
        f.Select();
        f.Context.Flight.Snapshot();
        var source = f.Context.Pilot;
        var flight = f.Context.Flight;
        Assert.Equal(PilotCallResult.None, f.Call($"/tr me {OtherSystem}"));
        f.Context.LogOff();
        f.Clock.Seconds += FlightSession.TranslocationDurationTicks + 1;
        Assert.Null(flight.CompleteTranslocation());
        Assert.Equal(source, f.Db.Store.FindPilot(1000001));
    }

    [Fact]
    public async Task SpaceTransferStartsNativeCameraHandoffBeforeSessionChangeAndFinishesWithFreshStateAndArrival()
    {
        using Fixture f = new(inSpace: true);
        f.Select();
        f.Context.Flight.Snapshot();
        f.Context.Flight.Updates.Clear();
        int source = f.Context.Pilot.SystemId;
        var gate = f.Db.Objects.Single(body => body.DestinationGateId.HasValue);
        Assert.Equal(PilotCallResult.None, f.Context.Flight.Execute(new FlightCommand.WarpTo(gate.Id, 0)));
        Assert.Equal(PilotCallResult.None, f.Call($"/tr me {OtherSystem}"));
        Assert.True(f.Context.Flight.Translocating);
        Assert.False(f.Context.Flight.Moving);
        Assert.False(f.Context.Flight.CanTarget);
        Assert.False(f.Context.Flight.CanUseSelfModules);
        Assert.Equal(source, f.Db.Store.FindPilot(1000001)!.SystemId);
        Assert.IsType<PilotCallResult.Failure>(f.Call($"/tr me {HomeStationTestData.School}"));
        var departure = Assert.Single(f.Context.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.TranslocationOut>());
        var fields = BlueRead.Tuple(BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(departure)), 2)[1], 14);
        Assert.Equal("effects.JumpDriveOut", BlueMarshal.Text(fields[5]));
        Assert.Null(fields[3]);
        Assert.Equal(new object?[] { (long)OtherSystem }, fields[13]);
        List<object?> packets = [];
        await f.TickAsync(packets);
        Assert.DoesNotContain(packets.Cast<BlueInstance>(), p => p.Name.EndsWith(".SessionChangeNotification", StringComparison.Ordinal));
        Assert.NotEmpty(packets);
        f.Clock.Seconds += FlightSession.TranslocationDurationTicks - 1;
        await f.TickAsync(packets);
        Assert.Equal(source, f.Context.Pilot.SystemId);
        f.Clock.Seconds++;
        int beforeTransfer = packets.Count;
        await f.TickAsync(packets);
        var transition = Assert.Single(
            packets.Cast<BlueInstance>(),
            p => p.Name.EndsWith(".SessionChangeNotification", StringComparison.Ordinal)
        );
        Assert.True(packets.IndexOf(transition) >= beforeTransfer);
        Assert.Equal(OtherSystem, f.Context.Pilot.SystemId);
        Assert.False(f.Context.Flight.Translocating);
        Assert.False(f.Context.Flight.InBallpark);
        f.Context.Flight.Snapshot();
        Assert.IsType<DestinyCommand.SetState>(f.Context.Flight.Updates[0].Command);
        var arrival = Assert.Single(f.Context.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.SpecialEffect>());
        Assert.Equal("effects.JumpDriveIn", arrival.Graphic);
        f.Context.Flight.Updates.Clear();
        f.Context.Flight.Snapshot();
        Assert.DoesNotContain(f.Context.Flight.Updates, u => u.Command is DestinyCommand.SpecialEffect { Graphic: "effects.JumpDriveIn" });
        packets.Clear();
        f.Clock.Seconds += 10;
        await f.TickAsync(packets);
        Assert.DoesNotContain(packets.Cast<BlueInstance>(), p => p.Name.EndsWith(".SessionChangeNotification", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(OtherSystem, false)]
    [InlineData(OtherSystem, true)]
    [InlineData(HomeStationTestData.School, false)]
    [InlineData(HomeStationTestData.School, true)]
    [InlineData(SourceMoon, false)]
    [InlineData(OtherGate, true)]
    public void CatalogDestinationsMoveTheSameHullAndPreserveContentsWalletHomeAndRestartState(int targetId, bool inSpace)
    {
        using Fixture f = new(inSpace: inSpace);
        var previous = f.Db.Store.FindPilot(1000001)!;
        var previousItem = f.Db.Services.GetRequiredService<IPilotFleet>().Find(previous.PilotId, previous.ShipId)!;
        var peer = f.Db.Store.FindPilot(1000002)!;
        string items = Items(f.Db);
        var home = f.Db.Services.GetRequiredService<IPilotHomeStation>().Read(previous.PilotId);
        var travel = f.Db.Services.GetRequiredService<IPilotTranslocation>();
        var target = travel.Resolve(targetId);
        var result = travel.Move(previous.PilotId, previous.ShipId, targetId);
        var moved = result.Pilot;
        Assert.Equal(previous.ShipId, moved.ShipId);
        Assert.Equal(previous.ShipTypeId, moved.ShipTypeId);
        Assert.Equal(previous.ShipName, moved.ShipName);
        Assert.Equal(target.System.Id, moved.SystemId);
        Assert.Equal(previousItem, result.ShipChange.Previous);
        Assert.Equal(moved.LocationId, result.ShipChange.Item.LocationId);
        if (target.StationId is int station)
        {
            Assert.Null(moved.Position);
            Assert.Equal(station, moved.StationId);
            Assert.Equal((int)InventoryFlag.Hangar, result.ShipChange.Item.FlagId);
        }
        else
        {
            Assert.NotNull(moved.Position);
            foreach (var obstacle in f.Db.Services.GetRequiredService<ISystemObjects>().Load(moved.SystemId))
            {
                Assert.True(moved.Position.DistanceTo(obstacle.Position) >= obstacle.Radius + moved.Flight.ShipRadius);
            }
            Assert.Equal((int)InventoryFlag.AutoFit, result.ShipChange.Item.FlagId);
        }
        Assert.Equal(items, Items(f.Db));
        Assert.Equal(peer, f.Db.Store.FindPilot(1000002));
        Assert.Equal(home, f.Db.Services.GetRequiredService<IPilotHomeStation>().Read(previous.PilotId));
        Assert.Equal(50_000_000, f.Db.Services.GetRequiredService<IPilotWalletReader>().Balance(previous.PilotId));
        Assert.Equal(moved, WorldInitialization.Open(f.Db.Path).FindPilot(1000001));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidAndForeignMovesAndLateDatabaseFailureLeaveSavedStateUnchanged(bool stationTarget)
    {
        using Fixture f = new(inSpace: stationTarget);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        string items = Items(f.Db);
        var travel = f.Db.Services.GetRequiredService<IPilotTranslocation>();
        foreach (int target in new[] { 0, -1, int.MaxValue, (int)pilot.PilotId })
        {
            Assert.Throws<InvalidDataException>(() => travel.Move(pilot.PilotId, pilot.ShipId, target));
        }
        Assert.Throws<InvalidDataException>(() => travel.Move(pilot.PilotId, f.Db.Store.FindPilot(1000002)!.ShipId, OtherSystem));
        Assert.Throws<InvalidDataException>(() => travel.Move(long.MaxValue, pilot.ShipId, OtherSystem));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                stationTarget
                    ? "CREATE TRIGGER RejectTranslocation BEFORE UPDATE ON WorldShip BEGIN SELECT RAISE(ABORT, 'forced failure'); END"
                    : "CREATE TRIGGER RejectTranslocation BEFORE INSERT ON WorldPosition BEGIN SELECT RAISE(ABORT, 'forced failure'); END"
            );
        }
        Assert.Throws<DbUpdateException>(() =>
            travel.Move(pilot.PilotId, pilot.ShipId, stationTarget ? HomeStationTestData.School : OtherSystem)
        );
        Assert.Equal(pilot, f.Db.Store.FindPilot(1000001));
        Assert.Equal(items, Items(f.Db));
        Assert.Equal(50_000_000, f.Db.Services.GetRequiredService<IPilotWalletReader>().Balance(pilot.PilotId));
    }

    [Theory]
    [InlineData(PilotRole.Player, false)]
    [InlineData(PilotRole.QualityAssurance, false)]
    [InlineData(PilotRole.Developer, true)]
    [InlineData(PilotRole.WorldMod, true)]
    [InlineData(PilotRole.GameMasterLow, true)]
    public void NativeRoleGateAndPilotSelectionControlAccess(PilotRole roles, bool allowed)
    {
        using Fixture f = new(roles);
        Assert.Same(PilotCallResult.Unsupported, f.Call($"/tr me {OtherSystem}"));
        f.Select();
        var before = f.Context.Pilot;
        var result = f.Call($"/tr me {OtherSystem}");
        if (allowed)
        {
            Assert.Equal(PilotCallResult.None, result);
            f.Context.Flight.Snapshot();
            f.Clock.Seconds += FlightSession.TranslocationDurationTicks;
            Assert.NotNull(f.Context.Flight.CompleteTranslocation());
            Assert.Equal(OtherSystem, f.Context.Pilot.SystemId);
        }
        else
        {
            Assert.IsType<PilotCallResult.Failure>(result);
            Assert.Equal(before, f.Context.Pilot);
            Assert.Equal(before, f.Db.Store.FindPilot(1000001));
        }
    }

    [Fact]
    public void InvalidCommandsCannotMoveOtherPilotsOrCancelAnActiveCourse()
    {
        using Fixture f = new(inSpace: true);
        f.Select();
        f.Context.Flight.Snapshot();
        Assert.Equal(PilotCallResult.None, f.Context.Flight.Execute(new FlightCommand.Heading(1, 0, 0)));
        var before = f.Context.Pilot;
        var binding = f.Context.Reference("invbroker", PilotBindingLifetime.Location);
        foreach (
            string command in new[]
            {
                "",
                "/tr",
                "/tr me",
                "/tr me -1",
                "/tr me 0",
                "/tr me 2147483648",
                "/tr me 1e7",
                "/tr me 99999999",
                "/tr 90000002 30000144",
                "/tr me 90000002",
                "/tr me pos=1,2,3",
                "/tr me 30000144 noblock",
                "/heal me",
                new string('x', 129),
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(f.Call(command));
        }
        Assert.IsType<PilotCallResult.Failure>(f.Call($"/tr me {OtherSystem}", new() { ["role"] = (long)PilotRole.Developer }));
        foreach (object?[] args in new object?[][] { [], [1L], [null], [true], ["/tr me 30000144", 90000002L] })
        {
            Assert.IsType<PilotCallResult.Failure>(f.Router.Call(f.Context, "slash", "SlashCmd", args, _ => { }));
        }
        Assert.Equal(before, f.Context.Pilot);
        Assert.True(f.Context.Flight.Moving);
        Assert.Equal("invbroker", f.Context.ResolveReference(binding.Id));
        Assert.Equal(before, f.Db.Store.FindPilot(1000001));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedMovesResetNavigationRetainOnlyCurrentLocationBindingsAndUpdateObservers(bool acrossSystems)
    {
        using Fixture f = new(inSpace: true);
        f.Db.Store.SetPosition(1000000000002, new(2_000_000, 20_000, 0));
        using var peerScope = f.Db.Services.CreateScope();
        var peer = f.Db.Store.FindPilot(1000002)!;
        using var observer = peerScope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create(peer);
        f.Select();
        f.Context.Flight.Snapshot();
        observer.Snapshot();
        observer.RefreshPeers();
        for (int cycle = 0; cycle < 3; cycle++)
        {
            Assert.Equal(PilotCallResult.None, f.Context.Flight.Execute(new FlightCommand.Heading(1, 0, 0)));
            var binding = f.Context.Reference("invbroker", PilotBindingLifetime.Location);
            int target = acrossSystems && cycle % 2 == 0 ? OtherGate : SourceMoon;
            Assert.Equal(PilotCallResult.None, f.Call($" /TR  ME  {target} ", new() { ["machoVersion"] = 1L }));
            if (f.Context.Flight.Translocating)
            {
                f.Clock.Seconds += FlightSession.TranslocationDurationTicks;
                await f.TickAsync([]);
            }
            Assert.Equal(acrossSystems ? null : "invbroker", f.Context.ResolveReference(binding.Id));
            Assert.False(f.Context.Flight.Moving);
            Assert.False(f.Context.Flight.InWarp);
            var moved = f.Context.Pilot;
            if (!f.Context.Flight.InBallpark)
            {
                // A cross-system move waits for the client's new ballpark bind.
                f.Context.Flight.Snapshot();
            }
            f.Clock.Seconds += 5;
            f.Context.Flight.Checkpoint();
            Assert.Equal(moved.Position, f.Context.Pilot.Position);
            Assert.Equal(moved, f.Db.Store.FindPilot(1000001));
            observer.Updates.Clear();
            observer.RefreshPeers();
            if (moved.SystemId == peer.SystemId)
            {
                Assert.Contains(observer.VisiblePilots(), p => p.ShipId == moved.ShipId && p.Position == moved.Position);
                if (!acrossSystems)
                {
                    Assert.Contains(observer.Updates, u => u.Command is DestinyCommand.SetBallPosition setter && setter.Id == moved.ShipId);
                    Assert.Contains(f.Context.Flight.Updates, u => u.Command is DestinyCommand.SetBallPosition);
                }
            }
            else
            {
                Assert.DoesNotContain(observer.VisiblePilots(), p => p.ShipId == moved.ShipId);
                Assert.Contains(observer.Updates, u => u.Command is DestinyCommand.RemoveBall removed && removed.Id == moved.ShipId);
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NativeMenuPacketUsesAuthenticatedRolesAndAnnouncesDockingOrSourceUndockBeforeTheReply(
        bool developer,
        bool stationTarget
    )
    {
        using Fixture f = new();
        int targetId = stationTarget ? HomeStationTestData.School : OtherSystem;
        int systemId = stationTarget ? HomeStationTestData.System : OtherSystem;
        f.Db.Services.GetRequiredService<LauncherStore>().Initialize();
        using (WorldDbContext world = new(f.Db.Path))
        {
            long roles = (long)(developer ? PilotRole.Developer : PilotRole.Player);
            world.Database.ExecuteSqlInterpolated(
                $"""
                INSERT INTO LauncherCredential(AccountId,NormalizedName,PasswordHash,Roles)
                VALUES (1000001,'TEST','unused',{roles})
                """
            );
        }
        Queue<object?> input = new(
            new[]
            {
                PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [HomeStationTestData.PilotId]),
                PilotServicesTests.Request("slash", "SlashCmd", [$"/tr me {targetId}"]),
            }.Select(packet => BlueMarshal.Decode(BlueMarshal.Encode(packet)))
        );
        List<BlueInstance> output = [];
        using var scope = f.Db.Services.CreateScope();
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            scope
                .ServiceProvider.GetRequiredService<IPilotSession>()
                .RunAsync(
                    () => input.TryDequeue(out var packet) ? Task.FromResult(packet) : throw new EndOfStreamException(),
                    packet =>
                    {
                        output.Add(Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(packet))));
                        return Task.CompletedTask;
                    },
                    _ => { },
                    1000001,
                    42,
                    SignedSettings.Empty,
                    CancellationToken.None
                )
        );
        var transitions = output.Where(p => p.Name.EndsWith(".SessionChangeNotification", StringComparison.Ordinal)).ToArray();
        Assert.Equal(developer ? 2 : 1, transitions.Length);
        if (developer)
        {
            Assert.DoesNotContain(output, p => p.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
            var body = BlueRead.Tuple(BlueRead.Tuple(transitions[1].State, 14)[4], 3);
            var delta = BlueRead.Fields(BlueRead.Tuple(body[1], 2)[1]);
            if (stationTarget)
            {
                Assert.Equal(new object?[] { 30000142L, (long)systemId }, delta["solarsystemid2"]);
            }
            else
            {
                Assert.Equal(new object?[] { null, 30000142L }, delta["solarsystemid"]);
                Assert.DoesNotContain("solarsystemid2", delta.Keys);
            }
            Assert.Equal(new object?[] { (long)HomeStationTestData.Origin, stationTarget ? (long)targetId : null }, delta["stationid"]);
            int index = output.IndexOf(transitions[1]);
            Assert.EndsWith(".CallRsp", output[index + 1].Name);
            var response = BlueRead.Tuple(BlueRead.Tuple(output[index + 1].State, 14)[4], 1);
            Assert.Null(BlueMarshal.Decode(BlueRead.Bytes(response[0])));
            // Closing this simulated connection before its source bind cancels
            // a queued space transfer; the real timer/state sequence is covered above.
            Assert.Equal(stationTarget ? systemId : 30000142, f.Db.Store.FindPilot(1000001)!.SystemId);
        }
        else
        {
            Assert.Single(output, p => p.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
            Assert.Null(f.Db.Store.FindPilot(1000001)!.Position);
        }
    }

    [Fact]
    public void ArrivalClearsOverlappingBodiesAndRejectsNonfiniteCoordinates()
    {
        TranslocationTarget target = new(1, new(1, 2, 3), null, SpacePosition.Zero, 0);
        StaticSpaceObject[] obstacles = [new(1, 1, 1, 1, 1, new(15_000, 0, 0), 10_000), new(2, 1, 1, 1, 1, new(30_000, 0, 0), 30_000)];
        var point = TranslocationRules.Arrival(target, 100, obstacles);
        Assert.All(
            obstacles,
            body => Assert.True(point.DistanceTo(body.Position) >= body.Radius + 100 + TranslocationRules.ArrivalClearance)
        );
        Assert.Throws<InvalidDataException>(() => TranslocationRules.Arrival(target with { Position = new(double.NaN, 0, 0) }, 100, []));
        Assert.Throws<InvalidDataException>(() => TranslocationRules.Arrival(target, double.PositiveInfinity, []));
    }

    private static string Items(TestWorld db)
    {
        using WorldDbContext world = new(db.Path);
        return JsonSerializer.Serialize(world.Set<OwnedItem>().OrderBy(i => i.Id).ToArray());
    }

    private sealed class Fixture : IDisposable
    {
        public TestClock Clock { get; } = new();
        public TestWorld Db { get; }
        public PilotCallContext Context { get; }
        private readonly IServiceScope _scope;
        public PilotCallRouter Router => _scope.ServiceProvider.GetRequiredService<PilotCallRouter>();

        public Fixture(PilotRole roles = PilotRole.Developer, bool inSpace = false)
        {
            Db = new(clock: Clock);
            HomeStationTestData.Prepare(Db);
            _scope = Db.Services.CreateScope();
            var pilot = Db.Store.FindPilot(1000001)!;
            if (inSpace)
            {
                Db.Store.SetPosition(pilot.ShipId, new(2_000_000, 0, 0));
                pilot = Db.Store.FindPilot(1000001)!;
            }
            Context = new(
                1000001,
                pilot.Account,
                [pilot],
                _scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
                42,
                SignedSettings.Empty
            )
            {
                Roles = (long)roles,
            };
        }

        public void Select()
        {
            Context.Select(Db.Store.FindPilot(1000001)!);
            _scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>().Enter(Context);
        }

        public PilotCallResult Call(string command, Dictionary<string, object?>? keywords = null) =>
            Router.Call(
                Context,
                "slash",
                "SlashCmd",
                BlueMarshal.Decode(BlueMarshal.Encode(new object?[] { command })),
                _ => { },
                keywords
            );

        public Task TickAsync(List<object?> output)
        {
            PilotSessionOutput writer = new(
                Context,
                1000001,
                42,
                packet =>
                {
                    output.Add(packet);
                    return Task.CompletedTask;
                },
                _ => { }
            );
            PilotSessionTick tick = new(
                Clock,
                _scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>(),
                _scope.ServiceProvider.GetRequiredService<SkillHandler>(),
                _scope.ServiceProvider.GetRequiredService<IAsteroidField>()
            );
            return tick.AdvanceAsync(Context, writer, _ => { }, 1000001, 42);
        }

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
            Db.Dispose();
        }
    }
}
