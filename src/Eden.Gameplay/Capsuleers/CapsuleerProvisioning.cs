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
)
{
    public bool CurrentPolicy { get; init; }
}

public sealed record CapsuleerProvisioningTurn(int RegisteredSystems, int CreatedCommunities, int PendingCommunities)
{
    public bool HasMoreWork { get; init; }
    public long? CreatedPilotId { get; init; }
}

// Completed counts include cached systems and systems examined but deferred this run.
public sealed record CapsuleerPreparationProgress(int CompletedSystems, int TotalSystems, int DeferredSystems);

public sealed record CapsuleerBackgroundTurn(CapsuleerPreparationProgress Progress, int? SystemId, bool Complete)
{
    public string? Failure { get; init; }
}

public interface ICapsuleerBackgroundPreparation
{
    Task Changed { get; }
    CapsuleerBackgroundTurn PrepareNext(CancellationToken cancellationToken = default);
}

public sealed record CapsuleerPopulationSummary(int RegisteredSystems, int EligibleSystems, int Communities, int Agents, long WorldSeed)
{
    public int PausedAgents { get; init; }
    public int EnabledAgents => Agents - PausedAgents;
    public int PreparedSystems { get; init; }
    public int PreparedEligibleSystems { get; init; }
}

public interface ICapsuleerProvisioning
{
    CapsuleerProvisioningTurn Reconcile(
        int maximumCommunities = int.MaxValue,
        CancellationToken cancellationToken = default,
        Action<CapsuleerPreparationProgress>? progress = null
    );
}

public static class CapsuleerProvisioningRules
{
    public const int Version = 6;
    public const int StaggerMilliseconds = 30_000;
    public const double MinimumSecurity = 0.45;
    private static readonly string[] GivenNames =
    [
        "Ari",
        "Taren",
        "Mira",
        "Kira",
        "Dara",
        "Oren",
        "Jori",
        "Sera",
        "Alia",
        "Bren",
        "Cora",
        "Darin",
        "Elara",
        "Faren",
        "Galen",
        "Hana",
        "Ilia",
        "Jalen",
        "Kael",
        "Lena",
        "Maren",
        "Nara",
        "Orin",
        "Petra",
        "Quinn",
        "Rhea",
        "Soren",
        "Talia",
        "Una",
        "Vera",
        "Wren",
        "Yara",
        "Zara",
        "Anya",
        "Bela",
        "Caden",
        "Dalia",
        "Emil",
        "Freya",
        "Garin",
        "Hale",
        "Irena",
        "Jessa",
        "Keira",
        "Lian",
        "Mika",
        "Niko",
        "Olia",
        "Pavel",
        "Rian",
        "Selene",
        "Tomas",
        "Vela",
        "Willa",
        "Yuri",
        "Zane",
        "Aster",
        "Cassia",
        "Davin",
        "Eira",
        "Finn",
        "Liora",
        "Nessa",
        "Silas",
    ];
    private static readonly string[] FamilyNames =
    [
        "Voss",
        "Vartio",
        "Auvinen",
        "Kade",
        "Renn",
        "Sorin",
        "Arden",
        "Neris",
        "Aalto",
        "Bell",
        "Calder",
        "Dane",
        "Ellis",
        "Falk",
        "Grove",
        "Hale",
        "Ivers",
        "Jansen",
        "Kellan",
        "Lorne",
        "Marlow",
        "Nolan",
        "Orlov",
        "Perrin",
        "Quill",
        "Reeve",
        "Sable",
        "Thorne",
        "Ulven",
        "Vale",
        "Ward",
        "Yarrow",
        "Zorin",
        "Ashby",
        "Baird",
        "Corvin",
        "Drake",
        "Elden",
        "Frost",
        "Greer",
        "Harker",
        "Inari",
        "Jarven",
        "Korven",
        "Larkin",
        "Mercer",
        "North",
        "Oakes",
        "Pike",
        "Rook",
        "Saar",
        "Torven",
        "Venn",
        "West",
        "Wren",
        "Ylinen",
        "Alden",
        "Briar",
        "Cairn",
        "Doran",
        "Evren",
        "Fenn",
        "Kestrel",
        "Linden",
    ];
    public static int MaximumNameAttempts => checked(GivenNames.Length * FamilyNames.Length * GivenNames.Length + 1);

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
            CapsuleerEligibility.NoReciprocalRoute => "No usable station or reciprocal gate route",
            CapsuleerEligibility.MissingOrigin => "No supported pilot origin",
            CapsuleerEligibility.MissingEquipment => "Required equipment is unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(eligibility)),
        };

    public static ulong Seed(long worldSeed, int systemId, CapsuleerCareer career, int slot = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        // SplitMix64's fixed integer mixer is independent of runtime string hashes
        // and Random implementations. Save the world seed once, then derive each role.
        ulong value = unchecked(
            (ulong)worldSeed + (ulong)(uint)systemId * 0x9E3779B97F4A7C15UL + (ulong)career + (ulong)slot * 0xD6E8FEB86659FD93UL
        );
        value = unchecked((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL);
        value = unchecked((value ^ (value >> 27)) * 0x94D049BB133111EBUL);
        return value ^ (value >> 31);
    }

    public static string Name(long worldSeed, int systemId, CapsuleerCareer career, int slot = 0, int attempt = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(attempt, MaximumNameAttempts);
        ulong seed = Seed(worldSeed, systemId, career, slot);
        if (attempt == 0)
        {
            return $"{GivenNames[seed % (ulong)GivenNames.Length]} {FamilyNames[(seed >> 8) % (ulong)FamilyNames.Length]}";
        }
        // A collision immediately adds a natural middle name. The odd stride
        // permutes this power-of-two namespace without repeating candidates.
        ulong combinations = (ulong)(MaximumNameAttempts - 1);
        ulong index = ((seed >> 16) + (ulong)(attempt - 1) * 7919UL) % combinations;
        string given = GivenNames[index % (ulong)GivenNames.Length];
        string middle = GivenNames[index / (ulong)GivenNames.Length % (ulong)GivenNames.Length];
        string family = FamilyNames[index / (ulong)(GivenNames.Length * GivenNames.Length)];
        return $"{given} {middle} {family}";
    }

    public static long FirstDueTicks(long elapsedTicks, long worldSeed, int systemId, CapsuleerCareer career, int slot = 0) =>
        checked(
            elapsedTicks
            + (1 + (long)(Seed(worldSeed, systemId, career, slot) % StaggerMilliseconds) + (long)slot * StaggerMilliseconds)
                * TimeSpan.TicksPerMillisecond
        );
}
