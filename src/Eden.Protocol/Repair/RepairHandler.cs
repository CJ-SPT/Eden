using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Repair;

[Injectable]
internal sealed class RepairHandler(IPilotRepair repair, IPilotHealth health, TimeProvider clock) : ISelectedPilotCallHandler
{
    private static BlueInstance Quote(RepairQuote quote) => new("utillib.KeyVal", new Dictionary<string, object?>
    {
        ["itemID"] = quote.ItemId,
        ["typeID"] = quote.TypeId,
        ["groupID"] = quote.GroupId,
        ["damage"] = quote.Damage,
        ["maxHealth"] = quote.MaxHealth,
        ["costToRepairOneUnitOfDamage"] = (double)RepairDefinitions.IskPerDamagePoint,
    });

    public object? Call(PilotCallContext context, string service, string method, object?[] args, Dictionary<string, object?>? keywords = null)
    {
        bool standing = service == "corpStationMgr" && method == "DoStandingCheckForStationService" && args is [(long)StationService.Repair];
        if (service != "repairSvc" && !standing) { return PilotCallRouter.Unsupported; }
        try
        {
            if (standing)
            {
                repair.CheckStation(context.Pilot.PilotId, context.Pilot.StationId);
                return null;
            }
            if (args.FirstOrDefault() is not List<object?> items || !items.All(i => i is long)) { return PilotCallRouter.Unsupported; }
            var ids = items.Cast<long>().ToArray();
            if (method == "GetRepairQuotes" && args.Length == 1)
            {
                return repair.Quotes(context.Pilot.PilotId, context.Pilot.StationId, ids)
                    .ToDictionary(p => p.Key, p => (object?)p.Value.Select(q => (object?)Quote(q)).ToList());
            }
            if (method != "RepairItemsInStation" || args.Length != 2 || args[1] is not (double or long)) { return PilotCallRouter.Unsupported; }
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            long payment = RepairDefinitions.PaymentCents(args[1] is double amount ? amount : (long)args[1]!);
            var result = repair.Repair(context.Pilot.PilotId, context.Pilot.StationId, ids, payment, now);
            context.Notifications.Add(("OnAccountChange", ["cash", context.Pilot.PilotId, (double)result.BalanceCents / MarketDefinitions.CentsPerIsk]));
            if (result.Ships.Any(s => s.ShipId == context.Pilot.ShipId))
            {
                var current = health.Read(context.Pilot.PilotId, now);
                context.Notifications.Add(("OnModuleAttributeChanges", [new List<object?>
                {
                    Attribute(DogmaAttributeId.ArmorDamage, current.Defense.Armor - current.Remaining.Armor),
                    Attribute(DogmaAttributeId.Damage, current.Defense.Structure - current.Remaining.Structure),
                }]));
            }
            return null;

            object?[] Attribute(DogmaAttributeId attribute, double value) =>
                ["OnModuleAttributeChange", context.Pilot.PilotId, context.Pilot.ShipId, (int)attribute, now, value, null, now];
        }
        catch (InvalidDataException error) { return new PilotCallRouter.Error(error.Message); }
        catch (OverflowException) { return new PilotCallRouter.Error("This repair payment is too large."); }
    }
}
