using System.Globalization;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Combat;

namespace Eden.World.Fighters;

internal static class FighterBombCatalog
{
    // Build 3662168 point-targeted slot-two ability 7 and its racial micro bombs.
    private const int LaunchBombAbility = 7;
    private static readonly CatalogDefinition<FighterBomb?> s_bombs = new("fighter-bomb", Load);

    internal static FighterBomb? Read(CatalogDbContext catalog, int type) => catalog.Definition(s_bombs, type);

    private static FighterBomb? Load(CatalogDbContext catalog, int fighter)
    {
        var slot = catalog.Read(CatalogData.Client.Fighterabilitiesbytype, fighter.ToString(CultureInfo.InvariantCulture))?.AbilitySlot2;
        if (
            slot
            is not { AbilityID: LaunchBombAbility, Charges: { ChargeCount: > 0 and long charges, RearmTimeSeconds: > 0 and double rearm } }
        )
        {
            return null;
        }
        var attributes = catalog.FittingAttributes(fighter);
        int type = checked((int)attributes.GetValueOrDefault(DogmaAttributeId.FighterBombType));
        double duration = attributes.GetValueOrDefault(DogmaAttributeId.FighterBombDuration);
        var payload = AreaWeaponCatalog.Bomb(catalog, type);
        if (
            new[]
            {
                duration,
                rearm,
                payload.Speed,
                payload.Lifetime,
                payload.Radius,
                payload.SignatureResolution,
                payload.Damage.Total,
            }.Any(value => !double.IsFinite(value) || value <= 0)
        )
        {
            throw new InvalidDataException("Invalid fighter bomb catalog attributes.");
        }
        return new(payload, duration, checked((int)charges), rearm * 1000);
    }
}
