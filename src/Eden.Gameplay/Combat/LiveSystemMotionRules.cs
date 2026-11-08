using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;

namespace Eden.Gameplay.Combat;

public sealed record LiveSystemMotion(
    IReadOnlyList<ShipMotionResult> Ships,
    IReadOnlyList<LiveCombatNpc> Npcs,
    IReadOnlyList<CollisionResult> Collisions
);

public static class LiveSystemMotionRules
{
    public static LiveSystemMotion Advance(
        int stamp,
        long now,
        IReadOnlyList<ShipMotion> ships,
        IReadOnlyList<StaticSpaceObject> objects,
        IReadOnlyList<LiveCombatNpc> npcs,
        IReadOnlyDictionary<long, LiveAggression> aggressionByNpc,
        IReadOnlyDictionary<long, IReadOnlyDictionary<DogmaAttributeId, double>> effectiveByNpc
    )
    {
        var starts = npcs.ToDictionary(n => n.Id, n => n.Position);
        var hulls = npcs.ToDictionary(
            n => n.Id,
            n => new FlightProperties(
                0,
                (float)n.Radius,
                n.Mass,
                NpcMotionRules.Agility(n.Attributes),
                (float)effectiveByNpc[n.Id].GetValueOrDefault(DogmaAttributeId.MaxVelocity),
                0
            )
        );
        var available = ships.Where(s => s.Solid && s.Navigation.Warp is null && s.Navigation.WarpDestination is null);
        var targets = available.ToDictionary(s => s.Id, s => new StaticSpaceObject(s.Id, 0, 0, 0, 0, s.Position, s.Hull.ShipRadius));
        foreach (var npc in npcs)
        {
            targets.Add(npc.Id, new(npc.Id, npc.TypeId, 0, 0, 0, starts[npc.Id], hulls[npc.Id].ShipRadius));
        }
        var obstacles = objects.Where(o => o.GroupId != AsteroidDefinitions.BeltGroup).ToArray();
        List<ShipMotionResult> players = [];
        List<CollisionMotion> bodies = [];
        foreach (var ship in ships)
        {
            var navigation = ship.Navigation.Copy();
            var tick = navigation.Advance(
                ship.Id,
                stamp,
                ship.Hull,
                ship.Position,
                id =>
                    targets.GetValueOrDefault(id)
                    ?? (ship.Target?.Id == id && ships.All(s => s.Id != id) ? ship.Target : null)
                    ?? objects.FirstOrDefault(o => o.Id == id),
                obstacles
            );

            bodies.Add(
                new(
                    ship.Id,
                    ship.Hull,
                    ship.Position,
                    tick.InitialVelocity,
                    tick.Position,
                    navigation.Velocity,
                    ship.Solid && ship.Navigation.Warp is null && navigation.Warp is null
                )
            );
            players.Add(new(ship with { Stamp = stamp, Position = tick.Position, Navigation = navigation }, tick.Changes));
        }
        List<LiveCombatNpc> moved = [];
        foreach (var npc in npcs)
        {
            var effective = effectiveByNpc[npc.Id];
            var aggression = aggressionByNpc.GetValueOrDefault(npc.Id);
            var target = aggression is null ? null : targets.GetValueOrDefault(aggression.ShipId);
            var next = Move(npc, effective, target, aggression?.ReadyAt ?? long.MaxValue, now, stamp);
            var initial =
                npc.Navigation == NpcNavigationState.Idle
                && next.Navigation == NpcNavigationState.Idle
                && npc.Role == NpcRole.Pirate
                && npc.MotionAt / TimeSpan.TicksPerSecond < now / TimeSpan.TicksPerSecond
                    ? NpcMotionRules.CoastVelocity(npc.Velocity)
                    : npc.Velocity;
            bodies.Add(
                new(
                    npc.Id,
                    hulls[npc.Id],
                    npc.Position,
                    initial,
                    next.Position,
                    next.Velocity,
                    npc.Navigation != NpcNavigationState.Warping
                )
            );
            moved.Add(next);
        }
        var resolved = ShipCollisions.Resolve(bodies).ToDictionary(b => b.Id);
        for (int index = 0; index < players.Count; index++)
        {
            var result = players[index];
            var collision = resolved[result.Ship.Id];
            result.Ship.Navigation.SetVelocity(collision.Velocity);
            players[index] = result with { Ship = result.Ship with { Position = collision.Position }, Bumped = collision.Bumped };
        }
        return new(
            players,
            moved.Select(n => n with { Position = resolved[n.Id].Position, Velocity = resolved[n.Id].Velocity }).ToArray(),
            resolved.Values.ToArray()
        );
    }

