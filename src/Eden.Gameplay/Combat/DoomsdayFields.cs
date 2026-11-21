using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Combat;

public enum DoomsdayFieldShape
{
    Cylinder = 1,
    Sweep = 2,
    Cone = 3,
}

public sealed record DoomsdayFieldProfile(
    DoomsdayFieldShape Shape,
    double Radius,
    double Signature,
    double DamageDuration,
    double DamageCycle,
    double NeutralizationRadius,
    double NeutralizationAmount,
    double NeutralizationSignature
);

public sealed record DoomsdayFieldContact(
    long Id,
    StaticSpaceObject? Beacon,
    SpacePosition SourcePosition,
    long ShipId,
    long ModuleId,
    int TypeId,
    long VisualAt,
    long VisualEndsAt,
    SpacePosition? StartOffset = null,
    SpacePosition? EndOffset = null
);

public static class DoomsdayFieldRules
{
    // Build 3662168 item types, effect registry, and the 30-degree span in tacticalNavigation.ArcPath.
    public const int HolyDestiny = 40631;
    public const int IronPike = 41439;
    public const int Phalarica = 41440;
    public const int Geiravor = 41441;
    public const int BeaconType = 41233;
    public const int BeaconGroup = 1704;
    public const int DivineHarvest = 40632;
    public const int ColdWind = 41442;
    public const int Guillotine = 41443;
    public const int Jormungandr = 41444;
    public const int BosonicFieldGenerator = 40633;
    private const double SweepAngle = 15 * Math.PI / 180;

    public static bool IsEffect(DogmaEffect effect) =>
        effect is DogmaEffect.DoomsdayBeam or DogmaEffect.DoomsdaySlash or DogmaEffect.DoomsdayCone;

    public static string Graphic(int type) =>
        type switch
        {
            HolyDestiny => "effects.SuperWeaponLanceAmarr",
            IronPike => "effects.SuperWeaponLanceCaldari",
            Phalarica => "effects.SuperWeaponLanceGallente",
            Geiravor => "effects.SuperWeaponLanceMinmatar",
            DivineHarvest => "effects.SWSlashBeamAmarr",
            ColdWind => "effects.SWSlashBeamCaldari",
            Guillotine => "effects.SWSlashBeamGallente",
            Jormungandr => "effects.SWSlashBeamMinmatar",
            // Use the pinned point indicator when no Bosonic-specific graphic is available.
            BosonicFieldGenerator => "effects.IndicatorBeamMoving",
            _ => throw new InvalidDataException("Unsupported area doomsday."),
        };

    public static DoomsdayFieldProfile Profile(DogmaEffect effect, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        double Read(DogmaAttributeId id) => attributes.GetValueOrDefault(id);
        var shape = effect switch
        {
            DogmaEffect.DoomsdayBeam => DoomsdayFieldShape.Cylinder,
            DogmaEffect.DoomsdaySlash => DoomsdayFieldShape.Sweep,
            DogmaEffect.DoomsdayCone => DoomsdayFieldShape.Cone,
            _ => throw new InvalidDataException("Unsupported area doomsday shape."),
        };
        var profile = new DoomsdayFieldProfile(
            shape,
            Read(DogmaAttributeId.DoomsdayDamageRadius),
            Read(DogmaAttributeId.SignatureRadius),
            Read(DogmaAttributeId.DoomsdayDamageDuration),
            Read(DogmaAttributeId.DoomsdayDamageCycle),
            Read(DogmaAttributeId.DoomsdayNeutralizationRadius),
            Read(DogmaAttributeId.DoomsdayNeutralizationAmount),
            Read(DogmaAttributeId.DoomsdayNeutralizationSignature)
        );
        if (
            Read(DogmaAttributeId.DoomsdayAreaShape) != (int)shape
            || Read(DogmaAttributeId.DoomsdayFixedRange) != 1
            || shape != DoomsdayFieldShape.Cone && (!double.IsFinite(profile.Radius) || profile.Radius <= 0)
            || new[]
            {
                profile.Signature,
                profile.DamageDuration,
                profile.DamageCycle,
                profile.NeutralizationRadius,
                profile.NeutralizationAmount,
                profile.NeutralizationSignature,
            }.Any(value => !double.IsFinite(value) || value <= 0)
            || profile.DamageCycle > profile.DamageDuration
        )
        {
            throw new InvalidDataException("Invalid area doomsday catalog data.");
        }
        return profile;
    }

    public static void CheckSweep(SpacePosition source, double radius, double range, SpacePosition first, SpacePosition second)
    {
        var middle = (first + second) * 0.5 - source;
        var halfSpan = (second - first) * 0.5;
        double expected = Math.Tan(SweepAngle) * range;
        // Allow source movement between client aim and command settlement.
        if (
            !double.IsFinite(middle.Length)
            || !double.IsFinite(halfSpan.Length)
            || Math.Abs(middle.Length - (range + radius)) > range / 200
            || Math.Abs(halfSpan.Length - expected) > range / 1000
            || Math.Abs(middle.Unit.Dot(halfSpan)) > range / 1000
        )
        {
            throw new InvalidDataException("Aim a Reaper across its fixed sweep arc.");
        }
    }

    public static bool Intersects(
        SpacePosition start,
        SpacePosition axis,
        double length,
        double radius,
        SpacePosition center,
        double targetRadius
    )
    {
        var offset = center - start;
        double axial = offset.Dot(axis);
        double axialGap = Math.Max(0, Math.Max(-axial, axial - length));
        double radialGap = Math.Max(0, (offset - axis * axial).Length - radius);
        return axialGap * axialGap + radialGap * radialGap <= targetRadius * targetRadius;
    }

    public static bool IntersectsCone(SpacePosition origin, SpacePosition axis, double length, SpacePosition center, double radius)
    {
        var offset = center - origin;
        double depth = offset.Dot(axis);
        if (depth < -radius || depth > length + radius)
        {
            return false;
        }
        // The stock indication scales its width and length together, producing a 45-degree half angle.
        return (offset - axis * depth).Length <= Math.Clamp(depth, 0, length) + radius;
    }
}
