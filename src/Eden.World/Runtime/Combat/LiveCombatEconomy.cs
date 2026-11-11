using Eden.Data.World.Inventory;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    internal bool OverlayEconomicItem(OwnedItem item)
    {
        if (!_itemPilots.TryGetValue(item.Id, out long pilot) || !_ships.TryGetValue(pilot, out var ship))
        {
            return true;
        }
        var turret = ship.Turrets.GetValueOrDefault(item.LocationId);
        if (turret?.ChargeItemId == item.Id)
        {
            item.Quantity = turret.Quantity;
            item.CrystalDamage = turret.CrystalDamage;
            return item.Quantity > 0;
        }
        return true;
    }

    internal void ObserveEconomicItems(IReadOnlyCollection<CombatRowChange> changes, Func<long, OwnedItem?> read)
    {
        foreach (var change in changes.Where(c => c.Table == "WorldItem"))
        {
            if (!_itemPilots.TryGetValue(change.Id, out long pilot) || !_ships.TryGetValue(pilot, out var ship))
            {
                continue;
            }
            var pair = ship.Turrets.FirstOrDefault(p => p.Value.ChargeItemId == change.Id);
            if (pair.Value is null)
            {
                continue;
            }
            var item = read(change.Id);
            bool loaded = item is not null && item.OwnerId == pilot && item.LocationId == pair.Key;
            var next = pair.Value with
            {
                ChargeItemId = loaded ? item!.Id : null,
                Quantity = loaded ? item!.Quantity : 0,
                CrystalDamage = loaded ? item!.CrystalDamage : 0,
            };
            _ships[pilot] = ship with { Turrets = ship.Turrets.ToDictionary(p => p.Key, p => p.Key == pair.Key ? next : p.Value) };
            if (_savedShips.TryGetValue(pilot, out var saved))
            {
                // The economic projection now owns these scalar changes. Preserve
                // every unrelated dirty health, energy, timer and repair difference.
                _savedShips[pilot] = saved with
                {
                    Turrets = saved.Turrets.ToDictionary(
                        p => p.Key,
                        p =>
                            p.Key == pair.Key
                                ? p.Value with
                                {
                                    ChargeItemId = next.ChargeItemId,
                                    Quantity = next.Quantity,
                                    CrystalDamage = next.CrystalDamage,
                                }
                                : p.Value
                    ),
                };
            }
        }
    }
}
