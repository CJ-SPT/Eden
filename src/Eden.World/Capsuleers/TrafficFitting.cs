using Eden.Data.Catalog.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Skills;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal static class TrafficFitting
{
    private readonly record struct MagazineBatch(int TypeId, int PartialSlot);

    private sealed record Magazine(OwnedItem Module, TrafficEquipment Charge);

    internal static void CheckTypes(CatalogDbContext catalog, TrafficProfile profile)
    {
        var hull = catalog.FittingType(profile.HullTypeId);
        if (
            hull is null
            || !HullCatalog.CanAssemble(catalog, hull, hull.CategoryId)
            || !TrafficRules.RouteAllows((HullClass)hull.GroupId, false)
        )
        {
            throw new InvalidDataException("Unsupported hull.");
        }
        if (profile.Equipment.Any(item => item.Quantity <= 0 || !InventoryDefinitions.IsFittedSlot(item.Flag)))
        {
            throw new InvalidDataException("Invalid fitting slot or quantity.");
        }
        foreach (var item in profile.Equipment)
        {
            var type = catalog.FittingType(item.TypeId) ?? throw new InvalidDataException("Unknown equipment type.");
            if (
                type.CategoryId != (int)InventoryCategory.Charge
                && (item.Quantity != 1 || FittingCatalog.Definition(catalog, item.TypeId) is null)
            )
            {
                throw new InvalidDataException("Unsupported equipment.");
            }
        }
        var modules = profile
            .Equipment.Where(item => catalog.RequireItem(item.TypeId).CategoryId != (int)InventoryCategory.Charge)
            .ToArray();
        var charges = profile
            .Equipment.Where(item => catalog.RequireItem(item.TypeId).CategoryId == (int)InventoryCategory.Charge)
            .ToArray();
        if (
            modules.GroupBy(item => item.Flag).Any(group => group.Count() != 1)
            || charges.GroupBy(item => item.Flag).Any(group => group.Count() != 1)
        )
        {
            throw new InvalidDataException("Duplicate equipment in a fitting slot.");
        }
        foreach (var charge in charges)
        {
            var module =
                modules.SingleOrDefault(item => item.Flag == charge.Flag)
                ?? throw new InvalidDataException("Loaded charge has no fitted module.");
            var type = catalog.RequireItem(charge.TypeId);
            double volume = PilotInventoryStore.Volume(catalog, charge.TypeId);
            double capacity = catalog.RequireItem(module.TypeId).Capacity ?? 0;
            if (
                volume <= 0
                || volume * charge.Quantity > capacity + InventoryDefinitions.ResourceTolerance
                || charge.Quantity != 1
                    && (AmmunitionDefinitions.IsCrystalGroup(type.GroupId) || SupportModuleDefinitions.IsScript(type.GroupId))
            )
            {
                throw new InvalidDataException("Recorded ammunition exceeds the legal magazine capacity.");
            }
        }
    }

    internal static void Apply(WorldDbContext world, CatalogDbContext catalog, long pilot, long ship, TrafficProfile profile, long now)
    {
        CheckTypes(catalog, profile);
        var hull = world.Ships.Find(ship) ?? throw new InvalidDataException("Missing traffic ship.");
        hull.TypeId = profile.HullTypeId;
        GrantSkills(world, catalog, pilot, profile);
        world.SaveChanges();
        // Fit rigs before bringing resource consumers online. The normal fitting
        // owner remains responsible for slots, hardpoints, calibration and effects.
        var modules = profile
            .Equipment.Where(item => catalog.RequireItem(item.TypeId).CategoryId != (int)InventoryCategory.Charge)
            .OrderByDescending(item => InventoryDefinitions.IsRigSlot(item.Flag))
            .ThenBy(item => item.Flag)
            .ToArray();
        Dictionary<long, int> placements = [];
        long nextItem = InventoryPersistence.NextId(world);
        foreach (var module in modules)
        {
            var item = new OwnedItem
            {
                Id = nextItem++,
                OwnerId = pilot,
                TypeId = module.TypeId,
                LocationId = hull.StationId,
                FlagId = (int)InventoryFlag.Hangar,
                Quantity = 1,
            };
            world.Add(item);
            placements.Add(item.Id, module.Flag);
        }
        world.SaveChanges();
        if (placements.Count > 0)
        {
            PilotFittingStore.Fit(
                world,
                catalog,
                pilot,
                placements.Keys.ToArray(),
                hull.StationId,
                (int)InventoryFlag.AutoFit,
                now,
                placements,
                completeOnline: true
            );
        }
        var fitted = world.Set<OwnedItem>().Where(item => item.LocationId == ship).OrderBy(item => item.FlagId).ToArray();
        var magazines = profile
            .Equipment.Where(item => catalog.RequireItem(item.TypeId).CategoryId == (int)InventoryCategory.Charge)
            .Select(charge => new Magazine(
                fitted.SingleOrDefault(item => item.FlagId == charge.Flag)
                    ?? throw new InvalidDataException("Loaded charge has no fitted module."),
                charge
            ))
            .GroupBy(magazine =>
            {
                var type = catalog.RequireItem(magazine.Charge.TypeId);
                double volume = PilotInventoryStore.Volume(catalog, type.Id);
                int full = checked(
                    (int)
                        Math.Floor(
                            (catalog.RequireItem(magazine.Module.TypeId).Capacity ?? 0) / volume + InventoryDefinitions.ResourceTolerance
                        )
                );
                if (AmmunitionDefinitions.IsCrystalGroup(type.GroupId) || SupportModuleDefinitions.IsScript(type.GroupId))
                {
                    full = Math.Min(full, 1);
                }
                // Full magazines can share one load. Partial magazines retain their exact per-slot quantities.
                return new MagazineBatch(type.Id, magazine.Charge.Quantity == full ? 0 : magazine.Charge.Flag);
            });
        foreach (var batch in magazines)
        {
            var targets = batch.ToArray();
            var item = new OwnedItem
            {
                Id = InventoryPersistence.NextId(world),
                OwnerId = pilot,
                TypeId = batch.Key.TypeId,
                LocationId = hull.StationId,
                FlagId = (int)InventoryFlag.Hangar,
                Quantity = targets.Sum(target => target.Charge.Quantity),
            };
            world.Add(item);
            world.SaveChanges();
            PilotAmmunitionStore.Load(
                world,
                catalog,
                pilot,
                ship,
                targets.Select(target => target.Module.Id).ToArray(),
                [item.Id],
                hull.StationId,
                now
            );
            var moduleIds = targets.Select(target => target.Module.Id).ToArray();
            var loaded = world
                .Set<OwnedItem>()
                .Where(value => moduleIds.Contains(value.LocationId))
                .ToDictionary(value => value.LocationId);
            if (
                targets.Any(target =>
                    !loaded.TryGetValue(target.Module.Id, out var charge)
                    || charge.TypeId != target.Charge.TypeId
                    || charge.Quantity != target.Charge.Quantity
                )
            )
            {
                throw new InvalidDataException("Recorded ammunition exceeds the legal magazine capacity.");
            }
        }
        world.SaveChanges();
        if (
            world
                .Set<OwnedItem>()
                .Any(item =>
                    item.LocationId == ship
                    && item.FlagId >= (int)InventoryFlag.LowSlot0
                    && item.FlagId <= (int)InventoryFlag.HighSlot7
                    && !item.Online
                )
        )
        {
            throw new InvalidDataException("The complete fitting cannot remain online.");
        }
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot);
        double used = InventoryPersistence.UsedVolume(world, catalog, pilot, ship, (int)InventoryFlag.Cargo);
        if (used > attributes.GetValueOrDefault(DogmaAttributeId.CargoCapacity) + InventoryDefinitions.ResourceTolerance)
        {
            throw new InvalidDataException("Existing cargo does not fit the selected traffic hull.");
        }
    }

    private static void GrantSkills(WorldDbContext world, CatalogDbContext catalog, long pilot, TrafficProfile profile)
    {
        HashSet<int> visited = [];
        HashSet<int> required = [];
        Collect(profile.HullTypeId);
        foreach (var item in profile.Equipment)
        {
            Collect(item.TypeId);
        }
        var skills = Enum.GetValues<FittingSkill>()
            .Select(skill => (int)skill)
            .Concat(Enum.GetValues<NavigationSkill>().Select(skill => (int)skill))
            .Concat(Enum.GetValues<RiggingSkill>().Select(skill => (int)skill))
            .Concat(Enum.GetValues<CombatSkill>().Select(skill => (int)skill))
            .Concat(Enum.GetValues<DefenseSkill>().Select(skill => (int)skill))
            .Concat(PassiveModules.Skills)
            .Concat(required)
            .Distinct();
        List<SqliteParameter> parameters = [new("$pilot", pilot)];
        List<string> rows = [];
        foreach (int skill in skills)
        {
            if (catalog.FittingAttributes(skill).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant) is not > 0)
            {
                continue;
            }
            long points = TrainedSkill.PointsForLevel(
                catalog.FittingAttributes(skill)[DogmaAttributeId.SkillTimeConstant],
                TrafficRules.MaximumSkillLevel
            );
            int index = rows.Count;
            rows.Add($"($pilot,$skill{index},$points{index})");
            parameters.Add(new SqliteParameter($"$skill{index}", skill));
            parameters.Add(new SqliteParameter($"$points{index}", points));
        }
        if (rows.Count > 0)
        {
            // Only generated parameter names enter the statement; all catalog
            // identifiers, points and the pilot identity remain bound values.
            string statement =
                "INSERT INTO WorldPilotSkill(PilotId,TypeId,Points) VALUES "
                + string.Join(",", rows)
                + " ON CONFLICT(PilotId,TypeId) DO UPDATE SET Points=MAX(Points,excluded.Points)";
            world.Database.ExecuteSqlRaw(statement, parameters);
        }

        void Collect(int type)
        {
            if (!visited.Add(type))
            {
                return;
            }
            var attributes = catalog.FittingAttributes(type);
            foreach (var (skillAttribute, _) in InventoryDefinitions.RequiredSkills)
            {
                int skill = checked((int)attributes.GetValueOrDefault(skillAttribute));
                if (skill != 0)
                {
                    required.Add(skill);
                    Collect(skill);
                }
            }
        }
    }
}

