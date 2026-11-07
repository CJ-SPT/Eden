using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;

namespace Eden.World.Combat.Npcs;

internal static class NpcRepairPersistence
{
    private const int ProbabilityResolution = 1_000_000;

    internal static double Draw() =>
        System.Security.Cryptography.RandomNumberGenerator.GetInt32(ProbabilityResolution) / (double)ProbabilityResolution;

    internal static void Advance(WorldNpc npc, IReadOnlyDictionary<DogmaAttributeId, double> attributes, long now)
    {
        var defense = ShipHealth.DefenseFrom(attributes);
        LiveHealth health = new(
            defense,
            new(npc.ShieldCharge, Math.Max(0, defense.Armor - npc.ArmorDamage), Math.Max(0, defense.Structure - npc.StructureDamage)),
            npc.UpdatedAt,
            ArmorDamage: npc.ArmorDamage,
            StructureDamage: npc.StructureDamage
        );
        NpcRepairState state = new(npc.ShieldRepairAt, npc.ArmorRepairAt, npc.RepairRevision);
        var result = NpcRepairRules.Advance(
            NpcRepairRules.Profile(attributes),
            state,
            health,
            npc.DestroyedAt is null && npc.RetiredAt is null,
            now,
            Draw
        );
        if (result.Health != health)
        {
            npc.ShieldCharge = result.Health.Remaining.Shield;
            npc.ArmorDamage = result.Health.ArmorDamage ?? npc.ArmorDamage;
            npc.UpdatedAt = result.Health.UpdatedAt;
        }
        npc.ShieldRepairAt = result.State.ShieldReadyAt;
        npc.ArmorRepairAt = result.State.ArmorReadyAt;
        npc.RepairRevision = result.State.Revision;
    }
}
