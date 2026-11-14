using System.Text.Json;
using Eden.Data.World.Mining;
using Eden.Data.World.Structures;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Structures;

internal sealed partial class PilotStructureStore
{
    public MoonExtraction? Current(long pilotId, long structureId) =>
        ReadMoonMining(
            (world, _) =>
            {
                if (!Access(world, Require(world, structureId), pilotId))
                {
                    throw new InvalidDataException("Access to this refinery is denied.");
                }
                return ActiveExtraction(world, structureId) is { } extraction && extraction.AutoFractureAt > Now
                    ? ReadExtraction(extraction)
                    : null;
            }
        );

    public IReadOnlyList<MoonExtraction> CorporationExtractions(long pilotId, int corporationId) =>
        ReadMoonMining(
            (world, _) =>
            {
                var corporation = world.Set<Eden.Data.World.Corporations.SavedCorporation>().Find(corporationId);
                if (
                    corporation is null
                    || Pilot(world, pilotId).CorporationId != corporationId
                    || !Eden
                        .World.Corporations.CorporationRoleStore.Effective(world, pilotId, corporation)
                        .Roles.Has(Eden.Gameplay.Corporations.CorporationRole.StationManager)
                )
                {
                    throw new InvalidDataException("Corporation structure management authority is required.");
                }
                return world
                    .Filter<SavedMoonExtraction>(e => e.CorporationId == corporationId && e.Ended == false && e.AutoFractureAt > Now)
                    .OrderBy(e => e.ReadyAt)
                    .AsEnumerable()
                    .Select(ReadExtraction)
                    .ToArray();
            }
        );

    public IReadOnlyList<MoonOreShare> Resources(long pilotId, long structureId) =>
        ReadMoonMining(
            (world, catalog) =>
            {
                var structure = Require(world, structureId);
                if (!Access(world, structure, pilotId))
                {
                    throw new InvalidDataException("Access to this refinery is denied.");
                }
                return Composition(catalog, structure);
            }
        );

    public MoonExtraction Start(long pilotId, long structureId, long seconds) =>
        Execute(
            (world, catalog, now) =>
            {
                var structure = Require(world, structureId);
                Manage(world, structure, pilotId);
                if (ActiveExtraction(world, structureId) is not null)
                {
                    throw new InvalidDataException("This refinery already has a moon extraction.");
                }
                var composition = Composition(catalog, structure);
                if (
                    seconds is < MoonMiningRules.MinimumExtractionSeconds or > MoonMiningRules.MaximumExtractionSeconds
                    || composition.Count == 0
                    || !Snapshot(world, catalog, structure).Services.Contains(StructureService.MoonMining)
                )
                {
                    throw new InvalidDataException("Choose a valid duration and an online moon drill at an eligible mining point.");
                }
                long ready = checked(now + seconds * TimeSpan.TicksPerSecond);
                var extraction = new SavedMoonExtraction
                {
                    StructureId = structureId,
                    MoonId = structure.MoonId!.Value,
                    SystemId = structure.SystemId,
                    CorporationId = structure.CorporationId,
                    StartedAt = now,
                    ReadyAt = ready,
                    AutoFractureAt = checked(ready + MoonMiningRules.AutoFractureDelay),
                    Composition = JsonSerializer.Serialize(composition),
                };
                world.Add(extraction);
                world.SaveChanges();
                return ReadExtraction(extraction);
            },
            pilotId: pilotId,
            structureId: structureId
        );

    public void Cancel(long pilotId, long structureId) =>
        Execute(
            (world, _, now) =>
            {
                Manage(world, Require(world, structureId), pilotId);
                var extraction = ActiveExtraction(world, structureId);
                if (extraction is null || now >= extraction.ReadyAt)
                {
                    throw new InvalidDataException("There is no extraction in progress to cancel.");
                }
                extraction.Ended = true;
                return true;
            },
            pilotId: pilotId,
            structureId: structureId
        );

