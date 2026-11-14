namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    internal int CompactPositions => _positions.Count;
    internal int DetailedEntries => _ships.Count + _npcs.Count + _contacts.Count + _npcSceneIndices.Count;

    internal bool HasDetailedInteraction(long now) =>
        _ships.Values.Any(s => s.NpcAggression?.ActiveUntil > now)
        || _aggressions?.Values.Any(a => a.ShipId != 0) == true
        || _droneHostilities.Values.Any(h => h.ExpiresAt > now)
        || _liveDrones.Values.Any(d => d.Contact.TargetId != 0);

    private void TrimColdDetails()
    {
        if (
            HasDetailedInteraction(owner.SimulationTime)
            || owner.Economy.HasActiveAction
            || owner.HasOpenSqlTransaction
            || owner.HasUnpublishedSqlChanges
        )
        {
            return;
        }
        var systems = _ships
            .Values.Select(s => s.SystemId)
            .Concat(_npcs.Values.Select(n => n.SystemId))
            .Concat(_systemNpcs.Keys)
            .Where(s => !owner.IsDetailedSystem(s))
            .ToHashSet();
        if (systems.Count == 0)
        {
            return;
        }
        PublishResident(CaptureOwnCheckpoint(new(systems: systems)));
        var pilots = _ships.Values.Where(s => systems.Contains(s.SystemId)).Select(s => s.PilotId).ToHashSet();
        var ships = _shipPilots.Where(p => pilots.Contains(p.Value)).Select(p => p.Key).ToHashSet();
        foreach (long pilot in pilots)
        {
            _ships.Remove(pilot);
            _savedShips.Remove(pilot);
            _staleShips.Remove(pilot);
            _acquisitionShips.Remove(pilot);
            _dronePilots.Remove(pilot);
            _dronePilotSystems.Remove(pilot);
        }
        foreach (long ship in ships)
        {
            _shipPilots.Remove(ship);
            _positions.Remove(ship);
            _savedPositions.Remove(ship);
            _dronePlayers.Remove(ship);
            _droneCombatTriggers.Remove(ship);
        }
        foreach (long item in _itemPilots.Where(p => pilots.Contains(p.Value)).Select(p => p.Key).ToArray())
        {
            _itemPilots.Remove(item);
        }
        foreach (long id in _npcs.Values.Where(n => systems.Contains(n.SystemId)).Select(n => n.Id).ToArray())
        {
            RemoveNpc(id);
            _savedNpcs.Remove(id);
            _staleNpcs.Remove(id);
            _effectiveNpcs.Remove(id);
            _aggressions?.Remove(id);
        }
        foreach (int system in systems)
        {
            _systemNpcs.Remove(system);
            _populationDue.Remove(system);
            _contacts.Remove(system);
            _npcSceneIndices.Remove(system);
            _droneSystems.Remove(system);
        }
        foreach (long id in _sharedNpcContacts.Where(p => systems.Contains(p.Value.Source.SystemId)).Select(p => p.Key).ToArray())
        {
            _sharedNpcContacts.Remove(id);
        }
        foreach (long id in _liveDrones.Where(p => systems.Contains(p.Value.Contact.SystemId)).Select(p => p.Key).ToArray())
        {
            _liveDrones.Remove(id);
        }
        foreach (var key in _guardGroups.Keys.Where(k => systems.Contains(k.System)).ToArray())
        {
            _guardGroups.Remove(key);
        }
        _acquisitions.RemoveAll(a => a.Candidates.Any(c => pilots.Contains(c.PilotId)));
    }
}
