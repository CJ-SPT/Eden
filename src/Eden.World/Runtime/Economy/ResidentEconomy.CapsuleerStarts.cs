using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    // Use the committed job index without materializing pilot, ship or progress rows.
    internal IEnumerable<(long DueTicks, long Pilot)> CapsuleerStartCandidates()
    {
        var agents = (EconomyTable<SavedCapsuleerAgent, EconomySavedCapsuleerAgent>)_tables[typeof(SavedCapsuleerAgent)];
        var jobs = (EconomyTable<SavedCapsuleerJob, EconomySavedCapsuleerJob>)_tables[typeof(SavedCapsuleerJob)];
        var suspensions = _tables[typeof(SavedCapsuleerSuspension)];
        foreach (var key in agents.Indexed("Career", CapsuleerCareer.Mining).Concat(agents.Indexed("Career", CapsuleerCareer.Hauling)))
        {
            if (!suspensions.Contains(key) && jobs.Snapshot(key) is { } job)
            {
                yield return (job.DueTicks, job.PilotId);
            }
        }
    }
}