    public void Fracture(long pilotId, long structureId) =>
        Execute(
            (world, catalog, now) =>
            {
                var structure = Require(world, structureId);
                Manage(world, structure, pilotId);
                var extraction = ActiveExtraction(world, structureId);
                if (
                    extraction is null
                    || now < extraction.ReadyAt
                    || !Snapshot(world, catalog, structure).Services.Contains(StructureService.MoonMining)
                )
                {
                    throw new InvalidDataException("An online drill and a ready moon chunk are required.");
                }
                Fracture(world, catalog, extraction, structure, now, now);
                return true;
            },
            pilotId: pilotId,
            structureId: structureId
        );

    public IReadOnlyList<MoonMiningObserver> Observers(long pilotId) =>
        ReadMoonMining(
            (world, _) =>
            {
                int corporation = Pilot(world, pilotId).CorporationId;
                long first = MiningLedgerRules.EarliestDay(Now);
                return world
                    .Filter<SavedStructure>(s =>
                        s.CorporationId == corporation && world.Set<SavedMoonMiningLog>().Any(l => l.StructureId == s.Id && l.Day >= first)
                    )
                    .OrderBy(s => s.Id)
                    .Select(s => new MoonMiningObserver(s.Id, s.Name, s.SystemId))
                    .ToArray();
            }
        );

    public IReadOnlyList<PersonalMiningLog> Ledger(long pilotId, long structureId) =>
        ReadMoonMining(
            (world, _) =>
            {
                if (Require(world, structureId).CorporationId != Pilot(world, pilotId).CorporationId)
                {
                    throw new InvalidDataException("This refinery does not belong to your corporation.");
                }
                long first = MiningLedgerRules.EarliestDay(Now);
                return world
                    .Filter<SavedMoonMiningLog>(l => l.StructureId == structureId && l.Day >= first)
                    .OrderByDescending(l => l.Day)
                    .ThenBy(l => l.Id)
                    .AsEnumerable()
                    .Select(l => l.Snapshot())
                    .ToArray();
            }
        );

