using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Ammunition;

[Injectable]
internal sealed class AmmunitionHandler(TimeProvider clock, IPilotAmmunition ammunition, IPilotFitting fitting, IPilotCombat combat, IPilotInventory inventory) : ISelectedPilotCallHandler
{
    internal static object?[] Key(Magazine magazine) => [magazine.ShipId, magazine.FlagId, magazine.TypeId];

    internal BlueInstance Prime(Magazine magazine)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var attributes = fitting.Attributes(magazine.TypeId).ToDictionary(a => (long)a.Key, a => (object?)a.Value);
        attributes[(long)DogmaAttributeId.Quantity] = (double)magazine.Quantity;
        return new("utillib.KeyVal", new Dictionary<string, object?>
        {
            ["itemID"] = Key(magazine),
            ["invItem"] = null,
            ["attributes"] = attributes,
            ["activeEffects"] = new Dictionary<long, object?>(),
            ["time"] = now,
            ["wallclockTime"] = now,
        });
    }

    internal static void NotifyQuantity(PilotCallContext context, Magazine magazine, int quantity, long now) =>
        context.Notifications.Add(("OnModuleAttributeChanges", [new List<object?>
        {
            new object?[] { "OnModuleAttributeChange", context.Pilot.PilotId, Key(magazine), (int)DogmaAttributeId.Quantity, now, (double)quantity, null, now },
        }]));

    internal void NotifyChanges(PilotCallContext context, IReadOnlyList<MagazineChange> changes, double reload = 0)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        foreach (var change in changes)
        {
            if (change.Previous is { } old && (change.Current is null || old.TypeId != change.Current.TypeId))
            {
                NotifyQuantity(context, old, 0, now);
            }
            if (change.Current is { } current)
            {
                if (change.Previous?.TypeId != current.TypeId)
                {
                    context.Notifications.Add(("OnGodmaPrimeItem", [current.ShipId, Prime(current)]));
                }
                // Godma primes its row, but clientDogmaLocation fits a tuple
                // sublocation only on attributeQuantity (build 3662168).
                NotifyQuantity(context, current, current.Quantity, now);
                if (reload > 0)
                {
                    context.Notifications.Add(("OnChargeBeingLoadedToModule", [new List<object?> { current.ModuleId }, current.TypeId, reload]));
                }
            }
            var magazine = change.Current ?? change.Previous!;
            var module = inventory.List(context.Pilot.PilotId, context.Pilot.ShipId).SingleOrDefault(i => i.Id == magazine.ModuleId && InventoryDefinitions.IsHighSlot(i.FlagId));
            if (module is null)
            {
                continue;
            }
            var attributes = combat.ModuleAttributes(context.Pilot.PilotId, module);
            var values = new[] { DogmaAttributeId.MaxRange, DogmaAttributeId.CapacitorNeed, DogmaAttributeId.NextActivationTime }
                .Select(attribute => (object?)new object?[] { "OnModuleAttributeChange", context.Pilot.PilotId, magazine.ModuleId,
                    (int)attribute, now, attributes.GetValueOrDefault(attribute), null, now }).ToList();
            context.Notifications.Add(("OnModuleAttributeChanges", [values]));
        }
    }

    private static long[]? Ids(object? value) => value switch
    {
        long id => [id],
        List<object?> ids when ids.All(i => i is long) => ids.Cast<long>().ToArray(),
        _ => null,
    };

    private static (long Location, long Owner, int Flag)? Destination(object? value, DockedPilot pilot) => value switch
    {
        object?[] { Length: 3 } parts when parts is [long location, long owner, long flag] && flag is >= 0 and <= int.MaxValue => (location, owner, (int)flag),
        long location => (location, pilot.PilotId, location == pilot.ShipId ? (int)InventoryFlag.Cargo : (int)InventoryFlag.Hangar),
        _ => null,
    };

    public object? Call(PilotCallContext context, string service, string method, object?[] args, Dictionary<string, object?>? keywords = null)
    {
        if (service != "dogmaIM" || method is not ("LoadAmmo" or "UnloadAmmo"))
        {
            return PilotCallRouter.Unsupported;
        }
        try
        {
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            AmmunitionTransfer transfer;
            if (method == "LoadAmmo" && args is [long ship, var modules, var stacks, long source] && Ids(modules) is { } moduleIds && Ids(stacks) is { } chargeIds)
            {
                transfer = ammunition.Load(context.Pilot.PilotId, ship, moduleIds, chargeIds, source, now);
            }
            else if (method == "UnloadAmmo" && args.Length is 3 or 4 && args[0] is long hull && Ids(args[1]) is { } weapons &&
                Destination(args[2], context.Pilot) is { } destination && destination.Owner == context.Pilot.PilotId &&
                (args.Length == 3 || args[3] is null || args[3] is long requested && requested is > 0 and <= int.MaxValue))
            {
                int? quantity = args.ElementAtOrDefault(3) is long count ? (int)count : null;
                transfer = ammunition.Unload(context.Pilot.PilotId, hull, weapons, destination.Location, destination.Flag, quantity, now);
            }
            else
            {
                return PilotCallRouter.Unsupported;
            }
            NotifyChanges(context, transfer.Magazines, transfer.ReloadMilliseconds);
            foreach (var change in transfer.Items)
            {
                InventoryHandler.NotifyItemChange(context, change);
            }
            return null;
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
            return new PilotCallRouter.Error("The ammunition quantity or reload time exceeds the local limit.");
        }
    }
}
