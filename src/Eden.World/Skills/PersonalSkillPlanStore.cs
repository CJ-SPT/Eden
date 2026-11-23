using System.Buffers.Binary;
using System.Collections.Immutable;
using Eden.Data.World.Characters;
using Eden.Data.World.Skills;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Skills;

[Injectable]
internal sealed class PersonalSkillPlanStore(WorldDatabase database) : IPersonalSkillPlans
{
    public IReadOnlyList<Guid> List(SkillPlanOwner owner) => ReadBook(owner, book => book.Plans.Select(plan => plan.Id).ToArray());

    public SkillPlanDetails Read(SkillPlanOwner owner, Guid planId) => ReadBook(owner, book => Details(RequirePlan(book, planId)));

    public Guid Create(SkillPlanOwner owner, SkillPlanDetails details) =>
        Update(
            owner,
            book =>
            {
                if (book.Plans.Length >= SkillPlanRules.MaxPlans)
                {
                    throw new InvalidDataException($"You can save up to {SkillPlanRules.MaxPlans} personal skill plans.");
                }
                string name = SkillPlanRules.NormalizeName(details.Name);
                SkillPlanRules.CheckDescription(details.Description);
                SavedPersonalSkillPlan plan = new(Guid.NewGuid(), name, details.Description, ReadSkills(details.Skills), []);
                return (book with { Plans = book.Plans.Add(plan) }, plan.Id);
            }
        );

    public void Rename(SkillPlanOwner owner, Guid planId, string name) =>
        EditPlan(owner, planId, plan => plan with { Name = SkillPlanRules.NormalizeName(name) });

    public void Describe(SkillPlanOwner owner, Guid planId, string description) =>
        EditPlan(
            owner,
            planId,
            plan =>
            {
                SkillPlanRules.CheckDescription(description);
                return plan with { Description = description };
            }
        );

    public void SetSkills(SkillPlanOwner owner, Guid planId, IReadOnlyList<SkillPlanSkill> skills) =>
        EditPlan(owner, planId, plan => plan with { Skills = ReadSkills(skills) });

    public void Delete(SkillPlanOwner owner, Guid planId) =>
        Update(
            owner,
            book =>
                book.Plans.FirstOrDefault(plan => plan.Id == planId) is { } plan
                    ? book with
                    {
                        Plans = book.Plans.Remove(plan),
                        TrackedPlanId = book.TrackedPlanId == planId ? Guid.Empty : book.TrackedPlanId,
                    }
                    : book
        );

    public TrackedSkillPlan ReadTracked(SkillPlanOwner owner) =>
        ReadBook(
            owner,
            book => new TrackedSkillPlan(
                book.TrackedPlanId,
                book.Plans.FirstOrDefault(plan => plan.Id == book.TrackedPlanId) is { } plan ? Details(plan) : null
            )
        );

    public void Track(SkillPlanOwner owner, Guid planId) =>
        Update(
            owner,
            book =>
            {
                if (planId != Guid.Empty && !book.Plans.Any(plan => plan.Id == planId) && !IsCertifiedPlan(planId))
                {
                    throw new KeyNotFoundException("Skill plan not found.");
                }
                return book with { TrackedPlanId = planId };
            }
        );

    public IReadOnlyList<PersonalSkillMilestone> Milestones(SkillPlanOwner owner, Guid planId) =>
        ReadBook(owner, book => RequirePlan(book, planId).Milestones.Select(milestone => Milestone(planId, milestone)).ToArray());

    public Guid AddMilestone(SkillPlanOwner owner, Guid planId, SkillPlanMilestoneTarget target, string description) =>
        Update(
            owner,
            book =>
            {
                var plan = RequirePlan(book, planId);
                SkillPlanRules.CheckDescription(description);
                var existing = plan.Milestones.FirstOrDefault(milestone =>
                    milestone.TypeId == target.TypeId && milestone.Level == target.Level
                );
                if (existing is not null)
                {
                    return (book, existing.Id);
                }
                if (plan.Milestones.Length >= SkillPlanRules.MaxMilestones)
                {
                    throw new InvalidDataException($"A skill plan can have up to {SkillPlanRules.MaxMilestones} milestones.");
                }
                using CatalogDbContext catalog = new(database.CatalogPath);
                CheckTarget(catalog, target.TypeId, target.Level);
                SavedSkillPlanMilestone milestone = new(Guid.NewGuid(), target.TypeId, target.Level, description);
                return (ReplacePlan(book, plan, plan with { Milestones = plan.Milestones.Add(milestone) }), milestone.Id);
            }
        );

    public void DescribeMilestone(SkillPlanOwner owner, Guid milestoneId, string description) =>
        Update(
            owner,
            book =>
            {
                var plan = FindMilestonePlan(book, milestoneId) ?? throw new KeyNotFoundException("Skill plan milestone not found.");
                var milestone = plan.Milestones.First(milestone => milestone.Id == milestoneId);
                SkillPlanRules.CheckDescription(description);
                var milestones = plan.Milestones.Replace(milestone, milestone with { Description = description });
                return ReplacePlan(book, plan, plan with { Milestones = milestones });
            }
        );

    public void DeleteMilestone(SkillPlanOwner owner, Guid milestoneId) =>
        Update(
            owner,
            book =>
            {
                if (FindMilestonePlan(book, milestoneId) is not { } plan)
                {
                    return book;
                }
                return ReplacePlan(
                    book,
                    plan,
                    plan with
                    {
                        Milestones = plan.Milestones.RemoveAll(milestone => milestone.Id == milestoneId),
                    }
                );
            }
        );

