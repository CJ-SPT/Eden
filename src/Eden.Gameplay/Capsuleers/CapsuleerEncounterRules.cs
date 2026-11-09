namespace Eden.Gameplay.Capsuleers;

public enum MinerEncounterOpening
{
    Docked,
    Returning,
    Mining,
}

public static class CapsuleerEncounterRules
{
    public const int CorridorGateCount = 3;

    public static IReadOnlyList<CapsuleerCareer> Careers(long seed, int systemId, int gates, bool supportsMining)
    {
        List<CapsuleerCareer> careers = [];
        if (supportsMining)
        {
            careers.Add(CapsuleerCareer.Mining);
        }
        // Corridors always have a hauler. Half of quiet systems retain one,
        // determined by the saved seed so repeated visits never reroll grants.
        if (gates >= CorridorGateCount || ((seed ^ systemId) & 1) == 0)
        {
            careers.Add(CapsuleerCareer.Hauling);
        }
        return careers;
    }

    public static MinerEncounterOpening Opening(long seed, int systemId) =>
        ((seed + systemId) & 3) switch
        {
            0 => MinerEncounterOpening.Docked,
            1 => MinerEncounterOpening.Returning,
            _ => MinerEncounterOpening.Mining,
        };
}
