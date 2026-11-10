using System.Collections.Concurrent;
using Eden.Gameplay.Thermal;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime;

internal sealed partial class LiveWorld
{
    private sealed record HeatOwner(int System, LiveShipHeat Heat);

    private sealed record HeatMembership(long Id, long Pilot, int Station, int? System, bool InSpace);

    private readonly ConcurrentDictionary<long, HeatOwner> _heatOwners = new();

    internal IDisposable? UseHeatSystem(long ship) => _heatOwners.TryGetValue(ship, out var owner) ? UseSystem(owner.System) : null;

    internal void OwnHeat(LiveShipHeat heat, long ship, int? system)
    {
        if (system is int id)
        {
            _heatOwners[ship] = new(id, heat);
        }
    }

    internal void ReconcileHeatMembership(IReadOnlyCollection<Combat.CombatRowChange> changes)
    {
        var ships = changes
            .Where(change => change.Table is "WorldPositionMembership" or "WorldShip")
            .Select(change => change.Id)
            .Distinct()
            .Where(_heatOwners.ContainsKey)
            .ToArray();
        if (ships.Length == 0)
        {
            return;
        }
        using WorldDbContext world = new(_path) { IsResidentProjection = true };
        var rows = world
            .Ships.Where(ship => ships.Contains(ship.Id))
            .Select(ship => new HeatMembership(
                ship.Id,
                ship.OwnerId,
                ship.StationId,
                ship.Position == null ? null : ship.Position.SystemId,
                ship.Position != null
            ))
            .ToDictionary(ship => ship.Id);
        foreach (long ship in ships)
        {
            var previous = _heatOwners[ship];
            if (!rows.TryGetValue(ship, out var membership))
            {
                using var ownership = _systems.Get(previous.System).Enter("destroyed heat owner");
                previous.Heat.Forget(ship);
                _heatOwners.TryRemove(ship, out _);
                continue;
            }
            int system = membership.System ?? _database.Catalog.FindStation(membership.Station)!.SolarSystemId;
            using var systems = _systems.Enter([previous.System, system], "heat membership");
            var target = previous.Heat;
            if (system != previous.System)
            {
                target = _systems.Get(system).State.Heat;
                previous.Heat.TransferShipTo(target, ship, system);
                _heatOwners[ship] = new(system, target);
            }
            if (!membership.InSpace)
            {
                target.ForgetRequests(ship);
            }
        }
    }
}
