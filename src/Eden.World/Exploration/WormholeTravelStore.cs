using Eden.Data.World.Characters;
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
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Exploration;

[Injectable(InjectionType.Singleton)]
internal sealed class WormholeTravelStore(WorldDatabase database, SqliteWorld ships, TimeProvider clock) : IWormholeTravel
{
    private const double Placement = 28 * CombatAnomalyRules.PlacementOffset;
    private const long PolarizationTicks = WormholeRules.PolarizationSeconds * TimeSpan.TicksPerSecond;
    private const int FirstTargetCode = 17_000_000;
    private const int TargetCodeCount = 576_000;
    private readonly Dictionary<int, IReadOnlyList<CombatAnomaly>> _views = [];

    private LiveWorld Owner =>
        LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Wormhole operations require the resident world.");

    private sealed record Connection(
        SavedWormholeConnection Saved,
        StaticSpaceObject Entrance,
        StaticSpaceObject Exit,
        int Destination,
        int TargetClass,
        double MaximumShipMass,
        double ShipMass
    );

    public IReadOnlyList<CombatAnomaly> Signatures(int systemId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var authority = WormholeCatalog.Read(catalog);
        if (!authority.Systems.ContainsKey(systemId) || authority.Types.Count == 0)
        {
            return Array.Empty<CombatAnomaly>();
        }
        using var action = Owner.Economy.Begin(TransactionParticipants.None);
        var world = action;
        long now = Now;
        foreach (var slot in authority.Slots.GetValueOrDefault(systemId) ?? [])
        {
            if (world.Set<SavedWormholeConnection>().Find(slot.Id) is null)
            {
                world.Add(Create(world, authority, slot, 0, now));
            }
        }
        var rows = world
            .Filter<SavedWormholeConnection>(c => c.SourceSystem == systemId)
            .Concat(world.Filter<SavedWormholeConnection>(c => c.DestinationSystem == systemId))
            .DistinctBy(c => c.Id)
            .ToArray();
        List<CombatAnomaly> signatures = [];
        foreach (var saved in rows)
        {
            Advance(saved, now);
            if (saved.ReplaceAt != 0 && saved.ReplaceAt <= now)
            {
                var slot = authority.Slots.GetValueOrDefault(saved.SourceSystem)?.SingleOrDefault(s => s.Id == saved.Id);
                if (slot is null)
                {
                    continue;
                }
                var next = Create(world, authority, slot, saved.Generation + 1, now);
                // Keep the route slot; replace only its connection identity and state.
                world.Remove(saved);
                world.Add(next);
                Add(next);
            }
            else
            {
                Add(saved);
            }
        }
        action.Commit();
        var current = signatures.OrderBy(s => s.Id).ToArray();
        if (_views.TryGetValue(systemId, out var previous) && Same(previous, current))
        {
            return previous;
        }
        _views[systemId] = current;
        return current;

        void Add(SavedWormholeConnection saved)
        {
            if (
                saved.ReplaceAt != 0
                || saved.SourceSystem != systemId && saved.DestinationSystem != systemId
                || saved.SourceSystem != systemId && !saved.ExitVisible
            )
            {
                return;
            }
            bool reverse = saved.SourceSystem != systemId;
            var body = Body(catalog, authority, saved, reverse, now);
            signatures.Add(
                new(
                    body.Id,
                    ScannerTargetIds.FromSequence(FirstTargetCode + (body.Id - WormholeRules.FirstEndpointId) % TargetCodeCount),
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
                )
            );
        }
    }

    private static bool Same(IReadOnlyList<CombatAnomaly> first, IReadOnlyList<CombatAnomaly> second) =>
        first.Count == second.Count
        && first.Zip(second).All(pair => pair.First.Id == pair.Second.Id && pair.First.Objects.Single() == pair.Second.Objects.Single());

    private static SavedWormholeConnection Create(
        WorldAction world,
        WormholeAuthority authority,
        WormholeSlot slot,
        int generation,
        long now
    )
    {
        var type = authority.Types[slot.Type];
        var candidates = authority.Classes[type.TargetClass].Where(s => s.Id != slot.Source).ToArray();
        if (candidates.Length == 0)
        {
            throw new InvalidDataException("No distinct destination is available for the wormhole slot.");
        }
        uint seed = NpcSpawnRules.Seed(slot.Source, (int)(slot.Id % 10), generation);
        int destination =
            generation == 0 && candidates.Any(s => s.Id == slot.InitialDestination)
                ? slot.InitialDestination
                : candidates[(int)(seed % candidates.Length)].Id;
        // Never reuse the six permanent reservoir endpoints from worlds predating version 171.
        long sequence = world.Next("WorldWormholeEndpoint", minimum: 3);
        long endpoint = checked(WormholeRules.FirstEndpointId + 2 * (sequence - 1));
        if (!WormholeRules.IsEndpoint(endpoint + 1))
        {
            throw new InvalidDataException("Wormhole endpoint identities are exhausted.");
        }
        double total = type.TotalMass * (.9 + seed % 2001 / 10000d);
        return new()
        {
            Id = slot.Id,
            EndpointId = endpoint,
            SourceSystem = slot.Source,
            DestinationSystem = destination,
            TypeId = type.Id,
            Generation = generation,
            TotalMass = total,
            RemainingMass = total,
            MassUpdatedAt = now,
        };
    }

