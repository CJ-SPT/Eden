using Eden.World.Capsuleers;
using Eden.World.Runtime.Combat;

namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels
{
    private readonly ResidentSnapshot<long, CapsuleerDefenseProfile> _defenseProfiles = new("capsuleer.defense-profile");

    private void ObserveDefenseProfiles(IReadOnlyCollection<CombatRowChange> changes)
    {
        foreach (var change in changes)
        {
            if (change.Table == "WorldAccount")
            {
                _defenseProfiles.Clear();
                return;
            }
            // Fit revision triggers already cover skills, hulls, equipment, scripts, active modules and incoming EWAR.
            // Invalidate the actual pilot rather than every actor in a populated system.
            if (change.Table is "WorldFitRevision" or "WorldPilot")
            {
                _defenseProfiles.Remove(change.Id);
            }
        }
    }

    internal CapsuleerDefenseProfile DefenseProfile(long pilot, Func<CapsuleerDefenseProfile> load) => Read(_defenseProfiles, pilot, load);
}
