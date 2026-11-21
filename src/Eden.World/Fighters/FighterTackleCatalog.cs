using System.Globalization;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;

namespace Eden.World.Fighters;

internal static class FighterTackleCatalog
{
    // Build 3662168 space-superiority fighters use ability 16 in slot two.
    private const int TackleAbility = 16;
    private static readonly CatalogDefinition<bool> s_supported = new(
        "fighter-tackle",
        (catalog, type) =>
            catalog.Read(CatalogData.Client.Fighterabilitiesbytype, type.ToString(CultureInfo.InvariantCulture))?.AbilitySlot2?.AbilityID
            == TackleAbility
    );

    internal static FighterTackle? Read(CatalogDbContext catalog, int type, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        if (!catalog.Definition(s_supported, type))
        {
            return null;
        }
        double range = attributes.GetValueOrDefault(DogmaAttributeId.FighterTackleRange);
        double duration = attributes.GetValueOrDefault(DogmaAttributeId.FighterTackleDuration);
        double web = attributes.GetValueOrDefault(DogmaAttributeId.FighterTackleWebPercent);
        double warp = attributes.GetValueOrDefault(DogmaAttributeId.FighterTackleWarpStrength);
        if (new[] { range, duration, -web, warp }.Any(value => !double.IsFinite(value) || value <= 0) || web <= -100)
        {
            throw new InvalidDataException("Invalid fighter tackle attributes.");
        }
        return new(range, duration, web, warp);
    }
}
