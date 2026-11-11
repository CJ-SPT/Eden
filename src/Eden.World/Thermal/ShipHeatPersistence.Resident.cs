using System.Security.Cryptography;
using System.Text.Json;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Repair;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Thermal;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Thermal;

internal static partial class ShipHeatPersistence
{
    internal static void CancelForTravel(WorldAction world, long ship)
    {
        if (world.Set<SavedShipHeat>().Find(ship) is { } saved)
        {
            var state = Decode(saved.StateJson);
            if (state.Cycles.Count > 0)
            {
                saved.StateJson = Encode(state with { Cycles = Array.Empty<PaidHeatCycle>() });
            }
        }
    }
}
