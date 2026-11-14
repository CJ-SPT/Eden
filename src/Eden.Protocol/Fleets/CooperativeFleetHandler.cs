using System.Globalization;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Fleets;

[Injectable(InjectionType.Scoped)]
internal sealed class CooperativeFleetHandler(ICooperativeFleets fleets, ISystemObjects systems) : ISelectedPilotCallHandler, IDisposable
{
    internal const string Prefix = "@fleet:";
    private readonly Guid _session = Guid.NewGuid();
    private long? _pilot;
    private FleetPilot? _presence;
    private const int MaximumDamageSenders = 100; // Pinned evefleet.MAX_DAMAGE_SENDERS.
    private FleetMembership? _watchingAs;
    private readonly Dictionary<long, FleetMembership> _watchlist = [];
    private readonly Dictionary<long, ShipHealth> _watchHealth = [];
    private int _watchSystem;
    public bool IsMonitoring => _pilot.HasValue;

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            Dispose();
            return;
        }
        var pilot = context.Pilot;
        _pilot = pilot.PilotId;
        if (
            _presence is not { } last
            || last.Id != pilot.PilotId
            || last.CorporationId != pilot.CorpId
            || last.ShipId != pilot.ShipId
            || last.ShipTypeId != pilot.ShipTypeId
            || last.SystemId != pilot.SystemId
            || last.Position != pilot.Position
            || last.InTransit != IsInTransit(context)
        )
        {
            _presence = new(
                pilot.PilotId,
                pilot.CorpId,
                pilot.ShipId,
                pilot.ShipTypeId,
                pilot.SystemId,
                pilot.Position,
                IsInTransit(context)
            );
            fleets.Update(_session, _presence);
        }
        var identity = fleets.Identity(pilot.PilotId);
        if (pilot.Identity.Fleet != identity)
        {
            context.Flight.UpdateFleet(identity);
            context.ReleaseFleetReferences(identity?.FleetId);
        }
        AdvanceWatchlist(context);
        foreach (var delivery in fleets.Drain(pilot.PilotId))
        {
            var value = delivery.Event;
            if (
                value is not (FleetEvent.Invitation or FleetEvent.MissionStanding)
                && delivery.FleetId != identity?.FleetId
                && !(identity is null && value is FleetEvent.Left { PilotId: var departed } && departed == pilot.PilotId)
            )
            {
                continue;
            }
            switch (value)
            {
                case FleetEvent.Invitation invite:
                    if (identity is null && fleets.CanBind(pilot.PilotId, invite.FleetId))
                    {
                        context.Notifications.Add(new FleetInviteNotification(invite.FleetId, invite.InviterId));
                    }
                    break;
                case FleetEvent.Joined joined:
                    context.Notifications.Add(new FleetJoinNotification(joined.Member));
                    if (joined.Member.Pilot.Id == pilot.PilotId)
                    {
                        context.Notifications.Add(new FleetStateNotification());
                    }
                    break;
                case FleetEvent.Left left:
                    context.Notifications.Add(new FleetLeaveNotification(left.PilotId));
                    break;
                case FleetEvent.Changed changed when identity is not null:
                    context.Notifications.Add(
                        new FleetMemberNotification(identity.FleetId, changed.Before, changed.After, changed.OnlyMember)
                    );
                    break;
                case FleetEvent.Hierarchy hierarchy when identity is not null:
                    context.Notifications.Add(new FleetHierarchyNotification(hierarchy));
                    break;
                case FleetEvent.Options options when identity is not null:
                    context.Notifications.Add(new FleetOptionsNotification(options.Before, options.After));
                    break;
                case FleetEvent.Motd motd when identity is not null:
                    context.Notifications.Add(new FleetMotdNotification(motd.Text));
                    break;
                case FleetEvent.Broadcast broadcast when identity is not null:
                    context.Notifications.Add(new FleetBroadcastNotification(broadcast.Value));
                    break;
                case FleetEvent.Warp warp:
                    if (context.Flight.CanTarget && fleets.CanExecuteWarp(pilot.PilotId, warp.Order))
                    {
                        var order = warp.Order;
                        context.Flight.Execute(
                            order.Point is { } point
                                ? new FlightCommand.WarpToPoint(order.TargetId, point, order.Range)
                                : new FlightCommand.WarpTo(order.TargetId, order.Range)
                        );
                    }
                    break;
                case FleetEvent.MissionStanding standing:
                    context.Notifications.Add(new NpcStandingSet(standing.SourceId, pilot.PilotId, standing.Value));
                    break;
                case FleetEvent.Resync resync:
                    context.Notifications.Add(
                        resync.Member is null ? new FleetLeaveNotification(pilot.PilotId) : new FleetJoinNotification(resync.Member)
                    );
                    break;
            }
        }
    }

    private static bool IsInTransit(PilotCallContext context) => context.Flight.InWarp || context.Flight.Translocating;

    private void UpdateWatchlist(long actor, long fleet, string method, object?[] args)
    {
        IEnumerable<long> members = (method, args) switch
        {
            ("RegisterForDamageUpdates", [var current]) => WatchlistMembers(current),
            ("AddToWatchlist", [List<object?> added, var current]) => WatchlistMembers(current).Concat(WatchlistMembers(added)),
            ("RemoveFromWatchlist", [long removed, var current]) => WatchlistMembers(current).Where(id => id != removed),
            _ => throw new InvalidDataException("Invalid fleet watchlist request."),
        };
        var selection = members.Where(id => id != actor).Distinct().ToArray();
        if (selection.Length > MaximumDamageSenders)
        {
            throw new InvalidDataException($"Watch up to {MaximumDamageSenders} fleet members.");
        }
        _watchingAs = fleets.JoinMembership(actor);
        _watchlist.Clear();
        foreach (long id in selection)
        {
            if (fleets.JoinMembership(id) is { FleetId: var memberFleet } membership && memberFleet == fleet)
            {
                _watchlist[id] = membership;
            }
        }
        foreach (long id in _watchHealth.Keys.Where(id => !_watchlist.ContainsKey(id)).ToArray())
        {
            _watchHealth.Remove(id);
        }
    }

    private static IEnumerable<long> WatchlistMembers(object? value) =>
        value switch
        {
            null => [],
            HashSet<long> set when set.Count <= MaximumDamageSenders => set,
            List<object?> list when list.Count <= MaximumDamageSenders && list.All(id => id is long) => list.Cast<long>(),
            _ => throw new InvalidDataException("Choose valid fleet watchlist members."),
        };

    private void AdvanceWatchlist(PilotCallContext context)
    {
        if (_watchlist.Count == 0)
        {
            return;
        }
        if (_watchingAs != fleets.JoinMembership(context.Pilot.PilotId))
        {
            _watchingAs = null;
            _watchlist.Clear();
            _watchHealth.Clear();
            return;
        }
        if (!context.Flight.CanTarget || _watchSystem != context.Pilot.SystemId)
        {
            _watchHealth.Clear();
            _watchSystem = context.Pilot.SystemId;
        }
        if (!context.Flight.CanTarget)
        {
            return;
        }
        var visible = context.Flight.VisiblePeers;
        foreach (var watched in _watchlist)
        {
            var peer = visible.FirstOrDefault(ship => ship.Pilot.PilotId == watched.Key);
            if (
                peer is not { Warp: null, DepartureGate: null, Arriving: false, Health: { } health }
                || fleets.JoinMembership(watched.Key) != watched.Value
            )
            {
                _watchHealth.Remove(watched.Key);
                continue;
            }
            if (_watchHealth.GetValueOrDefault(watched.Key) != health)
            {
                _watchHealth[watched.Key] = health;
                context.Flight.NotifyFleetHealth(health);
            }
        }
    }

    public void Dispose()
    {
        fleets.Update(_session, null);
        _pilot = null;
        _presence = null;
        _watchingAs = null;
        _watchlist.Clear();
        _watchHealth.Clear();
    }

    public void CancelWarpOrders(long actor) => fleets.CancelWarpOrders(actor);

    public FlightCommand MemberWarp(PilotCallContext context, FlightCommand.WarpToFleetMember request)
    {
        Advance(context);
        var identity = fleets.Identity(context.Pilot.PilotId) ?? throw new InvalidDataException("Join a fleet before warping to a member.");
        var member = fleets.Read(context.Pilot.PilotId, identity.FleetId).Members.SingleOrDefault(m => m.Pilot.Id == request.PilotId);
        if (member is null || member.Pilot.SystemId != context.Pilot.SystemId || member.Pilot.Position is not { } position)
        {
            throw new InvalidDataException("Choose a fleet member in space in your current system.");
        }
        return new FlightCommand.WarpToPoint(-request.PilotId, position, request.Range);
    }

    public PilotCallResult Locations(PilotCallContext context, object?[] args, Dictionary<string, object?>? keywords)
    {
        Advance(context);
        try
        {
            if (args.Length != 0 || keywords is { Count: > 0 } || context.Pilot.Position is null)
            {
                throw new InvalidDataException("Request fleet locations in space without extra arguments.");
            }
            var identity =
                fleets.Identity(context.Pilot.PilotId) ?? throw new InvalidDataException("Join a fleet before reading locations.");
            return new FleetLocationsReply(
                fleets
                    .Read(context.Pilot.PilotId, identity.FleetId)
                    .Members.Where(m => m.Pilot.SystemId == context.Pilot.SystemId && m.Pilot.Position is not null)
                    .ToArray()
            );
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    public PilotCallResult Warp(PilotCallContext context, FlightCommand command, object? formation = null)
    {
        Advance(context);
        try
        {
            if (formation is not null)
            {
                var settings = BlueRead.Fields(formation);
                // Michelle.SetFleetFormationSettings always supplies these three fields.
                // Point (evefleet.const.POINT=0) has no geometric offsets or skill bonuses.
                if (
                    settings.Count != 3
                    || settings.GetValueOrDefault("formationType") is not 0L
                    || settings.GetValueOrDefault("formationSpacing") is not long spacing
                    || spacing <= 0
                    || settings.GetValueOrDefault("formationSize") is not long size
                    || size <= 0
                )
                {
                    throw new InvalidDataException("Only the standard point fleet formation is supported.");
                }
            }
            var identity =
                fleets.Identity(context.Pilot.PilotId) ?? throw new InvalidDataException("Join a fleet before commanding fleet warp.");
            var member = fleets.Read(context.Pilot.PilotId, identity.FleetId).Members.Single(m => m.Pilot.Id == context.Pilot.PilotId);
            if (member.Placement.Role == FleetRole.Member || !context.Flight.CanTarget)
            {
                throw new InvalidDataException("A commander ready to warp must issue fleet warp.");
            }
            var (target, range, point) = command switch
            {
                FlightCommand.WarpTo warp => (warp.TargetId, warp.Range, (SpacePosition?)null),
                FlightCommand.WarpToPoint warp => (warp.OrderId, warp.Range, (SpacePosition?)warp.Position),
                _ => throw new InvalidDataException("This fleet movement is unsupported."),
            };
            if (!double.IsFinite(range) || range < 0 || point is null && !systems.Load(context.Pilot.SystemId).Any(o => o.Id == target))
            {
                throw new InvalidDataException("Choose a known warp destination in this system and a valid distance.");
            }
            var result = context.Flight.Execute(command);
            if (result is PilotCallResult.Success)
            {
                fleets.Warp(context.Pilot.PilotId, identity.FleetId, target, range, point);
            }
            return result;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool bound = service.StartsWith(Prefix, StringComparison.Ordinal);
        if (!bound && service is not ("fleetObjectHandler" or "fleetMgr" or "fleetProxy"))
        {
            return PilotCallResult.Unsupported;
        }
        Advance(context);
        long actor = context.Pilot.PilotId;
        try
        {
            if (service == "fleetProxy" && method is "GetAvailableFleetAds" or "GetMyFleetFinderAdvert" && keywords is not { Count: > 0 })
            {
                return method == "GetMyFleetFinderAdvert" ? PilotCallResult.None : BlueEmpty.Dictionary;
            }
            if (service == "fleetObjectHandler" && method == "CreateFleet" && args.Length == 0 && keywords is not { Count: > 0 })
            {
                var created = fleets.Create(actor);
                context.Notifications.RemoveAll(n => n is FleetNotification);
                return context.Reference($"{Prefix}{created.Id}", PilotBindingLifetime.Pilot);
            }
            if (service == "fleetObjectHandler" && method is "MachoResolveObject" or "MachoBindObject")
            {
                if (
                    keywords is { Count: > 0 }
                    || args.Length != (method == "MachoBindObject" ? 2 : 1)
                    || args[0] is not long fleetId
                    || !fleets.CanBind(actor, fleetId)
                )
                {
                    throw new InvalidDataException("Bind your fleet or a current invitation.");
                }
                if (method == "MachoResolveObject")
                {
                    return PilotLogin.NodeId;
                }
                string name = $"{Prefix}{fleetId}";
                PilotCallResult result = PilotCallResult.None;
                if (args[1] is object?[] { Length: 2 or 3 } first && first[1] is object?[] innerArgs)
                {
                    result = Call(
                        context,
                        name,
                        BlueMarshal.Text(first[0]),
                        innerArgs,
                        first.ElementAtOrDefault(2) as Dictionary<string, object?>
                    );
                }
                else if (args[1] is not null)
                {
                    throw new InvalidDataException("Invalid first fleet call.");
                }
                return result is PilotCallResult.Success success
                    ? new BoundPilotReply(context.Reference(name, PilotBindingLifetime.Pilot), success.Reply)
                    : result;
            }
            long id;
            if (bound)
            {
                if (!long.TryParse(service.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0)
                {
                    throw new InvalidDataException("Invalid fleet reference.");
                }
            }
            else
            {
                id = fleets.Identity(actor)?.FleetId ?? throw new InvalidDataException("Join a fleet before using fleet services.");
            }
            if (!fleets.CanBind(actor, id))
            {
                throw new InvalidDataException("This fleet reference is no longer accessible.");
            }
            if (
                bound
                && method == "AcceptInvite"
                && args is [var invitedShip]
                && (invitedShip is null || Equals(invitedShip, (long)context.Pilot.ShipTypeId))
                && keywords is not { Count: > 0 }
            )
            {
                fleets.Accept(actor, id);
                context.Notifications.RemoveAll(n => n is FleetNotification);
                return PilotCallResult.None;
            }
            if (bound && method == "RejectInvite" && (args.Length == 0 || args is [bool]) && keywords is not { Count: > 0 })
            {
                fleets.Reject(actor, id);
                return PilotCallResult.None;
            }
            var fleet = fleets.Read(actor, id);
            if (
                bound
                && method == "Init"
                && args is [var shipType, null]
                && (shipType is null || Equals(shipType, (long)context.Pilot.ShipTypeId))
                && keywords?.Keys.Any(k => k != "adInfoData") != true
                && keywords?.GetValueOrDefault("adInfoData") is null
            )
            {
                return PilotCallResult.None;
            }
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected fleet arguments.");
            }
            if (service == "fleetMgr")
            {
                if (method == "ForceLeaveFleet" && args.Length == 0)
                {
                    fleets.Leave(actor, id);
                    return PilotCallResult.None;
                }
                if (method is "BroadcastToSystem" or "BroadcastToBubble")
                {
                    SendBroadcast(actor, id, args, method == "BroadcastToSystem" ? FleetBroadcastRange.System : FleetBroadcastRange.Bubble);
                    return PilotCallResult.None;
                }
                if (method is "RegisterForDamageUpdates" or "AddToWatchlist" or "RemoveFromWatchlist")
                {
                    UpdateWatchlist(actor, id, method, args);
                    AdvanceWatchlist(context);
                    return true;
                }
                throw new InvalidDataException("This fleet manager operation is not supported.");
            }
            if (!bound)
            {
                throw new InvalidDataException("Use a bound fleet reference.");
            }
            switch (method, args)
            {
                case ("GetFleetID", []):
                    return id;
                case ("GetInitState", []):
                    return new FleetInitReply(fleet);
                case ("GetWings", []):
                    return new FleetWingsReply(fleet.Wings);
                case ("GetMembers", []):
                    return new BlueMap<long, FleetMemberReply>(fleet.Members.ToDictionary(m => m.Pilot.Id, m => new FleetMemberReply(m)));
                case ("GetOptions", []):
                    return new FleetOptionsReply(fleet.FreeMove);
                case ("GetJoinRequests", []):
                    return BlueEmpty.Dictionary;
                case ("GetMotd", []):
                    return fleet.Motd;
                case ("Init", [var ship, null]) when ship is null || Equals(ship, (long)context.Pilot.ShipTypeId):
                    return PilotCallResult.None;
                case ("UpdateMemberInfo", [var ship]) when ship is null || Equals(ship, (long)context.Pilot.ShipTypeId):
                    return PilotCallResult.None;
                case ("Reconnect" or "FinishMove", []):
                    return PilotCallResult.None;
                case ("Invite", [long target, var wing, var squad, long role]):
                    fleets.Invite(actor, id, target, Placement(wing, squad, role));
                    return PilotCallResult.None;
                case ("MoveMember", [long target, var wing, var squad, long role]):
                    fleets.Move(actor, id, target, Placement(wing, squad, role));
                    return false;
                case ("MakeLeader", [long target]):
                    fleets.MakeBoss(actor, id, target);
                    return PilotCallResult.None;
                case ("KickMember", [long target]):
                    fleets.Kick(actor, id, target);
                    return PilotCallResult.None;
                case ("LeaveFleet", []):
                    fleets.Leave(actor, id);
                    return PilotCallResult.None;
                case ("DisbandFleet", []):
                    fleets.Disband(actor, id);
                    return PilotCallResult.None;
                case ("CreateWing", []):
                    return fleets.CreateWing(actor, id);
                case ("CreateSquad", [long wing]):
                    return fleets.CreateSquad(actor, id, wing);
                case ("DeleteWing" or "DeleteSquad", [long group]):
                    fleets.ChangeGroup(actor, id, group, method == "DeleteSquad", null);
                    return PilotCallResult.None;
                case ("ChangeWingName" or "ChangeSquadName", [long group, var text]) when text is string or byte[]:
                    fleets.ChangeGroup(actor, id, group, method == "ChangeSquadName", BlueMarshal.Text(text));
                    return PilotCallResult.None;
                case ("SetOptions", [var options]):
                    var fields = options is BlueInstance instance ? BlueRead.Fields(instance.State) : BlueRead.Fields(options);
                    if (
                        fields.Keys.Any(k => k is not ("isFreeMove" or "isRegistered" or "autoJoinSquadID"))
                        || fields.GetValueOrDefault("isRegistered") is not (null or false)
                        || fields.GetValueOrDefault("autoJoinSquadID") is not null
                        || fields.GetValueOrDefault("isFreeMove") is not bool enabled
                    )
                    {
                        throw new InvalidDataException("Only the free-move option can be changed.");
                    }
                    fleets.SetFreeMove(actor, id, enabled);
                    return PilotCallResult.None;
                case ("SetMotdEx", [var motdText]) when motdText is string or byte[]:
                    fleets.SetMotd(actor, id, BlueMarshal.Text(motdText));
                    return PilotCallResult.None;
                case ("SetTakesFleetWarp", [bool takesWarp]):
                    fleets.SetTakesWarp(actor, id, takesWarp);
                    return PilotCallResult.None;
                case ("SendBroadcast", _):
                    SendBroadcast(actor, id, args, FleetBroadcastRange.Universe);
                    return PilotCallResult.None;
                default:
                    throw new InvalidDataException("Invalid or unsupported fleet request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static FleetPlacement Placement(object? wing, object? squad, long role)
    {
        if (wing is not (null or long) || squad is not (null or long) || role is < (long)FleetRole.Commander or > (long)FleetRole.Member)
        {
            throw new InvalidDataException("Choose a valid fleet command position.");
        }
        return new(wing as long? ?? FleetRules.NoGroup, squad as long? ?? FleetRules.NoGroup, (FleetRole)role);
    }

    private void SendBroadcast(long actor, long fleet, object?[] args, FleetBroadcastRange range)
    {
        if (
            args is not [var name, long direction, var item, var type]
            || name is not (string or byte[])
            || !Enum.TryParse<FleetBroadcastKind>(BlueMarshal.Text(name), out var kind)
            || !Enum.IsDefined(kind)
            || kind.ToString() != BlueMarshal.Text(name)
            || direction is < (long)FleetBroadcastScope.Down or > (long)FleetBroadcastScope.All
            || item is not (null or long)
            || type is not (null or long)
            || type is long typeId && typeId is <= 0 or > int.MaxValue
        )
        {
            throw new InvalidDataException("Invalid fleet broadcast.");
        }
        fleets.Broadcast(actor, fleet, kind, (FleetBroadcastScope)direction, range, item as long?, type is long t ? (int)t : null);
    }
}
