using Eden.Gameplay.Combat;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

// These records preserve the implemented empty compatibility states. They do not
// define schemas or behavior for contracts, contacts, fighters, clones, or campaigns.
internal sealed record ContractLoginState() : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["assignedToMe"] = new List<object?>(),
                ["needsAttention"] = new List<object?>(),
                ["inProgress"] = new List<object?>(),
            }
        );
}

internal sealed record BountiesAndKillRightsState() : BlueModel
{
    public override object ToBlue() => new object?[] { new List<object?>(), new List<object?>() };
}

internal sealed record BountyReply(long TargetId) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            TargetId,
            new BlueInstance("utillib.KeyVal", new Dictionary<string, object?> { ["targetID"] = TargetId, ["bounty"] = 0d }),
        };
}

internal sealed record LocalSystemState(int SystemId) : BlueModel
{
    public override object ToBlue() => new object?[] { SystemId, null };
}

internal sealed record DynamicBountyState() : BlueModel
{
    public override object ToBlue() => new object?[] { 1d, false };
}

internal sealed record ContactListState() : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?> { ["addresses"] = new List<object?>(), ["blocked"] = new List<object?>() }
        );
}

internal sealed record CrimewatchClientState(ShipSafetyLevel Safety) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Enumerable.Range(0, 5).Select(_ => (object?)new object?[] { null, null }).ToArray(),
            new Dictionary<string, object?>(),
            new object?[] { new List<object?>(), new List<object?>() },
            (int)Safety,
        };
}

internal sealed record JumpCloneState() : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["clones"] = new CompatibilityRowset(["jumpCloneID", "locationID", "cloneName"], []),
                ["implants"] = new CompatibilityRowset(["jumpCloneID", "typeID"], []),
                ["timeLastJump"] = null,
            }
        );
}

internal sealed record JumpTimerState() : BlueModel
{
    public override object ToBlue() => new object?[] { null, null, null };
}

internal sealed record FighterState() : BlueModel
{
    public override object ToBlue() => new object?[] { new List<object?>(), new List<object?>(), new Dictionary<string, object?>() };
}

internal sealed record AgentJournalState() : BlueModel
{
    public override object ToBlue() => new object?[] { new List<object?>(), new List<object?>() };
}

internal sealed record PersonalBookmarkState(long PilotId) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            new List<object?>
            {
                new BlueInstance(
                    "utillib.KeyVal",
                    new Dictionary<string, object?>
                    {
                        ["folderID"] = 1,
                        ["folderName"] = "Personal Locations",
                        ["isPersonal"] = true,
                        ["creatorID"] = PilotId,
                        ["ownerID"] = PilotId,
                        ["isActive"] = true,
                        ["description"] = "",
                    }
                ),
            },
            new List<object?>(),
            new List<object?>(),
        };
}

internal sealed record AchievementState() : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["completedDict"] = new Dictionary<string, object?>(),
            ["eventDict"] = new Dictionary<string, object?>(),
        };
}
