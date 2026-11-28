using Eden.Gameplay.Corporations;
using Eden.Protocol.Calendar;
using Eden.Protocol.Docking;
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
            _lastMilitia = null;
            _observedPilot = context.Pilot.PilotId;
            _cursor = membership.Head(context.Pilot.PilotId);
            _allianceCursor = allianceMembership.Head;
            _observedAlliances.Clear();
            _observedCorporationAlliances.Clear();
        }
        Advance(context);
    }

    public void Reset()
    {
        _observedPilot = null;
        _lastMilitia = null;
        _cursor = 0;
        _allianceCursor = 0;
        _observedAlliances.Clear();
        _observedCorporationAlliances.Clear();
        allianceSocial.Reset();
    }

    private void DrainChanges(PilotCallContext context)
    {
        if (_observedPilot != context.Pilot.PilotId)
        {
            return;
        }
        foreach (var change in membership.Changes(context.Pilot.PilotId, _cursor))
        {
            if (change.Storage is { MarketDelivery: true } delivery)
            {
                foreach (var item in delivery.Items.Where(item => item.Item.OwnerId == context.Pilot.PilotId))
                {
                    InventoryChangesWire.NotifyItemChange(context, item);
                }
            }
            if (
                change.Storage is { Office: { } office } stored
                && stored.ActorId != context.Pilot.PilotId
                && office.CorporationId == corporations.Identity(context.Pilot.PilotId).Id
            )
            {
                if (stored.RentalChanged)
                {
                    context.Notifications.Add(new CorporationOfficeRentalChanged(office.CorporationId, office.Id));
                }
                else if (context.Pilot.Position is null && context.Pilot.StationId == office.StationId)
                {
                    foreach (var item in stored.Items)
                    {
                        bool beforeVisible =
                            item.Previous is { } old
                            && old.LocationId == office.Id
                            && storage.CanObserve(context.Pilot.PilotId, office.Id, old.FlagId);
                        bool afterVisible =
                            item.Item.LocationId == office.Id && storage.CanObserve(context.Pilot.PilotId, office.Id, item.Item.FlagId);
                        if (afterVisible)
                        {
                            InventoryChangesWire.NotifyItemChange(context, beforeVisible ? item : item with { Previous = null });
                        }
                        else if (beforeVisible)
                        {
                            InventoryChangesWire.NotifyItemChange(context, new(item.Previous! with { Quantity = 0 }, item.Previous));
                        }
                    }
                }
            }
            if (change.Calendar is { } calendar && calendar.Event.OwnerId == corporations.Identity(context.Pilot.PilotId).Id)
            {
                context.Notifications.Add(CalendarNotification.From(calendar));
            }
            if (change.Shares is { } shareChange && shares.CanObserve(context.Pilot.PilotId, shareChange.ShareholderId))
            {
                context.Notifications.Add(new CorporationShareCacheInvalidated(shareChange));
            }
            if (change.Administration is { } administrationChange)
            {
                if (
                    administrationChange.BlueprintLock is { } blueprintLock
                    && blueprintLock.CorporationId == corporations.Identity(context.Pilot.PilotId).Id
                )
                {
                    context.Notifications.Add(new CorporationBlueprintLockNotification(blueprintLock));
                }
                if (administrationChange.Before.RecruitmentRevision != administrationChange.After.RecruitmentRevision)
                {
                    context.Notifications.Add(new CorporationRecruitmentChanged());
                }
                var welcome = administrationChange.After.Communications?.WelcomeMail;
                if (welcome is not null && welcome != administrationChange.Before.Communications?.WelcomeMail)
                {
                    context.Notifications.Add(new CorporationWelcomeMailChanged(welcome));
                }
                if (administrationChange.Vote is { } vote)
                {
                    context.Notifications.Add(
                        new CorporationVoteNotification(
                            vote.Event == CorporationVoteEvent.Cast ? "OnVoteCast" : "OnVoteCaseChanged",
                            administrationChange.CorporationId,
                            vote.CaseId
                        )
                    );
                    if (vote.Event is CorporationVoteEvent.Closed or CorporationVoteEvent.Sanctioned)
                    {
                        context.Notifications.Add(
                            new CorporationVoteNotification("OnSanctionedActionChanged", administrationChange.CorporationId, vote.CaseId)
                        );
                    }
                }
                else
                {
                    context.Notifications.Add(new CorporationDetailsChanged(administrationChange));
                }
            }
            if (change.Roles is { } roleChange)
            {
                context.Notifications.Add(new CorporationRolesChanged(roleChange));
            }
            if (change.Title is { } titleChange)
            {
                context.Notifications.Add(new CorporationTitleChanged(titleChange));
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
        if (
            service != "corpRegistry"
            || method
                is not (
                    "InsertApplication"
                    or "InsertInvitation"
                    or "UpdateApplicationOffer"
                    or "GetMyApplications"
                    or "GetApplications"
                    or "GetMyOldApplications"
                    or "GetOldApplications"
                    or "CanLeaveCurrentCorporation"
                    or "CanBeKickedOut"
                    or "KickOutMember"
                    or "KickOutMembers"
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
                case ("InsertInvitation", [long invitee]) when invitee > 0:
                    var invitation = membership.Invite(pilot, invitee);
                    Advance(context);
                    return invitation.Id;
                case ("InsertApplication", [long corporation, var text]) when corporation is > 0 and <= int.MaxValue:
                    var application = membership.Apply(pilot, (int)corporation, Text(text));
                    Advance(context);
                    return application.Id;
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
                        membership.Applications(pilot, forCorporation, CorporationApplicationView.Current),
                        forCorporation
                    );
                case ("GetMyOldApplications" or "GetOldApplications", []):
                    return new BlueList<CorporationApplicationReply>(
                        membership
                            .Applications(pilot, method == "GetOldApplications", CorporationApplicationView.Older)
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
                case ("KickOutMembers", [List<object?> targets])
                    when targets.Count is >= 1 and <= CorporationMembershipRules.MaximumMemberRemoval
                        && targets.All(id => id is long number && number > 0):
                    var removed = membership.RemoveMany(pilot, targets.Cast<long>().ToArray());
                    Advance(context);
                    return new CorporationMemberRemovalReply(removed);
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

internal sealed record CorporationMemberRemovalReply(CorporationMemberRemovalResult Result) : BlueModel
{
    // Build 3662168 base_corporation.KickOutMultipleMembers indexes both native Python lists.
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["kicked"] = Result.Kicked.Select(id => (object?)id).ToList(),
            ["notKicked"] = Result.NotKicked.Select(id => (object?)id).ToList(),
        };
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
            Permission.Allowed ? null : Permission.ErrorName,
            Permission.Allowed ? null
            : Permission.ErrorName == "CustomInfo" ? new Dictionary<string, object?> { ["info"] = new BlueUnicode(Permission.Reason) }
            : new Dictionary<string, object?>(),
        };
}

internal sealed record CorporationMemberPage(IReadOnlyList<CorporationMember> Members, int TotalCount, int Page) : BlueModel
{
    // Build 3662168 PagedResultSet is a new-style class with a four-argument constructor.
    public override object ToBlue() =>
        new BlueConstructor(
            "eve.common.script.util.pagedCollection.PagedResultSet",
            [
                new BlueList<PlayerCorporationMemberReply>(Members.Select(m => new PlayerCorporationMemberReply(m)).ToArray()),
                TotalCount,
                Page - 1,
                CorporationMembershipRules.MemberPageSize,
            ]
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
