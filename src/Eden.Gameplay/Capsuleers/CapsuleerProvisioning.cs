using System.Globalization;

namespace Eden.Gameplay.Capsuleers;

public enum CapsuleerEligibility
{
    Eligible,
    Disabled,
    UnsupportedSecurity,
    NoMarket,
    NoBelt,
    NoUsableOre,
    NoReciprocalRoute,
    MissingOrigin,
    MissingEquipment,
}

public sealed record CapsuleerSiteInputs(
    bool Enabled,
    double? Security,
    bool Market,
    bool Belt,
    bool Ore,
    bool Route,
    bool Origin,
    bool Equipment
);

public sealed record CapsuleerSystemSummary(
    int SystemId,
    CapsuleerEligibility Eligibility,
    int? HomeStationId,
    int? DestinationStationId,
    bool Provisioned
);

public sealed record CapsuleerProvisioningTurn(int RegisteredSystems, int CreatedCommunities, int PendingCommunities);

public sealed record CapsuleerPopulationSummary(int RegisteredSystems, int EligibleSystems, int Communities, int Agents, long WorldSeed);

public interface ICapsuleerProvisioning
{
    CapsuleerProvisioningTurn Reconcile(int maximumCommunities = int.MaxValue, CancellationToken cancellationToken = default);
}

public static class CapsuleerProvisioningRules
{
    public const int Version = 1;
    public const int FoundersPerCommunity = 2;
    public const int StaggerMilliseconds = 30_000;
    public const double MinimumSecurity = 0.45;
    private static readonly string[] GivenNames = ["Ari", "Taren", "Mira", "Kira", "Dara", "Oren", "Jori", "Sera"];
    private static readonly string[] FamilyNames = ["Voss", "Vartio", "Auvinen", "Kade", "Renn", "Sorin", "Arden", "Neris"];

    public static CapsuleerEligibility Eligibility(CapsuleerSiteInputs site) =>
        site switch
        {
            { Enabled: false } => CapsuleerEligibility.Disabled,
            { Security: double security } when !double.IsFinite(security) || security > 1 => CapsuleerEligibility.UnsupportedSecurity,
            { Security: null } => CapsuleerEligibility.UnsupportedSecurity,
            { Security: < MinimumSecurity } => CapsuleerEligibility.UnsupportedSecurity,
            { Market: false } => CapsuleerEligibility.NoMarket,
            { Belt: false } => CapsuleerEligibility.NoBelt,
            { Ore: false } => CapsuleerEligibility.NoUsableOre,
            { Route: false } => CapsuleerEligibility.NoReciprocalRoute,
            { Origin: false } => CapsuleerEligibility.MissingOrigin,
            { Equipment: false } => CapsuleerEligibility.MissingEquipment,
            _ => CapsuleerEligibility.Eligible,
        };

    public static string Description(CapsuleerEligibility eligibility) =>
        eligibility switch
        {
            CapsuleerEligibility.Eligible => "Supported community location",
            CapsuleerEligibility.Disabled => "Global provisioning is disabled",
            CapsuleerEligibility.UnsupportedSecurity => "Outside supported high-security space",
            CapsuleerEligibility.NoMarket => "No supported market at the community home",
            CapsuleerEligibility.NoBelt => "No asteroid belts",
            CapsuleerEligibility.NoUsableOre => "No supported mineable ore",
            CapsuleerEligibility.NoReciprocalRoute => "No reciprocal route to another market",
            CapsuleerEligibility.MissingOrigin => "No supported pilot origin",
            CapsuleerEligibility.MissingEquipment => "Required equipment is unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(eligibility)),
        };

    public static ulong Seed(long worldSeed, int systemId, CapsuleerCareer career)
    {
        // SplitMix64's fixed integer mixer is independent of runtime string hashes
        // and Random implementations. Save the world seed once, then derive each role.
        ulong value = unchecked((ulong)worldSeed + (ulong)(uint)systemId * 0x9E3779B97F4A7C15UL + (ulong)career);
        value = unchecked((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL);
        value = unchecked((value ^ (value >> 27)) * 0x94D049BB133111EBUL);
        return value ^ (value >> 31);
    }

    public static string Name(long worldSeed, int systemId, CapsuleerCareer career)
    {
        ulong seed = Seed(worldSeed, systemId, career);
        string role = career == CapsuleerCareer.Mining ? "M" : "H";
        return $"{GivenNames[seed % (ulong)GivenNames.Length]} {FamilyNames[(seed >> 8) % (ulong)FamilyNames.Length]} "
            + systemId.ToString(CultureInfo.InvariantCulture)
            + role;
    }

    public static long FirstDueTicks(long elapsedTicks, long worldSeed, int systemId, CapsuleerCareer career) =>
        checked(elapsedTicks + (1 + (long)(Seed(worldSeed, systemId, career) % StaggerMilliseconds)) * TimeSpan.TicksPerMillisecond);
}
