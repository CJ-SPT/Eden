namespace Eden.Gameplay.Capsuleers;

public static class CapsuleerAmmunitionSupplyRules
{
    // Build 3662168 small hybrid charge and its original blueprint.
    public const int ChargeType = 215;
    public const int BlueprintType = 1130;
    public const int ProductionRuns = 5;
    public const int MaximumSupplierJumps = 3;
    public const int DepotReorderQuantity = 100;
    public const int ShipmentQuantity = 200;
    public const int ListingDays = 7;
}

public sealed record CorporationSupplyRun(int CorporationId, long OrderId);
