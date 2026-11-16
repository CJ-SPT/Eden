namespace Eden.Gameplay.Flight;

public sealed record WarpCapacitorPayment(double Distance, double Cost, double Capacitor);

public interface IPilotWarpDrive
{
    WarpCapacitorPayment Activate(long pilotId, long shipId, double distance, long now);
}

public static class WarpCapacitorRules
{
    public const int WarpDriveOperationSkill = 3455;
    public const double AstronomicalUnit = 149_597_870_700;

    // Build 3662168 attribute 153 is GJ per AU per kilogram, after dogma modifiers.
    public static WarpCapacitorPayment Pay(double distance, double mass, double need, double capacitor)
    {
        if (
            !double.IsFinite(distance)
            || distance <= 0
            || !double.IsFinite(mass)
            || mass <= 0
            || !double.IsFinite(need)
            || need < 0
            || !double.IsFinite(capacitor)
            || capacitor < 0
        )
        {
            throw new InvalidDataException("Invalid warp capacitor inputs.");
        }
        double cost = distance / AstronomicalUnit * mass * need;
        if (!double.IsFinite(cost))
        {
            throw new InvalidDataException("Invalid warp capacitor cost.");
        }
        double availableDistance = cost <= capacitor ? distance : distance * (capacitor / cost);
        if (cost > capacitor && availableDistance < NavigationDefinitions.MinimumWarpDistance)
        {
            throw new InvalidDataException("Insufficient capacitor to initiate warp.");
        }
        double paid = Math.Min(cost, capacitor);
        return new(availableDistance, paid, Math.Max(0, capacitor - paid));
    }
}
