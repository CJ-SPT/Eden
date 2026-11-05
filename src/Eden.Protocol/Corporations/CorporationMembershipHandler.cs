using Eden.Gameplay.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

internal sealed partial class CorporationHandler
{
    private long? _observedPilot;
    private long _cursor;
    public bool IsMonitoring => _observedPilot.HasValue;

    public void Enter(PilotCallContext context)
    {
        if (_observedPilot != context.Pilot.PilotId)
        {
            _observedPilot = context.Pilot.PilotId;
            _cursor = membership.Head(context.Pilot.PilotId);
        }
        Advance(context);
    }

    public void Reset()
    {
        _observedPilot = null;
        _cursor = 0;
    }

    private void DrainChanges(PilotCallContext context)
    {
        if (_observedPilot != context.Pilot.PilotId)
        {
            return;
        }
        foreach (var change in membership.Changes(context.Pilot.PilotId, _cursor))
        {
            if (change.Administration is { } administrationChange)
            {
                context.Notifications.Add(new CorporationDetailsChanged(administrationChange));
            }
            if (change.Application is { } application)
            {
                context.Notifications.Add(new CorporationApplicationChanged(application));
            }
            if (change.Membership is { } member)
            {
                int corporation = member.PilotId == context.Pilot.PilotId ? member.CorporationId : context.Pilot.CorpId;
                context.Notifications.Add(new CorporationMemberChanged(corporation, member));
                if (context.Pilot.CorpId == member.PreviousCorporationId)
                {
                    context.Notifications.Add(
                        new CorporationCountChanged(
                            member.PreviousCorporationId,
                            member.PreviousMemberCount + 1,
                            member.PreviousMemberCount
                        )
                    );
                }
                if (context.Pilot.CorpId == member.CorporationId)
                {
                    context.Notifications.Add(
                        new CorporationCountChanged(member.CorporationId, member.MemberCount - 1, member.MemberCount)
                    );
                }
            }
            _cursor = change.Id;
        }
    }

