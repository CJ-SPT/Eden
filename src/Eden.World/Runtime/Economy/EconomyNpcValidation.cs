using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;

namespace Eden.World.Runtime.Economy;

internal sealed partial class EconomyTransaction
{
    private sealed record NpcSpawnSlot(long EncounterId, int Slot);

    private void ValidateNpcSpawnSlots()
    {
        // Match the saved partial unique index. Legacy pirates do not reserve slots.
        var encounters = Retained<WorldNpc>()
            .Where(npc => npc.Role != NpcRole.Pirate && npc.DestroyedAt is null && npc.RetiredAt is null)
            .Select(npc => npc.EncounterId)
            .ToHashSet();
        if (encounters.Count == 0)
        {
            return;
        }
        // Project both saved and private members once. Per-NPC queries repeatedly
        // merge every private row, making a population refresh quadratic.
        var slots = Filter<WorldNpc>(npc =>
                Enumerable.Contains(encounters, npc.EncounterId)
                && npc.Role != NpcRole.Pirate
                && npc.DestroyedAt == null
                && npc.RetiredAt == null
            )
            .Select(npc => new NpcSpawnSlot(npc.EncounterId, npc.SpawnSlot));
        HashSet<NpcSpawnSlot> occupied = [];
        foreach (var slot in slots)
        {
            if (!occupied.Add(slot))
            {
                throw new InvalidDataException("Duplicate resident WorldNpc unique value.");
            }
        }
    }
}
