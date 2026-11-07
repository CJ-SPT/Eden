namespace Eden.Gameplay.Fleets;

// Pure transient fleet state. The World adapter serializes calls; no client/session or SQL dependency.
public sealed class FleetWorld(long seed)
{
    private sealed class Fleet(long id)
    {
        public long Id { get; } = id;
        public Dictionary<long, FleetMember> Members { get; } = [];
        public Dictionary<long, FleetWing> Wings { get; } = [];
        public bool FreeMove { get; set; }
        public string Motd { get; set; } = "";
    }

    private sealed record Invitation(long FleetId, long Inviter, FleetPlacement Placement, long Expires);

    private readonly Dictionary<long, Fleet> _fleets = [];
    private readonly Dictionary<long, FleetPilot> _online = [];
    private readonly Dictionary<Guid, long> _sessions = [];
    private readonly Dictionary<long, Guid> _owners = [];
    private readonly Dictionary<long, FleetIdentity> _identities = [];
    private readonly Dictionary<long, FleetMembership> _memberships = [];
    private long _membershipVersion;
    private readonly Dictionary<long, Invitation> _invitations = [];
    private readonly Dictionary<long, Queue<FleetDelivery>> _events = [];
    private readonly Dictionary<long, long> _broadcastAt = [];
    private readonly Dictionary<long, long> _controlVersions = [];
    private long _nextId = seed;

    public void Update(Guid session, FleetPilot? pilot)
    {
        if (_sessions.TryGetValue(session, out long previous) && previous != pilot?.Id)
        {
            _sessions.Remove(session);
            if (_owners.GetValueOrDefault(previous) == session)
            {
                if (Identity(previous) is { } identity)
                {
                    Leave(previous, identity.FleetId);
                }
                _online.Remove(previous);
                _owners.Remove(previous);
                _events.Remove(previous);
                _invitations.Remove(previous);
                _broadcastAt.Remove(previous);
                _controlVersions.Remove(previous);
            }
        }
        if (pilot is null)
        {
            return;
        }
        if (
            _memberships.TryGetValue(pilot.Id, out var membership)
            && (
                _owners.GetValueOrDefault(pilot.Id) != session
                || _online.TryGetValue(pilot.Id, out var previousPilot)
                    && (
                        previousPilot.ShipId != pilot.ShipId
                        || previousPilot.SystemId != pilot.SystemId
                        || (previousPilot.Position is null) != (pilot.Position is null)
                    )
            )
        )
        {
            _memberships[pilot.Id] = new(membership.FleetId, checked(++_membershipVersion));
        }
        _sessions[session] = pilot.Id;
        _owners[pilot.Id] = session;
        _online[pilot.Id] = pilot;
        if (!_events.ContainsKey(pilot.Id))
        {
            _events.Add(pilot.Id, new());
        }
        if (Identity(pilot.Id) is { } current)
        {
            var fleet = _fleets[current.FleetId];
            fleet.Members[pilot.Id] = fleet.Members[pilot.Id] with { Pilot = pilot };
        }
    }

    public FleetIdentity? Identity(long actor) => _identities.GetValueOrDefault(actor);

    public FleetMembership? Membership(long actor) => _memberships.GetValueOrDefault(actor);

    public IReadOnlyList<FleetDelivery> Drain(long actor)
    {
        if (!_events.TryGetValue(actor, out var queue) || queue.Count == 0)
        {
            return [];
        }
        var result = queue.ToArray();
        queue.Clear();
        return result;
    }

    private void Queue(long actor, long fleetId, FleetEvent value)
    {
        if (!_events.TryGetValue(actor, out var queue))
        {
            return;
        }
        if (queue.Count >= FleetRules.MaximumPendingEvents)
        {
            queue.Clear();
            var member = _fleets.GetValueOrDefault(fleetId)?.Members.GetValueOrDefault(actor);
            queue.Enqueue(new(fleetId, new FleetEvent.Resync(member)));
        }
        queue.Enqueue(new(fleetId, value));
    }

    private void Publish(Fleet fleet, FleetEvent value)
    {
        foreach (long actor in fleet.Members.Keys)
        {
            Queue(actor, fleet.Id, value);
        }
    }

    private static CooperativeFleet Snapshot(Fleet fleet) =>
        new(
            fleet.Id,
            Array.AsReadOnly(fleet.Members.Values.OrderBy(m => m.Pilot.Id).ToArray()),
            Array.AsReadOnly(fleet.Wings.Values.OrderBy(w => w.Id).ToArray()),
            fleet.FreeMove,
            fleet.Motd
        );

