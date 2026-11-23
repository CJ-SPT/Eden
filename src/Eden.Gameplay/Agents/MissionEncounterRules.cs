using System.Text.Json.Serialization;
using Eden.Gameplay.Combat;

namespace Eden.Gameplay.Agents;

[JsonConverter(typeof(JsonStringEnumConverter<MissionHealthLayer>))]
public enum MissionHealthLayer
{
    Shield,
    Armor,
    Hull,
}

public sealed record MissionEnemyProgress(long Id, int Wave, bool Trigger, bool Required, bool CompletesMission);

public sealed record MissionEncounterDecision(bool SpawnNextWave, bool Complete);

public static class MissionEncounterRules
{
    public static double LayerHealth(HullDamage health, MissionHealthLayer layer) =>
        layer switch
        {
            MissionHealthLayer.Shield => health.Shield,
            MissionHealthLayer.Armor => health.Armor,
            MissionHealthLayer.Hull => health.Structure,
            _ => throw new ArgumentOutOfRangeException(nameof(layer)),
        };

    public static bool CrossesHealthThreshold(
        HullDamage before,
        HullDamage after,
        HullDefense defense,
        MissionHealthLayer layer,
        double remainingFraction
    )
    {
        double previous = LayerHealth(before, layer);
        double remaining = LayerHealth(after, layer);
        double capacity = layer switch
        {
            MissionHealthLayer.Shield => defense.Shield,
            MissionHealthLayer.Armor => defense.Armor,
            MissionHealthLayer.Hull => defense.Structure,
            _ => throw new ArgumentOutOfRangeException(nameof(layer)),
        };
        if (remainingFraction == 1)
        {
            return previous >= capacity && remaining < capacity;
        }
        double threshold = capacity * remainingFraction;
        return previous > threshold && remaining <= threshold;
    }

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
