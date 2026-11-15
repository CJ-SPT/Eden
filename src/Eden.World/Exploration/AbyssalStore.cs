using System.Security.Cryptography;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Exploration;
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
    IPilotCombatPresence? presence = null
) : IAbyssalDeadspace
{
    private const int DarkNebula = 22209;
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
            return participant is { Finished: false } && world.Set<SavedAbyssalRun>().Find(participant.RunId) is { } run ? View(run) : null;
        });

    private static AbyssalRun View(SavedAbyssalRun run) =>
        new(
            run.Id,
            run.SystemId,
            run.Room,
            run.Tier,
            (AbyssalWeather)run.Weather,
            (AbyssalStatus)run.Status,
            run.ExpiresAt,
            AbyssalRules.Conduit(run.Id, run.Room),
            DarkNebula
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
        if (mode != AbyssalMode.Cruiser || catalog.RequireItem(pilot.ActiveShip.TypeId).GroupId != AbyssalRules.CruiserGroup)
        {
            throw new InvalidDataException("This Abyssal filament requires a solo cruiser.");
        }
        var item = transaction.Set<OwnedItem>().Find(filamentId);
        if (
            item is null
            || item.OwnerId != pilotId
            || item.LocationId != shipId
            || item.FlagId != (int)InventoryFlag.Cargo
            || item.TypeId != AbyssalRules.TranquilDarkFilament
            || item.Quantity < 1
            || item.Singleton
        )
        {
            throw new InvalidDataException("A Tranquil Dark Filament must be in your active ship's cargo.");
        }
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
                    Weather = (int)AbyssalWeather.Dark,
                    CreatedAt = Now,
                    ExpiresAt = checked(Now + AbyssalRules.Duration),
                    Status = (int)AbyssalStatus.Active,
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
        var previous = PilotInventoryStore.Read(catalog, item);
        item.Quantity--;
        var change = new InventoryChange(previous with { Quantity = item.Quantity }, previous);
        if (item.Quantity == 0)
        {
            transaction.Remove(item);
        }
        Spawn(transaction, catalog, run, Now);
        SqliteWorld.SetPosition(transaction, catalog, shipId, AbyssalRules.Center(0), system, Now);
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
        world.Set<SavedAbyssalParticipant>().Find(pilotId) is { Finished: false } participant ? participant : null;

    public AbyssalTransition Activate(long pilotId, long shipId, long gateId)
    {
        using var selection = Live.EnterPersistence("Abyssal conduit", TransactionParticipants.None);
        var destinations = Live.Economy.Inspect(world =>
            ReadParticipant(world, pilotId) is { } p && world.Set<SavedAbyssalRun>().Find(p.RunId) is { } r
                ? new[] { r.SystemId, r.OriginSystemId }
                : []
        );
        using var transaction = Live.Economy.Begin(new(pilots: [pilotId], systems: destinations));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = RequirePilot(transaction, pilotId, shipId);
        var participant = ReadParticipant(transaction, pilotId) ?? throw new InvalidDataException("No Abyssal run is active.");
        var run = transaction.Set<SavedAbyssalRun>().Find(participant.RunId)!;
        AbyssalRules.RequireActive((AbyssalStatus)run.Status, run.ExpiresAt, Now);
        if (
            participant.ShipId != shipId
            || pilot.ActiveShip.Position!.SystemId != run.SystemId
            || gateId != AbyssalRules.Conduit(run.Id, run.Room)
        )
        {
            throw new InvalidDataException("This conduit is not in your active Abyssal room.");
        }
        var position = pilot.ActiveShip.Position;
        bool hostiles = transaction
            .Filter<WorldNpc>(n =>
                n.AnchorKind == NpcAnchorKind.Abyssal
                && n.EncounterId == run.Id
                && n.TypeId != AbyssalRules.CacheType
                && n.DestroyedAt == null
                && n.RetiredAt == null
            )
            .Any();
        AbyssalRules.RequireConduit(new(position.X, position.Y, position.Z), run.Room, hostiles);
        Retire(transaction, run, Now);
        if (run.Room + 1 == AbyssalRules.RoomCount)
        {
            run.Status = (int)AbyssalStatus.Completed;
            participant.Finished = true;
            SqliteWorld.SetPosition(transaction, catalog, shipId, new(run.OriginX, run.OriginY, run.OriginZ), run.OriginSystemId, Now);
        }
        else
        {
            run.Room++;
            Spawn(transaction, catalog, run, Now);
            SqliteWorld.SetPosition(transaction, catalog, shipId, AbyssalRules.Center(run.Room), run.SystemId, Now);
        }
        var destination = lookup.FindPilot(transaction, pilot.AccountId, pilotId)!;
        transaction.Commit();
        return new(destination, View(run), []);
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
                var traceType = database.Catalog.Items[AbyssalRules.TraceType];
                return traces
                    .Select(r => new StaticSpaceObject(
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
                        Abyssal = new(r.Id, r.FilamentTypeId, (AbyssalMode)r.Mode, false, true),
                    })
                    .ToArray();
            }
            var run = world.Filter<SavedAbyssalRun>(r => r.SystemId == systemId && r.Status == (int)AbyssalStatus.Active).SingleOrDefault();
            if (run is null)
            {
                return (IReadOnlyList<StaticSpaceObject>)Array.Empty<StaticSpaceObject>();
            }
            int typeId = run.Room == AbyssalRules.RoomCount - 1 ? AbyssalRules.OriginConduit : AbyssalRules.TransferConduit;
            var type = database.Catalog.Items[typeId];
            bool open = !world
                .Filter<WorldNpc>(n =>
                    n.AnchorKind == NpcAnchorKind.Abyssal
                    && n.EncounterId == run.Id
                    && n.TypeId != AbyssalRules.CacheType
                    && n.DestroyedAt == null
                    && n.RetiredAt == null
                )
                .Any();
            return
            [
                new StaticSpaceObject(
                    AbyssalRules.Conduit(run.Id, run.Room),
                    typeId,
                    type.GroupId,
                    type.CategoryId,
                    1,
                    AbyssalRules.ConduitPosition(run.Room),
                    (float)(type.Radius ?? 0)
                )
                {
                    Solid = false,
                    Abyssal = new(run.Id, run.FilamentTypeId, (AbyssalMode)run.Mode, open, false),
                },
            ];
        });

    private static void Spawn(WorldAction world, CatalogDbContext catalog, SavedAbyssalRun run, long now)
    {
        int typeId = run.Room switch
        {
            0 => Sparkneedle,
            1 => Emberneedle,
            _ => Strikeneedle,
        };
        int corporation = checked((int)catalog.Require(CatalogData.Client.Factions, (long)DroneFaction).CorporationID!.Value);
        Add(typeId, 0, NpcRole.Pirate);
        Add(AbyssalRules.CacheType, CacheSlot, NpcRole.MissionStructure);
        void Add(int type, int slot, NpcRole role)
        {
            var profile = new NpcProfileCatalog().Read(catalog, type);
            var point = AbyssalRules.Center(run.Room) + new SpacePosition(30_000, slot * 100, 0);
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
                    ShieldCharge = profile.Defense.Shield,
                    UpdatedAt = now,
                    MotionAt = now,
                }
            );
        }
    }

    private static void Retire(WorldAction world, SavedAbyssalRun run, long now)
    {
        foreach (var wreck in world.Filter<WorldWreck>(w => w.SystemId == run.SystemId && w.ExpiresAt > now).ToArray())
        {
            wreck.ExpiresAt = now;
            world.RemoveRange(world.Filter<OwnedItem>(i => i.LocationId == wreck.Id).ToArray());
        }
        foreach (
            var npc in world
                .Filter<WorldNpc>(n => n.AnchorKind == NpcAnchorKind.Abyssal && n.EncounterId == run.Id && n.RetiredAt == null)
                .ToArray()
        )
        {
            npc.RetiredAt = now;
        }
    }
}
