using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Combat;

public sealed record NpcMotionState(long Id, SpacePosition Position, SpacePosition Velocity, NpcNavigationState Navigation);

public static class NpcMotionRules
{
    public static SpacePosition CoastVelocity(SpacePosition velocity) => velocity with { Y = velocity.Y * (0.93 / 1.07) };

    public static NpcMotionState Coast(NpcMotionState npc, double inertia)
    {
        double decay = Math.Exp(-1 / inertia);
        var initial = CoastVelocity(npc.Velocity);
        return npc with { Position = npc.Position + initial * (inertia * (1 - decay)), Velocity = initial * decay };
    }

    public static bool Engaged(NpcMotionState npc) => npc.Navigation is NpcNavigationState.Pursuing or NpcNavigationState.Orbiting;

    public static float Agility(IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        attributes.GetValueOrDefault(DogmaAttributeId.Agility) is > 0 and var value ? (float)value : NpcEncounterDefinitions.DefaultAgility;

    public static double SpeedFraction(IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        double maximum = attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity);
        double cruise = attributes.GetValueOrDefault(DogmaAttributeId.EntityCruiseSpeed, maximum);
        if (!double.IsFinite(maximum) || maximum <= 0 || !double.IsFinite(cruise) || cruise <= 0)
        {
            throw new InvalidDataException("NPC movement needs a positive catalog maximum and cruise speed.");
        }
        return Math.Clamp(cruise / maximum, 0, 1);
    }

    public static NpcMotionState Pursue(
        NpcMotionState npc,
        SpacePosition target,
        double targetRadius,
        double mass,
        double radius,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        double seconds,
        int destinyStamp
    )
    {
        if (!Engaged(npc))
        {
            npc = npc with { Navigation = NpcNavigationState.Pursuing };
        }
        var radial = npc.Position - target;
        double distance = radial.DistanceTo(SpacePosition.Zero);
        double range = attributes.GetValueOrDefault(DogmaAttributeId.EntityFlyRange);
        double radii = radius + targetRadius;
        double standOff = range + radii;
        if (!double.IsFinite(standOff) || standOff < 0)
        {
            throw new InvalidDataException("Invalid NPC follow range.");
        }
        double surfaceRange = Math.Max(0, distance - radii);
        // Eden uses catalog chase/attack ranges as hysteresis thresholds. This
        // keeps an orbit from switching modes on every small radial overshoot.
        double entry = Math.Max(range, attributes.GetValueOrDefault(DogmaAttributeId.EntityChaseMaxDistance, range));
        double exit = Math.Max(entry, attributes.GetValueOrDefault(DogmaAttributeId.EntityAttackRange, entry));
        if (!double.IsFinite(entry) || !double.IsFinite(exit))
        {
            throw new InvalidDataException("Invalid NPC orbit thresholds.");
        }
        if (npc.Navigation == NpcNavigationState.Pursuing && surfaceRange <= entry)
        {
            npc = npc with { Navigation = NpcNavigationState.Orbiting };
        }
        else if (npc.Navigation == NpcNavigationState.Orbiting && surfaceRange > exit)
        {
            npc = npc with { Navigation = NpcNavigationState.Pursuing };
        }
        var destination = target + (distance > 0 ? radial * (1 / distance) : new SpacePosition(1, 0, 0)) * standOff;
        var orbitDirection =
            npc.Navigation == NpcNavigationState.Orbiting
                ? OrbitCourse.Direction(npc.Id, destinyStamp - 1, npc.Position, target, standOff)
                : null;
        return Advance(npc, destination, mass, radius, attributes, seconds, orbitDirection: orbitDirection);
    }

    public static NpcMotionState Advance(
        NpcMotionState npc,
        SpacePosition destination,
        double mass,
        double radius,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        double seconds,
        bool returning = false,
        SpacePosition? orbitDirection = null
    )
    {
        if (seconds <= 0)
        {
            return npc;
        }
        var position = npc.Position;
        var velocity = npc.Velocity;
        var delta = destination - position;
        double distance = delta.DistanceTo(SpacePosition.Zero);
        double cruise = attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity) * SpeedFraction(attributes);
        double inertia = mass * Agility(attributes) / NpcEncounterDefinitions.InertiaMassScale;
        if (
            !double.IsFinite(inertia)
            || inertia <= 0
            || !double.IsFinite(distance)
            || !double.IsFinite(velocity.DistanceTo(SpacePosition.Zero))
        )
        {
            throw new InvalidDataException("Invalid NPC movement geometry or inertia.");
        }
        SpacePosition next;
        if (
            orbitDirection is null
            && distance <= NpcEncounterDefinitions.ArrivalTolerance
            && velocity.DistanceTo(SpacePosition.Zero) <= NpcEncounterDefinitions.ArrivalSpeed
        )
        {
            // The native fourth-power homing thrust approaches asymptotically.
            // End a slow arrival within tolerance without snapping its position.
            next = position;
            velocity = SpacePosition.Zero;
            if (returning)
            {
                npc = npc with { Navigation = NpcNavigationState.Idle };
            }
        }
        else
        {
            // Carbon Follow chooses a point at the surface stand-off; GotoThrust
            // tapers within one tick's travel distance. Integrate the same drag
            // used by player flight, preserving velocity while turning around.
            double throttle = Math.Min(1, Math.Pow(distance / (cruise * seconds), 4));
            var desired =
                orbitDirection is not null ? orbitDirection * cruise
                : distance > 0 ? delta * (cruise * throttle / distance)
                : SpacePosition.Zero;
            double decay = Math.Exp(-seconds / inertia);
            next = position + desired * seconds + (velocity - desired) * (inertia * (1 - decay));
            velocity = desired + (velocity - desired) * decay;
        }
        if (!double.IsFinite(next.DistanceTo(SpacePosition.Zero)) || !double.IsFinite(velocity.DistanceTo(SpacePosition.Zero)))
        {
            throw new InvalidDataException("NPC movement exceeded the finite coordinate range.");
        }
        return npc with { Position = next, Velocity = velocity };
    }
}
