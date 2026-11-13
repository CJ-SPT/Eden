using System.Collections.Frozen;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Exploration;

[Injectable(InjectionType.Singleton)]
internal sealed class WormholeTravelStore(WorldDatabase database, SqliteWorld ships, TimeProvider clock) : IWormholeTravel
{
    private const int ExitType = 30831; // K162
    private const double Placement = 28 * CombatAnomalyRules.PlacementOffset;
    private const int MaximumJumpMassAttribute = 1385;
    private const long PolarizationTicks = WormholeRules.PolarizationSeconds * TimeSpan.TicksPerSecond;
    private const int FirstTargetCode = 17_000_000;

    private sealed record Route(int KnownSystem, int WormholeSystem, int Class, int Type);

    private sealed record Endpoint(int System, StaticSpaceObject Body);

    private sealed record Connection(Endpoint Entrance, Endpoint Exit, int TargetClass, double MaximumShipMass);

    // Stable Eden access routes for perimeter, frontier, and core gas reservoirs.
    private static readonly Route[] s_routes =
    [
        new(30002813, 31000356, 2, 30583), // Tama to J125227, R943
        new(30001398, 31000881, 3, 30584), // Aunenen to J114342, X702
        new(30005000, 31001880, 5, 30675), // Old Man Star to J152820, N062
    ];
    private static readonly CatalogDefinition<FrozenDictionary<long, Connection>> s_connections = new(
        "wormhole-reservoir-connections",
        Load
    );
    private static readonly CatalogDefinition<IReadOnlyList<CombatAnomaly>> s_signatures = new("wormhole-signatures", LoadSignatures);

    private static FrozenDictionary<long, Connection> Load(CatalogDbContext catalog, int unused)
    {
        Dictionary<long, Connection> connections = [];
        for (int i = 0; i < s_routes.Length; i++)
        {
            var route = s_routes[i];
            int knownClass = (int)(catalog.Read(CatalogData.Client.Systems, (long)route.KnownSystem)?.WormholeClassID ?? 0);
            int wormholeClass = (int)(catalog.Read(CatalogData.Client.Systems, (long)route.WormholeSystem)?.WormholeClassID ?? 0);
            var known = catalog.CelestialsInSystem(route.KnownSystem).OrderBy(c => c.Id).FirstOrDefault();
            var remote = catalog.CelestialsInSystem(route.WormholeSystem).OrderBy(c => c.Id).FirstOrDefault();
            var sourceType = catalog.FittingType(route.Type);
            var exitType = catalog.FittingType(ExitType);
            double mass = catalog.FittingAttributes(route.Type).GetValueOrDefault((DogmaAttributeId)MaximumJumpMassAttribute);
            if (
                knownClass is < 7 or > 9
                || wormholeClass != route.Class
                || known is null
                || remote is null
                || sourceType?.GroupId != WormholeRules.Group
                || exitType?.GroupId != WormholeRules.Group
                || mass is not > 0
            )
            {
                continue;
            }
            long id = WormholeRules.FirstEndpointId + 2 * i;
            var source = new Endpoint(
                route.KnownSystem,
                Body(
                    id,
                    route.Type,
                    (float)(sourceType.Radius ?? 0),
                    new(known.X + Placement, known.Y - Placement, known.Z + Placement),
                    route.WormholeSystem,
                    route.Class,
                    mass
                )
            );
            var exit = new Endpoint(
                route.WormholeSystem,
                Body(
                    id + 1,
                    ExitType,
                    (float)(exitType.Radius ?? 0),
                    new(remote.X + Placement, remote.Y - Placement, remote.Z + Placement),
                    route.KnownSystem,
                    knownClass,
                    mass
                )
            );
            connections.Add(id, new(source, exit, route.Class, mass));
            connections.Add(id + 1, new(exit, source, knownClass, mass));
        }
        return connections.ToFrozenDictionary();
    }

    private static StaticSpaceObject Body(
        long id,
        int type,
        float radius,
        SpacePosition position,
        int destination,
        int destinationClass,
        double mass
    ) =>
        new(id, type, WormholeRules.Group, (int)InventoryCategory.Celestial, 1, position, radius)
        {
            IsGlobal = false,
            Solid = false,
            Wormhole = new(destination, destinationClass, mass),
        };