    public static LiveCombatNpc Move(
        LiveCombatNpc npc,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        StaticSpaceObject? target,
        long readyAt,
        long now,
        int stamp,
        bool contactsOnly = false
    )
    {
        if (npc.GroupId == NpcEncounterDefinitions.SentryGroup)
        {
            return npc with { Velocity = SpacePosition.Zero, Navigation = NpcNavigationState.Idle, MotionAt = now };
        }
        if (npc.Transit is { } warp)
        {
            FlightProperties hull = new(
                0,
                (float)npc.Radius,
                npc.Mass,
                NpcMotionRules.Agility(attributes),
                (float)attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity),
                warp.Speed
            );
            var sample = warp.Course(stamp, now, hull).Sample(stamp, Math.Min(hull.MaxSpeed / 2, 100));
            return npc with { Position = sample.Position, Velocity = sample.Velocity, MotionAt = now };
        }
        NpcMotionState state = new(npc.Id, npc.Position, npc.Velocity, npc.Navigation);
        long at = npc.MotionAt;
        bool engaged = NpcMotionRules.Engaged(state);
        bool pursue = !NpcPopulationRules.IsCivilian(npc.Role) && !contactsOnly && target is not null && (engaged || now >= readyAt);
        if (contactsOnly && engaged && target is null)
        {
            state = state with { Navigation = NpcNavigationState.Returning };
        }
        if (pursue && !engaged || at == 0)
        {
            at = now;
        }
        long elapsed = now / TimeSpan.TicksPerSecond - at / TimeSpan.TicksPerSecond;
        double step = elapsed < 1 ? 0 : Math.Min(elapsed, NpcEncounterDefinitions.MaximumMovementStepSeconds);
        long nextAt = elapsed < 1 ? at : now;
        if ((npc.Role == NpcRole.Convoy || npc.Navigation == NpcNavigationState.Travelling) && !contactsOnly)
        {
            state = NpcMotionRules.Advance(
                state with
                {
                    Navigation = NpcNavigationState.Travelling,
                },
                npc.Home,
                npc.Mass,
                npc.Radius,
                attributes,
                step
            );
        }
        else if (pursue)
        {
            state = NpcMotionRules.Pursue(state, target!.Position, target.Radius, npc.Mass, npc.Radius, attributes, step, stamp);
        }
        else if (state.Navigation == NpcNavigationState.Returning || !contactsOnly && engaged)
        {
            state = NpcMotionRules.Advance(
                state with
                {
                    Navigation = NpcNavigationState.Returning,
                },
                npc.Home,
                npc.Mass,
                npc.Radius,
                attributes,
                step,
                returning: true
            );
        }
        else if (contactsOnly)
        {
            return npc with { Navigation = state.Navigation };
        }
        else if (
            npc.Role is NpcRole.Navy or NpcRole.Customs
            && state.Navigation is NpcNavigationState.Idle or NpcNavigationState.Patrolling
        )
        {
            state = NpcMotionRules.Advance(
                state with
                {
                    Navigation = NpcNavigationState.Patrolling,
                },
                NpcGuardRules.PatrolDestination(npc.Home, npc.Id, now),
                npc.Mass,
                npc.Radius,
                attributes,
                step
            );
        }
        else if (npc.MotionAt / TimeSpan.TicksPerSecond < now / TimeSpan.TicksPerSecond)
        {
            state = NpcMotionRules.Coast(
                state,
                new FlightProperties(
                    0,
                    (float)npc.Radius,
                    npc.Mass,
                    NpcMotionRules.Agility(npc.Attributes),
                    (float)attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity),
                    0
                ).InertiaTime
            );
            nextAt = now;
        }
        else
        {
            return npc;
        }
        return npc with { Position = state.Position, Velocity = state.Velocity, Navigation = state.Navigation, MotionAt = nextAt };
    }

    public static NpcNavigationContact? Contact(
        LiveCombatNpc npc,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        long target,
        long now
    ) =>
        npc.Navigation switch
        {
            NpcNavigationState.Warping => new(npc.Velocity, 1, 0, 0, npc.Home, NpcNavigationState.Warping),
            NpcNavigationState.Travelling => new(
                npc.Velocity,
                NpcMotionRules.SpeedFraction(attributes),
                0,
                0,
                npc.Home,
                NpcNavigationState.Travelling
            ),
            NpcNavigationState.Patrolling => new(
                npc.Velocity,
                NpcMotionRules.SpeedFraction(attributes),
                0,
                0,
                NpcGuardRules.PatrolDestination(npc.Home, npc.Id, now),
                NpcNavigationState.Patrolling
            ),
            NpcNavigationState.Idle => npc.Velocity == SpacePosition.Zero
                ? null
                : new(npc.Velocity, 0, 0, 0, State: NpcNavigationState.Idle),
            NpcNavigationState.Pursuing or NpcNavigationState.Orbiting when target != 0 => new(
                npc.Velocity,
                NpcMotionRules.SpeedFraction(attributes),
                target,
                attributes.GetValueOrDefault(DogmaAttributeId.EntityFlyRange),
                State: npc.Navigation
            ),
            NpcNavigationState.Returning => new(
                npc.Velocity,
                NpcMotionRules.SpeedFraction(attributes),
                0,
                0,
                npc.Home,
                NpcNavigationState.Returning
            ),
            _ => throw new InvalidDataException("Invalid NPC navigation state."),
        };
}
