using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Fittings;

[Injectable]
internal sealed class MultifitHandler(IPilotMultifit fittings) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if ((service, method) != ("ship", "FitShips"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            var request = MultifitWire.Read(args, keywords);
            var result = fittings.FitShips(context.Pilot.PilotId, request);
            foreach (var change in result.Changes)
            {
                InventoryChangesWire.NotifyItemChange(context, change);
            }
            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                foreach (long ship in result.ShipIds)
                {
                    context.Notifications.Add(new ShipLabelChanged(ship, request.Name.Trim()));
                }
            }
            return result.Missing.Count == 0 ? PilotCallResult.None : new MultifitShortageReply(result.Missing);
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
            return new PilotCallResult.Failure("The multifit quantities exceed the supported limit.");
        }
    }
}

internal static class MultifitWire
{
    private const int NativeKeywordCount = 8;

    internal static MultifitRequest Read(object?[] args, Dictionary<string, object?>? keywords)
    {
        // Build 3662168 fittingSvc.DoFitManyShips sends eight keywords through ship.MachoBindObject.
        if (
            args.Length != 0
            || keywords is not { Count: NativeKeywordCount }
            || !keywords.TryGetValue("shipTypeID", out var type)
            || !keywords.TryGetValue("fitInfo", out var plan)
            || !keywords.TryGetValue("itemLocationID", out var location)
            || !keywords.TryGetValue("cargoItemsByType", out var cargo)
            || !keywords.TryGetValue("fitRigs", out var rigs)
            || !keywords.TryGetValue("name", out var name)
            || !keywords.TryGetValue("numToFit", out var count)
            || !keywords.TryGetValue("itemContainerID", out var container)
            || count is not long number
            || number is < 1 or > MultifitRules.MaximumShips
            || name is not (null or string or byte[])
            || container is not (null or long)
        )
        {
            throw new InvalidDataException("Invalid multifit request.");
        }
        var fitting = ApplyFittingWire.Read([0L, type, new Dictionary<long, object?>(), location, plan, cargo, rigs]);
        var request = new MultifitRequest(fitting, (int)number, name is null ? null : BlueMarshal.Text(name), (long?)container);
        MultifitRules.Check(request);
        return request;
    }
}

internal sealed record MultifitShortageReply(IReadOnlyList<MissingFittingItem> Missing) : BlueModel
{
    // DoFitManyShips unpacks (failedToLoad, failedShipID); an atomic rejection leaves no failed ship to rename.
    public override object ToBlue() =>
        new object?[] { new BlueList<MissingFittingItemReply>(Missing.Select(item => new MissingFittingItemReply(item)).ToArray()), null };
}
