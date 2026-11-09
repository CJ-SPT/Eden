using Eden.Gameplay.Redemption;
using Eden.Gameplay.Skills;

namespace Eden.Modding;

public interface IServerMod
{
    void Register(IModRegistration registration);
}

public enum ModEventKind
{
    Started,
    Stopping,
    PilotSelected,
    PilotDisconnected,
    LocationChanged,
}

public sealed record ModPilot(long AccountId, long PilotId, long Roles, int? StationId, int SolarSystemId);

public sealed record ModEvent(ModEventKind Kind, ModPilot? Pilot);

public sealed record ModCommandContext(ModPilot Pilot, string Arguments);

// JSON is an explicit dynamic boundary owned by the mod, never an untyped gameplay object graph.
public sealed record ModCommandReply(bool Success, string Json, string? Error = null)
{
    public static ModCommandReply Ok(string json = "null") => new(true, json);

    public static ModCommandReply Failed(string error) => new(false, "null", error);
}

public interface IModRegistration
{
    void Command(string name, long requiredRoles, Func<ModCommandContext, ModCommandReply> handler);
    void On(ModEventKind kind, Action<ModEvent> handler);
    IModServices Services { get; }
}

public interface IModServices
{
    string? ReadState(string key);
    void WriteState(string key, string json);
    int ResolveSkill(string logicalId);
    SkillDefinition? Skill(int typeId);
    IReadOnlyList<TrainedSkill> Skills(long pilotId);
    RedeemToken Grant(RedeemGrant grant);
    void Log(string message);
}

public interface IModCommands
{
    ModCommandReply Execute(string modId, string command, ModCommandContext context);
}
