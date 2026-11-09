using Eden.Gameplay.Characters;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;

namespace Eden.Gameplay.Agents;

// Build 3662168 appConst agentMissionState* and agentDialogueButton*.
public enum MissionState
{
    Offered = 1,
    Accepted = 2,
    Failed = 3,
    Completed = 4,
    Declined = 5,
    Quit = 6,
}

public enum MissionAction
{
    View = 1,
    Request = 2,
    Accept = 3,
    Complete = 6,
    Decline = 9,
    Defer = 10,
    Quit = 11,
}

public enum MissionKind
{
    Courier,
    Combat,
}

// Build 3662168 appConst factionsPirates and factionsWhoseStandingsAreNotAffectedBySkillBonuses.
public enum AgentStandingFaction
{
    Guristas = 500010,
    AngelCartel = 500011,
    BloodRaiders = 500012,
    Sansha = 500019,
    Serpentis = 500020,
    Drifters = 500024,
    RogueDrones = 500025,
    Triglavian = 500026,
    Edencom = 500027,
    Deathless = 500029,
}

public enum MissionSocialSkill
{
    Social = 3355,
    Negotiation = 3356,
    Diplomacy = 3357,
    Connections = 3359,
    CriminalConnections = 3361,
    DistributionConnections = 3894,
    SecurityConnections = 3895,
}

public sealed record MissionRewardBand(
    long MinimumIsk,
    long MaximumIsk,
    int MinimumLp,
    int MaximumLp,
    double MinimumCorporationPercent,
    double MaximumCorporationPercent,
    double MinimumFactionPercent,
    double MaximumFactionPercent
);

public sealed record MissionRewards(long IskCents, long LoyaltyPoints, double CorporationFraction, double FactionFraction);

public sealed record AgentAccess(bool Allowed, string Reason, double Agent, double Corporation, double Faction, double Required);

public sealed record MissionCargo(int TypeId, int Quantity, double Volume, int PickupStation, int DestinationStation)
{
    public int PickupTypeId { get; init; }
    public int DestinationTypeId { get; init; }
    public int PickupSystemId { get; init; }
}

public sealed record MissionOffer(
    long Id,
    int MissionId,
    string Name,
    string Briefing,
    CatalogAgent Agent,
    int Level,
    MissionKind Kind,
    MissionState State,
    long OfferedAt,
    long? AcceptedAt,
    long ExpiresAt,
    MissionRewards Rewards,
    bool Storyline,
    int? DungeonId,
    int SystemId,
    SpacePosition Position,
    bool ObjectiveComplete,
    MissionCargo? Cargo
)
{
    public int NameId { get; init; }
    public int BriefingId { get; init; }
    public long PilotId { get; init; }
}

public sealed record MissionStandingChange(long SourceId, double Before, double After);

public sealed record MissionUpdate(
    MissionOffer? Offer,
    string Event,
    IReadOnlyList<InventoryChange> Items,
    IReadOnlyList<MissionStandingChange> Standings,
    long? BalanceCents = null,
    long PointsBefore = 0,
    long PointsAfter = 0
);

public sealed record MissionSite(
    long Id,
    long PilotId,
    int SystemId,
    SpacePosition Position,
    bool Started,
    IReadOnlyList<StaticSpaceObject> Objects
)
{
    public int RoomId { get; init; }
    public SpacePosition Entrance { get; init; } = Position;
    public IReadOnlyList<MissionGate> Gates { get; init; } = [];
}

public sealed record MissionGate(long Id, SpacePosition Position, float Radius, SpacePosition Destination, bool RequiresClear);

public interface IPilotMissions
{
    AgentAccess Access(long pilotId, long agentId);
    IReadOnlyList<MissionOffer> Journal(long pilotId, long? agentId = null);
    MissionOffer? Current(long pilotId, long agentId);
    MissionUpdate Act(long pilotId, long agentId, MissionAction action, long? offerId = null);
    IReadOnlyList<MissionSite> Sites(int systemId);
    void Arrive(int systemId, SpacePosition position);
    MissionGate Gate(long pilotId, long gateId);
    SpacePosition WarpDestination(int systemId, SpacePosition origin, SpacePosition destination);
}

public static class MissionRules
{
    public const int MaximumDifficulty = 1000;
    private const double Percent = 100;
    private const decimal NegotiationBonusPerLevel = 0.05m;
    private const decimal DivisionConnectionsBonusPerLevel = 0.1m;
    private const double SocialBonusPerLevel = 0.05;
    private const double ConnectionsBonusPerLevel = 0.04;
    private const double MinimumAgentStanding = -2;
    public static readonly TimeSpan OfferLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan MissionLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan FreeDeclineInterval = TimeSpan.FromHours(4);
    public const int StorylineInterval = 16;
    public const int SecurityDivision = 24;
    public const int DistributionDivision = 22;
    public const double ArrivalRange = 150_000;
    public const double PlacementOffset = 10_000_000_000;

