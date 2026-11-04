using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Combat;

public enum NpcRole
{
    Pirate,
    Navy,
    Customs,
}

public enum NpcAnchorKind
{
    Belt,
    Gate,
    Station,
}

public static class NpcPopulationRules
{
    public static bool DetectsPilots(NpcRole role) => role == NpcRole.Pirate;

    public static bool RewardsPirateKill(NpcRole role) => role == NpcRole.Pirate;

    public static double DetectionRange(IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        Math.Max(NpcEncounterDefinitions.DetectionRange, attributes.GetValueOrDefault(DogmaAttributeId.EntityAttackRange));

    public static double Leash(IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        Math.Max(NpcEncounterDefinitions.ChaseLeash, DetectionRange(attributes) * 2);
}
