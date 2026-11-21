using System.Collections.Frozen;
using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Market;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;

namespace Eden.World.Capsuleers;

internal readonly record struct CapsuleerTrainingTurn(CapsuleerCareerPlan? Plan, bool MarketChanged = false, bool LevelChanged = false);

internal readonly record struct CapsuleerQualificationTurn(bool Progressed, bool MarketChanged);

[Injectable]
internal sealed class CapsuleerCareerTraining(PilotTrainingStore training, PilotMarketStore market)
{
    private const int SkillbookBudgetDivisor = 4;
    private static readonly CatalogDefinition<TrainingTarget[]?> s_qualifications = new("career-skill", LoadQualification);
    private static readonly CatalogDefinition<TrainingTarget[]?> s_shipPlans = new("career-ship-prerequisites", LoadShipPlan);
    private static readonly CatalogDefinition<int[]> s_ships = new("career-ships", LoadShips);
    private static readonly CatalogDefinition<TrainingTarget[]> s_foundation = new(
        "career-foundation",
        (catalog, _) => ExpandAvailable(catalog, CapsuleerCareerTrainingRules.Foundation)
    );
    private static readonly CatalogDefinition<IReadOnlyDictionary<(CapsuleerProfession, bool, bool), TrainingTarget[]>> s_work = new(
        "career-work",
        (catalog, _) =>
            (
                from profession in Enum.GetValues<CapsuleerProfession>()
                from factory in new[] { false, true }
                from manager in new[] { false, true }
                select (profession, factory, manager)
            ).ToDictionary(
                role => role,
                role => ExpandAvailable(catalog, CapsuleerCareerTrainingRules.Work(role.profession, role.factory, role.manager))
            )
    );

    internal static IReadOnlyList<int> Ships(CatalogDbContext catalog, CapsuleerProfession profession) =>
        catalog.Definition(s_ships, (int)profession);

