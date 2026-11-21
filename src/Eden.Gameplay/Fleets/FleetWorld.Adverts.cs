using Eden.Gameplay.Characters;

namespace Eden.Gameplay.Fleets;

public sealed partial class FleetWorld
{
    public FleetAdvert? MyAdvert(long actor) =>
        Identity(actor) is { FleetId: var id } && _fleets[id].Advert is not null ? AdvertSnapshot(_fleets[id]) : null;

    private static FleetAdvert AdvertSnapshot(Fleet fleet) =>
        new(
            fleet.Id,
            fleet.Members.Values.Single(m => (m.Job & FleetRules.BossJob) != 0),
            fleet.Members.Count,
            fleet.AdvertCreatedAt,
            fleet.AdvertUpdatedAt,
            fleet.Advert!
        );

    public FleetAdvert SetAdvert(long actor, FleetAdvertSettings settings, long now)
    {
        var fleet = BossFleet(actor, Identity(actor)?.FleetId ?? 0);
        if (
            string.IsNullOrWhiteSpace(settings.Name)
            || settings.Name.Length > FleetRules.MaximumAdvertNameLength
            || settings.Description.Length > FleetRules.MaximumAdvertDescriptionLength
            || settings.JoinLimit is < 1 or > FleetRules.MaximumMembers
            || (
                settings.Scope
                & ~(
                    FleetInviteScope.Corporation
                    | FleetInviteScope.Alliance
                    | FleetInviteScope.Militia
                    | FleetInviteScope.Public
                    | FleetInviteScope.PublicOpen
                )
            ) != 0
        )
        {
            throw new InvalidDataException(
                "Choose a fleet name, description and corporation, alliance, militia, or public access settings."
            );
        }
        if (settings.Scope.HasFlag(FleetInviteScope.Militia) && fleet.Members[actor].Pilot.WarFaction is null)
        {
            throw new InvalidDataException("Enlist with a militia before advertising a militia fleet.");
        }
        static FleetAdvertAccess Copy(FleetAdvertAccess access)
        {
            if (
                access.MinimumStanding is { } standing
                    && (!double.IsFinite(standing) || standing is < StandingRules.Minimum or > StandingRules.Maximum)
                || access.MinimumSecurity is { } security
                    && (!double.IsFinite(security) || security is < SecurityStatusRules.Minimum or > SecurityStatusRules.Maximum)
                || access.Allowed.Count > FleetRules.MaximumAdvertEntities
                || access.Disallowed.Count > FleetRules.MaximumAdvertEntities
                || access.Allowed.Concat(access.Disallowed).Any(id => id <= 0)
            )
            {
                throw new InvalidDataException("Choose valid fleet advert access restrictions.");
            }
            return access with
            {
                Allowed = Array.AsReadOnly(access.Allowed.Distinct().ToArray()),
                Disallowed = Array.AsReadOnly(access.Disallowed.Distinct().ToArray()),
            };
        }
        // Snapshot both lists before publishing; callers cannot mutate shared fleet state.
        var copy = settings with
        {
            Public = Copy(settings.Public),
            MemberGroups = Copy(settings.MemberGroups),
        };
        bool registered = fleet.Advert is not null;
        fleet.Advert = copy;
        fleet.AdvertUpdatedAt = now;
        if (!registered)
        {
            fleet.AdvertCreatedAt = now;
            Publish(fleet, new FleetEvent.Registration(false, true, fleet.FreeMove));
        }
        return AdvertSnapshot(fleet);
    }

    public void RemoveAdvert(long actor)
    {
        var fleet = BossFleet(actor, Identity(actor)?.FleetId ?? 0);
        ClearAdvert(fleet);
    }

    private void ClearAdvert(Fleet fleet)
    {
        if (fleet.Advert is null)
        {
            return;
        }
        fleet.Advert = null;
        fleet.JoinRequests.Clear();
        Publish(fleet, new FleetEvent.Registration(true, false, fleet.FreeMove));
    }

    public IReadOnlyList<FleetAdvert> AvailableAdverts(long actor, double security) =>
        _online.TryGetValue(actor, out var pilot)
            ? _fleets.Values.Where(f => Visible(f, pilot, security)).OrderBy(f => f.Id).Select(AdvertSnapshot).ToArray()
            : [];