internal sealed class TrafficFitValidator : IDisposable
{
    private const long ValidationAccountId = 1;
    private const long ValidationPilotId = 90_000_001;
    private const long ValidationShipId = 140_000_000_001;
    private readonly TrafficValidationImage _validation = new();
    private readonly CatalogDbContext _catalog;
    private readonly Dictionary<string, string?> _fits = [];
    private readonly int _station;

    internal TrafficFitValidator(string catalogPath)
    {
        _catalog = new(catalogPath);
        _station = _catalog.Definitions.StationNames.Keys.First();
        using WorldDbContext world = _validation.Open();
        world.Database.EnsureCreated();
        using var transaction = world.Database.BeginTransaction();
        world.Database.ExecuteSqlRaw("PRAGMA defer_foreign_keys=ON");
        world.Add(
            new Account
            {
                Id = ValidationAccountId,
                UserName = "Traffic validation",
                CloneGrade = Eden.Gameplay.Docking.AccountCloneGrade.Omega,
            }
        );
        world.Add(new InventoryOwner { Id = ValidationPilotId });
        world.Add(
            new Ship
            {
                Id = ValidationShipId,
                OwnerId = ValidationPilotId,
                StationId = _station,
                Name = "Traffic validation",
            }
        );
        world.Add(
            new Pilot
            {
                Id = ValidationPilotId,
                AccountId = ValidationAccountId,
                Name = "Traffic validation",
                ActiveShipId = ValidationShipId,
            }
        );
        world.SaveChanges();
        transaction.Commit();
    }

