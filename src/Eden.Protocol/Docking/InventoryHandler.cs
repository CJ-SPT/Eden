using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Combat;
using Eden.Protocol.Loot;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.Docking;

[Injectable(InjectionType.Scoped)]
internal sealed class InventoryHandler(
    TimeProvider clock,
    IPilotInventory inventory,
    IPilotFitting fitting,
    IPilotMining mining,
    IPilotFleet fleet,
    IPilotHealth health,
    IPilotCombat combat,
    IWrecks wrecks,
    IPilotShieldBoosting boosting,
    IPilotAmmunition ammunition,
    MagazineWire magazinesWire,
    PilotBrain brain,
    IPilotPropulsion propulsion,
    IEnumerable<IModuleEffectSource> moduleEffects
) : IPilotCallHandler, ISelectedPilotCallHandler
{
    private const string ShipInventoryPrefix = "@ship:";
    private long _refittingShip;
    private readonly HashSet<long> _refittingModules = [];
    private MagazineChange[] _refittingMagazines = [];

    internal static BlueConstructor ItemDescriptor() => InventoryWire.Descriptor();

    private static InventoryRow Item(InventoryItem item) => InventoryRow.From(item);

    private BlueList<InventoryRow> List(DockedPilot pilot, string service, long? flag)
    {
        List<InventoryRow> result = [];
        if (service == "@hangar" && pilot.Position is not null)
        {
            return new(result);
        }
        long location = service == "@hangar" ? pilot.StationId : ShipContainer(service, pilot);
        if (service == "@hangar" && (flag is null or (long)InventoryFlag.Hangar))
        {
            result.AddRange(fleet.Ships(pilot.PilotId, pilot.StationId).Select(Item));
        }
        result.AddRange(inventory.List(pilot.PilotId, location).Where(i => flag is null || i.FlagId == flag).Select(Item));
        return new(result);
    }

    private static long ShipContainer(string service, DockedPilot pilot) =>
        service == "@ship" ? pilot.ShipId
        : service.StartsWith(ShipInventoryPrefix, StringComparison.Ordinal)
        && long.TryParse(service.AsSpan(ShipInventoryPrefix.Length), out long id)
            ? id
        : 0;

    private PilotCallResult MoveFromWire(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords
    )
    {
        if (args.Length != 2 || args[1] is not long source)
        {
            return PilotCallResult.Unsupported;
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
            return PilotCallResult.Unsupported;
        }
        object? flagValue = keywords?.GetValueOrDefault("flag");
        object? quantityValue = keywords?.GetValueOrDefault("qty");
        if (
            (flagValue is not null && flagValue is not long)
            || (quantityValue is not null && quantityValue is not long)
            || (flagValue is long flagNumber && (flagNumber < 0 || flagNumber > int.MaxValue))
            || (quantityValue is long quantityNumber && (quantityNumber <= 0 || quantityNumber > int.MaxValue))
        )
        {
            return new PilotCallResult.Failure("Invalid cargo flag or quantity.");
        }
        int flag =
            flagValue is long f ? (int)f
            : service == "@hangar" ? (int)InventoryFlag.Hangar
            : (int)InventoryFlag.Cargo;
        int? quantity = quantityValue is long q ? (int)q : null;
        return Move(context, service, new InventoryMoveRequest(ids, source, flag, quantity));
    }

    private PilotCallResult Move(PilotCallContext context, string service, InventoryMoveRequest request)
    {
        var ids = request.ItemIds;
        long source = request.SourceId;
        int flag = request.FlagId;
        int? quantity = request.Quantity;
        long destination = service == "@hangar" ? context.Pilot.StationId : context.Pilot.ShipId;
        try
        {
            var previousMagazines = ammunition.Read(context.Pilot.PilotId, context.Pilot.ShipId);
            bool looting =
                context.Pilot.Position is not null
                && destination == context.Pilot.ShipId
                && flag == (int)InventoryFlag.Cargo
                && context.Flight.VisibleWrecks.Any(w => w.Id == source);
            if (looting && !context.Flight.CanTarget)
            {
                return new PilotCallResult.Failure("Wreck contents cannot be transferred during warp or a system transition.");
            }
            if (looting)
            {
                context.Flight.Checkpoint();
            }
            var changes = TransferItems(context, request, destination, looting);
            var currentMagazines = ammunition.Read(context.Pilot.PilotId, context.Pilot.ShipId).ToDictionary(m => m.ModuleId);
            var changedMagazines = previousMagazines
                .Where(m => !currentMagazines.TryGetValue(m.ModuleId, out var current) || m != current)
                .ToArray();
            // Charge sublocations are keyed by ship/slot/type in this client.
            // Clear every old slot before moving either module in a swap, then
            // prime the new slots after their inventory rows have moved.
            foreach (var magazine in changedMagazines)
            {
                MagazineWire.NotifyQuantity(context, magazine, 0, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            }
            var relocated = changes
                .Where(c =>
                    c.Previous is { } old
                    && old.LocationId == c.Item.LocationId
                    && old.FlagId != c.Item.FlagId
                    && InventoryDefinitions.IsFittedSlot(old.FlagId)
                    && InventoryDefinitions.IsFittedSlot(c.Item.FlagId)
                )
                .ToDictionary(c => c.Item.Id, c => c.Previous! with { FlagId = (int)InventoryFlag.Cargo });
            // A direct fitted-to-fitted flag change updates dogmaItem.flagID,
            // but build 3662168 retains its old ship slot/effect associations.
            // Detach all moving modules from the client rack before refitting
            // either one. This is cache staging, never a saved cargo transfer.
            foreach (var change in changes.Where(c => relocated.ContainsKey(c.Item.Id)))
            {
                InventoryChangesWire.NotifyItemChange(context, new(relocated[change.Item.Id], change.Previous));
            }
            foreach (var change in changes)
            {
                InventoryChangesWire.NotifyItemChange(
                    context,
                    relocated.TryGetValue(change.Item.Id, out var detached) ? change with { Previous = detached } : change
                );
            }
            if (looting)
            {
                context.Flight.RefreshWrecks();
            }
            if (
                changes.Any(c =>
                    InventoryDefinitions.IsFittedSlot(c.Item.FlagId)
                    || (c.Previous is not null && InventoryDefinitions.IsFittedSlot(c.Previous.FlagId))
                )
            )
            {
                // Let OnItemChange fetch the module through ItemGetInfo. A live
                // OnGodmaPrimeItem replaces its inventory row with an incompatible
                // godma row in build 3662168, removing it from clientDogmaLocation.
                NotifyShipAttributes(context);
            }
            var movedMagazines = changedMagazines
                .Where(m => currentMagazines.ContainsKey(m.ModuleId))
                .Select(m => new MagazineChange(null, currentMagazines[m.ModuleId]))
                .ToArray();
            if (relocated.Count > 0 && movedMagazines.Length > 0)
            {
                // Refitting fetches ItemGetInfo asynchronously. A charge fitted
                // before those replies can start its passive effects with no
                // module in the new slot. Wait for the fresh module primes.
                _refittingShip = context.Pilot.ShipId;
                _refittingModules.Clear();
                _refittingModules.UnionWith(relocated.Keys);
                _refittingMagazines = movedMagazines;
            }
            else
            {
                magazinesWire.NotifyChanges(context, movedMagazines);
            }
            return PilotCallResult.None;
        }
        catch (CargoCapacityException error)
        {
            return new PilotCallResult.NamedFailure("NotEnoughCargoSpace", new CargoSpaceErrorValues(error.Available, error.Required));
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private IReadOnlyList<InventoryChange> TransferItems(
        PilotCallContext context,
        InventoryMoveRequest request,
        long destination,
        bool looting
    )
    {
        if (looting)
        {
            return LootWreck(context, request);
        }
        if (
            destination == context.Pilot.ShipId
            && (request.FlagId == (int)InventoryFlag.AutoFit || InventoryDefinitions.IsFittedSlot(request.FlagId))
        )
        {
            return FitItems(context, request);
        }
        return MoveInventory(context, request, destination);
    }

    private IReadOnlyList<InventoryChange> LootWreck(PilotCallContext context, InventoryMoveRequest request) =>
        wrecks.Take(
            context.Pilot.PilotId,
            context.Pilot.ShipId,
            request.SourceId,
            request.ItemIds,
            request.Quantity,
            clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()
        );

    private IReadOnlyList<InventoryChange> FitItems(PilotCallContext context, InventoryMoveRequest request) =>
        fitting.Fit(context.Pilot.PilotId, request.ItemIds, request.SourceId, request.FlagId);

    private IReadOnlyList<InventoryChange> MoveInventory(PilotCallContext context, InventoryMoveRequest request, long destination) =>
        inventory.Move(context.Pilot.PilotId, request.ItemIds, request.SourceId, destination, request.FlagId, request.Quantity);

    private static InventoryRow Item(DockedPilot pilot, string kind) =>
        kind switch
        {
            "ship" => new(
                pilot.ShipId,
                pilot.ShipTypeId,
                pilot.PilotId,
                pilot.LocationId,
                pilot.Position is null ? (int)InventoryFlag.Hangar : (int)InventoryFlag.AutoFit,
                InventoryWireDefinitions.AssembledQuantity,
                pilot.ShipGroupId,
                pilot.ShipCategoryId
            ),
            // Build 3662168: character inventory type 1373, pilot flag 57, group/category 1.
            "pilot" => new(pilot.PilotId, 1373, pilot.PilotId, pilot.ShipId, 57, InventoryWireDefinitions.AssembledQuantity, 1, 1),
            _ => new(
                pilot.StationId,
                pilot.StationTypeId,
                pilot.StationOwnerId,
                pilot.SystemId,
                (int)InventoryFlag.AutoFit,
                InventoryWireDefinitions.AssembledQuantity,
                pilot.Station.GroupId,
                pilot.Station.CategoryId
            ),
        };

    private DogmaItemInfo<int> PrimePilot(DockedPilot pilot) =>
        new(
            new DogmaItemKey.Item(pilot.PilotId),
            Item(pilot, "pilot"),
            PilotProfile.TrainingAttributes(),
            new Dictionary<DogmaEffect, DogmaActiveEffect>(),
            clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()
        );

    private DogmaItemInfo<double> PrimeShip(DockedPilot pilot)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var attributes = fitting.ShipAttributes(pilot.PilotId).ToDictionary(a => a.Key, a => a.Value);
        var current = health.Read(pilot.PilotId, now);
        attributes[DogmaAttributeId.Damage] = current.Defense.Structure - current.Remaining.Structure;
        attributes[DogmaAttributeId.ArmorDamage] = current.Defense.Armor - current.Remaining.Armor;
        attributes[DogmaAttributeId.CapacitorCharge] = mining.Capacitor(pilot.PilotId, now);
        attributes[DogmaAttributeId.MaxVelocity] = pilot.Flight.MaxSpeed;
        attributes[DogmaAttributeId.Mass] = pilot.Flight.Mass;
        attributes[DogmaAttributeId.CargoCapacity] = pilot.CargoCapacity;
        attributes[DogmaAttributeId.ShieldCharge] = current.Remaining.Shield;
        return new(
            new DogmaItemKey.Item(pilot.ShipId),
            Item(pilot, "ship"),
            attributes,
            new Dictionary<DogmaEffect, DogmaActiveEffect>(),
            now
        );
    }

    private DogmaItemInfo<double> PrimeModule(DockedPilot pilot, InventoryItem module)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var attributes = (
            module.GroupId == PropulsionDefinitions.AfterburnerGroup ? propulsion.ModuleAttributes(pilot.PilotId, module)
            : module.GroupId == ShieldBoostDefinitions.Group ? boosting.ModuleAttributes(pilot.PilotId, module)
            : module.TypeId == (int)CombatWeaponType.CivilianGatlingRailgun || module.GroupId == CombatDefinitions.HybridWeaponGroup
                ? combat.ModuleAttributes(pilot.PilotId, module)
            : mining.ModuleAttributes(pilot.PilotId, module)
        ).ToDictionary(a => a.Key, a => a.Value);
        attributes[DogmaAttributeId.IsOnline] = module.Online ? 1d : 0d;
        attributes[DogmaAttributeId.Damage] = 0d;
        Dictionary<DogmaEffect, DogmaActiveEffect> effects = [];
        if (module.Online)
        {
            effects[DogmaEffect.Online] = new(module.Id, pilot.PilotId, pilot.ShipId, null, DogmaEffect.Online, now, null, 0);
        }
        foreach (var source in moduleEffects)
        {
            if (source.ActiveEffect(pilot, module.Id) is { } active)
            {
                effects[active.Effect] = active;
            }
        }
        return new(new DogmaItemKey.Item(module.Id), Item(module), attributes, effects, now);
    }

    internal DogmaAllInfo AllInfo(DockedPilot pilot)
    {
        var modules = inventory.List(pilot.PilotId, pilot.ShipId).Where(i => InventoryDefinitions.IsFittedSlot(i.FlagId)).ToArray();
        var shipInfo = new Dictionary<long, DogmaItemInfo<double>> { [pilot.ShipId] = PrimeShip(pilot) };
        var current = health.Read(pilot.PilotId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        Dictionary<long, DogmaInstanceState> instances = new()
        {
            [pilot.PilotId] = new(pilot.PilotId),
            [pilot.ShipId] = new(
                pilot.ShipId,
                HullDamage: current.Defense.Structure - current.Remaining.Structure,
                Capacitor: mining.Capacitor(pilot.PilotId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()),
                ArmorDamage: current.Defense.Armor - current.Remaining.Armor,
                Shield: current.Remaining.Shield
            ),
        };
        foreach (var module in modules)
        {
            shipInfo[module.Id] = PrimeModule(pilot, module);
            instances[module.Id] = new(module.Id, module.Online);
        }
        var magazines = ammunition.Read(pilot.PilotId, pilot.ShipId);
        Dictionary<long, IReadOnlyDictionary<int, MagazineQuantity>> quantities = [];
        if (magazines.Count > 0)
        {
            quantities[pilot.ShipId] = magazines.ToDictionary(m => m.FlagId, m => new MagazineQuantity(m));
        }
        return new(
            new(instances, quantities),
            shipInfo,
            magazines.ToDictionary(m => new DogmaItemKey.Charge(m.ShipId, m.FlagId, m.TypeId), m => magazinesWire.Prime(m)),
            PrimePilot(pilot),
            brain.Read(pilot.PilotId, pilot.ShipId)
        );
    }

    private void NotifyShipAttributes(PilotCallContext context)
    {
        context.Flight.RefreshModules(inventory.List(context.Pilot.PilotId, context.Pilot.ShipId));
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var attributes = fitting.ShipAttributes(context.Pilot.PilotId);
        var changes = new[] { DogmaAttributeId.CpuLoad, DogmaAttributeId.PowerLoad, DogmaAttributeId.TurretSlotsLeft }
            .Select(attribute => new ModuleAttributeChange(
                context.Pilot.PilotId,
                new DogmaItemKey.Item(context.Pilot.ShipId),
                attribute,
                now,
                attributes[attribute]
            ))
            .ToArray();
        context.Notifications.Add(new ModuleAttributesChanged(changes));
    }

    internal PilotCallResult SetOnline(PilotCallContext context, long ship, long module, bool online)
    {
        try
        {
            _ = fitting.SetOnline(context.Pilot.PilotId, ship, module, online);
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            context.Notifications.Add(
                new ShipEffectChanged(new(module, context.Pilot.PilotId, ship, null, DogmaEffect.Online, now, null, 0), now, online, online)
            );
            NotifyShipAttributes(context);
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private PilotCallResult ItemInfo(PilotCallContext context, long id)
    {
        var pilot = context.Pilot;
        if (id == pilot.ShipId)
        {
            return PrimeShip(pilot);
        }
        if (id == pilot.PilotId)
        {
            return PrimePilot(pilot);
        }
        var module = inventory
            .List(pilot.PilotId, pilot.ShipId)
            .FirstOrDefault(i => i.Id == id && InventoryDefinitions.IsFittedSlot(i.FlagId));
        if (module is null)
        {
            return PilotCallResult.Unsupported;
        }
        var result = PrimeModule(pilot, module);
        if (_refittingShip == pilot.ShipId && _refittingModules.Remove(id) && _refittingModules.Count == 0)
        {
            magazinesWire.NotifyChanges(context, _refittingMagazines);
            _refittingMagazines = [];
        }
        return result;
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object? arguments,
        Dictionary<string, object?>? keywords
    ) => (service, method) == ("invbroker", "GetItemDescriptor") ? ItemDescriptor() : PilotCallResult.Unsupported;

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        var pilot = context.Pilot;
        long container = ShipContainer(service, pilot);
        if (container != 0)
        {
            if (context.DestroyedShip is { } lost && lost.ShipId == container)
            {
                // During capsule adoption, holdTracker.OnCapacityChange can
                // rebind the old cargo after invCache has discarded it. Supply
                // an empty read for this session's just-lost hull, never assets
                // or a writable inventory. Departure/boarding expires it.
                return method switch
                {
                    "List" or "ListByFlags" => BlueEmpty.List,
                    "GetSelfInvItem" => Item(
                        new InventoryItem(
                            lost.ShipId,
                            lost.ShipTypeId,
                            lost.PilotId,
                            (long)InventoryLocation.Graveyard,
                            (int)InventoryFlag.AutoFit,
                            1,
                            lost.ShipGroupId,
                            lost.ShipCategoryId,
                            0,
                            true
                        )
                    ),
                    _ => new PilotCallResult.Failure("The ship was destroyed and its inventory is read-only."),
                };
            }
            var ship = fleet.Find(pilot.PilotId, container);
            if (ship is null || (container != pilot.ShipId && (pilot.Position is not null || ship.LocationId != pilot.StationId)))
            {
                return new PilotCallResult.Failure("The ship inventory is unavailable at your current location.");
            }
            if (method == "List")
            {
                return List(pilot, service, args.FirstOrDefault() as long?);
            }
            if (
                method == "ListByFlags"
                && args.Length == 0
                && keywords?.GetValueOrDefault("flags") is List<object?> flags
                && flags.All(f => f is long)
            )
            {
                return new BlueList<InventoryRow>(
                    inventory.List(pilot.PilotId, container).Where(i => flags.Contains((long)i.FlagId)).Select(Item).ToArray()
                );
            }
            if (method == "GetSelfInvItem")
            {
                return container == pilot.ShipId ? Item(pilot, "ship") : Item(ship);
            }
            if (method == "GetAvailableTurretSlots")
            {
                return checked((int)fitting.Attributes(ship.TypeId).GetValueOrDefault(DogmaAttributeId.TurretSlotsLeft));
            }
            if (method is "Add" or "MultiAdd")
            {
                return container == pilot.ShipId
                    ? MoveFromWire(context, "@ship", method, args, keywords)
                    : new PilotCallResult.Failure("Activate the ship before changing its cargo or fitting.");
            }
        }
        return (service, method) switch
        {
            ("corpStationMgr", "DoStandingCheckForStationService") when pilot.Position is null && args is [(long)StationService.Fitting] =>
                PilotCallResult.None,
            ("corpFittingMgr", "GetCommunityFittings" or "GetFittings") or ("charFittingMgr", "GetFittings") => BlueEmpty.IndexedDictionary,
            ("itemLocking", "GetItemsByLocation") when args is [long location] && location == pilot.LocationId =>
                BlueEmpty.IndexedDictionary,
            ("invbroker", "GetInventory") when pilot.Position is null && args is [10004L, ..] => context.Reference("@hangar"),
            ("invbroker", "GetInventoryFromId")
                when args is [long id, ..]
                    && fleet.Find(pilot.PilotId, id) is { } ship
                    && (id == pilot.ShipId || (pilot.Position is null && ship.LocationId == pilot.StationId)) => context.Reference(
                $"{ShipInventoryPrefix}{id}"
            ),
            ("invbroker", "GetInventoryFromId") when args is [long id, ..] && context.DestroyedShip?.ShipId == id => context.Reference(
                $"{ShipInventoryPrefix}{id}"
            ),
            ("invbroker", "GetInventoryFromId") when args.Length > 0 && Equals(args[0], pilot.PilotId) => context.Reference("@pilot"),
            ("@hangar" or "@ship", "List") => List(pilot, service, args.FirstOrDefault() as long?),
            ("@pilot", "List") => BlueEmpty.List,
            ("@hangar" or "@ship", "Add" or "MultiAdd") => MoveFromWire(context, service, method, args, keywords),
            ("@hangar" or "@ship" or "@pilot", "GetSelfInvItem") => Item(pilot, service[1..]),
            ("dogmaIM", "GetAllInfo") => AllInfo(pilot),
            ("dogmaIM", "ItemGetInfo") when args is [long id] => ItemInfo(context, id),
            ("dogmaIM", "ItemGetInfo")
                when args is [object?[] { Length: 3 } key] && key is [long hull, long slot, long type] && hull == pilot.ShipId => ammunition
                .Read(pilot.PilotId, hull)
                .FirstOrDefault(m => m.FlagId == slot && m.TypeId == type)
                is { } magazine
                ? magazinesWire.Prime(magazine)
                : PilotCallResult.None,
            ("dogmaIM", "SetModuleOnline" or "TakeModuleOffline") when args is [long ship, long module] => SetOnline(
                context,
                ship,
                module,
                method == "SetModuleOnline"
            ),
            _ => PilotCallResult.Unsupported,
        };
    }
}
