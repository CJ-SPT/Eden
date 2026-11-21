using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Combat;

public sealed record TitanLanceProfile(
    double Radius,
    double Signature,
    double DamageDuration,
    double DamageCycle,
    double NeutralizationRadius,
    double NeutralizationAmount,
    double NeutralizationSignature
);

public sealed record TitanLanceContact(
    StaticSpaceObject Beacon,
    SpacePosition SourcePosition,
    long ShipId,
    long ModuleId,
    int TypeId,
    long StartedAt,
    long EndsAt
);

public static class TitanLanceRules
{
    // Build 3662168 inventory types and the cylinder shape used by SinglePointPath.
    public const int HolyDestiny = 40631;
    public const int IronPike = 41439;
    public const int Phalarica = 41440;
    public const int Geiravor = 41441;
    public const int BeaconType = 41233;
    public const int BeaconGroup = 1704;
    public const int CylinderShape = 1;

    public static string Graphic(int type) =>
        type switch
        {
            HolyDestiny => "effects.SuperWeaponLanceAmarr",
            IronPike => "effects.SuperWeaponLanceCaldari",
            Phalarica => "effects.SuperWeaponLanceGallente",
            Geiravor => "effects.SuperWeaponLanceMinmatar",
            _ => throw new InvalidDataException("Unsupported Titan lance."),
        };

    public static TitanLanceProfile Profile(IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        double Read(DogmaAttributeId id) => attributes.GetValueOrDefault(id);
        var profile = new TitanLanceProfile(
            Read(DogmaAttributeId.DoomsdayDamageRadius),
            Read(DogmaAttributeId.SignatureRadius),
            Read(DogmaAttributeId.DoomsdayDamageDuration),
            Read(DogmaAttributeId.DoomsdayDamageCycle),
            Read(DogmaAttributeId.DoomsdayNeutralizationRadius),
            Read(DogmaAttributeId.DoomsdayNeutralizationAmount),
            Read(DogmaAttributeId.DoomsdayNeutralizationSignature)
        );
        if (
            Read(DogmaAttributeId.DoomsdayAreaShape) != CylinderShape
            || Read(DogmaAttributeId.DoomsdayFixedRange) != 1
            || new[]
            {
                profile.Radius,
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
            throw new InvalidDataException("Invalid Titan lance catalog data.");
        }
        return profile;
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
}
