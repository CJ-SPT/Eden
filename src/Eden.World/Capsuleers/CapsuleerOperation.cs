using Eden.Gameplay.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Eden.World.Capsuleers;

// Live role actions commit inventory, ownership and payments in a private resident view.
// Offline tools use the same typed operations within their SQL transaction.
internal sealed class CapsuleerOperation(
    WorldDatabase database,
    CapsuleerJob expected,
    MarketChangeSignal market,
    CapsuleerCoarseWork? coarse = null,
    TransactionParticipants? declared = null
) : IDisposable
{
    private WorldDbContext? _world;
    private WorldAction? _action;
    private EconomyTransaction? _resident;
    private CatalogDbContext? _catalog;
    private IDbContextTransaction? _transaction;
    internal bool MarketChanged { get; set; }
    internal bool TravelChanged { get; set; }
    internal bool HasWorld => _action is not null;
    internal List<HaulerCheckpoint> GangEnrollments { get; } = [];
    internal MinerCheckpoint? IncomingMiner { get; set; }
    internal HaulerCheckpoint? IncomingHauler { get; set; }

    internal WorldAction World
    {
        get
        {
            if (_action is null)
            {
                var live = LiveWorld.Find(database.Path);
                var miner = live?.MinerCheckpoints.GetValueOrDefault(expected.PilotId)?.Work;
                var work = live?.HaulerCheckpoints.GetValueOrDefault(expected.PilotId)?.Work;
                long ship = miner?.ShipId ?? work?.ShipId ?? 0;
                TransactionParticipants? participants = declared ?? new(pilots: [expected.PilotId], ships: ship > 0 ? [ship] : []);
                bool inbound = expected.Action == CapsuleerAction.Arrival && work?.Activity == HaulerActivity.Inbound;
                if (coarse is not null)
                {
                    participants = new(systems: coarse.Systems, pilots: [expected.PilotId], ships: ship > 0 ? [ship] : []);
                }
                else if (expected.Action == CapsuleerAction.GateTransfer || inbound)
                {
                    long? sourceGate =
                        inbound ? work?.TrafficLane?.DepartureGateId
                        : miner is { Activity: MinerActivity.Jumping } ? miner.TargetId
                        : work?.TargetId;
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
                if (live is not null)
                {
                    _resident = live.Economy.Begin(participants);
                    _action = _resident;
                    return _action;
                }
                _world = new(database.Path) { Participants = participants, UsesResidentCapsuleerRoutines = live is not null };
                _transaction = _world.Database.BeginTransaction();
                _action = new SqlWorldAction(_world);
                // Offline tools retain the SQL job contract. Live progress is published
                // only after this mutation commits, under the same scheduler owner.
                if (live is null)
                {
                    CapsuleerJobCheckpoint.Apply(_world, expected);
                }
            }
            return _action;
        }
    }

    internal CatalogDbContext Catalog => _catalog ??= new(database.CatalogPath);

    internal void Commit(CapsuleerJob next, MinerCheckpoint? miner, HaulerCheckpoint? hauler)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("traffic.commitPublication");
        var live = LiveWorld.Find(database.Path);
        if (
            hauler is not null
            && live?.HaulerCheckpoints.GetValueOrDefault(hauler.PilotId)?.Work.Gang is { } committedGang
            && (
                hauler.Work.Gang is null
                || hauler.Work.Gang.Id == committedGang.Id
                    && hauler.Work.Gang.Sequence == committedGang.Sequence
                    && hauler.Work.Gang.Revision < committedGang.Revision
            )
        )
        {
            // A paused or sampled worker may still hold an earlier command revision.
            hauler = hauler with
            {
                Work = hauler.Work with { Gang = committedGang },
            };
        }
        if (live?.CapsuleerJobs.TryGetValue(expected.PilotId, out var current) == true && current != expected)
        {
            throw new InvalidOperationException("A stale capsuleer job cannot commit.");
        }
        if (coarse is not null)
        {
            live?.CapsuleerRoutines.Validate(coarse);
        }
        if (_resident is not null)
        {
            // Keep the outgoing worker's training and memories without leaving a second live flight state.
            if (IncomingMiner is not null)
            {
                hauler!.Apply(_resident);
            }
            if (IncomingHauler is not null)
            {
                miner!.Apply(_resident);
            }
            _resident.Commit();
        }
        else if (_world is not null || live is null)
        {
            _ = World;
            var world = _world!;
            if (live is null)
            {
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
            }
            world.SaveChanges();
            _transaction!.Commit();
        }
        if (coarse is not null)
        {
            live?.CapsuleerRoutines.Complete(coarse, expected.PilotId);
        }
        if (live is not null)
        {
            if (
                database.Settings.Capsuleers.RegionalConflicts
                && hauler is not null
                && hauler.Work.RegionalEscort is null
                && !hauler.Work.Retreating
                && live.HaulerCheckpoints.GetValueOrDefault(hauler.PilotId)?.Work.RegionalEscort is { } escort
                && escort.ShipId == hauler.Work.ShipId
                && hauler.Work.ActiveTicks < escort.Operation.ExpiresAt
                && RegionalConflictStore.Operation(database, escort.Operation.Key)?.Status == RegionalOperationStatus.Active
            )
            {
                hauler = hauler with { Work = hauler.Work with { RegionalEscort = escort } };
            }
            // Enrollment and shared command revisions publish only after the owning action commits.
            // The existing checkpoint capture persists these immutable records in the same generation.
            foreach (var enrollment in GangEnrollments)
            {
                live.HaulerCheckpoints[enrollment.PilotId] = enrollment;
            }
            if (hauler?.Work.Gang is { } gang)
            {
                foreach (var member in gang.Members)
                {
                    if (
                        live.HaulerCheckpoints.GetValueOrDefault(member.PilotId) is { } checkpoint
                        && checkpoint.Work.Gang is { } previous
                        && previous.Id == gang.Id
                        && previous.Sequence == gang.Sequence
                        && previous.Revision < gang.Revision
                    )
                    {
                        live.HaulerCheckpoints[member.PilotId] = checkpoint with { Work = checkpoint.Work with { Gang = gang } };
                    }
                }
            }
            if (miner is not null)
            {
                live.MinerCheckpoints[miner.PilotId] = miner;
            }
            if (hauler is not null)
            {
                live.HaulerCheckpoints[hauler.PilotId] = hauler;
            }
            if (IncomingMiner is { } mining)
            {
                live.HaulerCheckpoints.Remove(mining.PilotId);
                live.ProjectedHaulers.Remove(mining.PilotId);
                live.MinerCheckpoints[mining.PilotId] = mining;
                live.CapsuleerWorkerChanges[mining.PilotId] = CapsuleerCareer.Mining;
            }
            if (IncomingHauler is { } hauling)
            {
                live.MinerCheckpoints.Remove(hauling.PilotId);
                live.ProjectedMiners.Remove(hauling.PilotId);
                live.HaulerCheckpoints[hauling.PilotId] = hauling;
                live.CapsuleerWorkerChanges[hauling.PilotId] = CapsuleerCareer.Hauling;
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
        Dispose();
    }

    public void Dispose()
    {
        _resident?.Dispose();
        _resident = null;
        _action = null;
        _transaction?.Dispose();
        _transaction = null;
        _world?.Dispose();
        _world = null;
        _catalog?.Dispose();
        _catalog = null;
    }
}