    internal static void PrepareChoices(CatalogDbContext catalog, int hull, CapsuleerProfession profession, CapsuleerCareerPlan? saved)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("traffic.prepareCareerChoices");
        var (stage, preferred) = PreferredShip(catalog, hull, profession, saved);
        if (preferred == 0)
        {
            _ = catalog.Definition(s_careerChoices[profession], stage + 1);
        }
    }

    internal static void PrepareFreshChoices(CatalogDbContext catalog, CapsuleerProfession profession) =>
        _ = catalog.Definition(s_careerFitPlans, EntryHull(profession));

    internal CapsuleerTrainingTurn Advance(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        CapsuleerProfession profession,
        CapsuleerCareerPlan? saved,
        long now
    )
    {
        if (
            pilot.Position is not null
            || world.Pilots.Find(pilot.PilotId) is not { AccountId: CapsuleerCommunityStore.MinerAccountId } profile
            || world.Set<SavedCapsuleerAgent>().Find(pilot.PilotId) is not { } agent
        )
        {
            return new(saved);
        }
        long id = pilot.PilotId;
        bool reset = world.Settings.FreshUniverse && pilot.ShipGroupId == (int)HullClass.Corvette && saved is { HighestHullStage: > 0 };
        var plan = Choose(world, catalog, pilot, profession, !reset && saved?.Profession == profession ? saved : null);
        if (PilotTrainingStore.ReadFreePoints(world, id) == 0)
        {
            return new(plan);
        }
        var clone = catalog.Read(CatalogData.Client.Clonegrades, profile.RaceId.ToString(CultureInfo.InvariantCulture));
        if (clone?.Skills is null)
        {
            return new(plan);
        }
        bool fresh = world.Settings.FreshUniverse;
        bool primaryRole = fresh ? StarterIndustryRules.IsProducerSlot(agent.Slot) : agent.Slot == StarterIndustryRules.ProducerSlot;
        bool manager = world.Set<SavedCorporation>().Find(pilot.CorpId)?.CeoId == id || primaryRole;
        bool factory =
            profession == CapsuleerProfession.Mining
            && (profile.CorporationId >= CorporationRules.FirstCorporationId || fresh && primaryRole);
        var focus = plan.NextFocus;
        for (int turn = 0; turn < 3; turn++, focus = CapsuleerCareerTrainingRules.Next(focus))
        {
            var targets = focus switch
            {
                CapsuleerTrainingFocus.Work => catalog.Definition(s_work, 0)[(profession, factory, manager)],
                CapsuleerTrainingFocus.Ship when plan.ShipTypeId > 0 => catalog.Definition(s_careerFitPlans, plan.ShipTypeId),
                CapsuleerTrainingFocus.Ship => null,
                _ => catalog.Definition(s_foundation, 0),
            };
            if (targets is not null && Spend(world, catalog, pilot, targets, now, out bool marketChanged, out bool levelChanged))
            {
                return new(plan with { NextFocus = CapsuleerCareerTrainingRules.Next(focus) }, marketChanged, levelChanged);
            }
        }
        return new(plan);
    }

    internal CapsuleerTrainingTurn AdvanceFreshCareer(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        CapsuleerProfession profession,
        long now
    )
    {
        var plan = new CapsuleerCareerPlan(profession, EntryHull(profession), CapsuleerTrainingFocus.Ship);
        long id = pilot.PilotId;
        var state = training.Advance(world, id, now);
        bool trained = state.ChangedSkills.Count > 0;
        if (catalog.Definition(s_careerFitPlans, plan.ShipTypeId) is not { } targets)
        {
            return new(plan, LevelChanged: trained);
        }
        if (PilotTrainingStore.ReadFreePoints(world, id) > 0)
        {
            if (Spend(world, catalog, pilot, targets, now, out bool boughtBook, out bool allocatedLevel))
            {
                return new(plan, boughtBook, trained || allocatedLevel);
            }
            if (
                targets.All(target => PilotSkillReader.Level(world, catalog, id, target.TypeId) >= target.Level)
                && (
                    Spend(
                        world,
                        catalog,
                        pilot,
                        catalog.Definition(s_work, 0)[(profession, false, false)],
                        now,
                        out boughtBook,
                        out allocatedLevel
                    ) || Spend(world, catalog, pilot, catalog.Definition(s_foundation, 0), now, out boughtBook, out allocatedLevel)
                )
            )
            {
                return new(plan, boughtBook, trained || allocatedLevel);
            }
        }
        bool marketChanged = state.Queue.Count == 0 && QueueNextLevel(world, catalog, pilot, targets, now, out _);
        return new(plan, marketChanged, trained);
    }

    internal bool TrainForModule(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, int module, long now) =>
        TrainForEquipment(world, catalog, pilot, [module], now);

    internal bool TrainForEquipment(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, int[] types, long now)
    {
        if (Expand(catalog, types.SelectMany(type => RequiredSkills(catalog, type))) is not { Length: > 0 } targets)
        {
            return false;
        }
        long id = pilot.PilotId;
        if (PilotTrainingStore.ReadFreePoints(world, id) > 0 && Spend(world, catalog, pilot, targets, now, out bool bought, out _))
        {
            return bought;
        }
        return training.Advance(world, id, now).Queue.Count == 0 && QueueNextLevel(world, catalog, pilot, targets, now, out _);
    }

    internal CapsuleerQualificationTurn AdvanceQualification(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        IReadOnlyList<int> equipment,
        TrainingTarget qualification,
        long now
    )
    {
        if (
            pilot.Position is not null
            || pilot.InStructure
            || Expand(catalog, equipment.SelectMany(type => RequiredSkills(catalog, type)).Append(qualification))
                is not { Length: > 0 } targets
        )
        {
            return default;
        }
        long id = pilot.PilotId;
        var state = training.Advance(world, id, now);
        if (PilotTrainingStore.ReadFreePoints(world, id) > 0 && Spend(world, catalog, pilot, targets, now, out bool bought, out _))
        {
            return new(true, bought);
        }
        bool queued = false;
        bool marketChanged = state.Queue.Count == 0 && QueueNextLevel(world, catalog, pilot, targets, now, out queued);
        return new(queued || state.ChangedSkills.Count > 0, marketChanged);
    }

    private bool QueueNextLevel(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        IReadOnlyList<TrainingTarget> targets,
        long now,
        out bool queued
    )
    {
        queued = false;
        long id = pilot.PilotId;
        foreach (var target in targets)
        {
            int level = PilotSkillReader.Level(world, catalog, id, target.TypeId);
            if (level >= target.Level)
            {
                continue;
            }
            bool marketChanged = false;
            if (
                world.Set<PilotSkill>().Find(id, target.TypeId) is not null
                || Learn(world, catalog, pilot, target.TypeId, now, out marketChanged)
            )
            {
                queued = training.QueueCareerLevel(world, id, new(target.TypeId, level + 1), now).Queue.Count > 0;
            }
            return marketChanged;
        }
        return false;
    }

    private bool Spend(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        IReadOnlyList<TrainingTarget> targets,
        long now,
        out bool marketChanged,
        out bool levelChanged
    )
    {
        marketChanged = false;
        levelChanged = false;
        long id = pilot.PilotId;
        foreach (var target in targets)
        {
            var skill = catalog.Definition(s_qualifications, target.TypeId);
            int current = PilotSkillReader.Level(world, catalog, id, target.TypeId);
            if (
                skill is null
                || target.Level <= current
                || skill.Any(dependency => PilotSkillReader.Level(world, catalog, id, dependency.TypeId) < dependency.Level)
            )
            {
                continue;
            }
            if (
                world.Set<PilotSkill>().Find(id, target.TypeId) is null
                && !Learn(world, catalog, pilot, target.TypeId, now, out marketChanged)
            )
            {
                continue;
            }
            var applied = training.Apply(world, id, new FreePointRequest.Targets([target]), now);
            levelChanged = PilotSkillReader.Level(world, catalog, id, target.TypeId) > current;
            return applied.Allocations.Count > 0;
        }
        return false;
    }

    private bool Learn(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, int type, long now, out bool marketChanged)
    {
        marketChanged = false;
        long id = pilot.PilotId;
        int station = pilot.StationId;
        var owned = world
            .Filter<OwnedItem>(item => item.OwnerId == id)
            .FirstOrDefault(item =>
                item.LocationId == station
                && item.FlagId == (int)InventoryFlag.Hangar
                && item.TypeId == type
                && !item.Singleton
                && item.Quantity > 0
            );
        if (owned is not null)
        {
            PilotSkillBookStore.Inject(world, catalog, id, [owned.Id], now);
            return true;
        }
        // Keep the fresh factory and its buyer's working capital available until each settles its first finished-goods trade.
        if (
            world.Settings.FreshUniverse
            && world.Set<SavedCapsuleerAgent>().Find(id)?.Career == CapsuleerCareer.Mining
            && !CapsuleerIndustry.StarterTradeSettled(world, id)
        )
        {
            return false;
        }
        long budget = (world.Set<PilotWallet>().Find(id)?.Cents ?? 0) / SkillbookBudgetDivisor;
        if (budget == 0)
        {
            return false;
        }
        if (PilotSkillPurchaseStore.TryPurchase(world, catalog, id, type, budget, now))
        {
            return true;
        }
        if (station <= 0)
        {
            return false;
        }
        var offer = market.SupplyOrders(world, catalog, station, [type], id).MinBy(order => order.PriceCents);
        if (offer is null || offer.PriceCents > budget)
        {
            return false;
        }
        var buy = market.ExecuteBuy(world, catalog, id, station, [new(type, offer.PriceCents, 1)], 0);
        PilotSkillBookStore.Inject(world, catalog, id, [buy.Changes.Single(change => change.Item.TypeId == type).Item.Id], now);
        marketChanged = true;
        return true;
    }

    private static CapsuleerCareerPlan Choose(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        CapsuleerProfession profession,
        CapsuleerCareerPlan? saved
    )
    {
        var (stage, preferred) = PreferredShip(catalog, pilot.ShipTypeId, profession, saved);
        var focus = saved?.NextFocus ?? CapsuleerTrainingFocus.Work;
        if (preferred != 0)
        {
            return new(profession, preferred, focus) { HighestHullStage = stage };
        }
        var available = catalog.Definition(s_careerChoices[profession], stage + 1);
        if (available.Length == 0)
        {
            return new(profession, 0, focus) { HighestHullStage = stage };
        }
        long seed = world.Set<SavedCapsuleerProvisioning>().Find(1)?.WorldSeed ?? 0;
        ulong draw = CapsuleerProvisioningRules.Seed(seed ^ pilot.PilotId, pilot.SystemId, CapsuleerCareer.Mining);
        return new(profession, available[(int)(draw % (ulong)available.Length)], focus) { HighestHullStage = stage };
    }

    private static (int Stage, int Preferred) PreferredShip(
        CatalogDbContext catalog,
        int hull,
        CapsuleerProfession profession,
        CapsuleerCareerPlan? saved
    )
    {
        var flown = catalog.Definition(s_careerHullStages, hull);
        int stage = Math.Max(
            saved?.Profession == profession ? saved.HighestHullStage : 0,
            flown.Profession == profession ? flown.Stage : 0
        );
        if (saved?.Profession == profession && saved.ShipTypeId > 0 && IsChoice(saved.ShipTypeId))
        {
            return (stage, saved.ShipTypeId);
        }
        int entry = EntryHull(profession);
        return (stage, stage == 0 && IsChoice(entry) ? entry : 0);

        bool IsChoice(int type) =>
            catalog.Definition(s_careerHullStages, type) == (profession, stage + 1)
            && EligibleShip(catalog, type)
            && catalog.Definition(s_careerFitPlans, type) is not null;
    }

    private static int EntryHull(CapsuleerProfession profession) =>
        profession switch
        {
            CapsuleerProfession.Mining => (int)TechOneHullType.Venture,
            CapsuleerProfession.Hauling => HaulingRules.BadgerType,
            _ => (int)TechOneHullType.Merlin,
        };

    private static readonly CatalogDefinition<(CapsuleerProfession? Profession, int Stage)> s_careerHullStages = new(
        "career-hull-stage",
        (catalog, type) =>
        {
            var group = (HullClass)(catalog.FittingType(type)?.GroupId ?? 0);
            var profession = Profession(catalog, type, group);
            var definition = catalog.Read(CatalogData.Client.Types, (long)type);
            int tech = (int)(definition?.TechLevel ?? (type == (int)TechOneHullType.Venture ? 1 : 0));
            return (
                profession,
                profession is { } role ? CapsuleerCareerTrainingRules.HullStage(role, group, tech, definition?.MetaGroupID is null or 1) : 0
            );
        }
    );

    private static readonly CatalogDefinition<TrainingTarget[]?> s_careerFitPlans = new(
        "career-fit-prerequisites",
        (catalog, type) =>
        {
            if (CapsuleerIndustry.CareerKit(catalog, type) is not { } kit)
            {
                return null;
            }
            return Expand(catalog, kit.Parts().SelectMany(part => RequiredSkills(catalog, part.TypeId)));
        }
    );

    private static readonly FrozenDictionary<CapsuleerProfession, CatalogDefinition<int[]>> s_careerChoices =
        Enum.GetValues<CapsuleerProfession>()
            .ToFrozenDictionary(
                profession => profession,
                profession => new CatalogDefinition<int[]>(
                    "career-goals/" + profession,
                    (catalog, stage) => LoadChoices(catalog, profession, stage)
                )
            );

    private static int[] LoadChoices(CatalogDbContext catalog, CapsuleerProfession profession, int stage)
    {
        // HullStage distinguishes standard tech-one hulls from other variants. Exclude entire groups
        // before reading individual type records, then check each candidate's actual catalog stage.
        var groups = Enum.GetValues<HullClass>()
            .Where(group =>
                CapsuleerCareerTrainingRules.HullStage(profession, group, 1, true) == stage
                || CapsuleerCareerTrainingRules.HullStage(profession, group, 2, false) == stage
            );
        return groups
            .SelectMany(group => catalog.Definitions.TypesInGroup((int)group))
            .Where(type =>
                catalog.Definition(s_careerHullStages, type) == (profession, stage)
                && EligibleShip(catalog, type)
                && catalog.Definition(s_careerFitPlans, type) is not null
            )
            .Order()
            .ToArray();
    }

    private static int[] LoadShips(CatalogDbContext catalog, int profession) =>
        catalog
            .Definitions.Items.Values.Where(item =>
                item.CategoryId == (int)InventoryCategory.Ship
                && Profession(catalog, item.Id, (HullClass)item.GroupId) == (CapsuleerProfession)profession
                && EligibleShip(catalog, item.Id)
            )
            .Select(item => item.Id)
            .Order()
            .ToArray();

    private static bool EligibleShip(CatalogDbContext catalog, int type) =>
        catalog.FittingType(type) is { CategoryId: (int)InventoryCategory.Ship } item
        && HullCatalog.CanAssemble(catalog, item, item.CategoryId)
        && catalog.Definition(s_shipPlans, type) is { Length: > 0 };

    internal static CapsuleerProfession? Profession(CatalogDbContext catalog, int ship, HullClass hull) =>
        hull == HullClass.Frigate
        && InventoryDefinitions.RequiredSkills.Any(pair =>
            catalog.FittingAttributes(ship).GetValueOrDefault(pair.Skill) == (int)MiningSkill.Frigate
        )
            ? CapsuleerProfession.Mining
            : CapsuleerCareerTrainingRules.Profession(hull);

    private static TrainingTarget[]? LoadShipPlan(CatalogDbContext catalog, int ship) => Expand(catalog, RequiredSkills(catalog, ship));

    private static IEnumerable<TrainingTarget> RequiredSkills(CatalogDbContext catalog, int type)
    {
        var dogma = catalog.FittingAttributes(type);
        return InventoryDefinitions
            .RequiredSkills.Select(pair => new TrainingTarget(
                (int)dogma.GetValueOrDefault(pair.Skill),
                (int)dogma.GetValueOrDefault(pair.Level)
            ))
            .Where(requirement => requirement.TypeId > 0 && requirement.Level > 0);
    }

    private static TrainingTarget[]? LoadQualification(CatalogDbContext catalog, int id)
    {
        double rank = catalog.FittingAttributes(id).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant);
        if (
            !double.IsFinite(rank)
            || rank <= 0
            || catalog.FittingType(id)?.CategoryId != (int)InventoryCategory.Skill
            || catalog.Read(CatalogData.Client.Types, (long)id)?.Published != 1
        )
        {
            return null;
        }
        var prerequisites = catalog.Read(CatalogData.Client.Requiredskillsfortypes, (long)id);
        return prerequisites
                ?.Select(row => new TrainingTarget(int.Parse(row.Key, CultureInfo.InvariantCulture), checked((int)row.Value)))
                .ToArray()
            ?? [];
    }

    private static TrainingTarget[]? Expand(CatalogDbContext catalog, IEnumerable<TrainingTarget> roots)
    {
        Dictionary<int, int> ordered = [];
        HashSet<int> pending = [];
        return roots.All(Visit) ? ordered.Select(pair => new TrainingTarget(pair.Key, pair.Value)).ToArray() : null;

        bool Visit(TrainingTarget task)
        {
            if (ordered.GetValueOrDefault(task.TypeId) >= task.Level)
            {
                return true;
            }
            if (task.Level is < 1 or > 5 || catalog.Definition(s_qualifications, task.TypeId) is not { } skill || !pending.Add(task.TypeId))
            {
                return false;
            }
            if (!skill.All(Visit))
            {
                return false;
            }
            pending.Remove(task.TypeId);
            ordered[task.TypeId] = task.Level;
            return true;
        }
    }

    // Keep the minimum qualification for one goal ahead of improvements wanted by later goals.
    private static TrainingTarget[] ExpandAvailable(CatalogDbContext catalog, IReadOnlyList<TrainingTarget> roots) =>
        roots.SelectMany(root => Expand(catalog, [root]) ?? []).Distinct().ToArray();
}
