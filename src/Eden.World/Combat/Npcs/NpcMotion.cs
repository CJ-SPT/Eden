using Eden.Data.Catalog;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;

namespace Eden.World.Combat.Npcs;

internal static class NpcMotion
{
    internal static SpacePosition Position(WorldNpc npc) => new(npc.X, npc.Y, npc.Z);

    internal static SpacePosition Velocity(WorldNpc npc) => new(npc.VelocityX, npc.VelocityY, npc.VelocityZ);

    internal static SpacePosition Home(Celestial belt) => new(belt.X + NpcEncounterDefinitions.AnchorOffset, belt.Y, belt.Z);

    internal static SpacePosition Home(WorldNpc npc, CatalogDbContext catalog) =>
        npc.HomeX is double x && npc.HomeY is double y && npc.HomeZ is double z
            ? new(x, y, z)
            : Home(catalog.Set<Celestial>().Single(b => b.Id == npc.BeltId));

    internal static float Agility(IReadOnlyDictionary<DogmaAttributeId, double> attributes) => NpcMotionRules.Agility(attributes);

    internal static void Return(WorldNpc npc) => npc.Navigation = NpcNavigationState.Returning;

    internal static void Patrol(
        WorldNpc npc,
        CatalogDbContext catalog,
        CatalogItemDefinition type,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        long now
    )
    {
        npc.Navigation = NpcNavigationState.Patrolling;
        Advance(npc, NpcGuardRules.PatrolDestination(Home(npc, catalog), npc.Id, now), type, attributes, now);
    }

    internal static bool Engaged(WorldNpc npc) => NpcMotionRules.Engaged(State(npc));

    private static NpcMotionState State(WorldNpc npc) => new(npc.Id, Position(npc), Velocity(npc), npc.Navigation);

    private static void Save(WorldNpc npc, NpcMotionState state)
    {
        (npc.X, npc.Y, npc.Z) = (state.Position.X, state.Position.Y, state.Position.Z);
        (npc.VelocityX, npc.VelocityY, npc.VelocityZ) = (state.Velocity.X, state.Velocity.Y, state.Velocity.Z);
        npc.Navigation = state.Navigation;
    }

    private static double Step(WorldNpc npc, long now)
    {
        if (npc.MotionAt == 0)
        {
            npc.MotionAt = now;
        }
        // Shared wall-clock boundaries serialize competing viewers within the owning transaction.
        long elapsed = now / TimeSpan.TicksPerSecond - npc.MotionAt / TimeSpan.TicksPerSecond;
        if (elapsed < 1)
        {
            return 0;
        }
        npc.MotionAt = now;
        return Math.Min(elapsed, NpcEncounterDefinitions.MaximumMovementStepSeconds);
    }

    internal static void Pursue(
        WorldNpc npc,
        SpacePosition target,
        double targetRadius,
        CatalogItemDefinition type,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        long now,
        int destinyStamp
    )
    {
        if (!Engaged(npc))
        {
            npc.MotionAt = now;
        }
        Save(
            npc,
            NpcMotionRules.Pursue(
                State(npc),
                target,
                targetRadius,
                type.Mass ?? 0,
                type.Radius ?? 0,
                attributes,
                Step(npc, now),
                destinyStamp
            )
        );
    }

    internal static void Advance(
        WorldNpc npc,
        SpacePosition destination,
        CatalogItemDefinition type,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        long now,
        bool returning = false,
        SpacePosition? orbitDirection = null
    ) =>
        Save(
            npc,
            NpcMotionRules.Advance(
                State(npc),
                destination,
                type.Mass ?? 0,
                type.Radius ?? 0,
                attributes,
                Step(npc, now),
                returning,
                orbitDirection
            )
        );

    internal static NpcNavigationContact? Contact(
        WorldNpc npc,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        long target,
        SpacePosition home,
        long now = 0
    ) =>
        npc.Navigation switch
        {
            NpcNavigationState.Warping => new(Velocity(npc), 1, 0, 0, home, NpcNavigationState.Warping),
            NpcNavigationState.Travelling => new(
                Velocity(npc),
                NpcMotionRules.SpeedFraction(attributes),
                0,
                0,
                home,
                NpcNavigationState.Travelling
            ),
            NpcNavigationState.Patrolling => new(
                Velocity(npc),
                NpcMotionRules.SpeedFraction(attributes),
                0,
                0,
                NpcGuardRules.PatrolDestination(home, npc.Id, now),
                NpcNavigationState.Patrolling
            ),
            NpcNavigationState.Idle => Velocity(npc) == SpacePosition.Zero
                ? null
                : new(Velocity(npc), 0, 0, 0, State: NpcNavigationState.Idle),
            NpcNavigationState.Pursuing or NpcNavigationState.Orbiting when target != 0 => new(
                Velocity(npc),
                NpcMotionRules.SpeedFraction(attributes),
                target,
                attributes.GetValueOrDefault(DogmaAttributeId.EntityFlyRange),
                State: npc.Navigation
            ),
            NpcNavigationState.Returning => new(
                Velocity(npc),
                NpcMotionRules.SpeedFraction(attributes),
                0,
                0,
                home,
                NpcNavigationState.Returning
            ),
            _ => throw new InvalidDataException("Invalid NPC navigation state."),
        };
}