    private static IReadOnlyList<CombatAnomaly> LoadSignatures(CatalogDbContext catalog, int systemId) =>
        catalog
            .Definitions.GlobalDefinition(s_connections)
            .Values.Where(connection => connection.Entrance.System == systemId)
            .Select(connection =>
            {
                var body = connection.Entrance.Body;
                return new CombatAnomaly(
                    body.Id,
                    ScannerTargetIds.FromSequence(FirstTargetCode + body.Id - WormholeRules.FirstEndpointId),
                    systemId,
                    0,
                    0,
                    1,
                    body.Position,
                    true,
                    [body],
                    0,
                    IsSignature: true,
                    Wormhole: true
                );
            })
            .ToArray();

    public IReadOnlyList<CombatAnomaly> Signatures(int systemId)
    {
        return database.Catalog.Definition(s_signatures, systemId);
    }

    internal static bool Exists(CatalogDbContext catalog, int systemId, long entranceId) =>
        catalog.Definitions.GlobalDefinition(s_connections).GetValueOrDefault(entranceId)?.Entrance.System == systemId;

    public WormholeJump Prepare(long pilotId, long shipId, long entranceId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var connection = Validate(world, catalog, pilotId, shipId, entranceId, Now);
        return new(connection.Exit.Body, SystemObjects.FindSystem(catalog, connection.Exit.System), connection.TargetClass);
    }

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public DockedPilot Transfer(long pilotId, long shipId, long entranceId, long exitId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        using var transaction = world.Database.BeginTransaction();
        long now = Now;
        var connection = Validate(world, catalog, pilotId, shipId, entranceId, now);
        if (connection.Exit.Body.Id != exitId)
        {
            throw new InvalidDataException("The requested wormhole exit does not match its entrance.");
        }
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId);
        double shipRadius = catalog.RequireItem(pilot.ActiveShip.TypeId).Radius ?? 0;
        var arrival = GateTravelRules.Arrival(shipId, connection.Exit.Body.Position, connection.Exit.Body.Radius, shipRadius);
        var saved =
            world.Set<SavedWormholeJump>().Find(pilotId, entranceId)
            ?? world.Add(new SavedWormholeJump { PilotId = pilotId, EntranceId = entranceId }).Entity;
        saved.JumpedAt = now;
        SqliteWorld.SetPosition(world, catalog, shipId, arrival, connection.Exit.System, now);
        transaction.Commit();
        LiveWorld.Find(database.Path)?.Heat.ClearSelection(pilotId, shipId);
        return ships.FindPilot(pilot.AccountId, pilotId)!;
    }

    public WormholePolarization? Polarization(long pilotId, int systemId, long entranceId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (!Exists(catalog, systemId, entranceId))
        {
            return null;
        }
        using WorldDbContext world = new(database.Path);
        return ReadPolarization(world, pilotId, entranceId, Now);
    }

    private static WormholePolarization? ReadPolarization(WorldDbContext world, long pilotId, long entranceId, long now) =>
        world.Set<SavedWormholeJump>().Find(pilotId, entranceId) is { } jump && jump.JumpedAt + PolarizationTicks > now
            ? new(jump.JumpedAt + PolarizationTicks, WormholeRules.PolarizationSeconds)
            : null;

    private static Connection Validate(WorldDbContext world, CatalogDbContext catalog, long pilotId, long shipId, long entranceId, long now)
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var position = pilot.ActiveShip.Position;
        var type = catalog.RequireItem(pilot.ActiveShip.TypeId);
        if (
            pilot.ActiveShipId != shipId
            || position?.SystemId is not int system
            || catalog.Definitions.GlobalDefinition(s_connections).GetValueOrDefault(entranceId) is not { } connection
            || connection.Entrance.System != system
            || new SpacePosition(position.X, position.Y, position.Z).DistanceTo(connection.Entrance.Body.Position)
                > connection.Entrance.Body.Radius + (type.Radius ?? 0) + WormholeRules.ActivationRange
        )
        {
            throw new InvalidDataException("Approach a wormhole with the active ship before jumping.");
        }
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot);
        double mass = attributes.GetValueOrDefault(DogmaAttributeId.Mass);
        if (!double.IsFinite(mass) || mass <= 0 || mass > connection.MaximumShipMass)
        {
            throw new InvalidDataException("The active ship exceeds the wormhole's jump mass limit.");
        }
        if (ReadPolarization(world, pilotId, entranceId, now) is not null)
        {
            throw new InvalidDataException("The ship's wormhole polarization has not expired.");
        }
        if (PilotHealthStore.Read(world, shipId, ShipHealth.DefenseFrom(attributes), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot enter a wormhole.");
        }
        CrimewatchPersistence.CheckTravel(world, catalog, pilot, CrimewatchTravel.Jump, now, connection.Exit.System);
        return connection;
    }
}
