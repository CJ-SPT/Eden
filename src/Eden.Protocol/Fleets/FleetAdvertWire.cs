using Eden.Gameplay.Fleets;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Fleets;

internal sealed record FleetAdvertReply(FleetAdvert Advert, bool Listed, double LeaderSecurity) : BlueModel
{
    public override object ToBlue()
    {
        var settings = Advert.Settings;
        object? Entities(IReadOnlyList<long> ids) => Listed ? null : new BlueConstructor("__builtin__.set", [ids.Cast<object?>().ToList()]);
        return new Dictionary<string, object?>
        {
            ["fleetID"] = Advert.FleetId,
            ["leader"] = new FleetApplicantReply(Advert.Leader.Pilot, LeaderSecurity),
            ["solarSystemID"] = Advert.Leader.Pilot.SystemId,
            ["numMembers"] = Advert.Members,
            ["dateCreated"] = Advert.CreatedAt,
            ["advertTime"] = Advert.UpdatedAt,
            ["fleetName"] = new BlueUnicode(settings.Name),
            ["description"] = new BlueUnicode(settings.Description),
            ["inviteScope"] = (int)settings.Scope,
            ["activityValue"] = settings.Activity,
            ["useAdvanceOptions"] = settings.Advanced,
            ["newPlayerFriendly"] = settings.NewPlayerFriendly,
            ["joinNeedsApproval"] = settings.Approval,
            ["hideInfo"] = settings.HideInfo,
            ["updateOnBossChange"] = settings.UpdateOnBossChange,
            ["advertJoinLimit"] = settings.JoinLimit,
            ["public_minStanding"] = settings.Public.MinimumStanding,
            ["public_minSecurity"] = settings.Public.MinimumSecurity,
            ["public_allowedEntities"] = Entities(settings.Public.Allowed),
            ["public_disallowedEntities"] = Entities(settings.Public.Disallowed),
            ["membergroups_minStanding"] = settings.MemberGroups.MinimumStanding,
            ["membergroups_minSecurity"] = settings.MemberGroups.MinimumSecurity,
            ["membergroups_allowedEntities"] = Entities(settings.MemberGroups.Allowed),
            ["membergroups_disallowedEntities"] = Entities(settings.MemberGroups.Disallowed),
        };
    }
}

internal sealed record FleetApplicantReply(FleetPilot Pilot, double? Security = null) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "util.KeyVal",
            new Dictionary<string, object?>
            {
                ["charID"] = Pilot.Id,
                ["corpID"] = Pilot.CorporationId,
                ["allianceID"] = null,
                ["warFactionID"] = null,
                ["securityStatus"] = Security,
                ["shipTypeID"] = Pilot.ShipTypeId,
                ["solarSystemID"] = Pilot.SystemId,
            }
        );
}

internal sealed record FleetRegistrationNotification(bool Before, bool After, bool FreeMove) : FleetNotification("OnFleetOptionsChanged")
{
    public override object?[] Arguments => [new FleetOptionsReply(FreeMove, Before), new FleetOptionsReply(FreeMove, After)];
}

internal sealed record FleetJoinRequestNotification(FleetPilot Pilot) : FleetNotification("OnFleetJoinRequest")
{
    public override object?[] Arguments => [new FleetApplicantReply(Pilot)];
}
