using Eden.Gameplay.Mining;

namespace Eden.Gameplay.Flight;

public static class DungeonObjectGeometry
{
    private const int CloudGroup = 227;
    private const int MassiveEnvironmentGroup = 1882;
    private const int NonInteractableObjectGroup = 1975;
    private const double MinimumAsteroidRadius = 2;

    public static bool IsSolid(int groupId) => groupId is not (CloudGroup or MassiveEnvironmentGroup or NonInteractableObjectGroup);

    public static float Radius(int categoryId, double? typeRadius, double? dungeonRadius, double unitVolume)
    {
        double radius = typeRadius ?? 0;
        if (categoryId == AsteroidDefinitions.Category && dungeonRadius is > 0 && unitVolume > 0)
        {
            // Asteroid dunRadius is a deposit quantity, not a physical radius.
            // Use Eden's existing belt volume/size policy for decorative site rocks.
            radius = Math.Pow(dungeonRadius.Value * unitVolume, AsteroidDefinitions.RadiusGrowthExponent);
        }
        if (categoryId == AsteroidDefinitions.Category)
        {
            // Build 3662168 asteroid.Assemble derives a rotation period from log(radius).
            // The catalog's one-metre placeholder gives period zero and crashes Trinity's native curve.
            radius = Math.Max(MinimumAsteroidRadius, radius);
        }
        // Other dungeon radius values describe authored content/visual bounds;
        // the type's physical ball radius is the collision input.
        return (float)radius;
    }
}
