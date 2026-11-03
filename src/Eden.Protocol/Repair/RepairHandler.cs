using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Repair;

[Injectable]
internal sealed class RepairHandler(IPilotRepair repair, IPilotHealth health, TimeProvider clock) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool standing =
            service == "corpStationMgr" && method == "DoStandingCheckForStationService" && args is [(long)StationService.Repair];
        if (service != "repairSvc" && !standing)
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (standing)
            {
                repair.CheckStation(context.Pilot.PilotId, context.Pilot.StationId);
                return PilotCallResult.None;
            }
            if (args.FirstOrDefault() is not List<object?> items || !items.All(i => i is long))
            {
                return PilotCallResult.Unsupported;
            }
            var ids = items.Cast<long>().ToArray();
            if (method == "GetRepairQuotes" && args.Length == 1)
            {
                return new BlueMap<long, BlueList<RepairQuoteReply>>(
                    repair
                        .Quotes(context.Pilot.PilotId, context.Pilot.StationId, ids)
                        .ToDictionary(
                            p => p.Key,
                            p => new BlueList<RepairQuoteReply>(p.Value.Select(q => new RepairQuoteReply(q)).ToArray())
                        )
                );
            }
            if (method != "RepairItemsInStation" || args.Length != 2 || args[1] is not (double or long))
            {
                return PilotCallResult.Unsupported;
            }
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            long payment = RepairDefinitions.PaymentCents(args[1] is double amount ? amount : (long)args[1]!);
            var result = repair.Repair(context.Pilot.PilotId, context.Pilot.StationId, ids, payment, now);
            context.Notifications.Add(
                new AccountBalanceChanged(context.Pilot.PilotId, (double)result.BalanceCents / MarketDefinitions.CentsPerIsk)
            );
            if (result.Ships.Any(s => s.ShipId == context.Pilot.ShipId))
            {
                var current = health.Read(context.Pilot.PilotId, now);
                context.Notifications.Add(
                    new ModuleAttributesChanged([
                        new(
                            context.Pilot.PilotId,
                            new DogmaItemKey.Item(context.Pilot.ShipId),
                            DogmaAttributeId.ArmorDamage,
                            now,
                            current.Defense.Armor - current.Remaining.Armor
                        ),
                        new(
                            context.Pilot.PilotId,
                            new DogmaItemKey.Item(context.Pilot.ShipId),
                            DogmaAttributeId.Damage,
                            now,
                            current.Defense.Structure - current.Remaining.Structure
                        ),
                    ])
                );
            }
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("This repair payment is too large.");
        }
    }
}
