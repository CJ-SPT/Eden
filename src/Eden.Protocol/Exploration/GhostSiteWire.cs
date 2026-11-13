using Eden.Gameplay.Exploration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Exploration;

internal sealed record GhostTimerValues(long? End) : BlueModel
{
    // Build 3662168 dungeon objectives 74, chain 115 and TimerTask.
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["destruction_timer"] = End is long end
                ? new Dictionary<string, object?> { ["start_time"] = end - GhostSiteRules.VisibleCountdown, ["end_time"] = end }
                : null,
        };
}

internal sealed record EnteringGhostDungeon(CombatAnomaly Site) : PilotNotification("OnEnteringDungeonRoom")
{
    public override object?[] Arguments =>
        [
            Site.DungeonId,
            Site.GhostRoomId,
            new object[] { Site.Position.X, Site.Position.Y, Site.Position.Z },
            Site.Id,
            new GhostTimerValues(Site.GhostCountdownEnd),
        ];
}

internal sealed record GhostDungeonValues(int Dungeon, long? End) : PilotNotification("OnDungeonValues")
{
    public override object?[] Arguments => [Dungeon, new GhostTimerValues(End)];
}

internal sealed record ExitingExplorationDungeon(int Dungeon) : PilotNotification("OnExitingDungeon")
{
    public override object?[] Arguments => [Dungeon];
}
