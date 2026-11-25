namespace Eden.Gameplay.Fleets;

// Pure transient fleet state. The World adapter serializes calls; no client/session or SQL dependency.
public sealed partial class FleetWorld(long seed)
{
    private sealed class Fleet(long id)
    {
        public long Id { get; } = id;
        public Dictionary<long, FleetMember> Members { get; } = [];
        public Dictionary<long, FleetWing> Wings { get; } = [];
        public bool FreeMove { get; set; }
        public int MaximumSize { get; set; } = FleetRules.MaximumMembers;
        public FleetPlacement? AutoJoinPlacement { get; set; }
        public FleetAdvertSettings? Advert { get; set; }
        public long AdvertCreatedAt { get; set; }
        public long AdvertUpdatedAt { get; set; }
        public Dictionary<long, FleetPilot> JoinRequests { get; } = [];
        public string Motd { get; set; } = "";
        public IReadOnlyList<FleetTargetTag> TargetTags { get; set; } = Array.Empty<FleetTargetTag>();
        public FleetOptions Options => new(FreeMove, Advert is not null, AutoJoinPlacement?.SquadId);
    }

    private sealed record Invitation(long FleetId, long Inviter, FleetPlacement Placement, long Expires, bool FromAdvert = false);

    private readonly Dictionary<long, Fleet> _fleets = [];
    private readonly Dictionary<long, FleetPilot> _online = [];
    private readonly Dictionary<Guid, long> _sessions = [];
    private readonly Dictionary<long, Guid> _owners = [];
    private readonly Dictionary<long, FleetIdentity> _identities = [];
    private readonly Dictionary<long, FleetMembership> _memberships = [];
    private long _membershipVersion;
    private readonly Dictionary<long, FleetMembership> _joinMemberships = [];
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
                foreach (var fleet in _fleets.Values)
                {
                    fleet.JoinRequests.Remove(previous);
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

    public void RefreshAffiliations(Func<long, FleetAffiliation?> resolve)
    {
        foreach (var pilot in _online.Values.ToArray())
        {
            if (
                resolve(pilot.Id) is not { } current
                || (
                    pilot.CorporationId == current.CorporationId
                    && pilot.AllianceId == current.AllianceId
                    && pilot.WarFaction == current.WarFaction
                )
            )
            {
                continue;
            }
            var updated = pilot with
            {
                CorporationId = current.CorporationId,
                AllianceId = current.AllianceId,
                WarFaction = current.WarFaction,
            };
            _online[pilot.Id] = updated;
            foreach (var fleet in _fleets.Values)
            {
                if (fleet.JoinRequests.ContainsKey(pilot.Id))
                {
                    fleet.JoinRequests[pilot.Id] = updated;
                }
            }
            if (Identity(pilot.Id) is { } identity)
            {
                var fleet = _fleets[identity.FleetId];
                var before = fleet.Members[pilot.Id];
                var after = before with { Pilot = updated };
                fleet.Members[pilot.Id] = after;
                Publish(fleet, new FleetEvent.Changed(before, after, false));
            }
        }
    }

    public FleetMembership? JoinMembership(long actor) => _joinMemberships.GetValueOrDefault(actor);

    public IReadOnlyList<FleetPilot> Fleetmates(long actor) =>
        Identity(actor) is { FleetId: var fleet }
            ? _fleets[fleet].Members.Values.Where(member => member.Pilot.Id != actor).Select(member => member.Pilot).ToArray()
            : [];

    public bool CanShareLoot(long actor, long owner, int system) =>
        Identity(actor) is { FleetId: var fleet }
        && Identity(owner)?.FleetId == fleet
        && _online.TryGetValue(owner, out var ownerPilot)
        && ownerPilot.SystemId == system;

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

    public void NotifyStandingChanged(long pilotId, long sourceId, double value)
    {
        Queue(pilotId, Identity(pilotId)?.FleetId ?? 0, new FleetEvent.StandingChanged(sourceId, value));
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
            fleet.Motd,
            fleet.Advert is not null,
            fleet.MaximumSize,
            fleet.AutoJoinPlacement?.SquadId
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

    public IReadOnlyList<(FleetPilot Pilot, bool Detailed)> Composition(long actor, long fleetId)
    {
        var fleet = MemberFleet(actor, fleetId);
        var viewer = fleet.Members[actor];
        bool boss = Boss(fleet, actor);
        return fleet
            .Members.Values.OrderBy(member => member.Pilot.Id)
            .Select(member => (member.Pilot, boss || FleetRules.Subordinate(viewer, member)))
            .ToArray();
    }

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
        foreach (var other in _fleets.Values)
        {
            other.JoinRequests.Remove(member.Pilot.Id);
        }
        fleet.Members.Add(member.Pilot.Id, member);
        var membership = new FleetMembership(fleet.Id, checked(++_membershipVersion));
        _memberships[member.Pilot.Id] = membership;
        _joinMemberships[member.Pilot.Id] = membership;
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
        if (SlotOccupied(fleet, target, placement))
        {
            throw new InvalidDataException("This command position is occupied.");
        }
    }

    private static bool SlotOccupied(Fleet fleet, long target, FleetPlacement placement) =>
        placement.Role != FleetRole.Member && fleet.Members.Values.Any(m => m.Pilot.Id != target && m.Placement == placement);

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

    public void Invite(long actor, long fleetId, long target, FleetPlacement requested, long now, Action? commitCommunication = null)
    {
        var fleet = MemberFleet(actor, fleetId);
        var placement = Placement(fleet, requested);
        AuthorizePlacement(fleet, actor, target, placement);
        CheckSlot(fleet, target, placement);
        if (!_online.ContainsKey(target) || Identity(target) is not null || actor == target || fleet.Members.Count >= fleet.MaximumSize)
        {
            throw new InvalidDataException("Invite an online pilot outside a fleet with an available position.");
        }
        if (commitCommunication is not null && IsPendingInvitation(fleetId, actor, target, placement, now))
        {
            return;
        }
        long expires = checked(now + FleetRules.InvitationLifetime.Ticks);
        commitCommunication?.Invoke();
        PublishInvitation(fleet, actor, target, placement, expires);
    }

    public FleetInviteBatchResult InviteMany(
        long actor,
        long fleetId,
        IReadOnlyList<long> targets,
        FleetPlacement requested,
        long now,
        Func<long, bool> permitsContact,
        Action<IReadOnlyList<long>> commitCharges
    )
    {
        if (targets.Count is < 1 or > FleetRules.MaximumMembers || targets.Any(id => id <= 0))
        {
            throw new InvalidDataException("Choose a valid group of pilots to invite.");
        }
        var fleet = MemberFleet(actor, fleetId);
        long[] candidates = targets.Distinct().Where(id => id != actor && _online.ContainsKey(id) && permitsContact(id)).ToArray();
        FleetPlacement placement;
        try
        {
            placement = Placement(fleet, requested);
        }
        catch (InvalidDataException)
        {
            return new([], candidates.ToDictionary(id => id, _ => FleetInviteRejection.NoPosition));
        }
        long expires = checked(now + FleetRules.InvitationLifetime.Ticks);
        List<long> invited = [];
        Dictionary<long, FleetInviteRejection> rejected = [];
        foreach (long target in candidates)
        {
            if (BatchInviteRejection(fleet, actor, target, placement) is { } rejection)
            {
                rejected.Add(target, rejection);
            }
            else if (!IsPendingInvitation(fleetId, actor, target, placement, now))
            {
                invited.Add(target);
            }
        }
        if (invited.Count > 0)
        {
            commitCharges(invited);
            foreach (long target in invited)
            {
                PublishInvitation(fleet, actor, target, placement, expires);
            }
        }
        return new(invited, rejected);
    }

    private bool IsPendingInvitation(long fleetId, long actor, long target, FleetPlacement placement, long now) =>
        _invitations.GetValueOrDefault(target) is { } pending
        && pending.FleetId == fleetId
        && pending.Inviter == actor
        && pending.Placement == placement
        && pending.Expires > now;

    private FleetInviteRejection? BatchInviteRejection(Fleet fleet, long actor, long target, FleetPlacement placement)
    {
        try
        {
            AuthorizePlacement(fleet, actor, target, placement);
        }
        catch (InvalidDataException)
        {
            return FleetInviteRejection.NotAllowed;
        }
        if (Identity(target) is not null)
        {
            return FleetInviteRejection.AlreadyInFleet;
        }
        if (fleet.Members.Count >= fleet.MaximumSize)
        {
            return FleetInviteRejection.FleetFull;
        }
        return SlotOccupied(fleet, target, placement) ? FleetInviteRejection.NoPosition : null;
    }

    private void PublishInvitation(Fleet fleet, long actor, long target, FleetPlacement placement, long expires)
    {
        bool militiaApplication = fleet.JoinRequests.Remove(target) && fleet.Advert?.Scope.HasFlag(FleetInviteScope.Militia) == true;
        _invitations[target] = new(fleet.Id, actor, placement, expires, militiaApplication);
        Queue(target, fleet.Id, new FleetEvent.Invitation(fleet.Id, actor));
    }

    public void Accept(long actor, long fleetId, long now, double security = 0)
    {
        if (
            !_invitations.TryGetValue(actor, out var invitation)
            || invitation.FleetId != fleetId
            || invitation.Expires <= now
            || !_fleets.TryGetValue(fleetId, out var fleet)
            || Identity(actor) is not null
            || !_online.TryGetValue(actor, out var pilot)
            || !fleet.Members.ContainsKey(invitation.Inviter)
            || fleet.Members.Count >= fleet.MaximumSize
        )
        {
            throw new InvalidDataException("This fleet invitation is no longer valid.");
        }
        if (
            invitation.FromAdvert
            && (
                fleet.Advert is not { } advert
                || !Visible(fleet, pilot, security)
                || fleet.Members.Count >= (advert.JoinLimit ?? FleetRules.MaximumMembers)
            )
        )
        {
            throw new InvalidDataException("This fleet advert is no longer accepting your application.");
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
        CancelMovementOrders(actor);
        Publish(fleet, new FleetEvent.Left(actor));
        bool boss = Boss(fleet, actor);
        fleet.Members.Remove(actor);
        _identities.Remove(actor);
        _memberships.Remove(actor);
        _joinMemberships.Remove(actor);
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
            if (fleet.Advert is { UpdateOnBossChange: false })
            {
                ClearAdvert(fleet);
            }
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

    public IReadOnlyList<long> MoveMany(long actor, long fleetId, IReadOnlyList<long> targets, FleetPlacement requested)
    {
        var fleet = MemberFleet(actor, fleetId);
        if (!Boss(fleet, actor) && fleet.Members[actor].Placement.Role == FleetRole.Member)
        {
            throw new InvalidDataException("Only a commander or the fleet boss may move a group.");
        }
        if (
            requested.Role != FleetRole.Member
            || targets.Count is < 1 or > FleetRules.MaximumMembers
            || targets.Any(id => id <= 0)
            || targets.Distinct().Count() != targets.Count
        )
        {
            throw new InvalidDataException("Choose distinct fleet members and a squad.");
        }
        var placement = Placement(fleet, requested);
        List<FleetMember> moves = [];
        // Authorize the entire selection against the original hierarchy, including a commander moving themselves.
        foreach (long target in targets)
        {
            if (!fleet.Members.TryGetValue(target, out var member) || member.Placement == placement)
            {
                continue;
            }
            try
            {
                AuthorizePlacement(fleet, actor, target, placement);
                moves.Add(member with { Placement = placement });
            }
            catch (InvalidDataException)
            {
                Queue(actor, fleetId, new FleetEvent.MoveFailed(target));
            }
        }
        if (moves.Count > 0)
        {
            Publish(fleet, new FleetEvent.MembersMoved(moves.Select(member => UpdateMember(fleet, member)).ToArray()));
        }
        return moves.Select(member => member.Pilot.Id).ToArray();
    }

    private void ChangeMember(Fleet fleet, FleetMember after) => Publish(fleet, UpdateMember(fleet, after));

    private FleetEvent.Changed UpdateMember(Fleet fleet, FleetMember after)
    {
        var before = fleet.Members[after.Pilot.Id];
        fleet.Members[after.Pilot.Id] = after;
        SaveIdentity(fleet, after);
        return new(before, after, fleet.Members.Count == 1);
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
        if (fleet.Advert is { UpdateOnBossChange: false })
        {
            ClearAdvert(fleet);
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
        if (squad && name is null && fleet.AutoJoinPlacement?.SquadId == groupId)
        {
            ApplyAutoJoinPlacement(fleet, null);
        }
    }

    public void SetFreeMove(long actor, long fleetId, bool enabled)
    {
        var fleet = BossFleet(actor, fleetId);
        var old = fleet.Options;
        fleet.FreeMove = enabled;
        Publish(fleet, new FleetEvent.Options(old, fleet.Options));
    }

    public int SetMaximumSize(long actor, long fleetId, int size)
    {
        var fleet = BossFleet(actor, fleetId);
        if (size is < 1 or > FleetRules.MaximumMembers)
        {
            throw new InvalidDataException("Choose a maximum fleet size between 1 and 256.");
        }
        fleet.MaximumSize = size;
        return size;
    }

    public void SetAutoJoinSquad(long actor, long fleetId, long? squad)
    {
        var fleet = BossFleet(actor, fleetId);
        FleetPlacement? placement = null;
        if (squad is long id)
        {
            var wing =
                fleet.Wings.Values.SingleOrDefault(wing => wing.Squads.Any(group => group.Id == id))
                ?? throw new InvalidDataException("Choose an existing squad in this fleet.");
            placement = new(wing.Id, id, FleetRole.Member);
        }
        ApplyAutoJoinPlacement(fleet, placement);
    }

    private void ApplyAutoJoinPlacement(Fleet fleet, FleetPlacement? placement)
    {
        if (fleet.AutoJoinPlacement == placement)
        {
            return;
        }
        var old = fleet.Options;
        fleet.AutoJoinPlacement = placement;
        Publish(fleet, new FleetEvent.Options(old, fleet.Options));
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

    public void SetAcceptsConduitJumps(long actor, long fleetId, bool enabled)
    {
        var fleet = MemberFleet(actor, fleetId);
        ChangeMember(fleet, fleet.Members[actor] with { AcceptsConduitJumps = enabled });
    }

    public void SetAcceptsRegroup(long actor, long fleetId, bool enabled)
    {
        var fleet = MemberFleet(actor, fleetId);
        ChangeMember(fleet, fleet.Members[actor] with { AcceptsFleetRegroups = enabled });
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
                            checked(now + FleetRules.MovementOrderLifetime.Ticks)
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

    public void Regroup(long actor, long fleetId, long now)
    {
        var fleet = MemberFleet(actor, fleetId);
        var commander = fleet.Members[actor];
        if (commander.Placement.Role == FleetRole.Member || commander.Pilot is not { InTransit: false, Position: not null })
        {
            throw new InvalidDataException("A commander in space must issue fleet regroup.");
        }
        var membership = _memberships[actor];
        long expiresAt = checked(now + FleetRules.MovementOrderLifetime.Ticks);
        foreach (var member in fleet.Members.Values.Where(member => CanRegroup(commander, member)))
        {
            long recipient = member.Pilot.Id;
            Queue(
                recipient,
                fleetId,
                new FleetEvent.Regroup(
                    new(
                        actor,
                        commander.Pilot.ShipId,
                        membership,
                        _memberships[recipient],
                        _controlVersions.GetValueOrDefault(recipient),
                        expiresAt
                    )
                )
            );
        }
    }

    public bool CanExecuteRegroup(long actor, FleetRegroupOrder order, long now) =>
        order.ExpiresAt > now
        && Membership(actor) == order.Recipient
        && Membership(order.CommanderId) == order.Commander
        && order.Recipient.FleetId == order.Commander.FleetId
        && _controlVersions.GetValueOrDefault(actor) == order.ControlVersion
        && _fleets.TryGetValue(order.Recipient.FleetId, out var fleet)
        && fleet.Members[order.CommanderId] is { } commander
        && commander.Pilot.ShipId == order.CommanderShipId
        && CanRegroup(commander, fleet.Members[actor]);

    private static bool CanRegroup(FleetMember commander, FleetMember member) =>
        commander.Pilot.Id != member.Pilot.Id
        && member.AcceptsFleetRegroups
        && !commander.Pilot.InTransit
        && !member.Pilot.InTransit
        && FleetRules.Subordinate(commander, member)
        && FleetRules.InCommandRange(commander.Pilot, member.Pilot);

    public void CancelMovementOrders(long actor)
    {
        if (Identity(actor) is not null)
        {
            _controlVersions[actor] = checked(_controlVersions.GetValueOrDefault(actor) + 1);
        }
    }
}
