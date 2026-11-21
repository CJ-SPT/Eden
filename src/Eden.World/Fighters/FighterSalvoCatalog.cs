using System.Globalization;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;

namespace Eden.World.Fighters;

internal static class FighterSalvoCatalog
{
    // Build 3662168 abilities Torpedo Salvo and Heavy Rocket Salvo both use effect 6431.
    private const int TorpedoSalvo = 19;
    private const int HeavyRocketSalvo = 33;
    private static readonly CatalogDefinition<Charges?> s_slots = new("fighter-salvo", Load);

    private sealed record Charges(int Count, double RearmMilliseconds);

    private static Charges? Load(CatalogDbContext catalog, int type)
    {
        var slot = catalog.Read(CatalogData.Client.Fighterabilitiesbytype, type.ToString(CultureInfo.InvariantCulture))?.AbilitySlot2;
        return
            slot
                is {
                    AbilityID: TorpedoSalvo or HeavyRocketSalvo,
                    Charges: { ChargeCount: > 0 and long count, RearmTimeSeconds: > 0 and double seconds }
                }
            ? new(checked((int)count), seconds * 1000)
            : null;
    }

    internal static FighterSalvo? Read(CatalogDbContext catalog, int type, IReadOnlyDictionary<DogmaAttributeId, double> values)
    {
        if (catalog.Definition(s_slots, type) is not { } slot)
        {
            return null;
        }
        double Value(DogmaAttributeId id) => values.GetValueOrDefault(id);
        var damage = new DamageVector(
            Value(DogmaAttributeId.FighterMissilesEmDamage),
            Value(DogmaAttributeId.FighterMissilesExplosiveDamage),
            Value(DogmaAttributeId.FighterMissilesKineticDamage),
            Value(DogmaAttributeId.FighterMissilesThermalDamage)
        ).Scale(Value(DogmaAttributeId.FighterMissilesDamageMultiplier));
        double range = Value(DogmaAttributeId.FighterMissilesRange);
        double duration = Value(DogmaAttributeId.FighterMissilesDuration);
        double radius = Value(DogmaAttributeId.FighterMissilesExplosionRadius);
        double speed = Value(DogmaAttributeId.FighterMissilesExplosionVelocity);
        double exponent =
            Math.Log(Value(DogmaAttributeId.FighterMissilesReductionFactor))
            / Math.Log(Value(DogmaAttributeId.FighterMissilesReductionSensitivity));
        if (
            new[] { damage.Total, range, duration, radius, speed, exponent, slot.RearmMilliseconds }.Any(value =>
                !double.IsFinite(value) || value <= 0
            )
        )
        {
            throw new InvalidDataException("Invalid fighter salvo catalog attributes.");
        }
        int resistance = (int)Value(DogmaAttributeId.FighterMissilesResistanceAttribute);
        return new(
            new(damage, radius, speed, exponent),
            range,
            duration,
            slot.Count,
            slot.RearmMilliseconds,
            resistance == 0 ? null : (DogmaAttributeId)resistance
        );
    }
}