    private Fleet MemberFleet(long actor, long fleetId) =>
        _fleets.GetValueOrDefault(fleetId) is { } fleet && fleet.Members.ContainsKey(actor) && _online.ContainsKey(actor)
            ? fleet
            : throw new InvalidDataException("You are not a member of this fleet.");

    private static bool Boss(Fleet fleet, long actor) => (fleet.Members[actor].Job & FleetRules.BossJob) != 0;

    private Fleet BossFleet(long actor, long fleetId)
    {
        var fleet = MemberFleet(actor, fleetId);
        if (!Boss(fleet, actor))
        {
            throw new InvalidDataException("Only the fleet boss may do this.");
        }
        return fleet;
    }

    public bool CanBind(long actor, long fleetId, long now) =>
        Identity(actor)?.FleetId == fleetId
        || _invitations.GetValueOrDefault(actor) is { } invite
            && invite.FleetId == fleetId
            && invite.Expires > now
            && _fleets.ContainsKey(fleetId);

    public CooperativeFleet Read(long actor, long fleetId) => Snapshot(MemberFleet(actor, fleetId));

    public CooperativeFleet Create(long actor)
    {
        if (!_online.TryGetValue(actor, out var pilot) || Identity(actor) is not null)
        {
            throw new InvalidDataException("Select a pilot outside a fleet before creating one.");
        }
        var fleet = new Fleet(++_nextId);
        _fleets.Add(fleet.Id, fleet);
        long wing = ++_nextId;
        fleet.Wings.Add(wing, new(wing, "", Array.AsReadOnly(new[] { new FleetSquad(++_nextId, "") })));
        Add(fleet, new(pilot, new(FleetRules.NoGroup, FleetRules.NoGroup, FleetRole.Commander), FleetRules.BossJob));
        _invitations.Remove(actor);
        return Snapshot(fleet);
    }

    private void Add(Fleet fleet, FleetMember member)
    {
        // Joining initializes the whole roster. Pending events from a previous membership
        // must not clear or populate that new client cache, even when rejoining the same fleet.
        _events[member.Pilot.Id].Clear();
        fleet.Members.Add(member.Pilot.Id, member);
        _memberships[member.Pilot.Id] = new(fleet.Id, checked(++_membershipVersion));
        SaveIdentity(fleet, member);
        Publish(fleet, new FleetEvent.Joined(member));
    }

    private void SaveIdentity(Fleet fleet, FleetMember member) =>
        _identities[member.Pilot.Id] = new(fleet.Id, member.Placement.WingId, member.Placement.SquadId, member.Placement.Role, member.Job);

    private static FleetPlacement Placement(Fleet fleet, FleetPlacement placement)
    {
        if (!Enum.IsDefined(placement.Role))
        {
            throw new InvalidDataException("Choose a known fleet rank.");
        }
        if (placement.Role == FleetRole.Commander)
        {
            return new(FleetRules.NoGroup, FleetRules.NoGroup, placement.Role);
        }
        long wingId = placement.WingId;
        if (wingId == FleetRules.NoGroup && placement.Role == FleetRole.Member)
        {
            wingId = fleet.Wings.Keys.Order().FirstOrDefault(FleetRules.NoGroup);
        }
        if (!fleet.Wings.TryGetValue(wingId, out var wing))
        {
            throw new InvalidDataException("Choose an existing wing.");
        }
        if (placement.Role == FleetRole.WingCommander)
        {
            return new(wingId, FleetRules.NoGroup, placement.Role);
        }
        long squadId =
            placement.SquadId == FleetRules.NoGroup && placement.Role == FleetRole.Member
                ? wing.Squads.FirstOrDefault()?.Id ?? FleetRules.NoGroup
                : placement.SquadId;
        if (!wing.Squads.Any(s => s.Id == squadId))
        {
            throw new InvalidDataException("Choose a squad in that wing.");
        }
        return new(wingId, squadId, placement.Role);
    }

    private static void CheckSlot(Fleet fleet, long target, FleetPlacement placement)
    {
        if (placement.Role != FleetRole.Member && fleet.Members.Values.Any(m => m.Pilot.Id != target && m.Placement == placement))
        {
            throw new InvalidDataException("This command position is occupied.");
        }
    }

