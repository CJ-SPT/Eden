using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    private readonly ResidentMap<int, NpcSceneIndex> _npcSceneIndices = [];

    internal NpcSceneContacts? ReadScene(int system, SpacePosition position, long now, Func<long> refreshPopulation)
    {
        return Execute(() => ReadSceneOwned(system, position, now, refreshPopulation), nameof(ReadScene));
    }

    private NpcSceneContacts? ReadSceneOwned(int system, SpacePosition position, long now, Func<long> refreshPopulation)
    {
        using var ownership = owner.Enter();
        if (!simulationObjects || !CanRun)
        {
            return null;
        }
        Trim();
        var contacts = SimulationContacts(system, now, refreshPopulation, position);
        return new(contacts, _npcSceneIndices[system].Warping);
    }

    private NpcSceneIndex SceneIndex(int system, IReadOnlyList<LiveCombatNpc> npcs)
    {
        if (_npcSceneIndices.TryGetValue(system, out var current))
        {
            return current;
        }
        var index = new NpcSceneIndex(npcs.Select(n => new NpcSimulationContact(n.Id, n.Position, n.Transit is not null)).ToArray());
        _npcSceneIndices[system] = index;
        return index;
    }

    private void SetNpc(LiveCombatNpc npc)
    {
        owner.TrackNpc(npc.Id, npc.SystemId);
        if (
            !_npcs.TryGetValue(npc.Id, out var previous)
            || previous.SystemId != npc.SystemId
            || previous.Position != npc.Position
            || previous.Available != npc.Available
            || (previous.Transit is null) != (npc.Transit is null)
        )
        {
            if (previous is not null)
            {
                _npcSceneIndices.Remove(previous.SystemId);
            }
            _npcSceneIndices.Remove(npc.SystemId);
        }
        _npcs[npc.Id] = npc;
    }

    private void RemoveNpc(long id)
    {
        if (_npcs.Remove(id, out var npc))
        {
            _npcSceneIndices.Remove(npc.SystemId);
        }
    }
}
