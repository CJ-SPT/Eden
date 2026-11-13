using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Exploration;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Mining;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Infrastructure.Registration;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Exploration;

[Injectable]
internal sealed class PilotProbeScanningStore(WorldDatabase database, ICombatAnomalies sites) : IPilotProbeScanning
{
    private const int CapsuleGroup = 29;
    private static readonly JsonSerializerOptions s_json = new() { IgnoreReadOnlyProperties = true };

    private T Write<T>(Func<WorldDbContext, CatalogDbContext, T> action)
    {
        return LiveWorld.Execute(database.Path, () => WriteOwned<T>(action), nameof(Write));
    }

    private T WriteOwned<T>(Func<WorldDbContext, CatalogDbContext, T> action)
    {
        using var owner = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            using WorldDbContext world = new(database.Path);
            using CatalogDbContext catalog = new(database.CatalogPath);
            using var transaction = world.Database.BeginTransaction();
            var result = action(world, catalog);
            world.SaveChanges();
            transaction.Commit();
            return result;
        }
    }

    private static Pilot Pilot(WorldDbContext world, CatalogDbContext catalog, long pilotId, long now)
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        if (pilot.ActiveShip.Position is null || catalog.RequireItem(pilot.ActiveShip.TypeId).GroupId == CapsuleGroup)
        {
            throw new InvalidDataException("Operate probes from a ship in space.");
        }
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot);
        if (PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(attributes), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot operate probes.");
        }
        return pilot;
    }

    private static ScanProbe Read(SavedScanProbe p) =>
        new(p.Id, p.TypeId, p.SystemId, new(p.X, p.Y, p.Z), p.ExpiresAt, JsonSerializer.Deserialize<ProbeProfile>(p.ProfileJson)!);

    private static int SystemId(Pilot pilot, CatalogDbContext catalog)
    {
        var position = pilot.ActiveShip.Position ?? throw new InvalidDataException("Operate probes from a ship in space.");
        // Ordinary undocking leaves the system implicit until the ship travels to another system.
        return position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
    }

    public IReadOnlyList<ScanProbe> List(long pilotId, int systemId, long now)
    {
        using WorldDbContext world = new(database.Path);
        return world
            .Set<SavedScanProbe>()
            .Where(p => p.PilotId == pilotId && p.SystemId == systemId && p.ExpiresAt > now)
            .OrderBy(p => p.Id)
            .AsEnumerable()
            .Select(Read)
            .ToArray();
    }

    public bool IsLauncher(long pilotId, long moduleId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var type = world
            .Set<OwnedItem>()
            .Where(i => i.Id == moduleId && i.OwnerId == pilotId)
            .Select(i => (int?)i.TypeId)
            .SingleOrDefault();
        return type is int id && catalog.FittingType(id)?.GroupId == ProbeScanRules.LauncherGroup;
    }

    public ProbeScan? Pending(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        return world.Set<SavedProbeScan>().Find(pilotId) is { } saved ? JsonSerializer.Deserialize<ProbeScan>(saved.ScanJson) : null;
    }

    public bool IsSurveyLauncher(long pilotId, long moduleId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var module = world.Set<OwnedItem>().Find(moduleId);
        return module?.OwnerId == pilotId && catalog.FittingType(module.TypeId)?.GroupId == ProbeScanRules.SurveyLauncherGroup;
    }

    public MoonSurveyLaunch LaunchSurvey(long pilotId, long moduleId, SpacePosition heading, long now) =>
        Write(
            (world, catalog) =>
            {
                var pilot = Pilot(world, catalog, pilotId, now);
                int system = SystemId(pilot, catalog);
                var module = world.Set<OwnedItem>().Find(moduleId);
                var probe = PilotAmmunitionStore.Contents(world, moduleId);
                if (
                    module is not { Online: true, Singleton: true }
                    || module.OwnerId != pilotId
                    || module.LocationId != pilot.ActiveShipId
                    || !InventoryDefinitions.IsHighSlot(module.FlagId)
                    || catalog.RequireItem(module.TypeId).GroupId != ProbeScanRules.SurveyLauncherGroup
                    || !ProbeCatalog.Launcher(catalog, module.TypeId, ProbeScanRules.SurveyLauncherGroup)
                    || probe is null
                    || probe.OwnerId != pilotId
                    || probe.Quantity <= 0
                    || catalog.RequireItem(probe.TypeId).GroupId != (int)HoldCargoGroup.SurveyProbe
                    || !ProbeCatalog.Probe(catalog, probe.TypeId, (int)HoldCargoGroup.SurveyProbe)
                    || world.Set<ModuleCooldown>().Find(moduleId)?.ReadyAt > now
                )
                {
                    throw new InvalidDataException("Use an online survey launcher with a ready, compatible probe.");
                }
                PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
                PilotSkillReader.CheckRequirements(world, catalog, pilotId, probe.TypeId);
                PilotFittingStore.CheckHull(catalog, module.TypeId, pilot.ActiveShip.TypeId);
                NaniteRepairPersistence.RequireAvailable(world, catalog, moduleId, now);
                var attributes = catalog.FittingAttributes(probe.TypeId);
                var launcher = catalog.FittingAttributes(module.TypeId);
                const DogmaAttributeId maximumScanRange = (DogmaAttributeId)2697;
                const DogmaAttributeId surveyTimeBonus = (DogmaAttributeId)2701;
                double range = attributes.GetValueOrDefault(maximumScanRange);
                double duration =
                    attributes.GetValueOrDefault(DogmaAttributeId.MissileFlightTime)
                    * (1 + launcher.GetValueOrDefault(surveyTimeBonus) / 100);
                if (!double.IsFinite(range) || range <= 0 || !double.IsFinite(duration) || duration <= 0)
                {
                    throw new InvalidDataException("The survey probe has invalid scan attributes.");
                }
                var position = pilot.ActiveShip.Position!;
                SpacePosition origin = new(position.X, position.Y, position.Z);
                var moon = Eden.World.Structures.MoonMiningCatalog.SurveyTarget(catalog, system, origin, heading, range);
                var composition =
                    moon is int id && catalog.Definitions.SystemSecurity.TryGetValue(system, out double security)
                        ? Eden.Gameplay.Structures.MoonMiningRules.Composition(id, security)
                        : [];
                long ready = checked(now + (long)Math.Ceiling(duration * TimeSpan.TicksPerMillisecond));
                var cooldown = world.Set<ModuleCooldown>().Find(moduleId) ?? world.Add(new ModuleCooldown { ModuleId = moduleId }).Entity;
                cooldown.Kind = ModuleCooldownKind.Cycle;
                cooldown.ReadyAt = checked(
                    now + (long)Math.Ceiling(launcher.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime) * TimeSpan.TicksPerMillisecond)
                );
                probe.Quantity--;
                var magazine = PilotAmmunitionStore.Read(world, module, probe) with { ReadyAt = cooldown.ReadyAt };
                if (probe.Quantity == 0)
                {
                    world.Remove(probe);
                }
                return new MoonSurveyLaunch(0, probe.TypeId, origin, ready, range, moon, composition, magazine);
            }
        );

    public ProbeLaunch Launch(long pilotId, long moduleId, int count, long now) =>
        Write(
            (world, catalog) =>
            {
                if (count is < 1 or > ProbeScanRules.MaximumProbes)
                {
                    throw new InvalidDataException("Launch between one and eight probes.");
                }
                var pilot = Pilot(world, catalog, pilotId, now);
                int system = SystemId(pilot, catalog);
                var module = world.Set<OwnedItem>().Find(moduleId);
                if (
                    module is not { Online: true, Singleton: true }
                    || module.OwnerId != pilotId
                    || module.LocationId != pilot.ActiveShipId
                    || !InventoryDefinitions.IsHighSlot(module.FlagId)
                    || !ProbeCatalog.Launcher(catalog, module.TypeId, catalog.RequireItem(module.TypeId).GroupId)
                )
                {
                    throw new InvalidDataException("Use an online probe launcher fitted to your active ship.");
                }
                PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
                PilotFittingStore.CheckHull(catalog, module.TypeId, pilot.ActiveShip.TypeId);
                NaniteRepairPersistence.RequireAvailable(world, catalog, moduleId, now);
                var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
                if (cooldown?.ReadyAt > now)
                {
                    throw new InvalidDataException("Wait for the probe launcher cycle or reload to finish.");
                }
                var charge = PilotAmmunitionStore.Contents(world, moduleId);
                if (
                    charge is null
                    || charge.OwnerId != pilotId
                    || !ProbeCatalog.Probe(catalog, charge.TypeId, catalog.RequireItem(charge.TypeId).GroupId)
                    || !ProbeCatalog.Compatible(catalog.FittingAttributes(module.TypeId), catalog.FittingAttributes(charge.TypeId))
                )
                {
                    throw new InvalidDataException("Load compatible scanner probes into the launcher.");
                }
                PilotSkillReader.CheckRequirements(world, catalog, pilotId, charge.TypeId);
                world.RemoveRange(world.Set<SavedScanProbe>().Where(p => p.PilotId == pilotId && p.ExpiresAt <= now));
                int deployed = world.Set<SavedScanProbe>().Count(p => p.PilotId == pilotId && p.SystemId == system && p.ExpiresAt > now);
                int quantity = Math.Min(count, Math.Min(charge.Quantity, ProbeScanRules.MaximumProbes - deployed));
                if (quantity <= 0)
                {
                    throw new InvalidDataException("No probe capacity remains in this system.");
                }
                var fit = FittingCalculation.Read(world, catalog, pilot);
                var profile = ProbeCatalog.FittedProfile(catalog, fit, charge.Id, charge.TypeId, pilot.ActiveShip.TypeId);
                ProbeScanRules.Validate(new(0, SpacePosition.Zero, 1), profile);
                if (!double.IsFinite(profile.LifetimeMs) || profile.LifetimeMs <= 0)
                {
                    throw new InvalidDataException("The probe has no valid flight lifetime.");
                }
                var position = pilot.ActiveShip.Position!;
                long next = Math.Max(
                    ProbeScanRules.FirstProbeId,
                    world
                        .Database.SqlQueryRaw<long>("SELECT COALESCE(MAX(seq),0) AS Value FROM sqlite_sequence WHERE name='WorldScanProbe'")
                        .AsEnumerable()
                        .Single() + 1
                );
                List<ScanProbe> launched = [];
                for (int i = 0; i < quantity; i++)
                {
                    var probe = world
                        .Add(
                            new SavedScanProbe
                            {
                                Id = next++,
                                PilotId = pilotId,
                                SystemId = system,
                                TypeId = charge.TypeId,
                                X = position.X,
                                Y = position.Y,
                                Z = position.Z,
                                ExpiresAt = checked(now + (long)Math.Ceiling(profile.LifetimeMs * TimeSpan.TicksPerMillisecond)),
                                ProfileJson = JsonSerializer.Serialize(profile),
                            }
                        )
                        .Entity;
                    launched.Add(Read(probe));
                }
                charge.Quantity -= quantity;
                var magazine = PilotAmmunitionStore.Read(world, module, charge);
                if (charge.Quantity == 0)
                {
                    world.Remove(charge);
                }
                cooldown ??= world.Add(new ModuleCooldown { ModuleId = moduleId }).Entity;
                cooldown.Kind = ModuleCooldownKind.Cycle;
                cooldown.ReadyAt = checked(
                    now + (long)Math.Ceiling(fit.Graph.Read(moduleId, DogmaAttributeId.WeaponCycleTime) * TimeSpan.TicksPerMillisecond)
                );
                return new ProbeLaunch(launched, magazine with { ReadyAt = cooldown.ReadyAt });
            }
        );

    private static SavedScanProbe[] Owned(WorldDbContext world, long pilotId, int systemId, IReadOnlyList<long> ids, long now)
    {
        if (ids.Count is < 1 or > ProbeScanRules.MaximumProbes || ids.Distinct().Count() != ids.Count)
        {
            throw new InvalidDataException("Select distinct deployed probes.");
        }
        var probes = world
            .Set<SavedScanProbe>()
            .Where(p => ids.Contains(p.Id) && p.PilotId == pilotId && p.SystemId == systemId && p.ExpiresAt > now)
            .OrderBy(p => p.Id)
            .ToArray();
        if (probes.Length != ids.Count)
        {
            throw new InvalidDataException("These probes are not available to your pilot in this system.");
        }
        return probes;
    }

    private static void Idle(WorldDbContext world, long pilotId, long now)
    {
        if (world.Set<SavedProbeScan>().Find(pilotId) is { } scan && scan.ReadyAt > now)
        {
            throw new InvalidDataException("Wait for the current probe scan to finish.");
        }
    }

    public ProbeRecovery Recover(long pilotId, IReadOnlyList<long> ids, long now) =>
        Write(
            (world, catalog) =>
            {
                var pilot = Pilot(world, catalog, pilotId, now);
                Idle(world, pilotId, now);
                var probes = Owned(world, pilotId, SystemId(pilot, catalog), ids, now);
                double required = probes.Sum(p => PilotInventoryStore.Volume(catalog, p.TypeId));
                double used = InventoryPersistence.UsedVolume(world, catalog, pilotId, pilot.ActiveShipId, (int)InventoryFlag.Cargo);
                double capacity = PilotInventoryStore.HoldCapacity(world, catalog, pilotId, pilot.ActiveShipId, (int)InventoryFlag.Cargo);
                if (used + required > capacity + InventoryDefinitions.ResourceTolerance)
                {
                    throw new CargoCapacityException(capacity - used, required);
                }
                var items = probes
                    .GroupBy(p => p.TypeId)
                    .Select(g =>
                        InventoryPersistence.MergeOutput(
                            world,
                            catalog,
                            pilotId,
                            pilot.ActiveShipId,
                            (int)InventoryFlag.Cargo,
                            g.Key,
                            g.Count()
                        )
                    )
                    .ToArray();
                world.RemoveRange(probes);
                return new ProbeRecovery(ids.ToArray(), items);
            }
        );

    public void Destroy(long pilotId, long id, long now) =>
        Write(
            (world, catalog) =>
            {
                var pilot = Pilot(world, catalog, pilotId, now);
                Idle(world, pilotId, now);
                world.RemoveRange(Owned(world, pilotId, SystemId(pilot, catalog), [id], now));
                return true;
            }
        );

    public ProbeScan Begin(long pilotId, IReadOnlyList<ProbePlacement> placements, long now)
    {
        return LiveWorld.Execute(database.Path, () => BeginOwned(pilotId, placements, now), nameof(Begin));
    }

    private ProbeScan BeginOwned(long pilotId, IReadOnlyList<ProbePlacement> placements, long now)
    {
        using var owner = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            // Seed/read sites before opening the scan transaction on another connection.
            int system;
            using (WorldDbContext world = new(database.Path))
            {
                using CatalogDbContext catalog = new(database.CatalogPath);
                var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
                system = SystemId(pilot, catalog);
            }
            var signatures = sites.Signatures(system, now);
            return Write(
                (world, catalog) =>
                {
                    var pilot = Pilot(world, catalog, pilotId, now);
                    Idle(world, pilotId, now);
                    var probes = Owned(world, pilotId, system, placements.Select(p => p.Id).ToArray(), now);
                    var launcher =
                        world
                            .Set<OwnedItem>()
                            .Where(i => i.OwnerId == pilotId && i.LocationId == pilot.ActiveShipId && i.Singleton && i.Online)
                            .AsEnumerable()
                            .FirstOrDefault(i =>
                                InventoryDefinitions.IsHighSlot(i.FlagId)
                                && ProbeCatalog.Launcher(catalog, i.TypeId, catalog.RequireItem(i.TypeId).GroupId)
                            )
                        ?? throw new InvalidDataException("Fit and online a probe launcher before scanning.");
                    PilotSkillReader.CheckRequirements(world, catalog, pilotId, launcher.TypeId);
                    NaniteRepairPersistence.RequireAvailable(world, catalog, launcher.Id, now);
                    var fitting = FittingCalculation.Read(world, catalog, pilot);
                    double duration = fitting.Graph.Read(launcher.Id, DogmaAttributeId.Duration);
                    if (!double.IsFinite(duration) || duration <= 0)
                    {
                        throw new InvalidDataException("The launcher has no scan duration.");
                    }
                    long ready = checked(now + (long)Math.Ceiling(duration * TimeSpan.TicksPerMillisecond));
                    var profiles = probes
                        .Select(p => p.TypeId)
                        .Distinct()
                        .ToDictionary(
                            type => type,
                            type =>
                                ProbeCatalog.FittedProfile(
                                    catalog,
                                    FittingCalculation.Read(world, catalog, pilot, ownerTypeId: type),
                                    FittingCalculation.OwnerTargetId,
                                    type,
                                    pilot.ActiveShip.TypeId
                                )
                        );
                    foreach (var probe in probes)
                    {
                        PilotSkillReader.CheckRequirements(world, catalog, pilotId, probe.TypeId);
                        probe.ProfileJson = JsonSerializer.Serialize(profiles[probe.TypeId]);
                    }
                    var pairs = probes.Select(p => (Probe: Read(p), Placement: placements.Single(x => x.Id == p.Id))).ToArray();
                    foreach (var pair in pairs)
                    {
                        ProbeScanRules.Validate(pair.Placement, pair.Probe.Profile);
                        if (pair.Probe.ExpiresAt <= ready)
                        {
                            throw new InvalidDataException("A selected probe expires before this scan can complete.");
                        }
                    }
                    var results = signatures.Select(site => ProbeScanRules.Scan(site, pairs)).OfType<ProbeScanResult>().ToArray();
                    foreach (var probe in probes)
                    {
                        var destination = placements.Single(p => p.Id == probe.Id).Destination;
                        probe.X = destination.X;
                        probe.Y = destination.Y;
                        probe.Z = destination.Z;
                    }
                    ProbeScan scan = new(pilot.ActiveShipId, system, now, ready, probes.Select(Read).ToArray(), results);
                    var saved = world.Set<SavedProbeScan>().Find(pilotId) ?? world.Add(new SavedProbeScan { PilotId = pilotId }).Entity;
                    saved.StartedAt = now;
                    saved.ReadyAt = ready;
                    saved.ScanJson = JsonSerializer.Serialize(scan, s_json);
                    return scan;
                }
            );
        }
    }

    public IReadOnlyList<ProbeScanResult> Complete(long pilotId, ProbeScan scan, long now) =>
        Write(
            (world, catalog) =>
            {
                var saved = world.Set<SavedProbeScan>().Find(pilotId);
                if (saved is null || saved.StartedAt != scan.StartedAt || saved.ReadyAt > now)
                {
                    return (IReadOnlyList<ProbeScanResult>)Array.Empty<ProbeScanResult>();
                }
                var authoritative = JsonSerializer.Deserialize<ProbeScan>(saved.ScanJson)!;
                world.Remove(saved);
                var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
                if (
                    pilot.ActiveShipId != authoritative.ShipId
                    || pilot.ActiveShip.Position is null
                    || SystemId(pilot, catalog) != authoritative.SystemId
                )
                {
                    return Array.Empty<ProbeScanResult>();
                }
                var ids = authoritative.Probes.Select(p => p.Id).ToArray();
                if (world.Set<SavedScanProbe>().Count(p => ids.Contains(p.Id) && p.PilotId == pilotId && p.ExpiresAt > now) != ids.Length)
                {
                    return Array.Empty<ProbeScanResult>();
                }
                var results = authoritative
                    .Results.Where(r =>
                        r.Site.Wormhole && WormholeTravelStore.Exists(catalog, authoritative.SystemId, r.Site.Id)
                        || world
                            .Set<WorldCombatAnomaly>()
                            .Any(s => s.Id == r.Site.Id && s.CompletedAt == null && s.IsSignature && s.SystemId == authoritative.SystemId)
                        || world
                            .Set<SavedExplorationSite>()
                            .Any(s => s.Id == r.Site.Id && s.SystemId == authoritative.SystemId && s.ReplaceAt == null && s.ExpiresAt > now)
                        || world
                            .Set<SavedMiningSite>()
                            .Any(s =>
                                s.Id == r.Site.Id
                                && s.SystemId == authoritative.SystemId
                                && s.Family == Eden.Gameplay.Mining.MiningFamily.Gas
                                && s.ReplaceAt == null
                                && s.ExpiresAt > now
                            )
                    )
                    .ToArray();
                foreach (var result in results.Where(r => r.Certainty >= 1))
                {
                    if (world.Set<SavedResolvedSignature>().Find(pilotId, result.Site.Id) is null)
                    {
                        world.Add(
                            new SavedResolvedSignature
                            {
                                PilotId = pilotId,
                                SiteId = result.Site.Id,
                                SystemId = authoritative.SystemId,
                            }
                        );
                    }
                }
                return results;
            }
        );

    public bool Resolved(long pilotId, long siteId, int systemId, long now)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return world.Set<SavedResolvedSignature>().Any(r => r.PilotId == pilotId && r.SiteId == siteId && r.SystemId == systemId)
            && (
                WormholeRules.IsEndpoint(siteId) && WormholeTravelStore.Exists(catalog, systemId, siteId)
                || world
                    .Set<WorldCombatAnomaly>()
                    .Any(s => s.Id == siteId && s.SystemId == systemId && s.IsSignature && s.CompletedAt == null)
                || world
                    .Set<SavedExplorationSite>()
                    .Any(s => s.Id == siteId && s.SystemId == systemId && s.ReplaceAt == null && s.ExpiresAt > now)
                || world
                    .Set<SavedMiningSite>()
                    .Any(s =>
                        s.Id == siteId
                        && s.SystemId == systemId
                        && s.Family == Eden.Gameplay.Mining.MiningFamily.Gas
                        && s.ReplaceAt == null
                        && s.ExpiresAt > now
                    )
            );
    }
}
