using Eden.Gameplay.AccessGroups;

namespace Eden.Data.World.AccessGroups;

public sealed class SavedAccessGroup
{
    public long Id { get; set; }
    public long CreatorId { get; set; }
    public bool IsCorporation { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class SavedAccessGroupMember
{
    public long GroupId { get; set; }
    public long OwnerId { get; set; }
    public AccessGroupRole Role { get; set; }
}

public sealed class SavedAccessGroupLog
{
    public long Id { get; set; }
    public long GroupId { get; set; }
    public long ActorId { get; set; }
    public long Time { get; set; }
    public AccessGroupChange Change { get; set; }
    public long? OwnerId { get; set; }
    public AccessGroupRole? OldRole { get; set; }
    public AccessGroupRole? NewRole { get; set; }
    public string? OldText { get; set; }
    public string? NewText { get; set; }

    public AccessGroupLog Snapshot() => new(Id, GroupId, ActorId, Time, Change, OwnerId, OldRole, NewRole, OldText, NewText);
}
