using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.FactionWarfare;

public enum MilitiaObjectiveKind
{
    SupplyCache,
    SupplyDepot,
    Rendezvous,
    Propaganda,
    ListeningPost,
}

public sealed record MilitiaDeployment(long ItemId, long ObjectId, InventoryChange Change);

public interface IMilitiaObjectiveDeployment
{
    IReadOnlyList<MilitiaDeployment> Deploy(long pilot, long ship, IReadOnlyList<long> items, long now);
}

public static class MilitiaObjectiveRules
{
    private const long FirstSite = 8_720_000_000_000;
    private const int SlotsPerGeneration = 1_000_000;
    private const int PropagandaType = 73225;
    private const int ListeningType = 73270;
    public const int AnchoringSkill = 11584;
    public const int RequiredAnchoringLevel = 3;
    public const int MaximumDeployments = 3;
    public const int SupplyReward = 15_000;
    public const int SupplyRewardCapacity = 5;
    public const int RendezvousReward = 15_000;
    public const int DeploymentReward = 10_000;
    public const double ObjectiveRange = 500_000;
    public const double DeploymentSpacing = 10_000_000;
    public const double MaximumSecurity = 0.45;
    public static readonly TimeSpan SupplyRespawn = TimeSpan.FromMinutes(250);
    public static readonly TimeSpan RendezvousRespawn = TimeSpan.FromHours(1);
    public static readonly TimeSpan DeploymentActivation = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DeploymentDuration = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DeploymentExpiry = TimeSpan.FromHours(1);

    public static long Slot(int system, MilitiaObjectiveKind kind, MilitiaFaction faction) =>
        FirstSite + (system - 30_000_000L) * 16 + (int)kind * 4 + MilitiaAdvantageRules.EmpireIndex(faction);

    public static long Site(long slot, int generation) => slot + generation * (long)SlotsPerGeneration;

    public static bool IsSite(long id) => id >= FirstSite && id < FirstSite + 10_000_000_000L;

    public static MilitiaObjectiveKind? Deployment(int type, MilitiaFaction faction)
    {
        int empire = MilitiaAdvantageRules.EmpireIndex(faction);
        return type == PropagandaType + empire ? MilitiaObjectiveKind.Propaganda
            : type == ListeningType + empire ? MilitiaObjectiveKind.ListeningPost
            : null;
    }

    public static bool IsDeployable(int type) =>
        type is >= PropagandaType and < PropagandaType + 4 or >= ListeningType and < ListeningType + 4;

    public static SpacePosition Grid(long site) =>
        new(
            -150_000_000_000 - site % 173 * 1_000_000_000,
            75_000_000_000 + site % 199 * 1_000_000_000,
            -200_000_000_000 - site % 211 * 1_000_000_000
        );
}