    internal static void RemoveForPilot(WorldAction world, long pilotId)
    {
        if (world.Set<SavedPersonalSkillPlans>().Find(pilotId) is { } saved)
        {
            world.Remove(saved);
        }
    }

    private LiveWorld Resident =>
        LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Personal skill plans require the resident world.");

    private T ReadBook<T>(SkillPlanOwner owner, Func<SavedSkillPlanBook, T> read) =>
        Resident.Economy.Inspect(
            world =>
            {
                RequireOwner(world, owner);
                return read(world.Set<SavedPersonalSkillPlans>().Find(owner.PilotId)?.Book ?? SavedSkillPlanBook.Empty);
            },
            new(pilots: [owner.PilotId])
        );

    private void Update(SkillPlanOwner owner, Func<SavedSkillPlanBook, SavedSkillPlanBook> change) =>
        Update(owner, book => (change(book), true));

    private T Update<T>(SkillPlanOwner owner, Func<SavedSkillPlanBook, (SavedSkillPlanBook Book, T Result)> change)
    {
        using var world = Resident.Economy.Begin(new(pilots: [owner.PilotId]));
        RequireOwner(world, owner);
        var saved = world.Set<SavedPersonalSkillPlans>().Find(owner.PilotId);
        var book = saved?.Book ?? SavedSkillPlanBook.Empty;
        var (next, result) = change(book);
        if (next != book)
        {
            if (next.TrackedPlanId == Guid.Empty && next.Plans.IsEmpty)
            {
                if (saved is not null)
                {
                    world.Remove(saved);
                }
            }
            else
            {
                (saved ?? world.Add(new SavedPersonalSkillPlans { PilotId = owner.PilotId }).Entity).Book = next;
            }
        }
        world.Commit();
        return result;
    }

    private void EditPlan(SkillPlanOwner owner, Guid planId, Func<SavedPersonalSkillPlan, SavedPersonalSkillPlan> edit) =>
        Update(
            owner,
            book =>
            {
                var plan = RequirePlan(book, planId);
                return ReplacePlan(book, plan, edit(plan));
            }
        );

    private bool IsCertifiedPlan(Guid planId)
    {
        Span<byte> bytes = stackalloc byte[16];
        planId.TryWriteBytes(bytes, bigEndian: true, out _);
        long id = BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
        if (BinaryPrimitives.ReadUInt64BigEndian(bytes) != 0 || id <= 0)
        {
            return false;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return catalog.Read(CatalogData.Client.Skillplans, id) is not null;
    }

    private ImmutableArray<SavedSkillPlanSkill> ReadSkills(IReadOnlyList<SkillPlanSkill> skills)
    {
        if (skills.Count > SkillPlanRules.MaxSkills)
        {
            throw new InvalidDataException($"A skill plan can have up to {SkillPlanRules.MaxSkills} skill entries.");
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var entries = ImmutableArray.CreateBuilder<SavedSkillPlanSkill>(skills.Count);
        foreach (var skill in skills)
        {
            CheckTarget(catalog, skill.TypeId, skill.Level);
            entries.Add(new(skill.TypeId, skill.Level));
        }
        return entries.MoveToImmutable();
    }

    private static void CheckTarget(CatalogDbContext catalog, int typeId, int? level)
    {
        if (
            typeId <= 0
            || catalog.FittingType(typeId) is not { } type
            || catalog.Read(CatalogData.Client.Types, (long)typeId)?.Published != 1
            || level is int skillLevel && (type.CategoryId != (int)InventoryCategory.Skill || !SkillPlanRules.ValidSkillLevel(skillLevel))
        )
        {
            throw new InvalidDataException("Choose a published item or a published skill at level 1 to 5.");
        }
    }

    private static void RequireOwner(WorldAction world, SkillPlanOwner owner)
    {
        if (
            owner.AccountId <= 0
            || world.Pilots.Find(owner.PilotId)?.AccountId != owner.AccountId
            || world.Set<SavedCharacterDeletion>().Find(owner.PilotId)?.DeletedAt is not null
        )
        {
            throw new UnauthorizedAccessException("This account does not own the selected pilot.");
        }
    }

    private static SavedPersonalSkillPlan RequirePlan(SavedSkillPlanBook book, Guid planId) =>
        book.Plans.FirstOrDefault(plan => plan.Id == planId) ?? throw new KeyNotFoundException("Skill plan not found.");

    private static SavedPersonalSkillPlan? FindMilestonePlan(SavedSkillPlanBook book, Guid milestoneId) =>
        book.Plans.FirstOrDefault(plan => plan.Milestones.Any(milestone => milestone.Id == milestoneId));

    private static SavedSkillPlanBook ReplacePlan(SavedSkillPlanBook book, SavedPersonalSkillPlan before, SavedPersonalSkillPlan after) =>
        book with
        {
            Plans = book.Plans.Replace(before, after),
        };

    private static SkillPlanDetails Details(SavedPersonalSkillPlan plan) =>
        new(plan.Name, plan.Description, plan.Skills.Select(skill => new SkillPlanSkill(skill.TypeId, skill.Level)).ToArray());

    private static PersonalSkillMilestone Milestone(Guid planId, SavedSkillPlanMilestone milestone) =>
        new(milestone.Id, planId, new(milestone.TypeId, milestone.Level), milestone.Description);
}
