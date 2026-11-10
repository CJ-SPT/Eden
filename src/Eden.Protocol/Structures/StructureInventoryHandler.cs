using System.Globalization;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Structures;

[Injectable]
internal sealed class StructureInventoryHandler(IPilotStructures structures) : ISelectedPilotCallHandler
{
    private const string Prefix = "@structure-inventory:";

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        try
        {
            if (
                service == "invbroker"
                && method == "GetInventoryFromId"
                && args is [long structureId, ..]
                && structureId >= StructureRules.FirstStructureId
            )
            {
                if (structures.Read(context.Pilot.PilotId, structureId) is null)
                {
                    return PilotCallResult.Unsupported;
                }
                _ = structures.Inventory(context.Pilot.PilotId, structureId);
                return context.Reference(Prefix + structureId.ToString(CultureInfo.InvariantCulture), PilotBindingLifetime.Location);
            }
            if (!service.StartsWith(Prefix, StringComparison.Ordinal) || !long.TryParse(service.AsSpan(Prefix.Length), out long id))
            {
                return PilotCallResult.Unsupported;
            }
            var items = structures.Inventory(context.Pilot.PilotId, id);
            if (method == "List" && args is [] or [long _])
            {
                return new BlueList<InventoryRow>(
                    items.Where(i => args.Length == 0 || i.FlagId == (long)args[0]!).Select(InventoryRow.From).ToArray()
                );
            }
            if (method == "GetSelfInvItem" && args.Length == 0)
            {
                var s = structures.Read(context.Pilot.PilotId, id)!;
                return InventoryRow.From(
                    new(
                        id,
                        s.Profile.TypeId,
                        s.CorporationId,
                        s.SystemId,
                        (int)InventoryFlag.AutoFit,
                        1,
                        s.Profile.GroupId,
                        StructureRules.CategoryId,
                        0,
                        true
                    )
                );
            }
            if (method == "Add" && args is [long item, long _])
            {
                if (keywords?.TryGetValue("qty", out var selectedQuantity) == true && selectedQuantity is not long)
                {
                    throw new InvalidDataException("Choose an integer structure transfer quantity.");
                }
                int flag = keywords?.GetValueOrDefault("flag") is long f
                    ? checked((int)f)
                    : throw new InvalidDataException("Choose a structure inventory flag.");
                int quantity = keywords?.GetValueOrDefault("qty") is long q ? checked((int)q) : 1;
                if (keywords?.Keys.Any(k => k is not ("flag" or "qty")) == true)
                {
                    throw new InvalidDataException("Unexpected structure transfer arguments.");
                }
                var result = flag switch
                {
                    StructureRules.CoreRoomFlag when quantity == 1 => structures.InstallCore(context.Pilot.PilotId, id, item),
                    StructureRules.FuelBayFlag => structures.DepositFuel(context.Pilot.PilotId, id, item, quantity),
                    >= StructureRules.FirstServiceSlot
                    and < StructureRules.FirstServiceSlot + StructureRules.MaximumServiceSlots when quantity == 1 => structures.FitService(
                        context.Pilot.PilotId,
                        id,
                        item,
                        flag
                    ),
                    _ => throw new InvalidDataException("Choose a core, service slot or structure fuel bay."),
                };
                StructureHandler.Notify(context, result);
                return PilotCallResult.None;
            }
            return PilotCallResult.Unsupported;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
