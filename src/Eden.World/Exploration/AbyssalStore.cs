using System.Security.Cryptography;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Exploration;

[Injectable(InjectionType.Singleton)]
internal sealed partial class AbyssalStore(
    WorldDatabase database,
    SqliteWorld lookup,
    TimeProvider clock,
    PilotLossSettlement losses,
    IPilotDamageEvents damageEvents,
    INpcCombat npcCombat,
    INpcSimulation simulation,
    ICooperativeFleets fleets,
    IPilotCombatPresence? presence = null
) : IAbyssalDeadspace
{
    private const int DroneFaction = 500025;
    private const int Sparkneedle = 47847;
    private const int Emberneedle = 47848;
    private const int Strikeneedle = 47849;
    private const int CacheSlot = 15;
    private const double AcquisitionRange = 150_000;

    private LiveWorld Live =>
        LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Abyssal runs require the resident world.");
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public AbyssalRun? Read(long pilotId) =>
        Live.Economy.Inspect(world =>
        {
            var participant = world.Set<SavedAbyssalParticipant>().Find(pilotId);
            return participant is { Finished: false, Entered: true } && world.Set<SavedAbyssalRun>().Find(participant.RunId) is { } run
                ? View(run, participant.Room)
                : null;
        });

    private static AbyssalRun View(SavedAbyssalRun run, int? room = null) =>
        new(
            run.Id,
            run.SystemId,
            room ?? run.Room,
            run.Tier,
            (AbyssalWeather)run.Weather,
            (AbyssalStatus)run.Status,
            run.ExpiresAt,
            AbyssalRules.Conduit(run.Id, room ?? run.Room),
            AbyssalContent.Nebula((AbyssalWeather)run.Weather, room ?? run.Room)
        );

    public AbyssalTransition Deploy(long pilotId, long shipId, long filamentId, AbyssalMode mode)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        using var selection = Live.EnterPersistence("Abyssal admission", TransactionParticipants.None);
        var used = Live.Economy.Inspect(world =>
            world.Filter<SavedAbyssalRun>(r => r.Status < (int)AbyssalStatus.Completed).Select(r => r.SystemId).ToHashSet()
        );
        int system = catalog.Definitions.Systems.Keys.Where(AbyssalRules.IsSystem).Order().FirstOrDefault(id => !used.Contains(id));
        if (system == 0)
        {
            throw new InvalidDataException("No Abyssal pocket is currently available.");
        }
        using var transaction = Live.Economy.Begin(new(pilots: [pilotId], systems: [system]));
        var pilot = RequirePilot(transaction, pilotId, shipId);
        var position = pilot.ActiveShip.Position!;
        int origin = position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
        if (ReadParticipant(transaction, pilotId) is not null || AbyssalRules.IsSystem(origin))
        {
            throw new InvalidDataException("An Abyssal run is already active.");
        }
        RequireHull(catalog, pilot, mode);
        int cost = AbyssalContent.Capacity(mode);
        if (mode != AbyssalMode.Cruiser && fleets.Identity(pilotId) is null)
        {
            throw new InvalidDataException("Join a fleet before deploying a cooperative Abyssal trace.");
        }
        var item = transaction.Set<OwnedItem>().Find(filamentId);
        if (
            item is null
            || item.OwnerId != pilotId
            || item.LocationId != shipId
            || item.FlagId != (int)InventoryFlag.Cargo
            || item.Quantity < cost
            || item.Singleton
        )
        {
            throw new InvalidDataException("Enough matching filaments must be in your active ship's cargo.");
        }
        var filament = AbyssalContent.RequireFilament(item.TypeId);
        RequireEntryState(transaction, catalog, pilot, filament.Tier, origin);
        transaction.RequireUncloaked(shipId);
        CrimewatchPersistence.CheckTravel(transaction, catalog, pilot, CrimewatchTravel.Jump, Now, origin);
        var point = new SpacePosition(position.X, position.Y, position.Z);
        if (
            transaction
                .Filter<SavedAbyssalRun>(r => r.OriginSystemId == origin && r.Status < (int)AbyssalStatus.Completed)
                .Any(r => point.DistanceTo(new(r.OriginX, r.OriginY, r.OriginZ)) < AbyssalRules.DeploymentClearance)
        )
        {
            throw new InvalidDataException("Move at least 1,000 km away from another Abyssal trace.");
        }
        if (
            SystemObjects
                .Geometry(catalog, origin)
                .Any(body =>
                    (body.DestinationSystemId is not null || body.CategoryId == (int)InventoryCategory.Station)
                    && point.DistanceTo(body.Position) - body.Radius < AbyssalRules.DeploymentClearance
                )
        )
        {
            throw new InvalidDataException("Move at least 1,000 km away from stations and stargates before activating a filament.");
        }
        var proximity = CatalogTypeLists.Read(catalog, 50);
        if (
            transaction
                .Filter<Eden.Data.World.Structures.SavedStructure>(b => b.SystemId == origin)
                .Any(b =>
                    proximity.Contains(b.TypeId, catalog.RequireItem(b.TypeId).GroupId, catalog.RequireItem(b.TypeId).CategoryId)
                    && point.DistanceTo(new(b.X, b.Y, b.Z)) < AbyssalRules.DeploymentClearance
                )
        )
        {
            throw new InvalidDataException("Move at least 1,000 km away from the nearby structure.");
        }
        if (
            transaction
                .Filter<WorldCombatAnomaly>(a => a.SystemId == origin && a.CompletedAt == null)
                .Any(a => point.DistanceTo(new(a.X, a.Y, a.Z)) < AbyssalRules.DeploymentClearance)
            || transaction
                .Filter<Eden.Data.World.Agents.SavedMission>(m => m.SystemId == origin && m.ClosedAt == null)
                .Any(m => point.DistanceTo(new(m.X, m.Y, m.Z)) < AbyssalRules.DeploymentClearance)
        )
        {
            throw new InvalidDataException("Leave the deadspace site before deploying an Abyssal filament.");
        }
        long id = Math.Max(AbyssalRules.FirstRunId, (transaction.Filter<SavedAbyssalRun>(_ => true).Max(r => (long?)r.Id) ?? 0) + 1);
        var run = transaction
            .Add(
                new SavedAbyssalRun
                {
                    Id = id,
                    OwnerPilotId = pilotId,
                    SystemId = system,
                    OriginSystemId = origin,
                    OriginX = point.X,
                    OriginY = point.Y,
                    OriginZ = point.Z,
                    FilamentTypeId = item.TypeId,
                    Mode = (int)mode,
                    Weather = (int)filament.Weather,
                    Tier = filament.Tier,
                    CreatedAt = Now,
                    ExpiresAt = checked(Now + (mode == AbyssalMode.Cruiser ? AbyssalRules.Duration : AbyssalRules.TraceDuration)),
                    Status = (int)(mode == AbyssalMode.Cruiser ? AbyssalStatus.Active : AbyssalStatus.Deployed),
                    Seed = RandomNumberGenerator.GetInt32(int.MaxValue),
                }
            )
            .Entity;
        var participant =
            transaction.Set<SavedAbyssalParticipant>().Find(pilotId)
            ?? transaction.Add(new SavedAbyssalParticipant { PilotId = pilotId }).Entity;
        participant.RunId = run.Id;
        participant.ShipId = shipId;
        participant.Finished = false;
        participant.OutsideSince = 0;
        participant.Room = 0;
        participant.Entered = mode == AbyssalMode.Cruiser;
        run.EnvironmentAt = Now;
        var previous = PilotInventoryStore.Read(catalog, item);
        item.Quantity -= cost;
        var change = new InventoryChange(previous with { Quantity = item.Quantity }, previous);
        if (item.Quantity == 0)
        {
            transaction.Remove(item);
        }
        if (mode == AbyssalMode.Cruiser)
        {
            Spawn(transaction, catalog, run, Now);
            SqliteWorld.SetPosition(transaction, catalog, shipId, AbyssalRules.Center(0), system, Now);
        }
        else
        {
            // Snapshot admission rights durably. Fleet services are connection state and do not survive restart.
            foreach (var mate in fleets.Fleetmates(pilotId).Where(p => p.Id != pilotId))
            {
                if (ReadParticipant(transaction, mate.Id) is not null)
                {
                    continue;
                }
                var invited =
                    transaction.Set<SavedAbyssalParticipant>().Find(mate.Id)
                    ?? transaction.Add(new SavedAbyssalParticipant { PilotId = mate.Id }).Entity;
                invited.RunId = run.Id;
                invited.ShipId = 0;
                invited.Entered = false;
                invited.Finished = false;
                invited.Room = 0;
                invited.OutsideSince = 0;
            }
        }
        var destination = lookup.FindPilot(transaction, pilot.AccountId, pilotId)!;
        transaction.Commit();
        return new(destination, View(run), [change]);
    }

    private static Pilot RequirePilot(WorldAction world, long pilotId, long shipId)
    {
        var pilot = world.Pilots.Find(pilotId);
        if (pilot is null || pilot.ActiveShipId != shipId || pilot.ActiveShip.Position is null || pilot.ActiveShip.OwnerId != pilotId)
        {
            throw new InvalidDataException("Activate an Abyssal filament from your active ship in space.");
        }
        return pilot;
    }

    private static SavedAbyssalParticipant? ReadParticipant(WorldAction world, long pilotId) =>
        world.Set<SavedAbyssalParticipant>().Find(pilotId) is { Finished: false } participant
        && (
            participant.Entered
            || world.Set<SavedAbyssalRun>().Find(participant.RunId) is { } run
                && run.OwnerPilotId == pilotId
                && run.Status == (int)AbyssalStatus.Deployed
        )
            ? participant
            : null;

    public AbyssalTransition Activate(long pilotId, long shipId, long gateId)
    {
        using var selection = Live.EnterPersistence("Abyssal conduit", TransactionParticipants.None);
        var destinations = Live.Economy.Inspect(world =>
            world.Set<SavedAbyssalParticipant>().Find(pilotId) is { } p && world.Set<SavedAbyssalRun>().Find(p.RunId) is { } r
                ? new[] { r.SystemId, r.OriginSystemId }
                : []
        );
        using var transaction = Live.Economy.Begin(new(pilots: [pilotId], systems: destinations));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = RequirePilot(transaction, pilotId, shipId);
        var participant = transaction.Set<SavedAbyssalParticipant>().Find(pilotId);
        if (participant is null || participant.Finished)
        {
            throw new InvalidDataException("No Abyssal admission is available.");
        }
        var run = transaction.Set<SavedAbyssalRun>().Find(participant.RunId)!;
        if (!participant.Entered)
        {
            EnterTrace(transaction, catalog, run, participant, pilot, gateId);
        }
        else
        {
            AbyssalRules.RequireActive((AbyssalStatus)run.Status, run.ExpiresAt, Now);
            int room = participant.Room;
            if (
                participant.ShipId != shipId
                || pilot.ActiveShip.Position!.SystemId != run.SystemId
                || gateId != AbyssalRules.Conduit(run.Id, room)
            )
            {
                throw new InvalidDataException("This conduit is not in your active Abyssal room.");
            }
            var position = pilot.ActiveShip.Position;
            bool hostiles = transaction
                .Filter<WorldNpc>(n =>
                    n.AnchorKind == NpcAnchorKind.Abyssal
                    && n.EncounterId == run.Id
                    && n.SpawnSlot / AbyssalRules.ObjectsPerRun == room
                    && n.Role != NpcRole.MissionStructure
                    && n.DestroyedAt == null
                    && n.RetiredAt == null
                )
                .Any();
            AbyssalRules.RequireConduit(new(position.X, position.Y, position.Z), room, hostiles);
            participant.OutsideSince = 0;
            if (room + 1 == AbyssalRules.RoomCount)
            {
                participant.Finished = true;
                ApplyReturnFlag(transaction, catalog, run, pilotId);
                SqliteWorld.SetPosition(transaction, catalog, shipId, new(run.OriginX, run.OriginY, run.OriginZ), run.OriginSystemId, Now);
            }
            else
            {
                participant.Room++;
                if (participant.Room > run.Room)
                {
                    run.Room = participant.Room;
                    Spawn(transaction, catalog, run, Now);
                }
                SqliteWorld.SetPosition(transaction, catalog, shipId, AbyssalRules.Center(participant.Room), run.SystemId, Now);
            }
            // Each pilot crosses independently. Keep a cleared room while another pilot is still looting.
            if (!transaction.Filter<SavedAbyssalParticipant>(p => p.RunId == run.Id && p.Entered && !p.Finished && p.Room == room).Any())
            {
                Retire(transaction, run, Now, room);
            }
            if (!transaction.Filter<SavedAbyssalParticipant>(p => p.RunId == run.Id && p.Entered && !p.Finished).Any())
            {
                run.Status = (int)AbyssalStatus.Completed;
                CloseInvitations(transaction, run);
            }
        }
        var destination = lookup.FindPilot(transaction, pilot.AccountId, pilotId)!;
        var view = View(run, participant.Room);
        if (participant.Finished)
        {
            view = view with { Status = AbyssalStatus.Completed };
        }
        transaction.Commit();
        return new(destination, view, []);
    }

    public IReadOnlyList<StaticSpaceObject> Objects(int systemId) =>
        Live.Economy.Inspect(world =>
        {
            if (!AbyssalRules.IsSystem(systemId))
            {
                var traces = world
                    .Filter<SavedAbyssalRun>(r => r.OriginSystemId == systemId && r.Status < (int)AbyssalStatus.Completed)
                    .ToArray();
                if (traces.Length == 0)
                {
                    return (IReadOnlyList<StaticSpaceObject>)Array.Empty<StaticSpaceObject>();
                }

                return traces
                    .Select(r =>
                    {
                        var traceType = database.Catalog.Items[
                            r.Mode == (int)AbyssalMode.Cruiser ? AbyssalRules.TraceType : AbyssalRules.CooperativeTraceType
                        ];
                        return new StaticSpaceObject(
                            AbyssalRules.Trace(r.Id),
                            traceType.Id,
                            traceType.GroupId,
                            traceType.CategoryId,
                            1,
                            new(r.OriginX, r.OriginY, r.OriginZ),
                            (float)(traceType.Radius ?? 0)
                        )
                        {
                            Solid = false,
                            Abyssal = new(
                                r.Id,
                                r.FilamentTypeId,
                                (AbyssalMode)r.Mode,
                                r.Status == (int)AbyssalStatus.Deployed || r.Room == 0,
                                true
                            ),
                        };
                    })
                    .ToArray();
            }
            var run = world.Filter<SavedAbyssalRun>(r => r.SystemId == systemId && r.Status == (int)AbyssalStatus.Active).SingleOrDefault();
            if (run is null)
            {
                return (IReadOnlyList<StaticSpaceObject>)Array.Empty<StaticSpaceObject>();
            }
            return Enumerable
                .Range(0, run.Room + 1)
                .Where(room =>
                    world.Filter<SavedAbyssalParticipant>(p => p.RunId == run.Id && p.Entered && !p.Finished && p.Room == room).Any()
                )
                .SelectMany(room => RoomObjects(world, run, room))
                .ToArray();
        });

    private IReadOnlyList<StaticSpaceObject> RoomObjects(WorldAction world, SavedAbyssalRun run, int room)
    {
        int typeId = room == AbyssalRules.RoomCount - 1 ? AbyssalRules.OriginConduit : AbyssalRules.TransferConduit;
        var type = database.Catalog.Items[typeId];
        bool open = !world
            .Filter<WorldNpc>(n =>
                n.AnchorKind == NpcAnchorKind.Abyssal
                && n.EncounterId == run.Id
                && n.SpawnSlot / AbyssalRules.ObjectsPerRun == room
                && n.Role != NpcRole.MissionStructure
                && n.DestroyedAt == null
                && n.RetiredAt == null
            )
            .Any();
        return
        [
            new StaticSpaceObject(
                AbyssalRules.Conduit(run.Id, room),
                typeId,
                type.GroupId,
                type.CategoryId,
                1,
                AbyssalRules.ConduitPosition(room),
                (float)(type.Radius ?? 0)
            )
            {
                Solid = false,
                Abyssal = new(run.Id, run.FilamentTypeId, (AbyssalMode)run.Mode, open, false),
            },
            .. AbyssalEnvironmentRules
                .Layout(room, run.Seed)
                .Where(field => field.Slot is 0 or 3)
                .Select(field =>
                {
                    var definition = database.Catalog.Items[field.TypeId];
                    return new StaticSpaceObject(
                        AbyssalEnvironmentRules.FieldId(run.Id, room, field.Slot),
                        field.TypeId,
                        definition.GroupId,
                        definition.CategoryId,
                        1,
                        field.Position,
                        (float)field.Range
                    )
                    {
                        Solid = false,
                        Abyssal = new(run.Id, run.FilamentTypeId, (AbyssalMode)run.Mode, false, false, true),
                    };
                }),
        ];
    }

    private static void Spawn(WorldAction world, CatalogDbContext catalog, SavedAbyssalRun run, long now)
    {
        int typeId = ((run.Seed % 3 + run.Room) % 3) switch
        {
            0 => Sparkneedle,
            1 => Emberneedle,
            _ => Strikeneedle,
        };
        int corporation = checked((int)catalog.Require(CatalogData.Client.Factions, (long)DroneFaction).CorporationID!.Value);
        int count = AbyssalContent.HostileCount(run.Tier, run.Room, run.Seed);
        for (int slot = 0; slot < count; slot++)
        {
            Add(run.Tier > 0 && slot == 0 ? AbyssalContent.Overmind(run.Tier) : typeId, slot, NpcRole.Pirate);
        }
        Add(
            run.Mode == (int)AbyssalMode.Cruiser ? AbyssalRules.CacheType : AbyssalRules.CooperativeCacheType,
            CacheSlot,
            NpcRole.MissionStructure
        );
        foreach (var field in AbyssalEnvironmentRules.Layout(run.Room, run.Seed).Where(field => field.Slot is 1 or 2))
        {
            Add(field.TypeId, CacheSlot - field.Slot, NpcRole.MissionStructure, field.Position);
        }
        void Add(int type, int slot, NpcRole role, SpacePosition? at = null)
        {
            var profile = new NpcProfileCatalog().Read(catalog, type);
            var point = at ?? AbyssalRules.Center(run.Room) + new SpacePosition(30_000, slot * 100, 0);
            world.Add(
                new WorldNpc
                {
                    SystemId = run.SystemId,
                    TypeId = type,
                    Name = profile.Type.Name,
                    FactionId = DroneFaction,
                    CorporationId = corporation,
                    Role = role,
                    AnchorKind = NpcAnchorKind.Abyssal,
                    EncounterId = run.Id,
                    SpawnSlot = run.Room * AbyssalRules.ObjectsPerRun + slot,
                    AcquisitionRange = AcquisitionRange,
                    X = point.X,
                    Y = point.Y,
                    Z = point.Z,
                    HomeX = point.X,
                    HomeY = point.Y,
                    HomeZ = point.Z,
                    ShieldCharge = ShipHealth
                        .DefenseFrom(AbyssalEnvironmentRules.Apply(profile.Attributes, AbyssalEnvironmentStore.At(run, point)))
                        .Shield,
                    UpdatedAt = now,
                    MotionAt = now,
                }
            );
        }
    }

    private static void Retire(WorldAction world, SavedAbyssalRun run, long now, int? room = null)
    {
        foreach (
            var wreck in world
                .Filter<WorldWreck>(w =>
                    w.SystemId == run.SystemId && w.ExpiresAt > now && (room == null || AbyssalContent.RoomAt(new(w.X, w.Y, w.Z)) == room)
                )
                .ToArray()
        )
        {
            wreck.ExpiresAt = now;
            world.RemoveRange(world.Filter<OwnedItem>(i => i.LocationId == wreck.Id).ToArray());
        }
        foreach (
            var npc in world
                .Filter<WorldNpc>(n =>
                    n.AnchorKind == NpcAnchorKind.Abyssal
                    && n.EncounterId == run.Id
                    && n.RetiredAt == null
                    && (room == null || n.SpawnSlot / AbyssalRules.ObjectsPerRun == room)
                )
                .ToArray()
        )
        {
            npc.RetiredAt = now;
        }
        foreach (var drone in world.Filter<WorldDrone>(d => d.SystemId == run.SystemId).ToArray())
        {
            if (room is not null && AbyssalContent.RoomAt(new(drone.X, drone.Y, drone.Z)) != room)
            {
                continue;
            }
            if (world.Set<OwnedItem>().Find(drone.ItemId) is { } item)
            {
                world.Remove(item);
            }
            world.Remove(drone);
        }
    }
}
