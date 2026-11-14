using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Bookmarks;
using Eden.Gameplay.Chat;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Loot;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Chat;
using Eden.Protocol.Fleets;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Ammunition;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CooperativeFleetTests
{
    [Fact]
    public void NativeCreateInviteAcceptAndLeaveKeepReferencesRosterAndSessionFieldsConsistent()
    {
        using TestWorld world = new();
        using FleetClient leader = new(world, 1000001);
        using FleetClient member = new(world, 1000002);
        long id = leader.Create();
        Assert.Equal(id, leader.Context.Pilot.Identity.Fleet!.FleetId);
        Assert.NotNull(leader.Context.ResolveReference(leader.Reference!.Id));
        Assert.IsType<PilotCallResult.Failure>(leader.CallService("@fleet:invalid", "GetInitState", []));
        Assert.Null(leader.Reply("Init", [(long)leader.Context.Pilot.ShipTypeId, null], new() { ["adInfoData"] = null }));
        var initial = BlueRead.Fields(Assert.IsType<BlueInstance>(leader.Reply("GetInitState", [])).State);
        Assert.Equal(id, initial["fleetID"]);
        var members = Assert.IsType<Dictionary<long, object?>>(initial["members"]);
        var boss = BlueRead.Fields(Assert.IsType<BlueInstance>(members[leader.PilotId]).State);
        Assert.Equal((long)FleetRole.Commander, boss["role"]);
        Assert.Equal((long)FleetRules.BossJob, boss["job"]);
        Assert.Null(leader.Reply("Invite", [member.PilotId, null, null, (long)FleetRole.Member]));
        member.Advance();
        var invite = Assert.Single(member.Context.Notifications.OfType<FleetInviteNotification>());
        Assert.Equal(id, invite.FleetId);
        Assert.Equal(leader.PilotId, invite.Inviter);
        Assert.Equal("AskJoinFleet", invite.Arguments[2]);
        Assert.NotEmpty(BlueMarshal.Encode(invite.Arguments));
        member.Bind(id);
        Assert.IsType<PilotCallResult.Failure>(member.CallBound(id, "GetInitState", []));
        Assert.Null(member.Reply("AcceptInvite", [(long)member.Context.Pilot.ShipTypeId]));
        leader.Advance();
        Assert.Equal(id, member.Context.Pilot.Identity.Fleet!.FleetId);
        Assert.NotNull(member.Context.ResolveReference(member.Reference!.Id));
        var state = BlueRead.Fields(Assert.IsType<BlueInstance>(member.Reply("GetInitState", [])).State);
        Assert.Equal(2, Assert.IsType<Dictionary<long, object?>>(state["members"]).Count);
        Assert.Contains(leader.Context.Notifications.OfType<FleetJoinNotification>(), joined => joined.Member.Pilot.Id == member.PilotId);
        var changes = PilotSessionWire.SessionChange(1000002, 1000002, member.Context.Pilot);
        Assert.NotEmpty(BlueMarshal.Encode(changes));
        Assert.Null(member.Reply("LeaveFleet", []));
        Assert.Null(member.Context.Pilot.Identity.Fleet);
        Assert.Null(member.Context.ResolveReference(member.Reference.Id));
        Assert.IsType<PilotCallResult.Failure>(member.CallBound(id, "GetInitState", []));
        Assert.Single(world.Services.GetRequiredService<ICooperativeFleets>().Read(leader.PilotId, id).Members);
    }

    [Fact]
    public void NativeHierarchyRolesOptionsAndBroadcastsReachOnlyAuthorizedMembers()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock);
        using FleetClient leader = new(world, 1000001);
        using FleetClient member = new(world, 1000002);
        long id = Join(leader, member);
        var state = world.Services.GetRequiredService<ICooperativeFleets>().Read(leader.PilotId, id);
        var wing = state.Wings[0];
        Assert.IsType<PilotCallResult.Failure>(member.CallBound(id, "MakeLeader", [member.PilotId]));
        var before = member.Context.Pilot;
        Assert.Equal(false, leader.Reply("MoveMember", [member.PilotId, wing.Id, wing.Squads[0].Id, (long)FleetRole.SquadCommander]));
        member.Advance();
        Assert.Equal(FleetRole.SquadCommander, member.Context.Pilot.Identity.Fleet!.Role);
        var session = SessionDelta(member.Context, before);
        Assert.Equal(new object?[] { (long)FleetRole.Member, (long)FleetRole.SquadCommander }, session["fleetrole"]);
        Assert.Contains(
            member.Context.Notifications.OfType<FleetMemberNotification>(),
            n => n.After.Placement.Role == FleetRole.SquadCommander
        );
        var options = new BlueInstance(
            "util.KeyVal",
            new Dictionary<string, object?>
            {
                ["isFreeMove"] = true,
                ["isRegistered"] = false,
                ["autoJoinSquadID"] = null,
            }
        );
        Assert.Null(leader.Reply("SetOptions", [options]));
        Assert.Null(leader.Reply("SetMotdEx", ["Mining operation"]));
        Assert.Equal("Mining operation", BlueMarshal.Text(member.Reply("GetMotd", [])));
        Assert.Null(leader.Reply("ChangeWingName", [wing.Id, "Mining"]));
        member.Advance();
        Assert.Contains(member.Context.Notifications.OfType<FleetHierarchyNotification>(), n => n.Change.Name == "Mining");
        Assert.IsType<PilotCallResult.Success>(member.CallService("ship", "Undock", [member.Context.Pilot.ShipId]));
        member.Context.Flight.Snapshot();
        Assert.Null(member.Reply("SendBroadcast", ["TravelTo", (long)FleetBroadcastScope.All, 30000144L, null]));
        leader.Advance();
        var broadcast = Assert.Single(leader.Context.Notifications.OfType<FleetBroadcastNotification>());
        var fields = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(broadcast.Arguments)), 6);
        Assert.Equal("TravelTo", BlueMarshal.Text(fields[0]));
        Assert.Equal(member.PilotId, fields[2]);
        Assert.Equal(30000144L, fields[4]);
        Assert.IsType<PilotCallResult.Failure>(member.CallBound(id, "SendBroadcast", ["TravelTo", 3L, 30000144L, null]));
        clock.AdvanceWallTime(FleetRules.BroadcastInterval);
        Assert.IsType<PilotCallResult.Failure>(member.CallBound(id, "SendBroadcast", ["0", 3L, null, null]));
        Assert.Null(leader.Reply("MakeLeader", [member.PilotId]));
        member.Advance();
        Assert.Equal(FleetRules.BossJob, member.Context.Pilot.Identity.Fleet!.Job);
        Assert.Null(member.Reply("DisbandFleet", []));
        leader.Advance();
        Assert.Null(leader.Context.Pilot.Identity.Fleet);
        Assert.Null(member.Context.Pilot.Identity.Fleet);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativePointFleetWarpAndPrivateBookmarkWarpUseOrdinaryFlightAndRespectOptOut(bool bookmark)
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock);
        using FleetClient leader = new(world, 1000001);
        using FleetClient member = new(world, 1000002);
        Join(leader, member);
        foreach (var pilot in new[] { leader, member })
        {
            Assert.IsType<PilotCallResult.Success>(pilot.CallService("ship", "Undock", [pilot.Context.Pilot.ShipId]));
            pilot.Context.Flight.Snapshot();
            pilot.Advance();
        }
        var target = world.Objects.First(o => o.Position.DistanceTo(leader.Context.Pilot.Position!) > 1000000);
        object?[] args = ["item", target.Id];
        if (bookmark)
        {
            var bookmarks = world.Services.GetRequiredService<IPilotBookmarks>();
            var folder = bookmarks.AddFolder(leader.PilotId, "Operation", "Fleet rendezvous");
            var saved = bookmarks.Create(
                leader.PilotId,
                new(null, target.TypeId, leader.Context.Pilot.SystemId, target.Position),
                folder.Id,
                null,
                new("Private rendezvous", ""),
                BookmarkExpiry.Never
            );
            args = ["bookmark", saved.Id];
        }
        Dictionary<string, object?> settings = new()
        {
            ["fleet"] = true,
            ["minRange"] = 0L,
            ["fleetFormationSettings"] = new Dictionary<string, object?>
            {
                ["formationType"] = 0L,
                ["formationSpacing"] = 1000L,
                ["formationSize"] = 10000L,
            },
        };
        Assert.Null(member.Reply("SetTakesFleetWarp", [false]));
        Assert.IsType<PilotCallResult.Success>(leader.CallService("beyonce", "CmdWarpToStuff", args, settings));
        member.Advance();
        var space = world.Services.GetRequiredService<SharedSpace>();
        Assert.Null(space.Peers(leader.Context.Pilot).Single(p => p.Pilot.PilotId == member.PilotId).AlignTo);
        Assert.IsType<PilotCallResult.Success>(leader.CallService("beyonce", "CmdStop", []));
        Assert.Null(member.Reply("SetTakesFleetWarp", [true]));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("beyonce", "CmdWarpToStuff", args, settings));
        Assert.IsType<PilotCallResult.Success>(leader.CallService("beyonce", "CmdWarpToStuff", args, settings));
        member.Advance();
        var aligned = space.Peers(leader.Context.Pilot).Single(p => p.Pilot.PilotId == member.PilotId);
        Assert.NotNull(aligned.AlignTo);
        Assert.NotNull(leader.Context.Pilot.Identity.Fleet);
        Assert.NotNull(member.Context.Pilot.Identity.Fleet);
    }

    [Fact]
    public void PendingWarpDoesNotOverrideNewerPersonalNavigationOrSurviveMembershipRevocation()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock);
        using FleetClient leader = new(world, 1000001);
        using FleetClient member = new(world, 1000002);
        long id = Join(leader, member);
        foreach (var pilot in new[] { leader, member })
        {
            pilot.CallService("ship", "Undock", [pilot.Context.Pilot.ShipId]);
            pilot.Context.Flight.Snapshot();
            pilot.Advance();
        }
        var target = world.Objects.First(o => o.Position.DistanceTo(leader.Context.Pilot.Position!) > 1000000);
        var fleets = world.Services.GetRequiredService<ICooperativeFleets>();
        fleets.Warp(leader.PilotId, id, target.Id, 0, null);
        Assert.IsType<PilotCallResult.Success>(member.CallService("beyonce", "CmdStop", []));
        var space = world.Services.GetRequiredService<SharedSpace>();
        Assert.Null(space.Peers(leader.Context.Pilot).Single(p => p.Pilot.PilotId == member.PilotId).AlignTo);
        fleets.Warp(leader.PilotId, id, target.Id, 0, null);
        Assert.IsType<PilotCallResult.Success>(member.CallService("beyonce", "UpdateStateRequest", []));
        Assert.NotNull(space.Peers(leader.Context.Pilot).Single(p => p.Pilot.PilotId == member.PilotId).AlignTo);
        member.CallService("beyonce", "CmdStop", []);
        fleets.Warp(leader.PilotId, id, target.Id, 0, null);
        fleets.Kick(leader.PilotId, id, member.PilotId);
        member.Advance();
        Assert.Null(member.Context.Pilot.Identity.Fleet);
        Assert.Null(space.Peers(leader.Context.Pilot).Single(p => p.Pilot.PilotId == member.PilotId).AlignTo);
    }

    [Fact]
    public void FleetChatRevokesSendingAndReceivingImmediatelyAfterKickBeforeTheOtherSessionPolls()
    {
        using TestWorld world = new();
        using FleetClient leader = new(world, 1000001);
        using FleetClient member = new(world, 1000002);
        long id = Join(leader, member);
        var chat = world.Services.GetRequiredService<IXmppChat>();
        var first = chat.Authenticate(leader.PilotId.ToString(), leader.Token());
        var second = chat.Authenticate(member.PilotId.ToString(), member.Token());
        ChatRoom room = new(ChatRoomKind.Fleet, id);
        Assert.Equal($"fleet_{id}", XmppWire.Channel(room));
        Assert.Equal(room, XmppWire.ParseChannel($"fleet_{id}"));
        chat.Join(first.Id, room, "a");
        chat.Join(second.Id, room, "b");
        Drain(first);
        Drain(second);
        chat.Send(first.Id, room, "Mining in belt one", "one");
        Assert.Single(Drain(second).OfType<ChatEvent.Message>());
        world.Services.GetRequiredService<ICooperativeFleets>().Kick(leader.PilotId, id, member.PilotId);
        chat.Send(first.Id, room, "Members only", "two");
        Assert.Empty(Drain(second).OfType<ChatEvent.Message>());
        Assert.Throws<InvalidDataException>(() => chat.Send(second.Id, room, "stale access", "three"));
        Assert.Throws<InvalidDataException>(() => chat.Join(second.Id, room, "rejoin"));
    }

    [Fact]
    public void IdleFleetMonitoringUsesNoDatabaseConnectionsCommandsOrFittingCalculationsAndRestartDropsTransientFleets()
    {
        using TestWorld world = new(liveWorld: true);
        long id;
        using (FleetClient leader = new(world, 1000001))
        using (FleetClient member = new(world, 1000002))
        {
            id = Join(leader, member);
            leader.Advance();
            member.Advance();
            using var counters = SessionPerformance.Begin("fleet-idle", _ => { }, force: true)!;
            for (int tick = 0; tick < 100; tick++)
            {
                leader.Advance();
                member.Advance();
            }
            Assert.Equal(0, counters.Commands);
            Assert.Equal(0, counters.Connections);
            Assert.Equal(0, counters.FittingCalculations);
        }
        world.Services.Dispose();
        WorldInitialization.Initialize(new(world.Path));
        using var reopened = ServerServices.Build(new(world.Path));
        Assert.Null(reopened.GetRequiredService<ICooperativeFleets>().Identity(90000001));
        Assert.False(reopened.GetRequiredService<ICooperativeFleets>().CanBind(90000001, id));
    }

    [Fact]
    public void OldFleetEventsCannotPopulateANewFleetAndDisconnectPromotesTheRemainingBoss()
    {
        using TestWorld world = new();
        using FleetClient leader = new(world, 1000001);
        using FleetClient member = new(world, 1000002);
        long old = Join(leader, member);
        var fleets = world.Services.GetRequiredService<ICooperativeFleets>();
        member.Context.Notifications.Clear();
        fleets.SetMotd(leader.PilotId, old, "Old fleet private text");
        fleets.Kick(leader.PilotId, old, member.PilotId);
        long current = fleets.Create(member.PilotId).Id;
        member.Advance();
        Assert.Equal(current, member.Context.Pilot.Identity.Fleet!.FleetId);
        Assert.DoesNotContain(member.Context.Notifications, n => n is FleetMotdNotification or FleetLeaveNotification);
        Assert.Single(member.Context.Notifications.OfType<FleetJoinNotification>());
        fleets.Disband(member.PilotId, current);
        member.Advance();
        leader.Reply("Invite", [member.PilotId, null, null, (long)FleetRole.Member]);
        member.Bind(old);
        member.Reply("AcceptInvite", [(long)member.Context.Pilot.ShipTypeId]);
        leader.Dispose();
        member.Advance();
        Assert.Equal(FleetRules.BossJob, member.Context.Pilot.Identity.Fleet!.Job);
        Assert.Single(fleets.Read(member.PilotId, old).Members);
    }

    [Fact]
    public async Task ConcurrentInvitationAcceptsHaveOneMembershipAndOneJoinEvent()
    {
        using TestWorld world = new();
        using FleetClient leader = new(world, 1000001);
        using FleetClient member = new(world, 1000002);
        long fleet = leader.Create();
        var fleets = world.Services.GetRequiredService<ICooperativeFleets>();
        fleets.Invite(leader.PilotId, fleet, member.PilotId, new(-1, -1, FleetRole.Member));
        fleets.Drain(member.PilotId);
        bool Accept()
        {
            try
            {
                fleets.Accept(member.PilotId, fleet);
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }
        var accepted = await Task.WhenAll(Task.Run(Accept), Task.Run(Accept));
        Assert.Single(accepted, result => result);
        Assert.Equal(2, fleets.Read(leader.PilotId, fleet).Members.Count);
        Assert.Single(fleets.Drain(member.PilotId), delivery => delivery.Event is FleetEvent.Joined);
    }

    [Fact]
    public void WarpToMemberAndFleetLocationsRequireCurrentMembershipAndSameSystemPresence()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock);
        var leaderPilot = world.Store.FindPilot(1000001)!;
        var memberPilot = world.Store.FindPilot(1000002)!;
        world.Store.SetPosition(leaderPilot.ShipId, SpacePosition.Zero);
        world.Store.SetPosition(memberPilot.ShipId, new(50000000, 0, 0));
        using FleetClient leader = new(world, 1000001);
        using FleetClient member = new(world, 1000002);
        long fleet = Join(leader, member);
        leader.Context.Flight.Snapshot();
        member.Context.Flight.Snapshot();
        var locations = Assert.IsType<Dictionary<long, object?>>(
            BlueMarshal.Decode(
                BlueMarshal.Encode(
                    Assert.IsType<PilotCallResult.Success>(leader.CallService("beyonce", "GetFleetLocationAndInfo", [])).Reply
                )
            )
        );
        var target = BlueRead.Fields(Assert.IsType<BlueInstance>(locations[member.PilotId]).State);
        Assert.Equal(new object?[] { 50000000d, 0d, 0d }, BlueRead.Tuple(target["pos"], 3));
        Assert.IsType<PilotCallResult.Success>(
            leader.CallService("beyonce", "CmdWarpToStuff", ["char", member.PilotId], new() { ["minRange"] = 10000L })
        );
        var contact = world
            .Services.GetRequiredService<SharedSpace>()
            .Peers(member.Context.Pilot with { Position = SpacePosition.Zero })
            .Single(p => p.Pilot.PilotId == leader.PilotId);
        Assert.NotNull(contact.AlignTo);
        leader.CallService("beyonce", "CmdStop", []);
        world.Services.GetRequiredService<ICooperativeFleets>().Kick(leader.PilotId, fleet, member.PilotId);
        Assert.IsType<PilotCallResult.Failure>(leader.CallService("beyonce", "CmdWarpToStuff", ["char", member.PilotId]));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("beyonce", "GetFleetLocationAndInfo", []));
    }

    [Fact]
    public void FleetWarpDoesNotBypassAnIndividualMembersWarpDisruption()
    {
        using var fixture = Eden.Protocol.Tests.Propulsion.TackleTests.Prepare(447);
        var world = fixture.Db;
        var clock = fixture.Clock;
        long module = fixture.Fit(447);
        using FleetClient leader = new(world, 1000001);
        using FleetClient member = new(world, 1000002);
        long fleet = Join(leader, member);
        foreach (var pilot in new[] { leader, member })
        {
            pilot.CallService("ship", "Undock", [pilot.Context.Pilot.ShipId]);
            pilot.Context.Flight.Snapshot();
            pilot.Advance();
        }
        var tackle = world.Services.GetRequiredService<Eden.Gameplay.Propulsion.IPilotTackle>();
        var activation = tackle.Activate(
            leader.PilotId,
            module,
            Eden.Gameplay.Inventory.DogmaEffect.WarpScrambler,
            new(member.Context.Pilot.ShipId, Eden.Gameplay.Propulsion.TackleTargetKind.Ship),
            new HashSet<long> { member.Context.Pilot.ShipId },
            clock.Now
        );
        Assert.True(activation.Module.Strength > 0);
        var target = world.Objects.First(o => o.Position.DistanceTo(leader.Context.Pilot.Position!) > 1000000);
        world.Services.GetRequiredService<ICooperativeFleets>().Warp(leader.PilotId, fleet, target.Id, 0, null);
        member.Advance();
        var space = world.Services.GetRequiredService<SharedSpace>();
        Assert.Null(space.Peers(leader.Context.Pilot).Single(p => p.Pilot.PilotId == member.PilotId).AlignTo);
    }

    [Fact]
    public void ImmediateRejoinCannotDeliverAnOldSelfLeaveAfterTheSuccessfulAcceptReply()
    {
        using TestWorld world = new();
        using FleetClient leader = new(world, 1000001);
        using FleetClient member = new(world, 1000002);
        long id = Join(leader, member);
        var fleets = world.Services.GetRequiredService<ICooperativeFleets>();
        member.Context.Notifications.Clear();
        fleets.Kick(leader.PilotId, id, member.PilotId);
        fleets.Invite(leader.PilotId, id, member.PilotId, new(-1, -1, FleetRole.Member));
        // The client accepts before its next periodic update. Dispatch may have
        // already queued the old departure, which must not clear the new fleet object.
        Assert.IsType<PilotCallResult.Success>(member.CallBound(id, "AcceptInvite", [(long)member.Context.Pilot.ShipTypeId]));
        Assert.Equal(id, member.Context.Pilot.Identity.Fleet!.FleetId);
        Assert.DoesNotContain(member.Context.Notifications, n => n is FleetLeaveNotification);
        Assert.Single(member.Context.Notifications.OfType<FleetJoinNotification>());
        Assert.Equal(2, fleets.Read(member.PilotId, id).Members.Count);
    }

    private static IReadOnlyList<ChatEvent> Drain(XmppChatConnection connection)
    {
        List<ChatEvent> events = [];
        while (connection.Events.TryRead(out var item))
        {
            events.Add(item.Event);
        }
        return events;
    }

    private static Dictionary<string, object?> SessionDelta(PilotCallContext context, Eden.Gameplay.Docking.DockedPilot previous)
    {
        var packet = Assert.IsType<BlueInstance>(
            BlueMarshal.Decode(BlueMarshal.Encode(PilotSessionWire.SessionChange(1000001, 1000001, context.Pilot, previous)))
        );
        var body = BlueRead.Tuple(BlueRead.Tuple(packet.State, 14)[4], 3);
        return BlueRead.Fields(BlueRead.Tuple(body[1], 2)[1]);
    }

    [Fact]
    public void WatchlistSendsPrivateNativeDamageUpdatesAndStopsOnLeave()
    {
        using TestWorld world = new(liveWorld: true);
        var source = world.Store.FindPilot(1000001)!;
        var target = world.Store.FindPilot(1000002)!;
        world.Store.SetPosition(source.ShipId, SpacePosition.Zero, source.SystemId);
        world.Store.SetPosition(target.ShipId, new(500, 0, 0), source.SystemId);
        using FleetClient leader = new(world, 1000001);
        using FleetClient member = new(world, 1000002);
        Join(leader, member);
        member.Context.Flight.Snapshot();
        leader.Context.Flight.Snapshot();
        object?[] favorites = [new BlueConstructor("__builtin__.set", [new List<object?> { member.PilotId }])];
        Assert.IsType<PilotCallResult.Success>(leader.CallService("fleetMgr", "RegisterForDamageUpdates", favorites));
        var initial = Assert.Single(leader.Context.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.OnFleetDamageStateChange>());
        Assert.Equal(target.ShipId, initial.Id);
        Assert.DoesNotContain(member.Context.Flight.Updates, u => u.Command is DestinyCommand.OnFleetDamageStateChange);
        leader.Context.Flight.Updates.Clear();
        leader.Advance();
        Assert.DoesNotContain(leader.Context.Flight.Updates, u => u.Command is DestinyCommand.OnFleetDamageStateChange);

        var health = world.Services.GetRequiredService<IPilotHealth>().Read(member.PilotId, DateTime.UtcNow.ToFileTimeUtc());
        var space = world.Services.GetRequiredService<SharedSpace>();
        space.ApplyDamage(health with { Remaining = health.Remaining with { Armor = health.Remaining.Armor / 2 } });
        leader.Context.Flight.RefreshPeers();
        leader.Advance();
        var damaged = Assert.Single(leader.Context.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.OnFleetDamageStateChange>());
        Assert.Equal(.5, damaged.Damage.ArmorFraction, 4);
        leader.Context.Flight.Updates.Clear();
        Assert.IsType<PilotCallResult.Success>(leader.CallService("fleetMgr", "RemoveFromWatchlist", [member.PilotId, favorites[0]]));
        space.ApplyDamage(health);
        leader.Context.Flight.RefreshPeers();
        leader.Advance();
        Assert.DoesNotContain(leader.Context.Flight.Updates, u => u.Command is DestinyCommand.OnFleetDamageStateChange);
        Assert.IsType<PilotCallResult.Success>(
            leader.CallService("fleetMgr", "AddToWatchlist", [new List<object?> { member.PilotId }, null])
        );
        leader.Context.Flight.Updates.Clear();
        member.Reply("LeaveFleet", []);
        space.ApplyDamage(health with { Remaining = health.Remaining with { Armor = health.Remaining.Armor / 2 } });
        leader.Context.Flight.RefreshPeers();
        leader.Advance();
        Assert.DoesNotContain(leader.Context.Flight.Updates, u => u.Command is DestinyCommand.OnFleetDamageStateChange);
    }

    [Fact]
    public async Task OnGridFleetSharesBountyAndCanLootWreckUntilMembershipEnds()
    {
        using AmmunitionTests.Fixture fixture = new(liveWorld: true);
        var source = fixture.Pilot;
        var target = fixture.Db.Store.FindPilot(1000002)!;
        var live = fixture.Db.Services.GetRequiredService<LiveWorld>();
        var fleets = fixture.Db.Services.GetRequiredService<ICooperativeFleets>();
        int system = fixture.Npc.SystemId;
        fixture.Db.Store.SetPosition(source.ShipId, fixture.Npc.Position, system);
        fixture.Db.Store.SetPosition(target.ShipId, fixture.Npc.Position + new SpacePosition(100, 0, 0), system);
        fleets.Update(Guid.NewGuid(), new(source.PilotId, source.CorpId, source.ShipId, source.ShipTypeId, system, fixture.Npc.Position));
        fleets.Update(Guid.NewGuid(), new(target.PilotId, target.CorpId, target.ShipId, target.ShipTypeId, system, fixture.Npc.Position));
        var group = fleets.Create(source.PilotId);
        long fleet = group.Id;
        fleets.Invite(source.PilotId, fleet, target.PilotId, group.Members[0].Placement with { Role = FleetRole.Member });
        fleets.Accept(target.PilotId, fleet);
        long before = live.WalletBalance(source.PilotId);
        long memberBefore = live.WalletBalance(target.PilotId);
        long now = fixture.Clock.Now;
        using var catalog = new CatalogDbContext(new WorldDatabase(fixture.Db.Path).CatalogPath);
        List<string> commands = [];
        using (SessionPerformance.Begin("resident fleet bounty", _ => { }, force: true, command: commands.Add))
        using (
            var action = live.Economy.Begin(
                new(systems: [system], pilots: [source.PilotId], resources: [new(SharedSimulationResource.Npc, fixture.Npc.Id)])
            )
        )
        {
            var npc = action.Set<WorldNpc>().Find(fixture.Npc.Id)!;
            var attributes = PilotFittingStore.ReadAttributes(catalog, npc.TypeId).ToDictionary();
            attributes[DogmaAttributeId.EntityKillBounty] = 3000.01;
            var result = NpcDamagePersistence.Apply(
                action,
                catalog,
                npc,
                action.Pilots.Find(source.PilotId)!,
                source.ShipId,
                attributes,
                new(10_000_000, 0, 0, 0),
                now
            );
            Assert.True(result.Destroyed);
            action.Commit();
        }
        Assert.Empty(commands);
        var leadReward = Assert.Single(fixture.Combat.Rewards(source.PilotId));
        var memberReward = Assert.Single(fixture.Combat.Rewards(target.PilotId));
        Assert.InRange(leadReward.AmountCents - memberReward.AmountCents, 0, 1);
        Assert.Equal(leadReward.AmountCents, live.WalletBalance(source.PilotId) - before);
        Assert.Equal(memberReward.AmountCents, live.WalletBalance(target.PilotId) - memberBefore);
        var wrecks = fixture.Db.Services.GetRequiredService<IWrecks>();
        long wreck = WreckDefinitions.FirstWreckId + fixture.Npc.Id - NpcEncounterDefinitions.FirstNpcId;
        var stack = Assert.Single(wrecks.Read(target.PilotId, wreck, now).Items);
        var split = wrecks.Take(target.PilotId, target.ShipId, wreck, [stack.Id], 1, now);
        Assert.Contains(split, change => change.Item.OwnerId == target.PilotId && change.Item.LocationId == target.ShipId);
        Assert.Equal(stack.Quantity - 1, Assert.Single(wrecks.Read(source.PilotId, wreck, now).Items).Quantity);
        fleets.Kick(source.PilotId, fleet, target.PilotId);
        Assert.Throws<InvalidDataException>(() => wrecks.Take(target.PilotId, target.ShipId, wreck, [stack.Id], 1, now));
        await live.FlushAsync();
        using WorldDbContext saved = new(fixture.Db.Path);
        Assert.Equal(2, saved.Set<NpcBounty>().Count(b => b.NpcId == fixture.Npc.Id));
    }

    private static long Join(FleetClient leader, FleetClient member)
    {
        long fleet = leader.Create();
        leader.Reply("Invite", [member.PilotId, null, null, (long)FleetRole.Member]);
        member.Advance();
        member.Bind(fleet);
        member.Reply("AcceptInvite", [(long)member.Context.Pilot.ShipTypeId]);
        leader.Advance();
        return fleet;
    }

    private sealed class FleetClient : IDisposable
    {
        private readonly CorporationTestClient _client;
        private readonly CooperativeFleetHandler _handler;
        private readonly ChatHandler _chat;
        public PilotCallContext Context => _client.Context;
        public long PilotId => Context.Pilot.PilotId;
        public BlueObjectReference? Reference { get; private set; }
        private long _fleet;

        public FleetClient(TestWorld world, long account)
        {
            _client = new(world, account);
            _handler = _client.Services.GetRequiredService<CooperativeFleetHandler>();
            _chat = _client.Services.GetRequiredService<ChatHandler>();
            Advance();
        }

        public void Advance()
        {
            _handler.Advance(Context);
            _chat.Advance(Context);
        }

        public string Token() =>
            Assert.IsType<string>(
                Assert
                    .IsType<PilotCallResult.Success>(CallService("chatAuthenticationService", "GetAuthenticationToken", []))
                    .Reply.ToBlue()
            );

        public long Create()
        {
            Reference = Assert.IsType<BlueObjectReference>(
                Assert.IsType<PilotCallResult.Success>(CallService("fleetObjectHandler", "CreateFleet", [])).Reply.ToBlue()
            );
            _fleet = Context.Pilot.Identity.Fleet!.FleetId;
            return _fleet;
        }

        public void Bind(long fleet)
        {
            var bound = Assert.IsType<BoundPilotReply>(
                Assert.IsType<PilotCallResult.Success>(CallService("fleetObjectHandler", "MachoBindObject", [fleet, null])).Reply
            );
            Reference = bound.Reference;
            _fleet = fleet;
        }

        public PilotCallResult CallService(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null)
        {
            var result = _client.CallService(service, method, args, keywords);
            Advance();
            return result;
        }

        public PilotCallResult CallBound(long fleet, string method, object?[] args) =>
            CallService($"{CooperativeFleetHandler.Prefix}{fleet}", method, args);

        public object? Reply(string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            BlueMarshal.Decode(
                BlueMarshal.Encode(
                    Assert
                        .IsType<PilotCallResult.Success>(CallService($"{CooperativeFleetHandler.Prefix}{_fleet}", method, args, keywords))
                        .Reply
                )
            );

        public void Dispose() => _client.Dispose();
    }
}
