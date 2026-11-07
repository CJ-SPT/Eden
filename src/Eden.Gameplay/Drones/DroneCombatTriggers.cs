namespace Eden.Gameplay.Drones;

// Revision distinguishes separate paid actions at the same simulated instant.
public sealed record DroneCombatTrigger(long Revision = 0, long OtherId = 0, long At = 0)
{
    public DroneCombatTrigger Record(long otherId, long now)
    {
        if (otherId <= 0 || now <= 0)
        {
            throw new InvalidDataException("A drone combat trigger requires an entity and a positive time.");
        }
        return now < At ? this : new(checked(Revision + 1), otherId, now);
    }
}

// Only paid fitted offensive modules advance Offensive. Drone attacks advance
// Incoming alone, so an assisted flight cannot recursively trigger another one.
public sealed record DroneCombatTriggers(long ShipId, DroneCombatTrigger Offensive, DroneCombatTrigger Incoming)
{
    public static DroneCombatTriggers Empty(long shipId) => new(shipId, new(), new());

    public DroneCombatTriggers Module(long targetId, long now) => this with { Offensive = Offensive.Record(targetId, now) };

    public DroneCombatTriggers Attacked(long sourceId, long now) => this with { Incoming = Incoming.Record(sourceId, now) };
}