    // Reserve distinct identities after anomaly objects/NPCs and inside the NPC contact range.
    public const long FirstSiteId = 7_300_000_000_000;
    public const long FirstNpcId = 7_400_000_000_000;
    public const long FirstObjectId = 7_500_000_000_000;
    public const int NpcsPerMission = 1000;
    public const int MembersPerWave = 100;
    public const int ActionStride = 32;

    // Eden's agreed base rewards, November 8, 2026. Percentages are percentages
    // of the remaining distance to +10, not absolute standing points.
    public static MissionRewardBand Band(int level) =>
        level switch
        {
            1 => new(100_000, 300_000, 500, 1500, 0.05, 0.1, 1.5, 2),
            2 => new(400_000, 600_000, 2000, 2500, 0.1, 0.3, 3, 4),
            3 => new(1_200_000, 1_800_000, 3000, 4000, 0.2, 0.6, 5, 6.5),
            4 => new(4_000_000, 6_000_000, 6000, 11000, 0.8, 1.5, 7.5, 9.3),
            5 => new(10_000_000, 20_000_000, 12000, 25000, 2, 3.5, 0, 0),
            _ => throw new InvalidDataException("Mission levels must be between one and five."),
        };

    public static MissionRewards Reward(int level, int difficulty, int negotiation, int divisionConnections, int social, bool storyline)
    {
        CheckSkill(negotiation);
        CheckSkill(divisionConnections);
        CheckSkill(social);
        if (difficulty is < 0 or > MaximumDifficulty || storyline && level == 5)
        {
            throw new InvalidDataException("Invalid mission reward inputs.");
        }
        var band = Band(level);
        decimal weight = difficulty / (decimal)MaximumDifficulty;
        long isk = checked(
            (long)
                decimal.Round(
                    (band.MinimumIsk + (band.MaximumIsk - band.MinimumIsk) * weight)
                        * MarketDefinitions.CentsPerIsk
                        * (1 + negotiation * NegotiationBonusPerLevel),
                    0,
                    MidpointRounding.ToEven
                )
        );
        long lp = storyline
            ? 0
            : checked(
                (long)
                    decimal.Floor(
                        (band.MinimumLp + (band.MaximumLp - band.MinimumLp) * weight)
                            * (1 + divisionConnections * DivisionConnectionsBonusPerLevel)
                    )
            );
        double corporation =
            (band.MinimumCorporationPercent + (band.MaximumCorporationPercent - band.MinimumCorporationPercent) * (double)weight) / Percent;
        double faction = storyline
            ? (band.MinimumFactionPercent + (band.MaximumFactionPercent - band.MinimumFactionPercent) * (double)weight) / Percent
            : 0;
        return new(isk, lp, corporation * (1 + social * SocialBonusPerLevel), faction * (1 + social * SocialBonusPerLevel));
    }

    public static double Gain(double current, double fraction)
    {
        CheckStanding(current);
        if (!double.IsFinite(fraction) || fraction is < 0 or > 1)
        {
            throw new InvalidDataException("Invalid mission standing gain.");
        }
        return Math.Min(10, current + (10 - current) * fraction);
    }

    public static double EffectiveStanding(
        double raw,
        int connections,
        int diplomacy,
        int criminalConnections,
        bool pirate,
        bool skillBonuses = true
    )
    {
        CheckStanding(raw);
        CheckSkill(connections);
        CheckSkill(diplomacy);
        CheckSkill(criminalConnections);
        int skill =
            raw < 0 ? diplomacy
            : pirate ? criminalConnections
            : connections;
        return skillBonuses ? raw + (StandingRules.Maximum - raw) * skill * ConnectionsBonusPerLevel : raw;
    }

    public static AgentAccess Access(int level, double agent, double corporation, double faction, bool omega)
    {
        _ = Band(level);
        CheckStanding(agent);
        CheckStanding(corporation);
        CheckStanding(faction);
        double required = level == 1 ? -10 : level * 2 - 3;
        string reason =
            level >= 4 && !omega ? "Level four and five missions require Omega."
            : level > 1 && Math.Min(agent, Math.Min(corporation, faction)) <= MinimumAgentStanding
                ? "Agent, corporation and faction standings must each be above -2.0."
            : level > 1 && Math.Max(agent, Math.Max(corporation, faction)) < required
                ? $"This agent requires effective agent, corporation or faction standing of {required:0.0}."
            : "";
        return new(reason.Length == 0, reason, agent, corporation, faction, required);
    }

    private static void CheckSkill(int level)
    {
        if (level is < 0 or > 5)
        {
            throw new InvalidDataException("Invalid mission social skill level.");
        }
    }

    private static void CheckStanding(double standing)
    {
        if (!double.IsFinite(standing) || standing is < -10 or > 10)
        {
            throw new InvalidDataException("Invalid NPC standing.");
        }
    }
}
