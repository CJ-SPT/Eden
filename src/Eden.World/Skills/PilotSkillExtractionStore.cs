using System.Globalization;
using System.Text.Json;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Skills;

public sealed class SavedSkillExtraction
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long ItemId { get; set; }
    public long Time { get; set; }
    public string AllocationsJson { get; set; } = "[]";
}

internal sealed record SkillExtractionResult(TrainingState Training, IReadOnlyList<SkillPointAllocation> Allocations);

internal sealed partial class PilotTrainingStore
{
    internal SkillExtractionResult Extract(
        WorldDbContext world,
        long pilotId,
        IReadOnlyList<SkillExtractionRequest> requests,
        IReadOnlyList<SkillExtractionFloor> floors,
        long now
    )
    {
        var state = Advance(world, pilotId, now);
        HashSet<int> changed = state.ChangedSkills.ToHashSet();
        if (state.Queue.FirstOrDefault() is { StartTime: not null } head)
        {
            world.Set<PilotSkill>().Find(pilotId, head.Target.TypeId)!.Points = head.PointsAt(now);
            changed.Add(head.Target.TypeId);
            world.SaveChanges();
        }
        var targets = world.Set<PilotSkill>().Where(s => s.PilotId == pilotId).Select(s => new TrainingTarget(s.TypeId, 5)).ToArray();
        var allocations = SkillExtractionRules.Plan(
            requests,
            ReadInputs(world, pilotId, targets, now),
            state.Queue.Select(e => e.Target).ToArray(),
            floors
        );
        foreach (var allocation in allocations)
        {
            // Zero points retains the injected skill; None means uninjected.
            world.Set<PilotSkill>().Find(pilotId, allocation.TypeId)!.Points = allocation.After;
            changed.Add(allocation.TypeId);
            Record(world, pilotId, now, allocation.TypeId, allocation.After, SkillExtractionRules.HistoryEvent);
        }
        world.SaveChanges();
        // Extraction cannot touch queued skills or their prerequisites, and retains
        // at least Alpha's entire passive-SP cap. Preserve the existing schedule,
        // including fractional progress and future booster-expiration periods.
        return new(new(state.Queue, changed.ToArray()), allocations);
    }
}

