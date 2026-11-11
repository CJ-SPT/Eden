using Eden.Gameplay.AccessGroups;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.AccessGroups;

[Injectable]
internal sealed class AccessGroupsHandler(IPilotAccessGroups groups, AccessGroupMonitor monitor) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "ownerGroupManager")
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected access list arguments.");
            }
            long pilotId = context.Pilot.PilotId;
            monitor.Advance(context);
            switch (method, args)
            {
                case ("GetMyGroups" or "SearchGroups" or "GetMyGroupsToUseForBookmarks", []):
                    return List(monitor.Prime(context));
                case ("GetGroup" or "GetPublicGroupInfo", [long id]):
                    return groups.Find(pilotId, id) is { } group ? new AccessGroupReply(group) : PilotCallResult.None;
                case ("GetGroupsMany", [var ids]):
                    return List(Ids(ids).Select(id => groups.Find(pilotId, id)).OfType<AccessGroup>().ToArray());
                case ("GetGroupLogs", [long id]):
                    return new BlueList<AccessGroupLogReply>(groups.Logs(pilotId, id).Select(l => new AccessGroupLogReply(l)).ToArray());
                case ("GetMembers", [long id]):
                    return Members(pilotId, id);
                case ("GetMembersForMultipleGroups", [var ids]):
                    return new BlueMap<long, BlueList<AccessGroupMemberReply>>(
                        Ids(ids)
                            .Distinct()
                            .Where(id => groups.Find(pilotId, id) is not null)
                            .ToDictionary(id => id, id => Members(pilotId, id))
                    );
                case ("Create" or "CreateGroup", [var name, var description]):
                    return new AccessGroupReply(groups.Create(pilotId, Text(name), Text(description)));
                case ("Delete" or "DeleteGroup", [long id]):
                    groups.Delete(pilotId, id);
                    return PilotCallResult.None;
                case ("UpdateName", [long id, var name]):
                    groups.Update(pilotId, id, Text(name), null);
                    return PilotCallResult.None;
                case ("UpdateDescription", [long id, var description]):
                    groups.Update(pilotId, id, null, Text(description));
                    return PilotCallResult.None;
                case ("AddMembers", [long id, Dictionary<long, object?> members]):
                    groups.ChangeMembers(pilotId, id, members.Select(m => new AccessGroupMember(m.Key, Role(m.Value))).ToArray());
                    return BlueEmpty.List;
                case ("DeleteMembers" or "RemoveMembers", [long id, var ids]):
                    groups.ChangeMembers(
                        pilotId,
                        id,
                        Ids(ids).Select(id => new AccessGroupMember(id, AccessGroupRole.None)).ToArray(),
                        true
                    );
                    return BlueEmpty.List;
                case ("UpdateMembershipTypes", [long id, var ids, var role]):
                    var selectedRole = Role(role);
                    groups.ChangeMembers(pilotId, id, Ids(ids).Select(id => new AccessGroupMember(id, selectedRole)).ToArray());
                    return BlueEmpty.List;
                default:
                    return PilotCallResult.Unsupported;
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private BlueList<AccessGroupMemberReply> Members(long pilotId, long id) =>
        new(groups.Members(pilotId, id).Select(m => new AccessGroupMemberReply(m)).ToArray());

    private static BlueList<AccessGroupReply> List(IReadOnlyList<AccessGroup> groups) =>
        new(groups.Select(g => new AccessGroupReply(g)).ToArray());

    private static long[] Ids(object? value) =>
        value is List<object?> list && list.Count <= AccessGroupRules.MaximumBatch && list.All(id => id is long and >= 0)
            ? list.Cast<long>().ToArray()
            : throw new InvalidDataException("Expected a list of member or access list IDs.");

    private static AccessGroupRole Role(object? value) =>
        value is long role and >= 0 and <= 3 ? (AccessGroupRole)role : throw new InvalidDataException("Choose a valid access list role.");

    private static string Text(object? value) =>
        value is string or byte[] ? BlueMarshal.Text(value) : throw new InvalidDataException("Expected access list text.");
}

internal sealed record AccessGroupReply(AccessGroup Group) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["groupID"] = Group.Id,
                ["creatorID"] = Group.CreatorId,
                ["name"] = new BlueUnicode(Group.Name),
                ["description"] = new BlueUnicode(Group.Description),
                ["membershipType"] = (int)Group.Role,
                ["admins"] = new BlueList<long>(Group.Admins),
            }
        );
}

internal sealed record AccessGroupMemberReply(AccessGroupMember Member) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?> { ["memberID"] = Member.OwnerId, ["membershipType"] = (int)Member.Role }
        );
}

internal sealed record AccessGroupLogReply(AccessGroupLog Log) : BlueModel
{
    public override object ToBlue() =>
        new BlueConstructor(
            "blue.DBRow",
            [
                BlueRows.Descriptor([
                    ("logID", BlueColumnType.Int64),
                    ("groupID", BlueColumnType.Int64),
                    ("characterID", BlueColumnType.Int64),
                    ("logDate", BlueColumnType.FileTime),
                    ("actionID", BlueColumnType.Int32),
                    ("ownerID", BlueColumnType.Int64),
                    ("oldMembershipType", BlueColumnType.Int32),
                    ("newMembershipType", BlueColumnType.Int32),
                    ("oldText", BlueColumnType.Unicode),
                    ("newText", BlueColumnType.Unicode),
                ]),
                new object?[]
                {
                    Log.Id,
                    Log.GroupId,
                    Log.ActorId,
                    Log.Time,
                    (int)Log.Change,
                    Log.OwnerId,
                    Log.OldRole is { } old ? (int)old : null,
                    Log.NewRole is { } current ? (int)current : null,
                    Log.OldText is { } oldText ? new BlueUnicode(oldText) : null,
                    Log.NewText is { } text ? new BlueUnicode(text) : null,
                },
            ]
        );
}
