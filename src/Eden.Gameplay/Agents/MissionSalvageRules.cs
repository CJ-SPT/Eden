namespace Eden.Gameplay.Agents;

public static class MissionSalvageRules
{
    // An admitted salvage site reserves the final scenery and NPC slots for its wreck and destroyed source.
    private const int WreckSlot = MissionRules.NpcsPerMission - 1;

    public static bool IsWreck(long id) =>
        id >= MissionRules.FirstObjectId
        && id < MissionContainerRules.FirstId
        && (id - MissionRules.FirstObjectId) % MissionRules.NpcsPerMission == WreckSlot;

    public static long WreckId(long missionId)
    {
        long id = checked(MissionRules.FirstObjectId + missionId * MissionRules.NpcsPerMission + WreckSlot);
        return missionId > 0 && IsWreck(id) ? id : throw new InvalidDataException("Invalid career wreck identity.");
    }

    public static long SourceId(long missionId) => MissionRules.FirstNpcId + WreckId(missionId) - MissionRules.FirstObjectId;

    public static long MissionId(long wreckId) => (wreckId - MissionRules.FirstObjectId) / MissionRules.NpcsPerMission;
}
