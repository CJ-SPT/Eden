using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Agents;

public sealed record ResearchField(int SkillId, string Name, int? DatacoreTypeId);

public sealed record ResearchProject(long Id, CatalogAgent Agent, int SkillId, double Points, double PointsPerDay);

public sealed record DatacorePurchase(long BalanceCents, InventoryChange Item);

public interface IPilotResearch
{
    IReadOnlyList<ResearchField> Fields(long pilotId, long agentId);
    IReadOnlyList<ResearchProject> Research(long pilotId, long? agentId = null);
    ResearchProject StartResearch(long pilotId, long agentId, int skillId);
    void CancelResearch(long pilotId, long agentId, long projectId);
    DatacorePurchase BuyDatacores(long pilotId, long agentId, long projectId, int quantity);
}

public static class ResearchRules
{
    public const int Science = 3402;
    public const int ProjectManagement = 12179;
    public const int DatacoreGroup = 333;
    public const int PointsPerDatacore = 100;
    public const long CentsPerDatacore = 1_000_000;
    private const decimal BaseQuality = 20;
    private const decimal NegotiationQuality = 5;
    private const decimal Percent = 100;

    public static double PointsPerDay(int agentLevel, int fieldLevel, int negotiation, double standing)
    {
        if (
            agentLevel is < 1 or > 5
            || fieldLevel is < 0 or > 5
            || negotiation is < 0 or > 5
            || !double.IsFinite(standing)
            || standing is < -10 or > 10
        )
        {
            throw new InvalidDataException("Invalid research rate inputs.");
        }
        decimal quality = 1 + (BaseQuality + NegotiationQuality * negotiation + (decimal)standing) / Percent;
        int combined = agentLevel + fieldLevel;
        return (double)(quality * combined * combined);
    }

    public static double Accrue(double points, double rate, long settledAt, long now)
    {
        if (!double.IsFinite(points) || points < 0 || !double.IsFinite(rate) || rate < 0 || settledAt < 0 || now < 0)
        {
            throw new InvalidDataException("Invalid saved research balance.");
        }
        return (double)((decimal)points + (decimal)rate * (Math.Max(now, settledAt) - settledAt) / TimeSpan.TicksPerDay);
    }

    public static long PurchaseCost(double points, int quantity)
    {
        if (quantity <= 0 || !double.IsFinite(points) || points < (double)quantity * PointsPerDatacore)
        {
            throw new InvalidDataException("Choose a positive quantity covered by this agent's research points.");
        }
        return checked(quantity * CentsPerDatacore);
    }

    public static AgentAccess Access(int level, double agent, double corporation, double faction, bool omega)
    {
        // Pinned eveCfg.CanUseAgent: R&D has no level-one exemption and
        // additionally requires corporation standing within two of the normal gate.
        var normal = MissionRules.Access(level, agent, corporation, faction, omega);
        double required = level * 2 - 3;
        string reason =
            !omega ? "Research agents require Omega."
            : Math.Min(agent, Math.Min(corporation, faction)) <= -2 ? "Research standings must each be above -2.0."
            : Math.Max(agent, Math.Max(corporation, faction)) < required || corporation < required - 2
                ? $"This research agent requires standing {required:0.0} and corporation standing {required - 2:0.0}."
            : "";
        return normal with { Allowed = reason.Length == 0, Reason = reason, Required = required };
    }
}