    private PilotCallResult CallMembership(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords
    )
    {
        if (service == "corpmgr" && method == "GetAggressionSettings")
        {
            return args is [long id] && id is > 0 and <= int.MaxValue && keywords is null or { Count: 0 }
                ? new CorporationAggressionReply(corporations.Read((int)id)?.Details.FriendlyFireEnabled ?? false)
                : new PilotCallResult.Failure("Choose a corporation.");
        }
        // Build 3662168 fwEnlistmentSvc forwards this Details-panel query to
        // fwCharacterEnlistmentMgr, independently of the older facWarMgr service.
        if (service == "fwCharacterEnlistmentMgr" && method == "GetCorpAllowedEnlistmentFactions")
        {
            return args is [long id] && id is > 0 and <= int.MaxValue && keywords is null or { Count: 0 }
                ? BlueEmpty.List
                : new PilotCallResult.Failure("Choose a corporation.");
        }
        if (
            service != "corpRegistry"
            || method
                is not (
                    "InsertApplication"
                    or "UpdateApplicationOffer"
                    or "GetMyApplications"
                    or "GetApplications"
                    or "GetMyOldApplications"
                    or "GetOldApplications"
                    or "CanLeaveCurrentCorporation"
                    or "CanBeKickedOut"
                    or "KickOutMember"
                    or "GetMembersPaged"
                    or "GetMembersByIds"
                    or "GetAggressionSettings"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected corporation membership arguments.");
            }
            long pilot = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("InsertApplication", [long corporation, var text]) when corporation is > 0 and <= int.MaxValue:
                    membership.Apply(pilot, (int)corporation, Text(text));
                    Advance(context);
                    return PilotCallResult.None;
                case ("UpdateApplicationOffer", [long id, long applicant, long corporation, var text, long status, var response, var date])
                    when id > 0
                        && applicant > 0
                        && corporation is > 0 and <= int.MaxValue
                        && status is >= 0 and <= (long)CorporationApplicationStatus.Invited:
                    var update = new CorporationApplicationUpdate(
                        id,
                        applicant,
                        (int)corporation,
                        Text(text),
                        (CorporationApplicationStatus)status,
                        response is null ? "" : Text(response),
                        date switch
                        {
                            null => null,
                            long time when time > 0 => time,
                            _ => throw new InvalidDataException("Invalid application date."),
                        }
                    );
                    if (!Enum.IsDefined(update.Status))
                    {
                        throw new InvalidDataException("Invalid application status.");
                    }
                    membership.Update(pilot, update);
                    Advance(context);
                    return PilotCallResult.None;
                case ("GetMyApplications" or "GetApplications", []):
                    bool forCorporation = method == "GetApplications";
                    return new CorporationApplicationsReply(
                        membership.Applications(pilot, forCorporation).Where(a => !CorporationMembershipRules.Closed(a.Status)).ToArray(),
                        forCorporation
                    );
                case ("GetMyOldApplications" or "GetOldApplications", []):
                    return new BlueList<CorporationApplicationReply>(
                        membership
                            .Applications(pilot, method == "GetOldApplications")
                            .Where(a => CorporationMembershipRules.Closed(a.Status))
                            .Select(a => new CorporationApplicationReply(a))
                            .ToArray()
                    );
                case ("CanLeaveCurrentCorporation", []):
                    return new CorporationLeaveReply(membership.CanRemove(pilot, pilot));
                case ("CanBeKickedOut", [long target]):
                    return membership.CanRemove(pilot, target).Allowed;
                case ("KickOutMember", [long target]):
                    membership.Remove(pilot, target);
                    Advance(context);
                    return PilotCallResult.None;
                case ("GetMembersByIds", [List<object?> ids])
                    when ids.Count <= CorporationMembershipRules.MaximumMemberLookup && ids.All(id => id is long number && number > 0):
                    return new BlueList<PlayerCorporationMemberReply>(
                        corporations
                            .Members(pilot)
                            .Where(m => ids.Contains(m.PilotId))
                            .Select(m => new PlayerCorporationMemberReply(m))
                            .ToArray()
                    );
                case ("GetMembersPaged", [long page]) when page is > 0 and <= int.MaxValue / CorporationMembershipRules.MemberPageSize:
                    var members = corporations.Members(pilot);
                    return new CorporationMemberPage(
                        members
                            .Skip(((int)page - 1) * CorporationMembershipRules.MemberPageSize)
                            .Take(CorporationMembershipRules.MemberPageSize)
                            .ToArray(),
                        members.Count,
                        (int)page
                    );
                case ("GetAggressionSettings", []):
                    return new CorporationAggressionReply(corporations.Read(context.Pilot.CorpId)?.Details.FriendlyFireEnabled ?? false);
                default:
                    throw new InvalidDataException("Invalid corporation membership request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record CorporationApplicationReply(CorporationApplication Application) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["applicationID"] = Application.Id,
                ["characterID"] = Application.PilotId,
                ["corporationID"] = Application.CorporationId,
                ["applicationText"] = new BlueUnicode(Application.Text),
                ["status"] = (int)Application.Status,
                ["applicationDateTime"] = Application.AppliedAt,
                ["lastCorpUpdaterID"] = null,
                ["lastCorpUpdaterDateTime"] = Application.UpdatedAt,
                ["responseText"] = new BlueUnicode(Application.Response),
            }
        );
}

internal sealed record CorporationApplicationsReply(IReadOnlyList<CorporationApplication> Applications, bool ForCorporation) : BlueModel
{
    public override object ToBlue() =>
        Applications
            .GroupBy(a => ForCorporation ? a.PilotId : a.CorporationId)
            .ToDictionary(
                g => g.Key,
                g => (object?)new BlueList<CorporationApplicationReply>(g.Select(a => new CorporationApplicationReply(a)).ToArray())
            );
}

internal sealed record CorporationApplicationChanged(CorporationApplication Application)
    : PilotNotification("OnCorporationApplicationChanged")
{
    public override object?[] Arguments =>
        [Application.CorporationId, Application.PilotId, Application.Id, new CorporationApplicationReply(Application)];
}

internal sealed record CorporationMemberChanged(int CorporationId, CorporationMemberChange Change)
    : PilotNotification("OnCorporationMemberChanged")
{
    public override object?[] Arguments =>
        [
            CorporationId,
            Change.PilotId,
            new Dictionary<string, object?> { ["corporationID"] = new object?[] { Change.PreviousCorporationId, Change.CorporationId } },
        ];
}

internal sealed record CorporationCountChanged(int CorporationId, int Before, int After) : PilotNotification("OnCorporationChanged")
{
    public override object?[] Arguments =>
        [CorporationId, new Dictionary<string, object?> { ["memberCount"] = new object?[] { Before, After } }];
}

internal sealed record CorporationLeaveReply(CorporationLeavePermission Permission) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Permission.Allowed,
            Permission.Allowed ? null : "CustomInfo",
            Permission.Allowed
                ? null
                : new Dictionary<string, object?> { ["info"] = new BlueUnicode(Permission.Reason) },
        };
}

internal sealed record CorporationMemberPage(IReadOnlyList<CorporationMember> Members, int TotalCount, int Page) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "eve.common.script.util.pagedCollection.PagedResultSet",
            new Dictionary<string, object?>
            {
                ["collection"] = new BlueList<PlayerCorporationMemberReply>(
                    Members.Select(m => new PlayerCorporationMemberReply(m)).ToArray()
                ),
                ["totalCount"] = TotalCount,
                ["page"] = Page - 1,
                ["perPage"] = CorporationMembershipRules.MemberPageSize,
            }
        );
}

internal sealed record CorporationAggressionReply(bool Enabled) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "crimewatch.corp_aggression.settings.AggressionSettings",
            new Dictionary<string, object?> { ["_enableAfter"] = Enabled ? 0L : null, ["_disableAfter"] = Enabled ? null : 0L }
        );
}
