using System.Globalization;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Fittings;

[Injectable]
internal sealed class ApplyFittingHandler(
    TimeProvider clock,
    IPilotApplyFitting fittings,
    IPilotAmmunition ammunition,
    FittingOperations fittingOperations
) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (method != "FitFitting" || !service.StartsWith(InventoryHandler.ShipInventoryPrefix, StringComparison.Ordinal))
        {
            return PilotCallResult.Unsupported;
        }

        var pilot = context.Pilot;
        try
        {
            if (
                service != InventoryHandler.ShipInventoryPrefix + pilot.ShipId.ToString(CultureInfo.InvariantCulture)
                || keywords is { Count: > 0 }
            )
            {
                throw new InvalidDataException("Apply the fitting to your docked active ship.");
            }

            var request = ApplyFittingWire.Read(args);
            var previousBanks = fittingOperations.ReadBanks(context);
            var previousMagazines = ammunition.Read(pilot.PilotId, pilot.ShipId);
            var result = fittings.Apply(pilot.PilotId, request);
            if (result.Changes.Count > 0)
            {
                var currentMagazines = ammunition.Read(pilot.PilotId, pilot.ShipId).Select(m => m.ModuleId).ToHashSet();
                long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
                foreach (var magazine in previousMagazines.Where(m => !currentMagazines.Contains(m.ModuleId)))
                {
                    MagazineWire.NotifyQuantity(context, magazine, 0, now);
                }
                foreach (var change in result.Changes)
                {
                    InventoryChangesWire.NotifyItemChange(context, change);
                }
                fittingOperations.NotifyOnlineChanges(context, result.Changes);
                fittingOperations.NotifyShipAttributes(context, previousBanks);
            }
            return new BlueList<MissingFittingItemReply>(result.Missing.Select(item => new MissingFittingItemReply(item)).ToArray());
        }
        catch (CargoCapacityException error)
        {
            return new PilotCallResult.NamedFailure("NotEnoughCargoSpace", new CargoSpaceErrorValues(error.Available, error.Required));
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("The fitting quantities exceed the supported limit.");
        }
    }
}