    private static void Advance(SavedWormholeConnection saved, long now)
    {
        if (saved.ReplaceAt == 0 && saved.ExpiresAt != 0 && saved.ExpiresAt <= now)
        {
            saved.ReplaceAt = saved.ExpiresAt + WormholeRules.ReplacementSeconds * TimeSpan.TicksPerSecond;
        }
        // Deterministic passive reveal, bounded by the original activation deadline, survives restart.
        if (
            saved.ActivatedAt != 0
            && now >= saved.ActivatedAt + TimeSpan.TicksPerMinute * 5
            && saved.ExpiresAt - now <= 15 * TimeSpan.TicksPerHour
        )
        {
            saved.ExitVisible = true;
        }
    }

    private static StaticSpaceObject Body(
        CatalogDbContext catalog,
        WormholeAuthority authority,
        SavedWormholeConnection saved,
        bool reverse,
        long now
    )
    {
        var source = authority.Systems[reverse ? saved.DestinationSystem : saved.SourceSystem];
        var target = authority.Systems[reverse ? saved.SourceSystem : saved.DestinationSystem];
        int typeId = reverse ? WormholeRules.ExitType : saved.TypeId;
        var type = catalog.FittingType(typeId)!;
        // Vary the location with each generation so an old coordinate bookmark is only an old location.
        double offset = Placement + (saved.EndpointId - WormholeRules.FirstEndpointId + 1) * CombatAnomalyRules.ArrivalRange;
        var profile = authority.Types[saved.TypeId];
        double mass = WormholeRules.Regenerate(saved.RemainingMass, saved.TotalMass, profile.Regeneration, saved.MassUpdatedAt, now);
        return new(
            saved.EndpointId + (reverse ? 1 : 0),
            typeId,
            WormholeRules.Group,
            (int)InventoryCategory.Celestial,
            1,
            source.Anchor + new SpacePosition(offset, -offset, offset),
            (float)(type.Radius ?? 0)
        )
        {
            IsGlobal = false,
            Solid = false,
            Wormhole = new(
                target.Id,
                target.Class,
                profile.JumpMass,
                WormholeRules.Age(saved.ExpiresAt, now),
                WormholeRules.Size(mass, saved.TotalMass)
            ),
        };
    }

    internal static bool Exists(WorldAction world, int systemId, long entranceId, long now)
    {
        if (world is not EconomyTransaction)
        {
            return false;
        }
        long source = entranceId - (entranceId - WormholeRules.FirstEndpointId) % 2;
        var saved = world.Filter<SavedWormholeConnection>(c => c.EndpointId == source).SingleOrDefault();
        return saved is not null
            && saved.ReplaceAt == 0
            && (saved.ExpiresAt == 0 || saved.ExpiresAt > now)
            && (
                saved.SourceSystem == systemId && entranceId == source
                || saved.DestinationSystem == systemId && entranceId == source + 1 && saved.ExitVisible
            );
    }

    public void Activate(int systemId, long entranceId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        using var action = Owner.Economy.Begin(TransactionParticipants.None);
        if (!Exists(action, systemId, entranceId, Now))
        {
            throw new InvalidDataException("This wormhole is no longer available.");
        }
        var saved = action.Filter<SavedWormholeConnection>(c => c.EndpointId == entranceId).SingleOrDefault();
        if (saved is not null)
        {
            Activate(saved, WormholeCatalog.Read(catalog).Types[saved.TypeId], Now);
        }
        action.Commit();
    }

    private static void Activate(SavedWormholeConnection saved, WormholeType type, long now)
    {
        if (saved.ActivatedAt == 0)
        {
            saved.ActivatedAt = now;
            saved.ExpiresAt = checked(now + (long)(type.LifetimeMinutes * TimeSpan.TicksPerMinute));
        }
    }