    private static void AuthorizePlacement(Fleet fleet, long actor, long target, FleetPlacement placement)
    {
        if (Boss(fleet, actor))
        {
            return;
        }
        var commander = fleet.Members[actor];
        bool selfMove =
            actor == target
            && fleet.FreeMove
            && commander.Placement.Role >= FleetRole.SquadCommander
            && placement.Role >= FleetRole.SquadCommander;
        var proposed = new FleetMember(commander.Pilot with { Id = target }, placement, 0);
        bool delegated =
            commander.Placement.Role != FleetRole.Member
            && FleetRules.Subordinate(commander, proposed)
            && placement.Role > commander.Placement.Role
            && (
                !fleet.Members.TryGetValue(target, out var old)
                || (old.Job & FleetRules.BossJob) == 0 && FleetRules.Subordinate(commander, old)
            );
        if (!selfMove && !delegated)
        {
            throw new InvalidDataException("This assignment exceeds your fleet authority.");
        }
    }

    public void Invite(long actor, long fleetId, long target, FleetPlacement requested, long now)
    {
        var fleet = MemberFleet(actor, fleetId);
        var placement = Placement(fleet, requested);
        AuthorizePlacement(fleet, actor, target, placement);
        CheckSlot(fleet, target, placement);
        if (
            !_online.ContainsKey(target)
            || Identity(target) is not null
            || actor == target
            || fleet.Members.Count >= FleetRules.MaximumMembers
        )
        {
            throw new InvalidDataException("Invite an online pilot outside a fleet with an available position.");
        }
        _invitations[target] = new(fleetId, actor, placement, checked(now + FleetRules.InvitationLifetime.Ticks));
        Queue(target, fleetId, new FleetEvent.Invitation(fleetId, actor));
    }

    public void Accept(long actor, long fleetId, long now)
    {
        if (
            !_invitations.TryGetValue(actor, out var invitation)
            || invitation.FleetId != fleetId
            || invitation.Expires <= now
            || !_fleets.TryGetValue(fleetId, out var fleet)
            || Identity(actor) is not null
            || !_online.TryGetValue(actor, out var pilot)
            || !fleet.Members.ContainsKey(invitation.Inviter)
            || fleet.Members.Count >= FleetRules.MaximumMembers
        )
        {
            throw new InvalidDataException("This fleet invitation is no longer valid.");
        }
        var placement = Placement(fleet, invitation.Placement);
        AuthorizePlacement(fleet, invitation.Inviter, actor, placement);
        CheckSlot(fleet, actor, placement);
        _invitations.Remove(actor);
        Add(fleet, new(pilot, placement, 0));
    }

    public void Reject(long actor, long fleetId)
    {
        if (_invitations.GetValueOrDefault(actor)?.FleetId != fleetId)
        {
            throw new InvalidDataException("No invitation to this fleet exists.");
        }
        _invitations.Remove(actor);
    }

    public void Leave(long actor, long fleetId)
    {
        var fleet = MemberFleet(actor, fleetId);
        CancelWarpOrders(actor);
        Publish(fleet, new FleetEvent.Left(actor));
        bool boss = Boss(fleet, actor);
        fleet.Members.Remove(actor);
        _identities.Remove(actor);
        _memberships.Remove(actor);
        foreach (long invited in _invitations.Where(i => i.Value.Inviter == actor).Select(i => i.Key).ToArray())
        {
            _invitations.Remove(invited);
        }
        if (fleet.Members.Count == 0)
        {
            _fleets.Remove(fleetId);
        }
        else if (boss)
        {
            long successor = fleet.Members.Values.OrderBy(m => m.Placement.Role).ThenBy(m => m.Pilot.Id).First().Pilot.Id;
            ChangeMember(fleet, fleet.Members[successor] with { Job = FleetRules.BossJob });
        }
    }

    public void Disband(long actor, long fleetId)
    {
        var fleet = BossFleet(actor, fleetId);
        foreach (long member in fleet.Members.Keys.ToArray())
        {
            Leave(member, fleetId);
        }
    }

    public void Move(long actor, long fleetId, long target, FleetPlacement requested)
    {
        var fleet = MemberFleet(actor, fleetId);
        if (!fleet.Members.TryGetValue(target, out var member))
        {
            throw new InvalidDataException("Choose a current fleet member.");
        }
        var placement = Placement(fleet, requested);
        AuthorizePlacement(fleet, actor, target, placement);
        CheckSlot(fleet, target, placement);
        ChangeMember(fleet, member with { Placement = placement });
    }

    private void ChangeMember(Fleet fleet, FleetMember after)
    {
        var before = fleet.Members[after.Pilot.Id];
        fleet.Members[after.Pilot.Id] = after;
        SaveIdentity(fleet, after);
        Publish(fleet, new FleetEvent.Changed(before, after, fleet.Members.Count == 1));
    }

