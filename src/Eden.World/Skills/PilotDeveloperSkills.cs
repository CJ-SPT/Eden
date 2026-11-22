using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Skills;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Skills;

internal sealed partial class PilotTrainingStore
{
    // Build 3662168 appConst.skillEventSkillRemoved.
    private const int GameMasterSkillRemoved = 177;

    public DeveloperSkillResult Execute(long caller, string target, DeveloperSkillAction action)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Developer skill changes require a running world.");
        using var selection = live.EnterPersistence("developer skill participants", TransactionParticipants.None);
        var (pilot, source) = live.Economy.Inspect(world =>
            (
                ResolvePilot(world, caller, target),
                action is DeveloperSkillAction.CopyFrom copy ? ResolvePilot(world, caller, copy.Source) : (long?)null
            )
        );
        using var world = live.Economy.Begin(new(pilots: source is long from ? [pilot, from] : [pilot]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        DeveloperSkillResult changed;
        if (action is DeveloperSkillAction.Remove remove)
        {
            changed = RemoveSkills(world, catalog, pilot, remove.Skill, now);
        }
        else
        {
            bool includeUnpublished =
                action is DeveloperSkillAction.CopyFrom
                || action is DeveloperSkillAction.Give giveAll && giveAll.Skill.Equals("super", StringComparison.OrdinalIgnoreCase);
            var grants = action switch
            {
                DeveloperSkillAction.Give give => GrantTargets(catalog, give),
                DeveloperSkillAction.Starting => StarterTargets(world.Pilots.Find(pilot)!, catalog),
                DeveloperSkillAction.CopyFrom => CompletedLevels(world, catalog, source!.Value, now),
                _ => throw new InvalidDataException("Unknown developer skill action."),
            };
            var result = UpdateCore(
                world,
                pilot,
                null,
                false,
                false,
                null,
                new(null, Grant: grants, IncludeUnpublished: includeUnpublished),
                false,
                now
            );
            changed = new(pilot, result.Points!.Remaining, result.PointRevision, result.Training, []);
        }
        world.Commit();
        return changed;
    }

    private DeveloperSkillResult RemoveSkills(EconomyTransaction world, CatalogDbContext catalog, long pilot, string skill, long now)
    {
        int? type = skill.Equals("all", StringComparison.OrdinalIgnoreCase) ? null : SkillType(catalog, skill);
        var removing = world.Filter<PilotSkill>(row => row.PilotId == pilot && (type == null || row.TypeId == type)).ToArray();
        var state = UpdateCore(world, pilot, removing.Length == 0 ? null : [], false, false, null, null, false, now);
        long balance = ReadFreePoints(world, pilot);
        foreach (var row in removing)
        {
            world.Remove(row);
            Record(world, pilot, now, row.TypeId, 0, GameMasterSkillRemoved);
        }
        if (removing.Length != 0 || state.Training.ChangedSkills.Count != 0)
        {
            SavePointBalance(world, pilot, balance);
        }
        world.SaveChanges();
        var removed = removing.Select(row => row.TypeId).ToArray();
        var training = state.Training with { ChangedSkills = state.Training.ChangedSkills.Concat(removed).Distinct().ToArray() };
        return new(pilot, balance, world.Set<SavedFreeSkillPoints>().Find(pilot)?.Revision ?? 0, training, removed);
    }

    private static long ResolvePilot(WorldAction world, long caller, string selector)
    {
        if (selector.Equals("me", StringComparison.OrdinalIgnoreCase))
        {
            return caller;
        }
        if (long.TryParse(selector, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && world.Pilots.Find(id) is not null)
        {
            return id;
        }
        var matching = world
            .Pilots.AsEnumerable()
            .Where(pilot => pilot.Name.Equals(selector, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matching is [var pilot] ? pilot.Id : throw new InvalidDataException("Choose me, a pilot ID, or an exact pilot name.");
    }

    private static int SkillType(CatalogDbContext catalog, string selector)
    {
        if (
            int.TryParse(selector, NumberStyles.None, CultureInfo.InvariantCulture, out int id)
            && catalog.FittingType(id)?.CategoryId == (int)InventoryCategory.Skill
        )
        {
            return id;
        }
        var matches = catalog
            .Definitions.Items.Values.Where(type =>
                type.CategoryId == (int)InventoryCategory.Skill && type.Name.Equals(selector, StringComparison.OrdinalIgnoreCase)
            )
            .Take(2)
            .ToArray();
        return matches is [var skill] ? skill.Id : throw new InvalidDataException("Choose an exact skill name or skill type ID.");
    }

    private static TrainingTarget[] GrantTargets(CatalogDbContext catalog, DeveloperSkillAction.Give give)
    {
        if (give.Level is < 0 or > 5)
        {
            throw new InvalidDataException("Choose a skill level from zero to five.");
        }
        string selector = give.Skill.ToLowerInvariant();
        if (selector is not ("all" or "super"))
        {
            return [new(SkillType(catalog, give.Skill), give.Level)];
        }
        return catalog
            .Definitions.Items.Values.Where(type =>
                type.CategoryId == (int)InventoryCategory.Skill
                && (selector == "super" || catalog.Read(CatalogData.Client.Types, (long)type.Id)?.Published == 1)
                && catalog.FittingAttributes(type.Id).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant) > 0
            )
            .OrderBy(type => type.Id)
            .Select(type => new TrainingTarget(type.Id, give.Level))
            .ToArray();
    }

    private static TrainingTarget[] StarterTargets(Pilot owner, CatalogDbContext catalog)
    {
        var skills =
            catalog.Require(CatalogData.Client.Races, (long)owner.RaceId).Skills
            ?? throw new InvalidDataException("The race has no starting skills in the catalog.");
        return skills
            .Select(skill => new TrainingTarget(int.Parse(skill.Key, CultureInfo.InvariantCulture), checked((int)skill.Value)))
            .ToArray();
    }

    private TrainingTarget[] CompletedLevels(EconomyTransaction world, CatalogDbContext catalog, long source, long now)
    {
        _ = Advance(world, source, now);
        return world
            .Filter<PilotSkill>(row => row.PilotId == source)
            .AsEnumerable()
            .Select(row =>
            {
                double rank = catalog.FittingAttributes(row.TypeId).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant);
                return new TrainingTarget(row.TypeId, new TrainedSkill(row.TypeId, row.Points, rank).Level);
            })
            .ToArray();
    }
}
