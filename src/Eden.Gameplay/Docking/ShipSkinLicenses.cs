using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Docking;

public sealed record ShipSkinLicense(int SkinId, long? Expires, bool IsSingleUse);

public sealed record ShipSkinActivation(ShipSkinLicense License, InventoryChange ItemChange);

public interface IPilotShipSkinLicenses
{
    IReadOnlyList<ShipSkinLicense> List(long pilotId, int? hullTypeId = null);
    ShipSkinLicense? Read(long pilotId, int skinId);
    IReadOnlyList<ShipSkinActivation> Activate(long pilotId, IReadOnlyList<long> itemIds);
}

public static class ShipSkinLicenseRules
{
    // Permanent license duration in build 3662168 client/skinlicenses.
    public const long Permanent = -1;

    public static long? Expiration(long durationDays, long now) =>
        durationDays switch
        {
            Permanent => null,
            > 0 => checked(now + durationDays * TimeSpan.TicksPerDay),
            _ => throw new InvalidDataException("This SKIN license has an invalid duration."),
        };

    public static bool IsActive(long? expires, long now) => expires is null || expires > now;
}
