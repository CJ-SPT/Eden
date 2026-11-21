using Eden.Gameplay.Exploration;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Exploration;

[Injectable]
internal sealed class CurrentDungeonHandler(IAbyssalDeadspace abyss, IPirateExploration exploration, TimeProvider clock)
    : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if ((service, method) != ("keeper", "GetCurrentDungeonForCharacter"))
        {
            return PilotCallResult.Unsupported;
        }
        if (args.Length != 0 || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Current dungeon information does not accept arguments.");
        }
        var pilot = context.Pilot;
        if (pilot.Position is not { } position || context.Flight.InWarp)
        {
            return PilotCallResult.None;
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (AbyssalRules.IsSystem(pilot.SystemId))
        {
            if (abyss.Read(pilot.PilotId) is { Status: AbyssalStatus.Active } run && run.SystemId == pilot.SystemId && now < run.ExpiresAt)
            {
                var scene = AbyssalRules.Scene(run.Room);
                return new CurrentDungeonReply(scene.DungeonId, scene.RoomId, run.Id, BlueEmpty.Dictionary);
            }
            return PilotCallResult.None;
        }
        return exploration.CurrentGhostDungeon(pilot.SystemId, position, now) is { } site
            ? new CurrentDungeonReply(site.DungeonId, site.RoomId, site.InstanceId, new GhostTimerValues(site.CountdownEnd))
            : PilotCallResult.None;
    }
}

internal sealed record CurrentDungeonReply(int DungeonId, int RoomId, long InstanceId, BlueModel Values) : BlueModel
{
    // Build 3662168 DungeonTracking.OnCharacterSessionChanged unpacks these four fields before ballpark binding.
    public override object ToBlue() => new object?[] { DungeonId, RoomId, InstanceId, Values };
}
