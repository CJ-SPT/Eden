using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Reprocessing;

[Injectable]
internal sealed class ReprocessingHandler(IPilotReprocessing reprocessing) : ISelectedPilotCallHandler
{
    private static long[]? ItemIds(object? value) =>
        value switch
        {
            Dictionary<long, object?> rows when rows.Values.All(v => v is BluePackedRow) => rows.Keys.ToArray(),
            Dictionary<string, object?> { Count: 0 } => [],
            List<object?> ids when ids.All(id => id is long) => ids.Cast<long>().ToArray(),
            _ => null,
        };

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool check =
            service == "corpStationMgr" && method == "DoStandingCheckForStationService" && args is [(long)StationService.Reprocessing];
        if (!check && (service != "reprocessingSvc" || method is not ("GetQuotes" or "Reprocess")))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (context.Pilot.Position is not null)
            {
                throw new InvalidDataException("Dock at a station to reprocess ore.");
            }
            if (check)
            {
                reprocessing.CheckStation(context.Pilot.PilotId, context.Pilot.StationId);
                return PilotCallResult.None;
            }
            if (method == "GetQuotes" && args is [var rows, long ship] && ItemIds(rows) is { } ids)
            {
                var quote = reprocessing.Quote(context.Pilot.PilotId, context.Pilot.StationId, ship, ids);
                return new ReprocessingQuoteReply(quote);
            }
            if (
                method == "Reprocess"
                && args is [List<object?> items, long source, null or long, long output, long flag]
                && items.All(id => id is long)
                && flag is >= 0 and <= int.MaxValue
            )
            {
                var result = reprocessing.Reprocess(
                    context.Pilot.PilotId,
                    context.Pilot.StationId,
                    items.Cast<long>().ToArray(),
                    source,
                    args[2] as long?,
                    output,
                    (int)flag
                );
                foreach (var change in result.Changes)
                {
                    InventoryChangesWire.NotifyItemChange(context, change);
                }
                // ItemReprocessor extends its result list with this pair, then the
                // controller unpacks input IDs and actual output quantities.
                return new ReprocessingCompleted(result);
            }
            return PilotCallResult.Unsupported;
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
            return new PilotCallResult.NamedFailure("ReprocessingPleaseSplit", BlueEmpty.Dictionary);
        }
    }
}