    public void MakeBoss(long actor, long fleetId, long target)
    {
        var fleet = BossFleet(actor, fleetId);
        if (!fleet.Members.ContainsKey(target))
        {
            throw new InvalidDataException("Choose a current fleet member.");
        }
        if (actor == target)
        {
            return;
        }
        ChangeMember(fleet, fleet.Members[actor] with { Job = 0 });
        ChangeMember(fleet, fleet.Members[target] with { Job = FleetRules.BossJob });
    }

    public void Kick(long actor, long fleetId, long target)
    {
        var fleet = MemberFleet(actor, fleetId);
        if (
            !fleet.Members.TryGetValue(target, out var member)
            || actor != target
                && !Boss(fleet, actor)
                && (!FleetRules.Subordinate(fleet.Members[actor], member) || member.Job == FleetRules.BossJob)
        )
        {
            throw new InvalidDataException("You cannot remove this fleet member.");
        }
        Leave(target, fleetId);
    }

    public long CreateWing(long actor, long fleetId)
    {
        var fleet = BossFleet(actor, fleetId);
        if (fleet.Wings.Count >= FleetRules.MaximumWings)
        {
            throw new InvalidDataException("The fleet has too many wings.");
        }
        long id = ++_nextId;
        fleet.Wings.Add(id, new(id, "", []));
        Publish(fleet, new FleetEvent.Hierarchy(id, null, false));
        return id;
    }

    public long CreateSquad(long actor, long fleetId, long wingId)
    {
        var fleet = MemberFleet(actor, fleetId);
        if (
            !Boss(fleet, actor)
            && (fleet.Members[actor].Placement.Role != FleetRole.WingCommander || fleet.Members[actor].Placement.WingId != wingId)
        )
        {
            throw new InvalidDataException("Wing authority is required.");
        }
        if (!fleet.Wings.TryGetValue(wingId, out var wing) || wing.Squads.Count >= FleetRules.MaximumSquads)
        {
            throw new InvalidDataException("Choose a wing with an available squad slot.");
        }
        long id = ++_nextId;
        fleet.Wings[wingId] = wing with { Squads = Array.AsReadOnly(wing.Squads.Append(new FleetSquad(id, "")).ToArray()) };
        Publish(fleet, new FleetEvent.Hierarchy(wingId, id, false));
        return id;
    }

    public void ChangeGroup(long actor, long fleetId, long groupId, bool squad, string? name)
    {
        var fleet = BossFleet(actor, fleetId);
        if (name is not null && (name.Length > FleetRules.MaximumNameLength || name.Any(char.IsControl)))
        {
            throw new InvalidDataException("Fleet group names allow at most ten characters without control codes.");
        }
        var wing = squad
            ? fleet.Wings.Values.SingleOrDefault(w => w.Squads.Any(s => s.Id == groupId))
            : fleet.Wings.GetValueOrDefault(groupId);
        if (wing is null)
        {
            throw new InvalidDataException("Unknown fleet group.");
        }
        if (name is null && fleet.Members.Values.Any(m => squad ? m.Placement.SquadId == groupId : m.Placement.WingId == groupId))
        {
            throw new InvalidDataException("Move members out before deleting this fleet group.");
        }
        if (!squad && name is null && wing.Squads.Count > 0)
        {
            throw new InvalidDataException("Delete the empty squads before deleting their wing.");
        }
        if (squad)
        {
            fleet.Wings[wing.Id] = wing with
            {
                Squads = Array.AsReadOnly(
                    wing.Squads.Where(s => name is not null || s.Id != groupId)
                        .Select(s => s.Id == groupId ? s with { Name = name! } : s)
                        .ToArray()
                ),
            };
        }
        else if (name is null)
        {
            fleet.Wings.Remove(groupId);
        }
        else
        {
            fleet.Wings[groupId] = wing with { Name = name };
        }
        Publish(fleet, new FleetEvent.Hierarchy(wing.Id, squad ? groupId : null, name is null, name));
    }

    public void SetFreeMove(long actor, long fleetId, bool enabled)
    {
        var fleet = BossFleet(actor, fleetId);
        bool old = fleet.FreeMove;
        fleet.FreeMove = enabled;
        Publish(fleet, new FleetEvent.Options(old, enabled));
    }

    public void SetMotd(long actor, long fleetId, string text)
    {
        var fleet = BossFleet(actor, fleetId);
        if (text.Length > FleetRules.MaximumMotdLength || text.Contains('\0'))
        {
            throw new InvalidDataException("Fleet MOTD is too long.");
        }
        fleet.Motd = text;
        Publish(fleet, new FleetEvent.Motd(text));
    }

