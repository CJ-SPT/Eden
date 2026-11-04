using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;

namespace Eden.Gameplay.Redemption;

public sealed record RedeemGrant(
    long AccountId,
    string Reference,
    int TypeId,
    int Quantity,
    string Label,
    string Description = "",
    int? StationId = null,
    long? ExpiresAt = null
);

public sealed record RedeemToken(
    long Id,
    int TypeId,
    int Quantity,
    string Label,
    string Description,
    int? StationId,
    long CreatedAt,
    long? ExpiresAt
);

public sealed record RedeemSelection(long TokenId, int TypeId);

public sealed record RedeemFailure(long TokenId, int TypeId, string Information);

public sealed record RedeemedItem(long TokenId, InventoryItem Item);

public sealed record RedeemResult(
    long PilotId,
    IReadOnlyList<RedeemedItem> Items,
    IReadOnlyList<RedeemFailure> Errors,
    long SkillPoints = 0,
    long? FreeSkillPoints = null,
    TrainingState? Training = null,
    long? FreePointRevision = null
)
{
    public IReadOnlyList<RedeemedCurrency> Currencies { get; init; } = [];
}

public interface IAccountRedemption
{
    RedeemToken Grant(RedeemGrant grant);
    IReadOnlyList<RedeemToken> Read(long accountId);
    RedeemResult Claim(long accountId, long pilotId, IReadOnlyList<RedeemSelection> tokens, bool useHomeStation);
    void Trash(long accountId, IReadOnlyList<long> tokens);
    InventoryChange Reverse(long accountId, long pilotId, long itemId);
}

public static class RedemptionRules
{
    // Eden bounds for its own administrative grants and batch requests.
    public const int MaximumBatch = 100;
    public const int MaximumReference = 128;
    public const int MaximumLabel = 255;
    public const int MaximumDescription = 4096;
    public static readonly long MaximumExpiry = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc).ToFileTimeUtc();

    public static void Check(RedeemGrant grant)
    {
        if (
            grant.AccountId <= 0
            || grant.TypeId <= 0
            || grant.Quantity <= 0
            || string.IsNullOrWhiteSpace(grant.Reference)
            || grant.Reference.Length > MaximumReference
            || grant.Reference.StartsWith('#')
            || string.IsNullOrWhiteSpace(grant.Label)
            || grant.Label.Length > MaximumLabel
            || grant.Description is null
            || grant.Description.Length > MaximumDescription
            || grant.Reference.Contains('\0')
            || grant.Label.Contains('\0')
            || grant.Description.Contains('\0')
            || grant.StationId is <= 0
            || grant.ExpiresAt is <= 0
            || grant.ExpiresAt > MaximumExpiry
        )
        {
            throw new InvalidDataException("Invalid redeem grant.");
        }
    }

    public static void CheckBatch(IReadOnlyList<long> ids)
    {
        if (ids.Count is < 1 or > MaximumBatch || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Count)
        {
            throw new InvalidDataException("Choose one to 100 distinct redeem tokens.");
        }
    }

    public static bool Deliverable(InventoryCategory category) =>
        category
            is InventoryCategory.Material
                or InventoryCategory.Ship
                or InventoryCategory.Module
                or InventoryCategory.Charge
                or InventoryCategory.Skill
                or InventoryCategory.Commodity
                or InventoryCategory.Drone
                or InventoryCategory.Implant
                or InventoryCategory.Deployable
                or InventoryCategory.Asteroid
                or InventoryCategory.Apparel
                or InventoryCategory.Subsystem
                or InventoryCategory.Relic
                or InventoryCategory.InfrastructureUpgrade
                or InventoryCategory.SovereigntyStructure
                or InventoryCategory.PlanetaryResource
                or InventoryCategory.PlanetaryCommodity
                or InventoryCategory.Orbitals
                or InventoryCategory.Structure
                or InventoryCategory.StructureModule
                or InventoryCategory.Fighter;
}