    private T ReadMoonMining<T>(Func<WorldAction, CatalogDbContext, T> read)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return WorldActionScope.Inspect(database, world => read(world, catalog));
    }

    private static SavedMoonExtraction? ActiveExtraction(WorldAction world, long structureId) =>
        world.Filter<SavedMoonExtraction>(e => e.StructureId == structureId && e.Ended == false).SingleOrDefault();

    private static IReadOnlyList<MoonOreShare> Composition(CatalogDbContext catalog, SavedStructure structure) =>
        structure.MoonId is int moonId
        && MoonMiningCatalog.Points(catalog, structure.SystemId).Any(p => p.MoonId == moonId)
        && catalog.Definitions.SystemSecurity.TryGetValue(structure.SystemId, out double security)
            ? MoonMiningRules.Composition(moonId, security)
            : [];

    private static MoonExtraction ReadExtraction(SavedMoonExtraction e) =>
        new(e.Id, e.StructureId, e.MoonId, e.SystemId, e.CorporationId, e.StartedAt, e.ReadyAt, e.AutoFractureAt);

    private static void Fracture(
        WorldAction world,
        CatalogDbContext catalog,
        SavedMoonExtraction extraction,
        SavedStructure structure,
        long at,
        long now
    )
    {
        extraction.Ended = true;
        extraction.FracturedAt = at;
        long expiresAt = checked(at + MoonMiningRules.FieldLifetime);
        if (expiresAt <= now)
        {
            return;
        }

        var shares =
            JsonSerializer.Deserialize<MoonOreShare[]>(extraction.Composition)
            ?? throw new InvalidDataException("The saved moon composition is missing.");
        if (shares.Length is < 1 or > 2 || shares.Any(s => s.Percentage <= 0) || shares.Sum(s => s.Percentage) != 100)
        {
            throw new InvalidDataException("The saved moon composition is invalid.");
        }
        double hours = (extraction.ReadyAt - extraction.StartedAt) / (double)TimeSpan.TicksPerHour;
        const int rocksPerShare = 16;
        const double fieldDistance = 95_000;
        var moon = catalog.Definitions.FindCelestial(extraction.MoonId);
        var refinery = new SpacePosition(structure.X, structure.Y, structure.Z);
        var direction = moon is null ? SpacePosition.Zero : (new SpacePosition(moon.X, moon.Y, moon.Z) - refinery).Unit;
        var center = refinery + (direction == SpacePosition.Zero ? new SpacePosition(fieldDistance, 0, 0) : direction * fieldDistance);
        int ordinal = 0;
        foreach (var share in shares)
        {
            double volume = catalog.Read(CatalogData.Client.Types, (long)share.TypeId)?.Volume ?? 0;
            if (!double.IsFinite(volume) || volume <= 0)
            {
                throw new InvalidDataException("The moon ore catalog is missing its unit volume.");
            }
            long units = checked((long)Math.Floor(hours * MoonMiningRules.CubicMetersPerHour * share.Percentage / 100 / volume));
            for (int index = 0; index < rocksPerShare && units > 0; index++)
            {
                long quantity = units / (rocksPerShare - index);
                units -= quantity;
                if (quantity == 0)
                {
                    continue;
                }
                double angle = ordinal * Math.Tau / (rocksPerShare * shares.Length);
                double radius = 8_000 + index * 850;
                world.Add(
                    new WorldAsteroid
                    {
                        Id = MoonMiningRules.RockId(extraction.Id, ordinal++),
                        BeltId = extraction.MoonId,
                        SystemId = extraction.SystemId,
                        TypeId = share.TypeId,
                        Quantity = quantity,
                        X = center.X + Math.Cos(angle) * radius,
                        Y = center.Y + (index % 4 - 2) * 650,
                        Z = center.Z + Math.Sin(angle) * radius,
                        Radius = (float)Math.Sqrt(quantity * volume),
                    }
                );
            }
        }
        extraction.FieldExpiresAt = expiresAt;
    }

    private static void InterruptMoonExtraction(WorldAction world, CatalogDbContext catalog, SavedStructure structure, long at)
    {
        if (
            ActiveExtraction(world, structure.Id) is { } extraction
            && extraction.ReadyAt > at
            && !Snapshot(world, catalog, structure).Services.Contains(StructureService.MoonMining)
        )
        {
            extraction.Ended = true;
        }
    }

    private static void AdvanceMoonExtractions(WorldAction world, CatalogDbContext catalog, long now, int? systemId)
    {
        var active = systemId is int system
            ? world.Filter<SavedMoonExtraction>(e => e.SystemId == system && e.Ended == false && e.AutoFractureAt <= now)
            : world.Filter<SavedMoonExtraction>(e => e.Ended == false && e.AutoFractureAt <= now);
        const int batchSize = 64;
        foreach (var extraction in active.OrderBy(e => e.AutoFractureAt).ThenBy(e => e.Id).Take(batchSize).ToArray())
        {
            if (world.Set<SavedStructure>().Find(extraction.StructureId) is { } structure)
            {
                Fracture(world, catalog, extraction, structure, extraction.AutoFractureAt, now);
            }
            else
            {
                extraction.Ended = true;
            }
        }
        var expired = systemId is int fieldSystem
            ? world.Filter<SavedMoonExtraction>(e => e.SystemId == fieldSystem && e.FieldExpiresAt <= now)
            : world.Filter<SavedMoonExtraction>(e => e.FieldExpiresAt != null && e.FieldExpiresAt <= now);
        foreach (var extraction in expired.OrderBy(e => e.FieldExpiresAt).ThenBy(e => e.Id).Take(batchSize).ToArray())
        {
            long first = MoonMiningRules.RockId(extraction.Id, 0);
            for (int offset = 0; offset < MoonMiningRules.MaximumRocks; offset++)
            {
                if (world.Set<WorldAsteroid>().Find(checked(first + offset)) is { } rock)
                {
                    world.Remove(rock);
                }
            }
            extraction.FieldExpiresAt = null;
        }
    }
}
