using Eden.Protocol.Combat;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class InventoryHandler(TimeProvider clock, IPilotInventory inventory, IPilotFitting fitting, IPilotMining mining, MiningHandler miningEffects, IPilotFleet fleet, IPilotHealth health) : IPilotCallHandler, ISelectedPilotCallHandler
{
    private const string ShipInventoryPrefix = "@ship:";
    private static BlueInstance KeyVal(Dictionary<string, object?> values) => new("utillib.KeyVal", values);
    private static readonly (string Name, int Type)[] s_columns =
    [
        ("itemID", 20), ("typeID", 3), ("ownerID", 3), ("locationID", 20),
        ("flagID", 2), ("quantity", 3), ("groupID", 3), ("categoryID", 3),
        ("customInfo", 129), ("stacksize", 3), ("singleton", 2),
    ];

    internal static BlueConstructor ItemDescriptor() => new("blue.DBRowDescriptor",
        [s_columns.Select(column => (object?)new object?[] { column.Name, column.Type }).ToArray()]);

    internal static BlueConstructor Item(InventoryItem item) => new("blue.DBRow", [ItemDescriptor(), new object?[]
    {
        item.Id, item.TypeId, item.OwnerId, item.LocationId, item.FlagId, item.Singleton ? InventoryDefinitions.AssembledQuantity : item.Quantity,
        item.GroupId, item.CategoryId, "", item.Quantity, item.Singleton ? 1 : 0,
    }]);

    private List<object?> List(DockedPilot pilot, string service, object?[] args)
    {
        List<object?> result = [];
        if (service == "@hangar" && pilot.Position is not null)
        {
            return result;
        }
        long location = service == "@hangar" ? pilot.StationId : ShipContainer(service, pilot);
        var flag = args.FirstOrDefault() as long?;
        if (service == "@hangar" && (flag is null or (long)InventoryFlag.Hangar))
        {
            result.AddRange(fleet.Ships(pilot.PilotId, pilot.StationId).Select(Item));
        }
        result.AddRange(inventory.List(pilot.PilotId, location).Where(i => flag is null || i.FlagId == flag).Select(Item));
        return result;
    }

    private static long ShipContainer(string service, DockedPilot pilot) => service == "@ship" ? pilot.ShipId :
        service.StartsWith(ShipInventoryPrefix, StringComparison.Ordinal) && long.TryParse(service.AsSpan(ShipInventoryPrefix.Length), out long id) ? id : 0;

    private object? Move(PilotCallContext context, string service, string method, object?[] args, Dictionary<string, object?>? keywords)
    {
        if (args.Length != 2 || args[1] is not long source)
        {
            return PilotCallRouter.Unsupported;
        }
        long[] ids;
        if (method == "Add" && args[0] is long id)
        {
            ids = [id];
        }
        else if (method == "MultiAdd" && args[0] is List<object?> list && list.All(i => i is long))
        {
            ids = list.Cast<long>().ToArray();
        }
        else
        {
            return PilotCallRouter.Unsupported;
        }
        long destination = service == "@hangar" ? context.Pilot.StationId : context.Pilot.ShipId;
        object? flagValue = keywords?.GetValueOrDefault("flag");
        object? quantityValue = keywords?.GetValueOrDefault("qty");
        if ((flagValue is not null && flagValue is not long) || (quantityValue is not null && quantityValue is not long) ||
            (flagValue is long flagNumber && (flagNumber < 0 || flagNumber > int.MaxValue)) ||
            (quantityValue is long quantityNumber && (quantityNumber <= 0 || quantityNumber > int.MaxValue)))
        {
            return new PilotCallRouter.Error("Invalid cargo flag or quantity.");
        }
        int flag = flagValue is long f ? (int)f : service == "@hangar" ? (int)InventoryFlag.Hangar : (int)InventoryFlag.Cargo;
        int? quantity = quantityValue is long q ? (int)q : null;
        try
        {
            var changes = destination == context.Pilot.ShipId && (flag == (int)InventoryFlag.AutoFit || InventoryDefinitions.IsHighSlot(flag))
                ? fitting.Fit(context.Pilot.PilotId, ids, source, flag)
                : inventory.Move(context.Pilot.PilotId, ids, source, destination, flag, quantity);
            foreach (var change in changes)
            {
                NotifyItemChange(context, change);
            }
            if (changes.Any(c => InventoryDefinitions.IsHighSlot(c.Item.FlagId) || (c.Previous is not null && InventoryDefinitions.IsHighSlot(c.Previous.FlagId))))
            {
                // Let OnItemChange fetch the module through ItemGetInfo. A live
                // OnGodmaPrimeItem replaces its inventory row with an incompatible
                // godma row in build 3662168, removing it from clientDogmaLocation.
                NotifyShipAttributes(context);
            }
            return null;
        }
        catch (CargoCapacityException error)
        {
            return new PilotCallRouter.UserError("NotEnoughCargoSpace", new()
            {
                ["available"] = error.Available,
                ["volume"] = error.Required,
            });
        }
        catch (InvalidDataException error)
        {
            return new PilotCallRouter.Error(error.Message);
        }
    }

    internal static void NotifyItemChange(PilotCallContext context, InventoryChange change)
    {
        // OnItemChange keys index the inventory DBRow; values describe the old row.
        // Build 3662168 removes exhausted stacks through a junk-location change.
        // Keep the detached stack's last quantity: the UI reconstructs the old
        // row's quantity but reads its separate stacksize when deciding to remove
        // a tile. A zero stacksize makes both the old and new rows appear absent.
        var item = change.Item.Quantity == 0
            ? change.Item with { LocationId = (int)InventoryLocation.Graveyard, FlagId = (int)InventoryFlag.AutoFit, Online = false, Quantity = change.Previous?.Quantity ?? 0 }
            : change.Item;
        Dictionary<long, object?> old = [];
        if (change.Previous is not { } previous)
        {
            // A new stack must enter the cached container. A quantity-only change
            // updates capacity but does not insert an unknown item in invCache.
            old[(long)InventoryColumn.Location] = 0L;
            old[(long)InventoryColumn.Flag] = 0;
            old[(long)InventoryColumn.Quantity] = 0;
            old[(long)InventoryColumn.StackSize] = 0;
        }
        else
        {
            if (previous.LocationId != item.LocationId) { old[(long)InventoryColumn.Location] = previous.LocationId; }
            if (previous.FlagId != item.FlagId) { old[(long)InventoryColumn.Flag] = previous.FlagId; }
            if (previous.Singleton != item.Singleton) { old[(long)InventoryColumn.Singleton] = previous.Singleton ? 1 : 0; }
            if (previous.Quantity != item.Quantity)
            {
                old[(long)InventoryColumn.Quantity] = previous.Quantity;
                old[(long)InventoryColumn.StackSize] = previous.Quantity;
            }
        }
        context.Notifications.Add(("OnItemChange", [Item(item), old, null]));
    }

    private BlueConstructor Item(DockedPilot pilot, string kind)
    {
        object?[] line = kind switch
        {
            "ship" => [pilot.ShipId, pilot.ShipTypeId, pilot.PilotId, pilot.LocationId, pilot.Position is null ? 4 : 0, -1, pilot.ShipGroupId, pilot.ShipCategoryId, "", 1, 1],
            "pilot" => [pilot.PilotId, 1373, pilot.PilotId, pilot.ShipId, 57, -1, 1, 1, "", 1, 1],
            _ => [pilot.StationId, pilot.StationTypeId, pilot.StationOwnerId, pilot.SystemId, 0, -1, pilot.Station.GroupId, pilot.Station.CategoryId, "", 1, 1],
        };
        return new("blue.DBRow", [ItemDescriptor(), line]);
    }

    private Dictionary<long, object?> PrimeItem(DockedPilot pilot, long id, string kind)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var attributes = kind == "pilot" ? PilotProfile.TrainingAttributes() : fitting.ShipAttributes(pilot.PilotId).ToDictionary(a => (long)a.Key, a => (object?)a.Value);
        if (kind != "pilot")
        {
            var current = health.Read(pilot.PilotId, now);
            attributes[(long)DogmaAttributeId.Damage] = current.Defense.Structure - current.Remaining.Structure;
            attributes[(long)DogmaAttributeId.ArmorDamage] = current.Defense.Armor - current.Remaining.Armor;
            attributes[(long)DogmaAttributeId.CapacitorCharge] = mining.Capacitor(pilot.PilotId, now);
            attributes[(long)DogmaAttributeId.MaxVelocity] = (double)pilot.Flight.MaxSpeed;
            attributes[(long)DogmaAttributeId.CargoCapacity] = pilot.CargoCapacity;
            attributes[(long)DogmaAttributeId.ShieldCharge] = current.Remaining.Shield;
        }
        return new()
        {
            [id] = KeyVal(new()
            {
                ["itemID"] = id,
                ["invItem"] = Item(pilot, kind),
                ["attributes"] = attributes,
                ["activeEffects"] = new Dictionary<long, object?>(),
                ["time"] = now,
                ["wallclockTime"] = now
            })
        };
    }

    private BlueInstance PrimeModule(DockedPilot pilot, InventoryItem module)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var attributes = mining.ModuleAttributes(pilot.PilotId, module).ToDictionary(a => (long)a.Key, a => (object?)a.Value);
        attributes[(long)DogmaAttributeId.IsOnline] = module.Online ? 1d : 0d;
        attributes[(long)DogmaAttributeId.Damage] = 0d;
        Dictionary<long, object?> effects = [];
        if (module.Online)
        {
            effects[(long)DogmaEffect.Online] = new object?[] { module.Id, pilot.PilotId, pilot.ShipId, null, null, Array.Empty<object?>(), (int)DogmaEffect.Online, now, InventoryDefinitions.InfiniteEffectDuration, 0 };
        }
        if (miningEffects.ActiveEffect(pilot, module.Id) is { } active) { effects[(long)DogmaEffect.MiningLaser] = active; }
        return KeyVal(new()
        {
            ["itemID"] = module.Id,
            ["invItem"] = Item(module),
            ["attributes"] = attributes,
            ["activeEffects"] = effects,
            ["time"] = now,
            ["wallclockTime"] = now,
        });
    }

    internal BlueInstance AllInfo(DockedPilot pilot)
    {
        var modules = inventory.List(pilot.PilotId, pilot.ShipId).Where(i => InventoryDefinitions.IsHighSlot(i.FlagId)).ToArray();
        var shipInfo = PrimeItem(pilot, pilot.ShipId, "ship");
        var current = health.Read(pilot.PilotId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        Dictionary<long, object?> instances = new()
        {
            [pilot.PilotId] = new object?[] { pilot.PilotId, 0, 0, 0, 0, 0, 0, 0 },
            [pilot.ShipId] = new object?[] { pilot.ShipId, 0, current.Defense.Structure - current.Remaining.Structure,
                mining.Capacitor(pilot.PilotId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()), 0,
                current.Defense.Armor - current.Remaining.Armor, current.Remaining.Shield, 0 },
        };
        foreach (var module in modules)
        {
            shipInfo[module.Id] = PrimeModule(pilot, module);
            instances[module.Id] = new object?[] { module.Id, module.Online ? 1 : 0, 0, 0, 0, 0, 0, 0 };
        }
        return KeyVal(new()
        {
            ["shipState"] = new object?[] { instances, new Dictionary<long, object?>(), new Dictionary<long, object?>(), new List<object?>() },
            ["structureInfo"] = null,
            ["shipInfo"] = shipInfo,
            ["charInfo"] = new object?[] { PrimeItem(pilot, pilot.PilotId, "pilot"), new object?[] { 0, Array.Empty<object?>(), Array.Empty<object?>(), Array.Empty<object?>() } },
            ["shipModifiedCharAttribs"] = null,
            ["systemWideEffectsOnShip"] = null,
        });
    }

    private void NotifyShipAttributes(PilotCallContext context)
    {
        context.Flight.RefreshModules(inventory.List(context.Pilot.PilotId, context.Pilot.ShipId));
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var attributes = fitting.ShipAttributes(context.Pilot.PilotId);
        var changes = new[] { DogmaAttributeId.CpuLoad, DogmaAttributeId.PowerLoad, DogmaAttributeId.TurretSlotsLeft }
            .Select(attribute => (object?)new object?[] { "OnModuleAttributeChange", context.Pilot.PilotId, context.Pilot.ShipId, (int)attribute, now, attributes[attribute], null, now }).ToList();
        context.Notifications.Add(("OnModuleAttributeChanges", [changes]));
    }

    private object? SetOnline(PilotCallContext context, long ship, long module, bool online)
    {
        try
        {
            _ = fitting.SetOnline(context.Pilot.PilotId, ship, module, online);
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            context.Notifications.Add(("OnGodmaShipEffect", [module, (int)DogmaEffect.Online, now, online, online,
                new object?[] { module, context.Pilot.PilotId, ship, null, null, Array.Empty<object?>(), (int)DogmaEffect.Online }, now, InventoryDefinitions.InfiniteEffectDuration, 0, null, null]));
            NotifyShipAttributes(context);
            return null;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallRouter.Error(error.Message);
        }
    }

    private object? ItemInfo(DockedPilot pilot, long id)
    {
        if (id == pilot.ShipId) { return PrimeItem(pilot, id, "ship")[id]; }
        if (id == pilot.PilotId) { return PrimeItem(pilot, id, "pilot")[id]; }
        var module = inventory.List(pilot.PilotId, pilot.ShipId).FirstOrDefault(i => i.Id == id && InventoryDefinitions.IsHighSlot(i.FlagId));
        return module is null ? PilotCallRouter.Unsupported : PrimeModule(pilot, module);
    }

    public object? Call(PilotCallContext context, string service, string method, object? arguments, Dictionary<string, object?>? keywords)
        => (service, method) == ("invbroker", "GetItemDescriptor") ? ItemDescriptor() : PilotCallRouter.Unsupported;

    public object? Call(PilotCallContext context, string service, string method, object?[] args, Dictionary<string, object?>? keywords = null)
    {
        var pilot = context.Pilot;
        long container = ShipContainer(service, pilot);
        if (container != 0)
        {
            var ship = fleet.Find(pilot.PilotId, container);
            if (ship is null || (container != pilot.ShipId && (pilot.Position is not null || ship.LocationId != pilot.StationId)))
            {
                return new PilotCallRouter.Error("The ship inventory is unavailable at your current location.");
            }
            if (method == "List") { return List(pilot, service, args); }
            if (method == "ListByFlags" && args.Length == 0 && keywords?.GetValueOrDefault("flags") is List<object?> flags && flags.All(f => f is long))
            {
                return inventory.List(pilot.PilotId, container).Where(i => flags.Contains((long)i.FlagId)).Select(i => (object?)Item(i)).ToList();
            }
            if (method == "GetSelfInvItem") { return container == pilot.ShipId ? Item(pilot, "ship") : Item(ship); }
            if (method == "GetAvailableTurretSlots") { return checked((int)fitting.Attributes(ship.TypeId).GetValueOrDefault(DogmaAttributeId.TurretSlotsLeft)); }
            if (method is "Add" or "MultiAdd")
            {
                return container == pilot.ShipId ? Move(context, "@ship", method, args, keywords) :
                    new PilotCallRouter.Error("Activate the ship before changing its cargo or fitting.");
            }
        }
        return (service, method) switch
        {
            ("corpStationMgr", "DoStandingCheckForStationService") when pilot.Position is null && args is [(long)StationService.Fitting] => null,
            ("corpFittingMgr", "GetCommunityFittings" or "GetFittings") or ("charFittingMgr", "GetFittings") => new Dictionary<long, object?>(),
            ("itemLocking", "GetItemsByLocation") when args is [long location] && location == pilot.LocationId => new Dictionary<long, object?>(),
            ("invbroker", "GetInventory") when pilot.Position is null && args is [10004L, ..] => context.Reference("@hangar"),
            ("invbroker", "GetInventoryFromId") when args is [long id, ..] && fleet.Find(pilot.PilotId, id) is { } ship &&
                (id == pilot.ShipId || (pilot.Position is null && ship.LocationId == pilot.StationId)) => context.Reference($"{ShipInventoryPrefix}{id}"),
            ("invbroker", "GetInventoryFromId") when args.Length > 0 && Equals(args[0], pilot.PilotId) => context.Reference("@pilot"),
            ("@hangar" or "@ship", "List") => List(pilot, service, args),
            ("@pilot", "List") => new List<object?>(),
            ("@hangar" or "@ship", "Add" or "MultiAdd") => Move(context, service, method, args, keywords),
            ("@hangar" or "@ship" or "@pilot", "GetSelfInvItem") => Item(pilot, service[1..]),
            ("dogmaIM", "GetAllInfo") => AllInfo(pilot),
            ("dogmaIM", "ItemGetInfo") when args is [long id] => ItemInfo(pilot, id),
            ("dogmaIM", "SetModuleOnline" or "TakeModuleOffline") when args is [long ship, long module] => SetOnline(context, ship, module, method == "SetModuleOnline"),
            ("dogmaIM", "UnloadAmmo") when args is [long ship, long module, ..] && ship == pilot.ShipId && inventory.List(pilot.PilotId, ship).Any(i => i.Id == module && InventoryDefinitions.IsHighSlot(i.FlagId)) => null,
            _ => PilotCallRouter.Unsupported,
        };
    }
}
