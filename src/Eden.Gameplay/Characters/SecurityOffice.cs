using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Characters;

public sealed record SecurityTagRequest(double Target, double QuotedStatus);

public sealed record SecurityTagRequirement(int TypeId, int Quantity);

public sealed record SecurityTagPlan(double Status, IReadOnlyList<SecurityTagRequirement> Tags);

public interface IPilotSecurityOffice
{
    IReadOnlyList<InventoryChange> Exchange(long pilotId, SecurityTagRequest request);
}

public static class SecurityOfficeRules
{
    // Build 3662168 crimewatch.const and inventorycommon.const.
    private const double GainPerTag = 0.5;
    private const double RoundingTolerance = 1e-9;
    private const int DedCorporation = 1000137;
    private static readonly (int Type, double Upper)[] s_bands = [(33138, -8), (33139, -5), (33140, -2), (33141, 0)];

    // securityOfficeSvc and eveuniverse.security classify stations by owner and raw system security.
    public static bool Available(int ownerId, double systemSecurity) =>
        ownerId is ConcordRules.Corporation or DedCorporation && systemSecurity is > 0 and < CrimewatchRules.HighSecurity;

    public static bool IsTag(int typeId) => s_bands.Any(band => band.Type == typeId);

    public static SecurityTagPlan Plan(double current, SecurityTagRequest request)
    {
        SecurityStatusRules.Check(current);
        if (request.QuotedStatus != current)
        {
            throw new InvalidDataException("Your security status changed. Select a new target in the Security Office.");
        }
        if (!double.IsFinite(request.Target) || request.Target <= current || current >= 0 || request.Target >= GainPerTag)
        {
            throw new InvalidDataException("Choose a reachable security status increase.");
        }

        List<SecurityTagRequirement> tags = [];
        double status = current;
        // Each tag grants the full amount, including the final tag that crosses a tier or zero.
        foreach (var (type, upper) in s_bands)
        {
            int quantity = 0;
            while (status < upper && status < request.Target && (status == current || request.Target - status > RoundingTolerance))
            {
                status += GainPerTag;
                quantity++;
            }
            if (quantity != 0)
            {
                tags.Add(new(type, quantity));
            }
        }
        if (tags.Count == 0 || request.Target - status > RoundingTolerance)
        {
            throw new InvalidDataException("Choose a reachable security status increase.");
        }
        return new(status, tags);
    }
}
