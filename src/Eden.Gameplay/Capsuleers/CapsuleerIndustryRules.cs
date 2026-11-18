using Eden.Gameplay.Industry;

namespace Eden.Gameplay.Capsuleers;

public static class CapsuleerIndustryRules
{
    private const int MaximumDemandRuns = 5;

    public static int DemandRuns(int demand, int unitsPerRun, long secondsPerRun, int? licensedRuns)
    {
        int runLimit = licensedRuns is >= 0
            ? Math.Min(MaximumDemandRuns, licensedRuns.Value)
            : (int)Math.Min(MaximumDemandRuns, Math.Ceiling((double)IndustryDefinitions.MaximumJobSeconds / secondsPerRun));
        return Math.Min(runLimit, demand / unitsPerRun);
    }
}
