using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Flight;

[Injectable]
internal sealed class PilotWarpDriveStore(WorldDatabase database) : IPilotWarpDrive
{
    public WarpCapacitorPayment Activate(long pilotId, long shipId, double distance, long now)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Warp requires the resident world.");
        using var transaction = live.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        Eden.World.Sovereignty.EntosisPersistence.RequireTravel(transaction, shipId, now);
        PilotCapitalTravelStore.RequireMobile(transaction, pilotId, shipId, now);
        Eden.World.Combat.LanceDisruption.RequireTravel(transaction, shipId, now);
        var pilot = transaction.Pilots.SingleOrDefault(p => p.Id == pilotId && p.ActiveShipId == shipId);
        if (pilot?.ActiveShip.Position is null)
        {
            throw new InvalidDataException("An active ship in space is required to warp.");
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var attributes = PilotFittingStore.ShipAttributes(transaction, catalog, pilot);
        if (!attributes.TryGetValue(DogmaAttributeId.WarpCapacitorNeed, out double need))
        {
            throw new InvalidDataException("This ship has no warp capacitor profile.");
        }
        var payment = WarpCapacitorRules.Pay(
            distance,
            attributes.GetValueOrDefault(DogmaAttributeId.Mass),
            need,
            ShipResources.Capacitor(transaction, shipId, attributes, now)
        );
        ShipResources.SpendCapacitor(transaction, shipId, attributes, payment.Cost, now);
        transaction.Commit();
        return payment;
    }
}
