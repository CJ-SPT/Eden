using System.Collections.Immutable;
using Eden.Gameplay.Market;

namespace Eden.Gameplay.Configuration;

public sealed record EconomicPolicy(bool ChargeServiceCosts = false, decimal DefaultManufacturingIndex = 0.01m);

public sealed record DevelopmentSupplyPolicy(
    bool TestingMarket = true,
    bool TestingGrants = true,
    int InstitutionalStock = NpcSupplyRules.Stock,
    int TestingStock = TestingMarketRules.Stock,
    long TestingPriceCents = TestingMarketRules.PriceCents,
    long GrantCents = TestingMarketRules.GrantCents
);

public sealed record ResourcePolicy(bool RenewBelts = true, int RenewalHourUtc = 11);

public sealed record NpcPopulationPolicy(
    bool Pirates = true,
    bool Guards = true,
    bool Convoys = true,
    bool Mining = true,
    bool MajorThreatAnomalies = false
);

public sealed record ProgressionPolicy(bool GrantStartingSkills = true);

public sealed record CapsuleerPopulationPolicy(bool GlobalProvisioning = true);

public sealed record SimulationPolicy(int CheckpointSeconds = 15, int MarketPollMilliseconds = 1000, int ResourceCheckSeconds = 60);

public sealed record WorldSettings
{
    public EconomicPolicy Economy { get; init; } = new();
    public DevelopmentSupplyPolicy DevelopmentSupply { get; init; } = new();
    public ResourcePolicy Resources { get; init; } = new();
    public NpcPopulationPolicy NpcPopulation { get; init; } = new();
    public ProgressionPolicy Progression { get; init; } = new();
    public CapsuleerPopulationPolicy Capsuleers { get; init; } = new();
    public SimulationPolicy Simulation { get; init; } = new();
}

// These are server-supplied economic projections, not edits to native client physics or recipes.
public sealed record AdjustedPriceOverride(int TypeId, decimal PriceIsk);

public sealed record ManufacturingIndexOverride(int SystemId, decimal Index);

public sealed record WorldConfigurationDraft
{
    public int FormatVersion { get; init; } = 1;
    public string CatalogFingerprint { get; init; } = "";
    public WorldSettings Settings { get; init; } = new();
    public ImmutableArray<AdjustedPriceOverride> AdjustedPrices { get; init; } = [];
    public ImmutableArray<ManufacturingIndexOverride> ManufacturingIndices { get; init; } = [];
}

public sealed record ConfigurationRevision(long Id, long? ParentId, string Author, long CreatedAt, WorldConfigurationDraft Draft);

public sealed record ConfigurationRevisionInfo(long Id, long? ParentId, string Author, long CreatedAt);

public sealed record ConfigurationState(
    long Generation,
    ConfigurationRevision Active,
    ConfigurationRevision? Pending,
    string? ActivationFailure
);

public interface IWorldConfiguration
{
    ConfigurationState Read();
    IReadOnlyList<ConfigurationRevisionInfo> History(int skip = 0, int limit = 50);
    ConfigurationRevision Get(long revisionId);
    void Validate(WorldConfigurationDraft draft);
    ConfigurationState Save(long expectedGeneration, WorldConfigurationDraft draft, string author);
    ConfigurationState Restore(long expectedGeneration, long revisionId, string author);
    string Export(long revisionId);
    ConfigurationState Import(long expectedGeneration, string document, string author);
}
