using System.Globalization;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
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
        if (
            method is not ("FitFitting" or "StripFitting")
            || !service.StartsWith(InventoryHandler.ShipInventoryPrefix, StringComparison.Ordinal)
        )
        {
            return PilotCallResult.Unsupported;
        }

        var pilot = context.Pilot;
        long shipId = InventoryHandler.ShipContainer(service, pilot);
        bool strip = method == "StripFitting";
        try
        {
            if (shipId <= 0 || keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Invalid ship fitting request.");
            }
            if (strip && args.Length != 0)
            {
                throw new InvalidDataException("StripFitting takes no arguments.");
            }
            if (!strip && service != InventoryHandler.ShipInventoryPrefix + pilot.ShipId.ToString(CultureInfo.InvariantCulture))
            {
                throw new InvalidDataException("Apply the fitting to your docked active ship.");
            }

            var request = strip ? null : ApplyFittingWire.Read(args);
            bool active = shipId == pilot.ShipId;
            var previousBanks = active ? fittingOperations.ReadBanks(context) : null;
            var previousMagazines = active ? ammunition.Read(pilot.PilotId, shipId) : [];
            if (request is null)
            {
                var changes = fittings.Strip(pilot.PilotId, shipId);
                if (changes.Count == 0)
                {
                    return PilotCallResult.None;
                }
                Notify(context, changes, previousBanks, previousMagazines);
                return true;
            }

            var result = fittings.Apply(pilot.PilotId, request);
            Notify(context, result.Changes, previousBanks, previousMagazines);
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

    private void Notify(
        PilotCallContext context,
        IReadOnlyList<InventoryChange> changes,
        IReadOnlyList<WeaponBank>? previousBanks,
        IReadOnlyList<Magazine> previousMagazines
    )
    {
        if (changes.Count == 0)
        {
            return;
        }
        if (previousMagazines.Count > 0)
        {
            var pilot = context.Pilot;
            var currentMagazines = ammunition.Read(pilot.PilotId, pilot.ShipId).Select(m => m.ModuleId).ToHashSet();
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            foreach (var magazine in previousMagazines.Where(m => !currentMagazines.Contains(m.ModuleId)))
            {
                MagazineWire.NotifyQuantity(context, magazine, 0, now);
            }
        }
        foreach (var change in changes)
        {
            InventoryChangesWire.NotifyItemChange(context, change);
        }
        fittingOperations.NotifyOnlineChanges(context, changes);
        if (previousBanks is not null)
        {
            fittingOperations.NotifyShipAttributes(context, previousBanks);
        }
    }
}
