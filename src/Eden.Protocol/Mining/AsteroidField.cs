using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;

namespace Eden.Protocol.Mining;

public interface IAsteroidField
{
    IReadOnlyList<StaticSpaceObject> List(int systemId);
}

public static class AsteroidDefinitions
{
    public const int BeltGroup = 9;
    public const int Category = 25;
    public const double VisibilityRange = 250_000;

    // Eden's local belt policy. The catalog supplies belt anchors, ore types and counts,
    // but not individual rock positions, IDs or deposit sizes. Persist the generated field.
    public const long FirstAsteroidId = 3_000_000_000_000;
    public const int IdsPerBelt = 10_000;
    public const double InnerFieldRadius = 6_000;
    public const double OuterFieldRadius = 18_000;
    public const double FieldThickness = 4_000;
    public const double MinimumOreVolume = 5_000;
    public const double MaximumOreVolume = 15_000;
    public const double RadiusGrowthExponent = 0.5;
}
