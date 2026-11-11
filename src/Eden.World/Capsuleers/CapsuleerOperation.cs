using Eden.Gameplay.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Eden.World.Capsuleers;

// A role action opens SQL only when it crosses an inventory, ownership or payment
// boundary. Navigation and nonfatal simulation keep their existing resident path.
internal sealed class CapsuleerOperation(
    WorldDatabase database,
    CapsuleerJob expected,
    MarketChangeSignal market,
    CapsuleerCoarseWork? coarse = null
) : IDisposable
{
    private WorldDbContext? _world;
    private CatalogDbContext? _catalog;
    private IDbContextTransaction? _transaction;
    internal bool MarketChanged { get; set; }
    internal bool TravelChanged { get; set; }
    internal bool HasWorld => _world is not null;

    internal WorldDbContext World
    {
        get
        {
            if (_world is null)
            {
                var live = LiveWorld.Find(database.Path);
                long ship =
                    live?.MinerCheckpoints.GetValueOrDefault(expected.PilotId)?.Work.ShipId
                    ?? live?.HaulerCheckpoints.GetValueOrDefault(expected.PilotId)?.Work.ShipId
                    ?? 0;
                TransactionParticipants? participants = new(pilots: [expected.PilotId], ships: ship > 0 ? [ship] : []);
                var work = live?.HaulerCheckpoints.GetValueOrDefault(expected.PilotId)?.Work;
                bool inbound = expected.Action == CapsuleerAction.Arrival && work?.Activity == HaulerActivity.Inbound;
                if (coarse is not null)
                {
                    participants = new(systems: coarse.Systems, pilots: [expected.PilotId], ships: ship > 0 ? [ship] : []);
                }
                else if (expected.Action == CapsuleerAction.GateTransfer || inbound)
                {
                    long? sourceGate = inbound ? work?.TrafficLane?.DepartureGateId : work?.TargetId;
                    if (sourceGate is > 0 and <= int.MaxValue && database.Catalog.Gates.TryGetValue((int)sourceGate, out var gate))
                    {
                        participants = new(
                            systems: [gate.SolarSystemId, gate.DestinationSystemId],
                            pilots: [expected.PilotId],
                            ships: ship > 0 ? [ship] : []
                        );
                    }
                    else
                    {
                        // A cold restored job has not published its route checkpoint yet.
                        // Preserve the measured conservative boundary until it does.
                        participants = null;
                    }
                }
                _world = new(database.Path) { Participants = participants };
                _transaction = _world.Database.BeginTransaction();
                // Resident scheduling is canonical between checkpoints. Bring its
                // expected revision into the same transaction before the mutation.
                CapsuleerJobCheckpoint.Apply(_world, expected);
            }
            return _world;
        }
    }

    internal CatalogDbContext Catalog => _catalog ??= new(database.CatalogPath);

    internal void Commit(CapsuleerJob next, MinerCheckpoint? miner, HaulerCheckpoint? hauler)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("traffic.commitPublication");
        var live = LiveWorld.Find(database.Path);
        if (_world is not null || live is null || coarse is not null)
        {
            var world = World;
            miner?.Apply(world);
            hauler?.Apply(world);
            int changed = world.Database.ExecuteSql(
                $"""
                UPDATE WorldCapsuleerJob SET Revision={next.Revision},Sequence={next.Sequence},
                    DueTicks={next.DueTicks},Action={next.Action}
                WHERE PilotId={expected.PilotId} AND Revision={expected.Revision} AND Sequence={expected.Sequence}
                    AND DueTicks={expected.DueTicks} AND Action={expected.Action}
                """
            );
            if (changed != 1)
            {
                throw new InvalidOperationException("The capsuleer job changed before settlement.");
            }
            if (coarse is not null)
            {
                int advanced = world.Database.ExecuteSql(
                    $"""
                    UPDATE WorldCapsuleerCoarseWork SET Cursor={expected.PilotId},Generation=Generation+1
                    WHERE SystemId={coarse.SystemId} AND Generation={coarse.Generation} AND Cursor={coarse.Cursor}
                    """
                );
                if (advanced != 1)
                {
                    throw new CapsuleerCoarseConflictException(coarse.SystemId);
                }
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldCapsuleerCoarsePilot(PilotId,SettledTicks) VALUES({expected.PilotId},{coarse.ThroughTicks})
                    ON CONFLICT(PilotId) DO UPDATE SET SettledTicks=excluded.SettledTicks
                    """
                );
            }
            world.SaveChanges();
            _transaction!.Commit();
            if (live is not null)
            {
                live.ProjectedCapsuleerJobs[next.PilotId] = next;
                if (miner is not null)
                {
                    live.ProjectedMiners[miner.PilotId] = miner;
                }
                if (hauler is not null)
                {
                    live.ProjectedHaulers[hauler.PilotId] = hauler;
                }
            }
        }
        Dispose();
        if (live is not null)
        {
            if (miner is not null)
            {
                live.MinerCheckpoints[miner.PilotId] = miner;
            }
            if (hauler is not null)
            {
                live.HaulerCheckpoints[hauler.PilotId] = hauler;
            }
            live.CapsuleerJobs[next.PilotId] = next;
            live.ObjectsChanged();
            if (TravelChanged)
            {
                live.Heat.ClearSelection(next.PilotId, miner?.Work.ShipId ?? hauler!.Work.ShipId);
            }
        }
        if (MarketChanged)
        {
            market.Changed();
        }
    }

    public void Dispose()
    {
        _transaction?.Dispose();
        _transaction = null;
        _world?.Dispose();
        _world = null;
        _catalog?.Dispose();
        _catalog = null;
    }
}
