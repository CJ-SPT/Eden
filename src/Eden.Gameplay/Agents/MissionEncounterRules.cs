namespace Eden.Gameplay.Agents;

public sealed record MissionEnemyProgress(long Id, int Wave, bool Trigger, bool Required, bool CompletesMission);

public sealed record MissionEncounterDecision(bool SpawnNextWave, bool Complete);

public static class MissionEncounterRules
{
    public static MissionEncounterDecision Killed(
        MissionEnemyProgress killed,
        int currentWave,
        int? nextWave,
        IReadOnlyList<MissionEnemyProgress> members,
        IReadOnlySet<long> alive,
        bool requiredWavesStarted
    )
    {
        if (killed.CompletesMission)
        {
            return new(false, true);
        }
        var current = members.Where(m => m.Wave == currentWave).ToArray();
        bool triggers = current.Any(m => m.Trigger);
        bool spawn =
            killed.Wave == currentWave
            && nextWave.HasValue
            && !current.Any(m => (triggers ? m.Trigger : m.Required) && m.Id != killed.Id && alive.Contains(m.Id));
        bool complete = requiredWavesStarted && !members.Any(m => m.Required && m.Id != killed.Id && alive.Contains(m.Id));
        return new(spawn, complete);
    }
}