    public WormholeJump Prepare(long pilotId, long shipId, long entranceId)
    {
        using var action = Owner.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var connection = Validate(action, catalog, pilotId, shipId, entranceId, Now);
        var result = new WormholeJump(connection.Exit, SystemObjects.FindSystem(catalog, connection.Destination), connection.TargetClass);
        action.Commit();
        return result;
    }

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public DockedPilot Transfer(long pilotId, long shipId, long entranceId, long exitId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        using var selection = Owner.Enter();
        long sourceId = entranceId - (entranceId - WormholeRules.FirstEndpointId) % 2;
        int destination = Owner.Economy.Inspect(world =>
        {
            var saved = world.Filter<SavedWormholeConnection>(c => c.EndpointId == sourceId).SingleOrDefault();
            return saved is null ? 0
                : entranceId == sourceId ? saved.DestinationSystem
                : saved.SourceSystem;
        });
        using var transaction = Owner.Economy.Begin(new(systems: destination == 0 ? [] : [destination], pilots: [pilotId]));
        var world = transaction;
        long now = Now;
        var connection = Validate(world, catalog, pilotId, shipId, entranceId, now);
        if (connection.Exit.Id != exitId)
        {
            throw new InvalidDataException("The requested wormhole exit does not match its entrance.");
        }
        var pilot = world.Filter<Pilot>(p => p.Id == pilotId).Single();
        double shipRadius = catalog.RequireItem(pilot.ActiveShip.TypeId).Radius ?? 0;
        var arrival = GateTravelRules.Arrival(shipId, connection.Exit.Position, connection.Exit.Radius, shipRadius);
        var saved =
            world.Set<SavedWormholeJump>().Find(pilotId, entranceId)
            ?? world.Add(new SavedWormholeJump { PilotId = pilotId, EntranceId = entranceId }).Entity;
        saved.JumpedAt = now;
        var profile = WormholeCatalog.Read(catalog).Types[connection.Saved.TypeId];
        Activate(connection.Saved, profile, now);
        connection.Saved.ExitVisible = true;
        connection.Saved.RemainingMass = Math.Max(
            0,
            WormholeRules.Regenerate(
                connection.Saved.RemainingMass,
                connection.Saved.TotalMass,
                profile.Regeneration,
                connection.Saved.MassUpdatedAt,
                now
            ) - connection.ShipMass
        );
        connection.Saved.MassUpdatedAt = now;
        if (connection.Saved.RemainingMass == 0)
        {
            connection.Saved.ReplaceAt = now + WormholeRules.ReplacementSeconds * TimeSpan.TicksPerSecond;
        }
        SqliteWorld.SetPosition(world, catalog, shipId, arrival, connection.Destination, now);
        Eden.World.Propulsion.GateCloakPersistence.Start(world, shipId, now);
        var result = ships.FindPilot(world, pilot.AccountId, pilotId)!;
        transaction.Commit();
        LiveWorld.Find(database.Path)?.Heat.ClearSelection(pilotId, shipId);
        return result;
    }

    public WormholePolarization? Polarization(long pilotId, int systemId, long entranceId)
    {
        return Owner.Economy.Inspect(world =>
            Exists(world, systemId, entranceId, Now) ? ReadPolarization(world, pilotId, entranceId, Now) : null
        );
    }

    private static WormholePolarization? ReadPolarization(WorldAction world, long pilotId, long entranceId, long now) =>
        world.Set<SavedWormholeJump>().Find(pilotId, entranceId) is { } jump && jump.JumpedAt + PolarizationTicks > now
            ? new(jump.JumpedAt + PolarizationTicks, WormholeRules.PolarizationSeconds)
            : null;

    private static Connection Validate(WorldAction world, CatalogDbContext catalog, long pilotId, long shipId, long entranceId, long now)
    {
        Eden.World.Flight.PilotCapitalTravelStore.RequireMobile(world, pilotId, shipId, now);
        var authority = WormholeCatalog.Read(catalog);
        long sourceId = entranceId - (entranceId - WormholeRules.FirstEndpointId) % 2;
        var saved = world.Filter<SavedWormholeConnection>(c => c.EndpointId == sourceId).SingleOrDefault();
        var pilot = world.Filter<Pilot>(p => p.Id == pilotId).Single();
        var position = pilot.ActiveShip.Position;
        var type = catalog.RequireItem(pilot.ActiveShip.TypeId);
        if (
            pilot.ActiveShipId != shipId
            || position?.SystemId is not int system
            || saved is null
            || !Exists(world, system, entranceId, now)
        )
        {
            throw new InvalidDataException("Approach a wormhole with the active ship before jumping.");
        }
        bool reverse = entranceId != saved.EndpointId;
        var body = Body(catalog, authority, saved, reverse, now);
        var exit = Body(catalog, authority, saved, !reverse, now);
        if (
            new SpacePosition(position.X, position.Y, position.Z).DistanceTo(body.Position)
            > body.Radius + (type.Radius ?? 0) + WormholeRules.ActivationRange
        )
        {
            throw new InvalidDataException("Approach the wormhole before jumping.");
        }
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot);
        double mass = attributes.GetValueOrDefault(DogmaAttributeId.Mass);
        if (!double.IsFinite(mass) || mass <= 0 || mass > authority.Types[saved.TypeId].JumpMass)
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
        CrimewatchPersistence.CheckTravel(world, catalog, pilot, CrimewatchTravel.Jump, now, body.Wormhole!.DestinationSystem);
        return new(saved, body, exit, body.Wormhole.DestinationSystem, body.Wormhole.DestinationClass, body.Wormhole.MaximumShipMass, mass);
    }
}