    public void SetTakesWarp(long actor, long fleetId, bool enabled)
    {
        var fleet = MemberFleet(actor, fleetId);
        ChangeMember(fleet, fleet.Members[actor] with { TakesFleetWarp = enabled });
    }

    public void Broadcast(
        long actor,
        long fleetId,
        FleetBroadcastKind kind,
        FleetBroadcastScope scope,
        FleetBroadcastRange range,
        long? item,
        int? type,
        long now
    )
    {
        var fleet = MemberFleet(actor, fleetId);
        var sender = fleet.Members[actor];
        if (
            !Enum.IsDefined(kind)
            || !Enum.IsDefined(scope)
            || !Enum.IsDefined(range)
            || item is <= 0
            || type is <= 0
            || sender.Pilot.Position is null
            || _broadcastAt.TryGetValue(actor, out long last) && now - last < FleetRules.BroadcastInterval.Ticks
        )
        {
            throw new InvalidDataException("Invalid or rate-limited fleet broadcast.");
        }
        _broadcastAt[actor] = now;
        var value = new FleetEvent.Broadcast(new(kind, scope, sender.Pilot, item, type));
        foreach (var member in fleet.Members.Values)
        {
            bool direction =
                scope == FleetBroadcastScope.All
                || scope == FleetBroadcastScope.Down && FleetRules.Subordinate(sender, member)
                || scope == FleetBroadcastScope.Up && FleetRules.Subordinate(member, sender);
            bool location =
                range == FleetBroadcastRange.Universe
                || sender.Pilot.SystemId == member.Pilot.SystemId
                    && (range == FleetBroadcastRange.System || FleetRules.InCommandRange(sender.Pilot, member.Pilot));
            if (direction && location)
            {
                Queue(member.Pilot.Id, fleetId, value);
            }
        }
    }

    public void Warp(long actor, long fleetId, long target, double range, Eden.Gameplay.Flight.SpacePosition? point, long now)
    {
        var fleet = MemberFleet(actor, fleetId);
        var sender = fleet.Members[actor];
        if (
            sender.Placement.Role == FleetRole.Member
            || sender.Pilot.Position is null
            || !double.IsFinite(range)
            || range < 0
            || range > FleetRules.MaximumWarpRange
            || target == 0
            || point is { } destination
                && (!double.IsFinite(destination.X) || !double.IsFinite(destination.Y) || !double.IsFinite(destination.Z))
        )
        {
            throw new InvalidDataException("A commander in space must issue fleet warp with a valid distance.");
        }
        foreach (var member in fleet.Members.Values)
        {
            if (
                member.Pilot.Id != actor
                && member.TakesFleetWarp
                && FleetRules.Subordinate(sender, member)
                && FleetRules.InCommandRange(sender.Pilot, member.Pilot)
            )
            {
                Queue(
                    member.Pilot.Id,
                    fleetId,
                    new FleetEvent.Warp(
                        new(
                            fleetId,
                            actor,
                            member.Pilot.ShipId,
                            sender.Pilot.SystemId,
                            target,
                            range,
                            point,
                            sender.Pilot.Position,
                            _controlVersions.GetValueOrDefault(member.Pilot.Id),
                            checked(now + FleetRules.WarpOrderLifetime.Ticks)
                        )
                    )
                );
            }
        }
    }

    public bool CanExecuteWarp(long actor, FleetWarpOrder order, long now) =>
        order.ExpiresAt > now
        && Identity(actor)?.FleetId == order.FleetId
        && Identity(order.SenderId)?.FleetId == order.FleetId
        && _fleets.TryGetValue(order.FleetId, out var fleet)
        && fleet.Members[actor] is { TakesFleetWarp: true } member
        && member.Pilot.ShipId == order.ShipId
        && member.Pilot.SystemId == order.SystemId
        && fleet.Members[order.SenderId] is { } sender
        && sender.Placement.Role != FleetRole.Member
        && FleetRules.Subordinate(sender, member)
        && sender.Pilot.SystemId == order.SystemId
        && _controlVersions.GetValueOrDefault(actor) == order.ControlVersion
        && member.Pilot.Position is { } position
        && position.DistanceTo(order.Origin) <= FleetRules.CommandRange;

    public void CancelWarpOrders(long actor)
    {
        if (Identity(actor) is not null)
        {
            _controlVersions[actor] = checked(_controlVersions.GetValueOrDefault(actor) + 1);
        }
    }
}
