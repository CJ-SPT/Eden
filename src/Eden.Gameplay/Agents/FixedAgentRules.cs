using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Agents;

public static class FixedAgentRules
{
    // Cargo exchange follows the normal 2,500 m interaction range, measured from the agent hull surface.
    public const double InteractionRange = 2500;

    public static AgentAccess Access(int level, double agent, double corporation, double faction, bool omega)
    {
        var access = MissionRules.Access(level, agent, corporation, faction, omega);
        double required = Math.Max(0, access.Required);
        return access.Allowed && Math.Max(agent, Math.Max(corporation, faction)) < required
            ? access with
            {
                Allowed = false,
                Required = required,
                Reason = "This Data Center agent requires nonnegative effective standing.",
            }
            : access with
            {
                Required = required,
            };
    }

    public static bool InRange(AgentSpaceLocation agent, int systemId, SpacePosition? position) =>
        systemId == agent.SystemId && position is { } point && agent.Position.DistanceTo(point) <= agent.Radius + InteractionRange;

    public static void RequireAvailable(bool resident, bool assigned, bool previouslyClosed)
    {
        if (!resident || !assigned || previouslyClosed)
        {
            throw new InvalidDataException("This one-time agent assignment is unavailable or has already ended.");
        }
    }
}
