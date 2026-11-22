using Eden.Data.World.Capsuleers;
using Eden.Data.World.Docking;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.FactionWarfare;

internal sealed partial class MilitiaComplexStore
{
    private readonly HashSet<int> _advancingSystems = [];

    public void Tick(long now)
    {
        if (Resident is not { } live)
        {
            return;
        }

        int[] systems;
        HashSet<int> deployments;
        using (live.EnterPersistence("militia system schedule", TransactionParticipants.None))
        {
            deployments = live.Economy.Inspect(world =>
                world.Filter<SavedMilitiaObjective>(row => row.PilotId != null).Select(row => row.SystemId).ToHashSet()
            );
            var owners = live.OwnerDirectory.Snapshot;
            var autonomous =
                database.Settings.Capsuleers.Enabled && live.CapsuleerActivity.Read().Active
                    ? live.CapsuleerSystems(
                        IsWarzone,
                        pilot => live.CapsuleerJobs.ContainsKey(pilot) && owners.PilotMilitia(pilot) is not null
                    )
                    : [];
            systems = _advancingSystems.Concat(presence.Systems()).Concat(autonomous).Concat(deployments).Distinct().Order().ToArray();
        }

        foreach (int system in systems)
        {
            using var context = live.UseSystem(system);
            using var owner = live.EnterPersistence("militia system step", TransactionParticipants.None);
            var participants = new TransactionParticipants(systems: [system]);
            using var systemOwner = live.EnterTransactionSystems(participants);
            var pilots = presence.Pilots(system).Concat(AutonomousPilots(live, participants, system)).ToArray();
            if (pilots.Length == 0 && !_advancingSystems.Contains(system) && !deployments.Contains(system))
            {
                continue;
            }
            AdvanceOwned(live, system, pilots, now);
            // Give a system one empty observation after its last participants leave.
            if (pilots.Length == 0)
            {
                _advancingSystems.Remove(system);
            }
            else
            {
                _advancingSystems.Add(system);
            }
        }
    }

    private IEnumerable<MilitiaComplexPilot> AutonomousPilots(LiveWorld live, TransactionParticipants participants, int system)
    {
        if (!database.Settings.Capsuleers.Enabled || live.CapsuleerActivity.Read() is not { Active: true } activity)
        {
            yield break;
        }
        var owners = live.OwnerDirectory.Snapshot;
        foreach (long pilot in live.CapsuleerParticipants(participants))
        {
            if (
                owners.PilotMilitia(pilot) is null
                || !live.CapsuleerJobs.ContainsKey(pilot)
                || live.Economy.Peek<SavedCapsuleerSuspension>(pilot) is not null
            )
            {
                continue;
            }
            var hauler = live.HaulerCheckpoints.GetValueOrDefault(pilot)?.Work;
            var miner = live.MinerCheckpoints.GetValueOrDefault(pilot)?.Work;
            long ship = hauler?.ShipId ?? miner?.ShipId ?? 0;
            if (
                (hauler?.Navigation ?? miner?.Navigation) is not { Warp: null, WarpDestination: null }
                || hauler?.Activity is HaulerActivity.Docked or HaulerActivity.Jumping or HaulerActivity.Inbound
                || hauler is not null && hauler.Stamp < hauler.ArrivalUntil
                || !live.Economy.ActiveShip(pilot, ship)
                || live.Economy.Peek<FlightPosition>(ship) is not { SystemId: int savedSystem } saved
                || savedSystem != system
            )
            {
                continue;
            }

            // Activity can pause and resume between samples. A new online interval cannot earn the missed time.
            yield return new(
                pilot,
                ship,
                live.PublishedPosition(system, ship) ?? new SpacePosition(saved.X, saved.Y, saved.Z),
                -activity.Resumes
            );
        }
    }
}
