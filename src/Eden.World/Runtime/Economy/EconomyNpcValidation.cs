using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;

namespace Eden.World.Runtime.Economy;

internal sealed partial class EconomyTransaction
{
    private readonly record struct NpcSpawnSlot(long EncounterId, int Slot);

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
        var npcs = (EconomySet<WorldNpc, EconomyWorldNpc>)Set<WorldNpc>();
        HashSet<NpcSpawnSlot> occupied = [];
        foreach (var npc in npcs.IndexedSnapshots(nameof(WorldNpc.EncounterId), encounters))
        {
            if (
                encounters.Contains(npc.EncounterId)
                && npc.Role != NpcRole.Pirate
                && npc.DestroyedAt is null
                && npc.RetiredAt is null
                && !occupied.Add(new(npc.EncounterId, npc.SpawnSlot))
            )
            {
                throw new InvalidDataException("Duplicate resident WorldNpc unique value.");
            }
        }
    }
}
