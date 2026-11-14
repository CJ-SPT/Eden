using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Planetary;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Market;
using Eden.Gameplay.Planetary;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Economy;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.World.Planetary;

[Injectable]
internal sealed class PilotPlanetaryIndustryStore(WorldDatabase database, TimeProvider clock, PilotTrainingStore training)
    : IPilotPlanetaryIndustry
{
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private static void RequireOmega(WorldAction world, long pilotId)
    {
        if (world.Filter<Pilot>(p => p.Id == pilotId).Select(p => p.Account.CloneGrade).Single() != AccountCloneGrade.Omega)
        {
            throw new InvalidDataException("Planetary operations require an Omega pilot.");
        }
    }

    private T Write<T>(
        long pilotId,
        Func<WorldAction, CatalogDbContext, long, T> action,
        Func<WorldAction, CatalogDbContext, int?>? orbitalSystem = null
    ) =>
        LiveWorld.Execute(
            database.Path,
            () =>
            {
                using var ownership = LiveWorld.Find(database.Path)?.EnterShared("planetary industry");
                using var movement = database.EnterMovement();
                using CatalogDbContext catalog = new(database.CatalogPath);
                using var transaction = new WorldActionScope(
                    database,
                    new(pilots: [pilotId]),
                    orbitalSystem is null
                        ? null
                        : world => new(pilots: [pilotId], systems: orbitalSystem(world, catalog) is int system ? [system] : [])
                );
                var world = transaction.World;
                long now = Now;
                var result = action(world, catalog, now);
                transaction.Commit();
                return result;
            },
            "planetary industry"
        );

    internal static PlanetaryColony ReadState(SavedPlanetaryColony saved) =>
        JsonSerializer.Deserialize<PlanetaryColony>(saved.StateJson) ?? throw new InvalidDataException("Invalid saved planetary colony.");

    internal static void Save(SavedPlanetaryColony saved, PlanetaryColony colony) => saved.StateJson = JsonSerializer.Serialize(colony);

    private static PlanetaryColony Advance(SavedPlanetaryColony saved, PlanetaryCatalog catalog, long now)
    {
        var colony = PlanetarySimulation.Advance(ReadState(saved), catalog, now);
        Save(saved, colony);
        return colony;
    }

    private static SavedPlanetaryColony Require(WorldAction world, long pilotId, int planetId) =>
        world.Set<SavedPlanetaryColony>().Find(pilotId, planetId)
        ?? throw new InvalidDataException("You do not have a colony on this planet.");

    public PlanetarySnapshot Read(long pilotId, int planetId) =>
        Write(
            pilotId,
            (world, catalog, now) =>
            {
                var planet = PlanetaryCatalogReader.Planet(catalog, planetId);
                var saved = world.Set<SavedPlanetaryColony>().Find(pilotId, planetId);
                return new PlanetarySnapshot(planet, saved is null ? null : Advance(saved, PlanetaryCatalogReader.Read(catalog), now));
            }
        );

    public IReadOnlyList<PlanetarySummary> List(long pilotId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return WorldActionScope.Inspect<IReadOnlyList<PlanetarySummary>>(
            database,
            world =>
            {
                return world
                    .Filter<SavedPlanetaryColony>(c => c.PilotId == pilotId)
                    .OrderBy(c => c.PlanetId)
                    .AsEnumerable()
                    .Select(c => new PlanetarySummary(PlanetaryCatalogReader.Planet(catalog, c.PlanetId), ReadState(c).Pins.Count))
                    .ToArray();
            }
        );
    }

    public IReadOnlyList<PlanetaryColony> Networks(int planetId)
    {
        return WorldActionScope.Inspect<IReadOnlyList<PlanetaryColony>>(
            database,
            world =>
            {
                return world
                    .Filter<SavedPlanetaryColony>(c => c.PlanetId == planetId)
                    .OrderBy(c => c.PilotId)
                    .AsEnumerable()
                    .Select(ReadState)
                    .ToArray();
            }
        );
    }

    public PlanetaryResourceField ResourceField(long pilotId, int planetId, int typeId, int bands)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return WorldActionScope.Inspect<PlanetaryResourceField>(
            database,
            world =>
            {
                RequireOmega(world, pilotId);
                var planet = PlanetaryCatalogReader.Planet(catalog, planetId);
                PlanetarySurveySkills skills = new(
                    PilotSkillReader.Level(world, catalog, pilotId, PlanetaryRules.RemoteSensingSkill),
                    PilotSkillReader.Level(world, catalog, pilotId, PlanetaryRules.PlanetologySkill),
                    PilotSkillReader.Level(world, catalog, pilotId, PlanetaryRules.AdvancedPlanetologySkill)
                );
                return PlanetaryResources.Survey(planet, typeId, bands, skills, ScanAccess(world, catalog, pilotId, planet));
            }
        );
    }

    private static PlanetarySurveyAccess ScanAccess(WorldAction world, CatalogDbContext catalog, long pilotId, PlanetaryPlanet planet)
    {
        var pilot = world.Filter<Pilot>(p => p.Id == pilotId).Single();
        int current =
            pilot.ActiveShip.Position?.SystemId
            ?? (pilot.ActiveShip.StructureId is long structure ? world.Structure(structure)?.SystemId : null)
            ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
        if (current == planet.SystemId || world.Set<SavedPlanetaryColony>().Find(pilotId, planet.Id) is not null)
        {
            return new(true, true, null, null);
        }
        var source = catalog.Read(CatalogData.Sde.MapSolarSystems, (long)current);
        var target = catalog.Read(CatalogData.Sde.MapSolarSystems, (long)planet.SystemId);
        if (
            source?.Position is not { } start
            || target?.Position is not { } end
            || source.WormholeClassID is > 0
            || target.WormholeClassID is > 0
        )
        {
            return new(false, false, null, null);
        }
        return new(false, true, new(start.X ?? 0, start.Y ?? 0, start.Z ?? 0), new(end.X ?? 0, end.Y ?? 0, end.Z ?? 0));
    }

    public double CustomsTaxRate(long pilotId, long officeId)
    {
        return WorldActionScope.Inspect<double>(
            database,
            world =>
            {
                if (
                    !PlanetaryRules.IsOffice(officeId)
                    || world.Set<SavedPlanetaryOffice>().Find(PlanetaryRules.OfficePlanet(officeId)) is null
                )
                {
                    throw new InvalidDataException("Choose an existing planetary customs office.");
                }
                // Interbus tariffs are unaffected by Customs Code Expertise. Player customs
                // ownership and its separate empire tax component are not enabled here.
                return PlanetaryRules.InterbusTaxRate;
            }
        );
    }

    public PlanetaryChange Update(long pilotId, int planetId, IReadOnlyList<PlanetaryCommand> commands) =>
        Write(
            pilotId,
            (world, catalog, now) =>
            {
                var planet = PlanetaryCatalogReader.Planet(catalog, planetId);
                var definitions = PlanetaryCatalogReader.Read(catalog);
                var pilot = world.Filter<Pilot>(p => p.Id == pilotId).Single();
                if (!planet.Colonizable || pilot.Account.CloneGrade != AccountCloneGrade.Omega)
                {
                    throw new InvalidDataException("Planetary colonies require an Omega pilot and a colonizable planet.");
                }
                training.Advance(world, pilotId, now);
                var saved = world.Set<SavedPlanetaryColony>().Find(pilotId, planetId);
                string hash = Convert.ToHexStringLower(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(
                            string.Join('\n', commands.Select(c => c.GetType().Name + ":" + JsonSerializer.Serialize(c, c.GetType())))
                        )
                    )
                );
                if (
                    saved is not null
                    && saved.LastEditHash == hash
                    && now >= saved.LastEditAt
                    && now - saved.LastEditAt <= 30 * TimeSpan.TicksPerSecond
                )
                {
                    return new PlanetaryChange(Advance(saved, definitions, now), []);
                }
                var state = saved is null ? PlanetaryRules.Empty(pilotId, planetId, now) : Advance(saved, definitions, now);
                var centers = commands
                    .OfType<CreatePlanetaryPin>()
                    .Where(c => PlanetaryRules.PinType(definitions, c.TypeId).Kind == PlanetaryPinKind.Command)
                    .ToArray();
                List<InventoryChange> changes = [];
                if (saved is null)
                {
                    if (
                        centers.Length != 1
                        || pilot.ActiveShip.Position is not { } position
                        || (position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId) != planet.SystemId
                    )
                    {
                        throw new InvalidDataException("Deploy a command center from a ship in the planet's solar system.");
                    }
                    int colonies = world.Filter<SavedPlanetaryColony>(c => c.PilotId == pilotId).Count();
                    int limit = 1 + PilotSkillReader.Level(world, catalog, pilotId, PlanetaryRules.InterplanetaryConsolidationSkill);
                    if (colonies >= limit)
                    {
                        throw new InvalidDataException("Train the planetary skills required for another colony.");
                    }
                    var center = centers[0];
                    if (
                        center.Pin.TemporaryKind != 0
                        || world.Set<OwnedItem>().Find(center.Pin.Value) is not { } item
                        || item.OwnerId != pilotId
                        || item.LocationId != pilot.ActiveShipId
                        || item.TypeId != center.TypeId
                        || item.FlagId is not ((int)InventoryFlag.Cargo) and not ((int)InventoryFlag.CommandCenterHold)
                        || item.Singleton
                        || item.Quantity < 1
                    )
                    {
                        throw new InvalidDataException("Carry the correct command center in your ship's cargo or command-center hold.");
                    }
                    var previous = PilotInventoryStore.Read(catalog, item);
                    item.Quantity--;
                    changes.Add(new(PilotInventoryStore.Read(catalog, item), previous));
                    if (item.Quantity == 0)
                    {
                        world.Remove(item);
                    }
                }
                else if (centers.Length != 0)
                {
                    throw new InvalidDataException("This planet already has your command center.");
                }
                var identity = world.Set<SavedPlanetaryIdentity>().Find(1)!;
                state = state with { NextPin = identity.NextPinId };
                var result = PlanetaryRules.Edit(
                    state,
                    planet,
                    definitions,
                    commands,
                    PilotSkillReader.Level(world, catalog, pilotId, PlanetaryRules.CommandCenterUpgradesSkill),
                    now
                );
                foreach (var create in commands.OfType<CreatePlanetaryPin>())
                {
                    PilotSkillReader.CheckRequirements(world, catalog, pilotId, create.TypeId);
                }
                long balance = EconomicStore.Pay(
                    world,
                    pilotId,
                    planetId,
                    planetId,
                    WalletEntryType.PlanetaryConstruction,
                    result.CostCents,
                    now
                );
                identity.NextPinId = result.Colony.NextPin;
                saved ??= world.Add(new SavedPlanetaryColony { PilotId = pilotId, PlanetId = planetId }).Entity;
                Save(saved, result.Colony);
                saved.LastEditHash = hash;
                saved.LastEditAt = now;
                PlanetaryOrbitStore.EnsureOffice(world, planet);
                return new PlanetaryChange(result.Colony, changes, balance);
            },
            (_, catalog) => PlanetaryCatalogReader.Planet(catalog, planetId).SystemId
        );

    public void Abandon(long pilotId, int planetId) =>
        Write(
            pilotId,
            (world, _, _) =>
            {
                world.Remove(Require(world, pilotId, planetId));
                // Existing orbital cargo belongs to its pilot independently of the colony.
                return true;
            }
        );

    public PlanetaryProgramQuote Quote(
        long pilotId,
        int planetId,
        PlanetaryId pinId,
        int typeId,
        IReadOnlyList<PlanetaryHead> heads,
        double radius
    )
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return WorldActionScope.Inspect<PlanetaryProgramQuote>(
            database,
            world =>
            {
                RequireOmega(world, pilotId);
                var saved = world.Set<SavedPlanetaryColony>().Find(pilotId, planetId);
                var colony = saved is null ? PlanetaryRules.Empty(pilotId, planetId, Now) : ReadState(saved);
                var planet = PlanetaryCatalogReader.Planet(catalog, planetId);
                var definitions = PlanetaryCatalogReader.Read(catalog);
                if (PilotSkillReader.Level(world, catalog, pilotId, PlanetaryRules.RemoteSensingSkill) < 1)
                {
                    throw new InvalidDataException("Train Remote Sensing before surveying extraction programs.");
                }
                PlanetaryResources.Proximity(
                    PilotSkillReader.Level(world, catalog, pilotId, PlanetaryRules.RemoteSensingSkill),
                    ScanAccess(world, catalog, pilotId, planet)
                );
                bool temporary = pinId.TemporaryKind == 1 && pinId.Value >= 0;
                PlanetaryPin pin;
                if (temporary)
                {
                    var type = definitions.Pins.Values.Single(p => p.Kind == PlanetaryPinKind.Extractor && p.PlanetTypeId == planet.TypeId);
                    pin = new(-1, type.TypeId, 0, 0, Now, []);
                }
                else if (pinId.TemporaryKind == 0)
                {
                    pin = PlanetaryRules.Pin(colony, pinId.Value);
                }
                else
                {
                    throw new InvalidDataException("Choose an extractor control unit.");
                }
                return PlanetaryRules.Quote(planet, definitions, pin, typeId, heads, radius, colony.Pins, validateRange: !temporary);
            }
        );
    }

    public PlanetaryTransfer Transfer(
        long pilotId,
        int planetId,
        IReadOnlyList<long> path,
        IReadOnlyList<PlanetaryCommodity> commodities
    ) =>
        Write(
            pilotId,
            (world, catalog, now) =>
            {
                RequireOmega(world, pilotId);
                var saved = Require(world, pilotId, planetId);
                var definitions = PlanetaryCatalogReader.Read(catalog);
                var transfer = PlanetarySimulation.Transfer(Advance(saved, definitions, now), definitions, path, commodities, now);
                Save(saved, transfer.Colony);
                return transfer;
            }
        );

    public PlanetaryChange Launch(long pilotId, int planetId, long pinId, IReadOnlyList<PlanetaryCommodity> commodities) =>
        Write(
            pilotId,
            (world, catalog, now) =>
            {
                RequireOmega(world, pilotId);
                var saved = Require(world, pilotId, planetId);
                var definitions = PlanetaryCatalogReader.Read(catalog);
                var colony = Advance(saved, definitions, now);
                var pin = PlanetaryRules.Pin(colony, pinId);
                if (definitions.Pins[pin.TypeId].Kind != PlanetaryPinKind.Command || pin.LastLaunchAt + PlanetaryRules.LaunchCooldown > now)
                {
                    throw new InvalidDataException("Launch from a ready command center.");
                }
                colony = PlanetarySimulation.Remove(colony, pinId, commodities);
                var planet = PlanetaryCatalogReader.Planet(catalog, planetId);
                long balance = EconomicStore.Pay(
                    world,
                    pilotId,
                    planetId,
                    planetId,
                    WalletEntryType.PlanetaryExportTax,
                    PlanetaryRules.TaxCents(definitions, commodities, 0.15),
                    now
                );
                var position = planet.Position + new Eden.Gameplay.Flight.SpacePosition(planet.Radius + 1_000_000, 0, 0);
                var launch = world
                    .Add(
                        new SavedPlanetaryLaunch
                        {
                            PilotId = pilotId,
                            PlanetId = planetId,
                            SystemId = planet.SystemId,
                            CreatedAt = now,
                            ExpiresAt = checked(now + PlanetaryRules.LaunchLifetime),
                            X = position.X,
                            Y = position.Y,
                            Z = position.Z,
                        }
                    )
                    .Entity;
                world.SaveChanges();
                if (!PlanetaryRules.IsLaunch(launch.Id))
                {
                    throw new InvalidDataException("Planetary launch identities are exhausted.");
                }
                foreach (var commodity in commodities)
                {
                    InventoryPersistence.MergeOutput(
                        world,
                        catalog,
                        pilotId,
                        launch.Id,
                        (int)InventoryFlag.Cargo,
                        commodity.TypeId,
                        commodity.Quantity
                    );
                }
                colony = colony with { Pins = colony.Pins.Select(p => p.Id == pinId ? p with { LastLaunchAt = now } : p).ToArray() };
                Save(saved, colony);
                return new PlanetaryChange(colony, [], balance);
            },
            (_, catalog) => PlanetaryCatalogReader.Planet(catalog, planetId).SystemId
        );

    public IReadOnlyList<PlanetaryLaunch> Launches(long pilotId)
    {
        return WorldActionScope.Inspect<IReadOnlyList<PlanetaryLaunch>>(
            database,
            world =>
            {
                long now = Now;
                return world
                    .Filter<SavedPlanetaryLaunch>(l => l.PilotId == pilotId && l.ExpiresAt > now)
                    .OrderBy(l => l.Id)
                    .AsEnumerable()
                    .Select(l => new PlanetaryLaunch(l.Id, l.PilotId, l.PlanetId, l.SystemId, l.CreatedAt, l.ExpiresAt, new(l.X, l.Y, l.Z)))
                    .ToArray();
            }
        );
    }

    public void DeleteLaunch(long pilotId, int launchId) =>
        Write(
            pilotId,
            (world, _, _) =>
            {
                var launch = world.Set<SavedPlanetaryLaunch>().Find(launchId);
                if (launch is null || launch.PilotId != pilotId)
                {
                    throw new InvalidDataException("This launch does not belong to your pilot.");
                }
                PlanetaryOrbitStore.DeleteLaunch(world, launch);
                return true;
            },
            (world, _) => world.Set<SavedPlanetaryLaunch>().Find(launchId)?.SystemId
        );

    public IReadOnlyList<WreckContact> OrbitContacts(int systemId, long now)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return WorldActionScope.Inspect<IReadOnlyList<WreckContact>>(
            database,
            world =>
            {
                return PlanetaryOrbitStore.Contacts(world, catalog, systemId, now);
            }
        );
    }

    public PlanetaryOrbitInventory OrbitInventory(long pilotId, long containerId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return WorldActionScope.Inspect<PlanetaryOrbitInventory>(
            database,
            world =>
            {
                return PlanetaryOrbitStore.Read(world, catalog, pilotId, containerId, Now);
            }
        );
    }

    public IReadOnlyList<InventoryChange> MoveOrbitCargo(long pilotId, long containerId, bool toOrbit, InventoryMoveRequest request) =>
        Write(pilotId, (world, catalog, now) => PlanetaryOrbitStore.Move(world, catalog, pilotId, containerId, toOrbit, request, now));

    public PlanetaryChange ImportExport(
        long pilotId,
        long officeId,
        long pinId,
        IReadOnlyList<PlanetaryCargoRequest> imports,
        IReadOnlyList<PlanetaryCommodity> exports
    ) =>
        Write(
            pilotId,
            (world, catalog, now) =>
            {
                RequireOmega(world, pilotId);
                PlanetaryOrbitStore.Read(world, catalog, pilotId, officeId, now);
                int planetId = PlanetaryRules.OfficePlanet(officeId);
                var saved = Require(world, pilotId, planetId);
                var definitions = PlanetaryCatalogReader.Read(catalog);
                var colony = Advance(saved, definitions, now);
                var pin = PlanetaryRules.Pin(colony, pinId);
                if (
                    definitions.Pins[pin.TypeId].Kind != PlanetaryPinKind.Spaceport
                    || imports.Count + exports.Count == 0
                    || imports.Count > 128
                    || imports.Select(i => i.ItemId).Distinct().Count() != imports.Count
                )
                {
                    throw new InvalidDataException("Choose a launchpad and distinct import/export quantities.");
                }
                var imported = new List<PlanetaryCommodity>();
                var changes = new List<InventoryChange>();
                foreach (var import in imports)
                {
                    var item = world.Set<OwnedItem>().Find(import.ItemId);
                    if (
                        item is null
                        || item.OwnerId != pilotId
                        || item.LocationId != officeId
                        || item.FlagId != (int)InventoryFlag.Hangar
                        || import.Quantity <= 0
                        || import.Quantity > item.Quantity
                        || !definitions.Volumes.ContainsKey(item.TypeId)
                    )
                    {
                        throw new InvalidDataException("Those imports are unavailable in your customs office compartment.");
                    }
                    imported.Add(new(item.TypeId, import.Quantity));
                    var previous = PilotInventoryStore.Read(catalog, item);
                    item.Quantity -= import.Quantity;
                    changes.Add(new(PilotInventoryStore.Read(catalog, item), previous));
                    if (item.Quantity == 0)
                    {
                        world.Remove(item);
                    }
                }
                imported = imported
                    .GroupBy(i => i.TypeId)
                    .Select(g => new PlanetaryCommodity(g.Key, checked(g.Sum(i => i.Quantity))))
                    .ToList();
                if (exports.Count > 0)
                {
                    colony = PlanetarySimulation.Remove(colony, pinId, exports);
                }
                if (imported.Count > 0)
                {
                    colony = PlanetarySimulation.Deposit(colony, definitions, pinId, imported);
                }
                double rate = PlanetaryRules.InterbusTaxRate;
                long importTax = PlanetaryRules.TaxCents(definitions, imported, rate, import: true);
                long exportTax = PlanetaryRules.TaxCents(definitions, exports, rate);
                EconomicStore.Pay(
                    world,
                    pilotId,
                    PlanetaryOrbitStore.InterbusCorporation,
                    planetId,
                    WalletEntryType.PlanetaryImportTax,
                    importTax,
                    now
                );
                long balance = EconomicStore.Pay(
                    world,
                    pilotId,
                    PlanetaryOrbitStore.InterbusCorporation,
                    planetId,
                    WalletEntryType.PlanetaryExportTax,
                    exportTax,
                    now
                );
                foreach (var export in exports)
                {
                    changes.Add(
                        InventoryPersistence.MergeOutput(
                            world,
                            catalog,
                            pilotId,
                            officeId,
                            (int)InventoryFlag.Hangar,
                            export.TypeId,
                            export.Quantity
                        )
                    );
                }
                PlanetaryOrbitStore.CheckOfficeCapacity(world, catalog, pilotId, officeId);
                colony = PlanetarySimulation.Advance(colony, definitions, now);
                Save(saved, colony);
                return new PlanetaryChange(colony, changes, balance);
            }
        );
}
