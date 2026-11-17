using System.Globalization;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Structures;

[Injectable]
internal sealed class StructureInventoryHandler(
    IPilotStructures structures,
    IPilotInventory personalInventory,
    IPilotFleet fleet,
    InventoryTransfers transfers,
    IPilotStructureFitting fitting,
    FittingOperations fittingOperations,
    IPilotStructureAmmunition ammunition
) : ISelectedPilotCallHandler
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
            if (context.Pilot.InStructure && context.Pilot.DockedLocationId == id)
            {
                // Native invCache shares one structure container between the fitting view and Item/Ship Hangars.
                // Keep the pilot's personal rows visible alongside the authorized corporation service bays.
                items = items
                    .Concat(personalInventory.List(context.Pilot.PilotId, id))
                    .Concat(fleet.Ships(context.Pilot.PilotId, id))
                    .DistinctBy(item => item.Id)
                    .ToArray();
            }
            if (
                method == "MultiAdd"
                && args is [List<object?> hangarItems, long hangarSource]
                && Equals(keywords?.GetValueOrDefault("flag"), (long)InventoryFlag.Hangar)
            )
            {
                if (
                    keywords?.Keys.Any(k => k is not ("flag" or "qty")) == true
                    || keywords?.GetValueOrDefault("qty") is not (null or long)
                    || hangarItems.Any(item => item is not long)
                )
                {
                    throw new InvalidDataException("Choose valid hangar transfer items and quantity.");
                }
                int? amount = keywords?.GetValueOrDefault("qty") is long selectedAmount ? checked((int)selectedAmount) : null;
                return transfers.Move(
                    context,
                    "@hangar",
                    new(hangarItems.Cast<long>().ToArray(), hangarSource, (int)InventoryFlag.Hangar, amount)
                );
            }
            if (
                method == "MultiAdd"
                && args is [List<object?> ammunitionItems, long _]
                && Equals(keywords?.GetValueOrDefault("flag"), (long)InventoryFlag.Cargo)
            )
            {
                if (
                    ammunitionItems.Any(item => item is not long)
                    || keywords?.Keys.Any(key => key is not ("flag" or "qty")) == true
                    || keywords?.GetValueOrDefault("qty") is not (null or long and > 0 and <= int.MaxValue)
                )
                {
                    throw new InvalidDataException("Choose valid ammunition stacks and quantity.");
                }
                int? amount = keywords?.GetValueOrDefault("qty") is long ammunitionAmount ? checked((int)ammunitionAmount) : null;
                fittingOperations.NotifyStructureChanges(
                    context,
                    ammunition.Deposit(
                        context.Pilot.PilotId,
                        id,
                        ammunitionItems.Select(item => new StructureAmmoDeposit((long)item!, amount)).ToArray()
                    )
                );
                return PilotCallResult.None;
            }
            if (
                method == "MultiAdd"
                && args is [List<object?> batch, long _]
                && Equals(keywords?.GetValueOrDefault("flag"), (long)InventoryFlag.AutoFit)
            )
            {
                if (
                    keywords?.Keys.Any(k => k is not ("flag" or "qty")) == true
                    || keywords?.GetValueOrDefault("qty") is not (null or 1L)
                    || batch.Any(item => item is not long)
                )
                {
                    throw new InvalidDataException("Choose whole structure equipment items to fit.");
                }
                fittingOperations.NotifyStructureChanges(
                    context,
                    fitting.Fit(context.Pilot.PilotId, id, batch.Select(item => new StructureServiceFit((long)item!)).ToArray())
                );
                return PilotCallResult.None;
            }
            // Native drag/drop uses MultiAdd even for a single selected item. Each installation is one atomic command.
            if (method == "MultiAdd" && args is [List<object?> { Count: 1 } selected, long source] && selected[0] is long itemId)
            {
                method = "Add";
                args = [itemId, source];
            }
            if (method == "MultiAdd")
            {
                throw new InvalidDataException("Install one structure item or fuel stack at a time.");
            }
            if (method == "List" && args is [] or [long _])
            {
                return new BlueList<InventoryRow>(
                    items.Where(i => args.Length == 0 || i.FlagId == (long)args[0]!).Select(InventoryRow.From).ToArray()
                );
            }
            if (method == "DestroyFitting" && args is [long rig] && keywords is not { Count: > 0 })
            {
                if (!items.Any(item => item.Id == rig && InventoryDefinitions.IsRigSlot(item.FlagId)))
                {
                    throw new InvalidDataException("Choose a fitted structure rig to destroy.");
                }
                fittingOperations.NotifyStructureChanges(context, fitting.Remove(context.Pilot.PilotId, id, rig));
                return PilotCallResult.None;
            }
            if (method == "GetSelfInvItem" && args.Length == 0)
            {
                var s = structures.Read(context.Pilot.PilotId, id)!;
                return StructureDogmaBuilder.Item(s);
            }
            if (method == "Add" && args is [long item, long transferSource])
            {
                if (keywords?.TryGetValue("qty", out var selectedQuantity) == true && selectedQuantity is not (null or long))
                {
                    throw new InvalidDataException("Choose an integer structure transfer quantity.");
                }
                int flag = keywords?.GetValueOrDefault("flag") is long f
                    ? checked((int)f)
                    : throw new InvalidDataException("Choose a structure inventory flag.");
                int? quantity = keywords?.GetValueOrDefault("qty") is long q ? checked((int)q) : null;
                if (keywords?.Keys.Any(k => k is not ("flag" or "qty")) == true)
                {
                    throw new InvalidDataException("Unexpected structure transfer arguments.");
                }
                if (flag == (int)InventoryFlag.Hangar)
                {
                    return transfers.Move(context, "@hangar", new([item], transferSource, flag, quantity));
                }
                var result = flag switch
                {
                    (int)InventoryFlag.Cargo => ammunition.Deposit(context.Pilot.PilotId, id, item, quantity),
                    (int)InventoryFlag.AutoFit when quantity is null or 1 => fitting.Fit(context.Pilot.PilotId, id, [new(item)]),
                    StructureRules.CoreRoomFlag when quantity is null or 1 => structures.InstallCore(context.Pilot.PilotId, id, item),
                    StructureRules.FuelBayFlag => structures.DepositFuel(context.Pilot.PilotId, id, item, quantity),
                    >= StructureRules.FirstServiceSlot
                    and < StructureRules.FirstServiceSlot + StructureRules.MaximumServiceSlots when quantity is null or 1 =>
                        structures.FitService(context.Pilot.PilotId, id, item, flag),
                    _ when InventoryDefinitions.IsFittedSlot(flag) && quantity is null or 1 => fitting.Fit(
                        context.Pilot.PilotId,
                        id,
                        [new(item, flag)]
                    ),
                    _ => throw new InvalidDataException("Choose a core, service slot or structure fuel bay."),
                };
                fittingOperations.NotifyStructureChanges(context, result);
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