    private static bool Visible(Fleet fleet, FleetPilot pilot, double security)
    {
        if (fleet.Advert is not { } ad)
        {
            return false;
        }
        var leader = fleet.Members.Values.Single(m => (m.Job & FleetRules.BossJob) != 0).Pilot;
        static bool Access(FleetAdvertAccess access, FleetPilot pilot, double security) =>
            double.IsFinite(security)
            && (access.MinimumSecurity is null || security >= access.MinimumSecurity)
            && !access.Disallowed.Contains(pilot.Id)
            && !access.Disallowed.Contains(pilot.CorporationId)
            && !(pilot.AllianceId is int excludedAlliance && access.Disallowed.Contains(excludedAlliance))
            // The boss supplies the entity sets resolved from the selected standings threshold.
            && (
                access.MinimumStanding is null
                || access.Allowed.Contains(pilot.Id)
                || access.Allowed.Contains(pilot.CorporationId)
                || pilot.AllianceId is int allowedAlliance && access.Allowed.Contains(allowedAlliance)
            );
        return fleet.Members.ContainsKey(pilot.Id)
            || (
                (
                    ad.Scope.HasFlag(FleetInviteScope.Corporation) && pilot.CorporationId == leader.CorporationId
                    || ad.Scope.HasFlag(FleetInviteScope.Alliance) && pilot.AllianceId.HasValue && pilot.AllianceId == leader.AllianceId
                    || ad.Scope.HasFlag(FleetInviteScope.Militia) && pilot.WarFaction.HasValue && pilot.WarFaction == leader.WarFaction
                ) && Access(ad.MemberGroups, pilot, security)
            )
            || ((ad.Scope & (FleetInviteScope.Public | FleetInviteScope.PublicOpen)) != 0 && Access(ad.Public, pilot, security));
    }

    public bool ApplyToFleet(long actor, long fleetId, double security, long now)
    {
        if (
            Identity(actor) is not null
            || !_online.TryGetValue(actor, out var pilot)
            || !_fleets.TryGetValue(fleetId, out var fleet)
            || !Visible(fleet, pilot, security)
            || fleet.Members.Count >= Math.Min(fleet.Advert!.JoinLimit ?? FleetRules.MaximumMembers, FleetRules.MaximumMembers)
        )
        {
            throw new InvalidDataException("This fleet advert is not accepting your application.");
        }
        if (fleet.Advert.Approval)
        {
            if (!fleet.JoinRequests.ContainsKey(actor))
            {
                if (fleet.JoinRequests.Count >= FleetRules.MaximumMembers)
                {
                    throw new InvalidDataException("This fleet has too many pending applications.");
                }
                fleet.JoinRequests.Add(actor, pilot);
                foreach (
                    var member in fleet.Members.Values.Where(m =>
                        (m.Job & FleetRules.BossJob) != 0 || m.Placement.Role == FleetRole.Commander
                    )
                )
                {
                    Queue(member.Pilot.Id, fleetId, new FleetEvent.JoinRequest(pilot));
                }
            }
            return true;
        }
        long boss = fleet.Members.Values.Single(m => (m.Job & FleetRules.BossJob) != 0).Pilot.Id;
        Invite(boss, fleetId, actor, new(FleetRules.NoGroup, FleetRules.NoGroup, FleetRole.Member), now);
        _invitations[actor] = _invitations[actor] with { FromAdvert = true };
        return false;
    }

    public IReadOnlyList<FleetPilot> JoinRequests(long actor, long fleetId)
    {
        var fleet = ApplicationFleet(actor, fleetId);
        return fleet.JoinRequests.Values.Where(p => _online.ContainsKey(p.Id) && Identity(p.Id) is null).ToArray();
    }

    public void RejectApplication(long actor, long fleetId, long target) => ApplicationFleet(actor, fleetId).JoinRequests.Remove(target);

    private Fleet ApplicationFleet(long actor, long fleetId)
    {
        var fleet = MemberFleet(actor, fleetId);
        if (!Boss(fleet, actor) && fleet.Members[actor].Placement.Role != FleetRole.Commander)
        {
            throw new InvalidDataException("Only the fleet boss or commander may manage applications.");
        }
        return fleet;
    }
}