[Injectable]
internal sealed class PilotSkillExtractionStore(WorldDatabase database, PilotTrainingStore training, TimeProvider clock)
    : IPilotSkillExtraction
{
    private static long Revision(WorldDbContext world, long pilotId) =>
        world.Set<SavedSkillExtraction>().Where(e => e.PilotId == pilotId).Max(e => (long?)e.Id) ?? 0;

    public long Revision(long pilotId)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("read.extraction");
        return LiveReadModels.Find(database.Path) is { } resident
            ? resident.Extraction(pilotId, () => RevisionSql(pilotId))
            : RevisionSql(pilotId);
    }

    private long RevisionSql(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        return Revision(world, pilotId);
    }

    public SkillExtractionState Snapshot(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var state = training.Advance(world, pilotId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        var ids = world.Set<PilotSkill>().Where(s => s.PilotId == pilotId).Select(s => s.TypeId).ToArray();
        long revision = Revision(world, pilotId);
        transaction.Commit();
        return new(revision, new(state.Queue, ids));
    }

    private static IReadOnlyList<SkillExtractionFloor> Restrictions(WorldDbContext world, CatalogDbContext catalog, long pilotId)
    {
        List<SkillExtractionFloor> floors = [];
        int clones = world.Set<SavedJumpClone>().Count(c => c.PilotId == pilotId && !c.Active);
        const int LevelsPerSkill = 5;
        var cloneSkills = new[] { InfomorphSkill.Psychology, InfomorphSkill.AdvancedPsychology, InfomorphSkill.ElitePsychology };
        for (int i = 0; i < cloneSkills.Length; i++)
        {
            int required = Math.Clamp(clones - i * LevelsPerSkill, 0, LevelsPerSkill);
            if (required > 0)
            {
                floors.Add(new((int)cloneSkills[i], required, "That skill level is required by an installed jump clone."));
            }
        }
        var implantTypes = (
            from implant in world.Set<SavedPilotImplant>()
            join body in world.Set<SavedJumpClone>() on implant.CloneId equals body.Id
            where body.PilotId == pilotId
            select implant.TypeId
        )
            .Distinct()
            .ToArray();
        HashSet<int> visited = [];
        foreach (int type in implantTypes)
        {
            ImplantRequirements(type);
        }
        return floors;

        void ImplantRequirements(int type)
        {
            if (!visited.Add(type))
            {
                return;
            }
            string key = type.ToString(CultureInfo.InvariantCulture);
            var required = catalog.Set<SourceRecord>().Find("client/requiredskillsfortypes", key)?.Read<Dictionary<string, long>>();
            var attributes = catalog.Set<TypeAttribute>().Where(a => a.ItemTypeId == type).ToDictionary(a => a.AttributeId, a => a.Value);
            Dictionary<int, int> levels = [];
            if (required is not null)
            {
                foreach (var pair in required)
                {
                    levels[int.Parse(pair.Key, CultureInfo.InvariantCulture)] = checked((int)pair.Value);
                }
            }
            foreach (var (skill, level) in InventoryDefinitions.RequiredSkills)
            {
                double id = attributes.GetValueOrDefault((int)skill);
                if (id == 0)
                {
                    continue;
                }
                double value = attributes.GetValueOrDefault((int)level);
                if (
                    !double.IsFinite(id)
                    || id is < 1 or > int.MaxValue
                    || id != Math.Truncate(id)
                    || !double.IsFinite(value)
                    || value is < 1 or > 5
                    || value != Math.Truncate(value)
                )
                {
                    throw new InvalidDataException("Invalid implant skill requirement.");
                }
                int skillId = checked((int)id);
                levels[skillId] = Math.Max(levels.GetValueOrDefault(skillId), checked((int)value));
            }
            foreach (var (skill, level) in levels)
            {
                floors.Add(new(skill, level, "That skill level is required by an implant in one of your clones."));
                ImplantRequirements(skill);
            }
        }
    }

    public SkillExtractionChange Extract(long pilotId, long itemId, IReadOnlyList<SkillExtractionRequest> requests)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        if (pilot.ActiveShip.Position is not null)
        {
            throw new InvalidSkillExtraction("SkillExtractorNotDockedInStation");
        }
        if (catalog.Set<ItemType>().Find(pilot.ActiveShip.TypeId)?.GroupId != FleetDefinitions.CapsuleGroup)
        {
            throw new InvalidSkillExtraction("You must be in a capsule to extract skill points.");
        }
        var item =
            InventoryPersistence.OwnedStack(world, pilotId, itemId)
            ?? throw new InvalidSkillExtraction("You do not own that skill extractor.");
        if (item.LocationId != pilot.ActiveShip.StationId || item.FlagId != (int)InventoryFlag.Hangar)
        {
            throw new InvalidSkillExtraction("Select a skill extractor in your current station hangar.");
        }
        if (
            item.TypeId != SkillExtractionRules.ExtractorType
            || item.Quantity < 1
            || item.Singleton
            || catalog
                .Set<SourceRecord>()
                .Find("client/types", item.TypeId.ToString(CultureInfo.InvariantCulture))
                ?.Read<ClientTypes>()
                .Published != 1
        )
        {
            throw new InvalidSkillExtraction("Select a published skill extractor.");
        }
        if (
            catalog.Set<ItemType>().Find((int)SkillInjectorType.Large)?.GroupId != SkillInjectorRules.Group
            || catalog
                .Set<SourceRecord>()
                .Find("client/types", ((int)SkillInjectorType.Large).ToString(CultureInfo.InvariantCulture))
                ?.Read<ClientTypes>()
                .Published != 1
        )
        {
            throw new InvalidDataException("The large skill injector catalog profile is unavailable.");
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var result = training.Extract(world, pilotId, requests, Restrictions(world, catalog, pilotId), now);
        var previous = PilotInventoryStore.Read(catalog, item);
        item.Quantity--;
        if (item.Quantity == 0)
        {
            world.Remove(item);
        }
        var output = InventoryPersistence.MergeOutput(
            world,
            catalog,
            pilotId,
            pilot.ActiveShip.StationId,
            (int)InventoryFlag.Hangar,
            (int)SkillInjectorType.Large,
            1
        );
        var receipt = world
            .Add(
                new SavedSkillExtraction
                {
                    PilotId = pilotId,
                    ItemId = itemId,
                    Time = now,
                    AllocationsJson = JsonSerializer.Serialize(result.Allocations),
                }
            )
            .Entity;
        world.SaveChanges();
        transaction.Commit();
        return new(receipt.Id, result.Training, [new(PilotInventoryStore.Read(catalog, item), previous), output]);
    }
}
