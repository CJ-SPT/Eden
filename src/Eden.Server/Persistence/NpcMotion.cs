using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Server.Catalog;

namespace Eden.Server.Persistence;

internal static class NpcMotion
{
    internal static SpacePosition Position(WorldNpc npc) => new(npc.X, npc.Y, npc.Z);
    internal static SpacePosition Velocity(WorldNpc npc) => new(npc.VelocityX, npc.VelocityY, npc.VelocityZ);
    internal static SpacePosition Home(Celestial belt) => new(belt.X + NpcEncounterDefinitions.AnchorOffset, belt.Y, belt.Z);
    internal static float Agility(IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        attributes.GetValueOrDefault(DogmaAttributeId.Agility) is > 0 and var value ? (float)value : NpcEncounterDefinitions.DefaultAgility;

    private static double SpeedFraction(IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        double maximum = attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity);
        double cruise = attributes.GetValueOrDefault(DogmaAttributeId.EntityCruiseSpeed, maximum);
        if (!double.IsFinite(maximum) || maximum <= 0 || !double.IsFinite(cruise) || cruise <= 0)
        {
            throw new InvalidDataException("NPC movement needs a positive catalog maximum and cruise speed.");
        }
        return Math.Clamp(cruise / maximum, 0, 1);
    }

    internal static void Return(WorldNpc npc) => npc.Navigation = NpcNavigationState.Returning;

    internal static void Pursue(WorldNpc npc, SpacePosition target, double targetRadius, ItemType type,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes, long now)
    {
        if (npc.Navigation != NpcNavigationState.Pursuing)
        {
            npc.Navigation = NpcNavigationState.Pursuing;
            npc.MotionAt = now;
        }
        var radial = Position(npc) - target;
        double distance = radial.DistanceTo(SpacePosition.Zero);
        double standOff = attributes.GetValueOrDefault(DogmaAttributeId.EntityFlyRange) + (type.Radius ?? 0) + targetRadius;
        if (!double.IsFinite(standOff) || standOff < 0) { throw new InvalidDataException("Invalid NPC follow range."); }
        var destination = target + (distance > 0 ? radial * (1 / distance) : new SpacePosition(1, 0, 0)) * standOff;
        Advance(npc, destination, type, attributes, now);
    }

    internal static void Advance(WorldNpc npc, SpacePosition destination, ItemType type,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes, long now, bool returning = false)
    {
        if (npc.MotionAt == 0) { npc.MotionAt = now; }
        // Share wall-clock second boundaries so small scheduling differences
        // cannot drop a movement tick or let another viewer advance it twice.
        long elapsed = now / TimeSpan.TicksPerSecond - npc.MotionAt / TimeSpan.TicksPerSecond;
        if (elapsed < 1) { return; }
        // The connected simulation advances one shared tick, never an offline
        // catch-up journey. MotionAt serializes repeated/competing viewers.
        double seconds = Math.Min(elapsed, NpcEncounterDefinitions.MaximumMovementStepSeconds);
        npc.MotionAt = now;
        var position = Position(npc);
        var velocity = Velocity(npc);
        var delta = destination - position;
        double distance = delta.DistanceTo(SpacePosition.Zero);
        double cruise = attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity) * SpeedFraction(attributes);
        double inertia = (type.Mass ?? 0) * Agility(attributes) / NpcEncounterDefinitions.InertiaMassScale;
        if (!double.IsFinite(inertia) || inertia <= 0 || !double.IsFinite(distance) || !double.IsFinite(velocity.DistanceTo(SpacePosition.Zero)))
        {
            throw new InvalidDataException("Invalid NPC movement geometry or inertia.");
        }
        SpacePosition next;
        if (distance <= NpcEncounterDefinitions.ArrivalTolerance && velocity.DistanceTo(SpacePosition.Zero) <= NpcEncounterDefinitions.ArrivalSpeed)
        {
            // The native fourth-power homing thrust approaches asymptotically.
            // End a slow arrival within tolerance without snapping its position.
            next = position;
            velocity = SpacePosition.Zero;
            if (returning) { npc.Navigation = NpcNavigationState.Idle; }
        }
        else
        {
            // Carbon Follow chooses a point at the surface stand-off; GotoThrust
            // tapers within one tick's travel distance. Integrate the same drag
            // used by player flight, preserving velocity while turning around.
            double throttle = Math.Min(1, Math.Pow(distance / (cruise * seconds), 4));
            var desired = distance > 0 ? delta * (cruise * throttle / distance) : SpacePosition.Zero;
            double decay = Math.Exp(-seconds / inertia);
            next = position + desired * seconds + (velocity - desired) * (inertia * (1 - decay));
            velocity = desired + (velocity - desired) * decay;
        }
        if (!double.IsFinite(next.DistanceTo(SpacePosition.Zero)) || !double.IsFinite(velocity.DistanceTo(SpacePosition.Zero)))
        {
            throw new InvalidDataException("NPC movement exceeded the finite coordinate range.");
        }
        npc.X = next.X;
        npc.Y = next.Y;
        npc.Z = next.Z;
        npc.VelocityX = velocity.X;
        npc.VelocityY = velocity.Y;
        npc.VelocityZ = velocity.Z;
    }

    internal static NpcNavigationContact? Contact(WorldNpc npc, IReadOnlyDictionary<DogmaAttributeId, double> attributes, long target, SpacePosition home) =>
        npc.Navigation switch
        {
            NpcNavigationState.Idle => null,
            NpcNavigationState.Pursuing when target != 0 => new(Velocity(npc), SpeedFraction(attributes), target, attributes.GetValueOrDefault(DogmaAttributeId.EntityFlyRange)),
            NpcNavigationState.Returning => new(Velocity(npc), SpeedFraction(attributes), 0, 0, home),
            _ => throw new InvalidDataException("Invalid NPC navigation state."),
        };
}