    internal string? Validate(TrafficProfile profile)
    {
        using var catalogScope = new CatalogReadScope().Begin();
        try
        {
            Check(profile);
        }
        catch (InvalidDataException error)
        {
            return error.Message;
        }
        string signature = TrafficSnapshot.ValidationSignature(profile);
        if (_fits.TryGetValue(signature, out string? cached))
        {
            return cached;
        }
        string? rejection = null;
        try
        {
            TrafficFitting.CheckTypes(_catalog, profile);
            using WorldDbContext world = _validation.Open();
            using var transaction = world.Database.BeginTransaction();
            TrafficFitting.Apply(
                world,
                _catalog,
                ValidationPilotId,
                ValidationShipId,
                profile,
                DateTimeOffset.UtcNow.UtcDateTime.ToFileTimeUtc()
            );
            // Every validation rolls back. No test identity or inventory reaches the save.
        }
        catch (InvalidDataException error)
        {
            rejection = error.Message;
        }
        _fits.Add(signature, rejection);
        return rejection;
    }

    internal void Check(TrafficProfile profile)
    {
        using var catalogScope = new CatalogReadScope().Begin();
        TrafficFitting.CheckTypes(_catalog, profile);
    }

    public void Dispose()
    {
        _catalog.Dispose();
        _validation.Dispose();
    }
}
