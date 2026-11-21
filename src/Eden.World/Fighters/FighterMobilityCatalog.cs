using System.Globalization;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;

namespace Eden.World.Fighters;

internal static class FighterMobilityCatalog
{
    private static readonly CatalogDefinition<ClientMobility?> s_mobility = new("fighter-mobility", Load);

    private sealed record ClientMobility(FighterMobilityKind Kind, double Cooldown);

    private static ClientMobility? Load(CatalogDbContext catalog, int type)
    {
        var key = type.ToString(CultureInfo.InvariantCulture);
        var slot = catalog.Read(CatalogData.Client.Fighterabilitiesbytype, key)?.AbilitySlot1;
        if (slot?.AbilityID is not (4 or 9 or 13))
        {
            return null;
        }
        return new((FighterMobilityKind)slot.AbilityID.Value, slot.CooldownSeconds.GetValueOrDefault() * 1000);
    }

    internal static FighterMobility? Read(CatalogDbContext catalog, int type, IReadOnlyDictionary<DogmaAttributeId, double> values)
    {
        if (catalog.Definition(s_mobility, type) is not { } slot)
        {
            return null;
        }
        double Value(DogmaAttributeId id) => values.GetValueOrDefault(id);
        var (duration, speed, signature) = slot.Kind switch
        {
            FighterMobilityKind.Microwarpdrive => (
                Value(DogmaAttributeId.FighterMicrowarpdriveDuration),
                Value(DogmaAttributeId.FighterMicrowarpdriveSpeed),
                Value(DogmaAttributeId.FighterMicrowarpdriveSignature)
            ),
            FighterMobilityKind.Afterburner => (
                Value(DogmaAttributeId.FighterAfterburnerDuration),
                Value(DogmaAttributeId.FighterAfterburnerSpeed),
                0
            ),
            _ => (
                Value(DogmaAttributeId.FighterEvasiveDuration),
                Value(DogmaAttributeId.FighterEvasiveSpeed),
                Value(DogmaAttributeId.FighterEvasiveSignature)
            ),
        };
        DamageVector? resistance =
            slot.Kind == FighterMobilityKind.EvasiveManeuvers
                ? new(
                    Value(DogmaAttributeId.FighterEvasiveEmResonance),
                    Value(DogmaAttributeId.FighterEvasiveExplosiveResonance),
                    Value(DogmaAttributeId.FighterEvasiveKineticResonance),
                    Value(DogmaAttributeId.FighterEvasiveThermalResonance)
                )
                : null;
        if (
            !double.IsFinite(duration)
            || duration <= 0
            || !double.IsFinite(slot.Cooldown)
            || slot.Cooldown <= 0
            || !double.IsFinite(speed)
            || speed <= 0
            || !double.IsFinite(signature)
            || signature <= -100
            || resistance is not null && (!double.IsFinite(resistance.Total) || resistance.Total <= 0)
        )
        {
            throw new InvalidDataException("Invalid fighter mobility attributes.");
        }
        return new(slot.Kind, duration, slot.Cooldown, 1 + speed / 100, 1 + signature / 100, resistance);
    }
}
