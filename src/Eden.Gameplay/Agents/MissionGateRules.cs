using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Agents;

public static class MissionGateRules
{
    public const double ActivationRange = 2500;

    // Eden places catalog rooms on separate grids; catalog room coordinates are layout hints.
    public const double RoomSpacing = 50_000_000;
    public const int AccelerationGateGroup = 366;

    public static void Validate(SpacePosition ship, double shipRadius, int shipGroup, MissionGate gate, bool cleared)
    {
        if (!double.IsFinite(shipRadius) || shipRadius < 0 || !double.IsFinite(ship.DistanceTo(gate.Position)))
        {
            throw new InvalidDataException("Invalid acceleration gate position or ship radius.");
        }
        if (ship.DistanceTo(gate.Position) > shipRadius + gate.Radius + ActivationRange)
        {
            throw new InvalidDataException("Approach within 2,500 meters of the acceleration gate.");
        }
        if (Enum.IsDefined((RestrictedShipGroup)shipGroup))
        {
            throw new InvalidDataException("Capsules and capital ships cannot use this mission acceleration gate.");
        }
        if (gate.RequiresClear && !cleared)
        {
            throw new InvalidDataException("Destroy the required enemies in this room before activating the gate.");
        }
    }

    // Build 3662168 inventory groups. The admitted gates have no narrower ship list or key requirement.
    private enum RestrictedShipGroup
    {
        Capsule = 29,
        Titan = 30,
        Dreadnought = 485,
        Freighter = 513,
        Supercarrier = 659,
        Carrier = 547,
        CapitalIndustrial = 883,
        JumpFreighter = 902,
        ForceAuxiliary = 1538,
        LancerDreadnought = 4594,
    }
}
