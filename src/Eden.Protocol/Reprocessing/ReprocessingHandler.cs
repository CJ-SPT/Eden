using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Reprocessing;

[Injectable]
internal sealed class ReprocessingHandler(IPilotReprocessing reprocessing) : ISelectedPilotCallHandler
{
    private static BlueInstance KeyVal(Dictionary<string, object?> values) => new("utillib.KeyVal", values);

    private static long[]? ItemIds(object? value) => value switch
    {
        Dictionary<long, object?> rows when rows.Values.All(v => v is BluePackedRow) => rows.Keys.ToArray(),
        Dictionary<string, object?> { Count: 0 } => [],
        List<object?> ids when ids.All(id => id is long) => ids.Cast<long>().ToArray(),
        _ => null,
    };

    public object? Call(PilotCallContext context, string service, string method, object?[] args, Dictionary<string, object?>? keywords = null)
    {
        bool check = service == "corpStationMgr" && method == "DoStandingCheckForStationService" && args is [(long)StationService.Reprocessing];
        if (!check && (service != "reprocessingSvc" || method is not ("GetQuotes" or "Reprocess")))
        {
            return PilotCallRouter.Unsupported;
        }
        try
        {
            if (context.Pilot.Position is not null) { throw new InvalidDataException("Dock at a station to reprocess ore."); }
            if (check)
            {
                reprocessing.CheckStation(context.Pilot.PilotId, context.Pilot.StationId);
                return null;
            }
            if (method == "GetQuotes" && args is [var rows, long ship] && ItemIds(rows) is { } ids)
            {
                var quote = reprocessing.Quote(context.Pilot.PilotId, context.Pilot.StationId, ship, ids);
                return new object?[]
                {
                    quote.StationTax,
                    new BlueDictionary([(null, quote.StationEfficiency)]),
                    quote.Items.ToDictionary(q => q.ItemId, q => (object?)KeyVal(new()
                    {
                        ["itemID"] = q.ItemId, ["typeID"] = q.TypeId, ["quantityToProcess"] = q.QuantityToProcess,
                        ["leftOvers"] = q.LeftOvers, ["portions"] = q.Portions, ["efficiency"] = q.Efficiency,
                        // Build 3662168 uses adjusted market prices for ISK fees, not
                        // mineral withholding. Eden has no market price feed yet;
                        // the pinned client's missing-price fallback is zero.
                        ["totalISKCost"] = 0d,
                        ["recoverables"] = q.Recoverables.Select(r => (object?)KeyVal(new()
                        {
                            ["typeID"] = r.TypeId, ["client"] = r.Quantity, ["unrecoverable"] = r.Unrecoverable, ["iskCost"] = 0d,
                        })).ToList(),
                    })),
                };
            }
            if (method == "Reprocess" && args is [List<object?> items, long source, null or long, long output, long flag] &&
                items.All(id => id is long) && flag is >= 0 and <= int.MaxValue)
            {
                var result = reprocessing.Reprocess(context.Pilot.PilotId, context.Pilot.StationId, items.Cast<long>().ToArray(),
                    source, args[2] as long?, output, (int)flag);
                foreach (var change in result.Changes) { InventoryHandler.NotifyItemChange(context, change); }
                // ItemReprocessor extends its result list with this pair, then the
                // controller unpacks input IDs and actual output quantities.
                return new object?[] { result.ProcessedIds.Cast<object?>().ToList(), result.Output.ToDictionary(p => (long)p.Key, p => (object?)p.Value) };
            }
            return PilotCallRouter.Unsupported;
        }
        catch (CargoCapacityException error)
        {
            return new PilotCallRouter.UserError("NotEnoughCargoSpace", new() { ["available"] = error.Available, ["volume"] = error.Required });
        }
        catch (InvalidDataException error)
        {
            return new PilotCallRouter.Error(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallRouter.UserError("ReprocessingPleaseSplit", new());
        }
    }
}
