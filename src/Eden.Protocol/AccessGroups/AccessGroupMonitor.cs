using Eden.Gameplay.AccessGroups;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.AccessGroups;

[Injectable(InjectionType.Scoped)]
internal sealed class AccessGroupMonitor(IPilotAccessGroups groups)
{
    private sealed record Snapshot(AccessGroup Group, IReadOnlyList<AccessGroupMember> Members);

    private Dictionary<long, Snapshot>? _before;
    private long _pilot;
    private int _corporation;
    private long _revision;
    public bool IsMonitoring => _before is not null;

    public void Reset() => _before = null;

    public IReadOnlyList<AccessGroup> Prime(PilotCallContext context)
    {
        _pilot = context.Pilot.PilotId;
        _corporation = context.Pilot.CorpId;
        _revision = groups.Revision;
        var contents = groups.ReadContents(_pilot);
        _before = contents.ToDictionary(c => c.Group.Id, c => new Snapshot(c.Group, c.Members));
        return contents.Select(c => c.Group).ToArray();
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected || _pilot != context.Pilot.PilotId)
        {
            Reset();
        }
        if (_before is not { } before || _revision == groups.Revision && _corporation == context.Pilot.CorpId)
        {
            return;
        }
        Prime(context);
        Dictionary<long, BlueList<int>> changes = [];
        foreach (long id in before.Keys.Union(_before!.Keys))
        {
            if (!before.TryGetValue(id, out var old) || !_before.TryGetValue(id, out var current))
            {
                // Membership changes also invalidate the listener's list of manageable groups.
                changes[id] = new(
                    before.ContainsKey(id)
                        ? [(int)AccessGroupChange.Deleted, (int)AccessGroupChange.ChangedForListener]
                        : [(int)AccessGroupChange.ChangedForListener]
                );
                continue;
            }
            List<int> codes = [];
            if (old.Group.Name != current.Group.Name)
            {
                codes.Add((int)AccessGroupChange.NameChanged);
            }
            if (old.Group.Description != current.Group.Description)
            {
                codes.Add((int)AccessGroupChange.DescriptionChanged);
            }
            if (!old.Members.SequenceEqual(current.Members))
            {
                codes.AddRange([
                    (int)AccessGroupChange.MemberAdded,
                    (int)AccessGroupChange.MemberRoleChanged,
                    (int)AccessGroupChange.MemberRemoved,
                ]);
            }
            if (old.Group.Role != current.Group.Role)
            {
                codes.Add((int)AccessGroupChange.ChangedForListener);
            }
            if (codes.Count > 0)
            {
                changes[id] = new(codes);
            }
        }
        if (changes.Count > 0)
        {
            context.Notifications.Add(new AccessGroupsUpdatedNotification(changes));
        }
    }
}

internal sealed record AccessGroupsUpdatedNotification(IReadOnlyDictionary<long, BlueList<int>> Changes)
    : PilotNotification("OnOwnerGroupsUpdated")
{
    public override object?[] Arguments => [new BlueMap<long, BlueList<int>>(Changes)];
}
