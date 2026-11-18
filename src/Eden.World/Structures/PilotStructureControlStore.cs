using System.Collections.Immutable;
using Eden.Gameplay.Structures;
using Eden.Gameplay.Targeting;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Structures;

[Injectable]
internal sealed class PilotStructureControlStore(WorldDatabase database, IPilotStructures structures) : IPilotStructureControl
{
    private LiveWorld Live =>
        LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Structure control requires the resident world.");

    public IStructureControlLease Acquire(long structureId, long pilotId, long sessionId)
    {
        var live = Live;
        using var ownership = live.EnterPersistence(nameof(Acquire));
        _ = structures.AuthorizeControl(pilotId, structureId);
        if (sessionId <= 0)
        {
            throw new InvalidDataException("An authenticated session is required to control a structure.");
        }
        if (live.StructureControllers.TryGetValue(structureId, out var previous))
        {
            if (previous.Token.PilotId == pilotId && previous.Token.SessionId == sessionId)
            {
                return previous;
            }
            throw new InvalidDataException("Another pilot is controlling this structure.");
        }
        var token = new StructureControlToken(structureId, pilotId, sessionId, Guid.NewGuid());
        var lease = new ResidentStructureControl(token, live, database.Path);
        if (!live.StructureControllers.TryAdd(structureId, lease))
        {
            throw new InvalidDataException("Another pilot is controlling this structure.");
        }
        return lease;
    }

    public long? Pilot(long structureId)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return null;
        }
        if (!live.StructureControllers.TryGetValue(structureId, out var lease))
        {
            return null;
        }
        return lease.Token.PilotId;
    }

    public StructureSnapshot Require(StructureControlToken token)
    {
        var live = Live;
        using var ownership = live.EnterPersistence(nameof(Require));
        if (!live.StructureControllers.TryGetValue(token.StructureId, out var lease) || lease.Token != token)
        {
            throw new InvalidDataException("This structure control lease is no longer active.");
        }
        return structures.AuthorizeControl(token.PilotId, token.StructureId);
    }
}

internal sealed class ResidentStructureControl(StructureControlToken token, LiveWorld owner, string path) : IStructureControlLease
{
    private readonly Lock _gate = new();
    public StructureControlToken Token { get; } = token;
    internal ImmutableDictionary<long, StructureTargetLock> Targets { get; set; } = ImmutableDictionary<long, StructureTargetLock>.Empty;
    internal ImmutableDictionary<long, StructureWeaponCycle> Weapons { get; set; } = ImmutableDictionary<long, StructureWeaponCycle>.Empty;
    internal long LockRevision { get; set; }

    internal T WithActive<T>(Func<T> action)
    {
        lock (_gate)
        {
            if (
                !ReferenceEquals(LiveWorld.Find(path), owner)
                || !owner.StructureControllers.TryGetValue(Token.StructureId, out var current)
                || !ReferenceEquals(current, this)
            )
            {
                throw new InvalidDataException("This structure control lease is no longer active.");
            }
            return action();
        }
    }

    public void Dispose()
    {
        // A connection may be disposed after its resident host, or after a new host has opened the same saved world.
        if (!ReferenceEquals(LiveWorld.Find(path), owner))
        {
            return;
        }
        using var ownership = owner.EnterPersistence(nameof(Dispose));
        lock (_gate)
        {
            if (!owner.StructureControllers.TryGetValue(Token.StructureId, out var lease) || !ReferenceEquals(lease, this))
            {
                return;
            }
            var webs = Weapons.Values.Where(cycle => cycle.Effect == Eden.Gameplay.Inventory.DogmaEffect.StructureStasisWebifier).ToArray();
            if (webs.Length > 0)
            {
                using var action = owner.Economy.Begin(new(ships: webs.Select(cycle => cycle.TargetId)));
                foreach (var cycle in webs)
                {
                    if (action.Set<Eden.Data.World.Propulsion.WorldTackle>().Find(cycle.ModuleId) is { } row)
                    {
                        action.Remove(row);
                    }
                }
                action.Commit();
            }
            if (owner.StructureControllers.TryRemove(KeyValuePair.Create(Token.StructureId, this)))
            {
                if (owner.TargetLocks is IPilotLockBreaks breaks)
                {
                    breaks.BreakOutgoing(Token.StructureId);
                }
                else
                {
                    foreach (long target in Targets.Keys)
                    {
                        owner.TargetLocks?.Remove(Token.StructureId, target);
                    }
                }
                Targets = ImmutableDictionary<long, StructureTargetLock>.Empty;
                Weapons = ImmutableDictionary<long, StructureWeaponCycle>.Empty;
            }
        }
    }
}
