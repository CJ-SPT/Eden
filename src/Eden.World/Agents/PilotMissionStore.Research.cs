using System.Collections.Immutable;
using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Economy;
using Eden.Data.World.Market;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    private static readonly CatalogDefinition<IReadOnlyList<ResearchField>> s_researchFields = new(
        "agents/research-fields",
        LoadResearchFields,
        Estimate: fields => 128 + fields.Count * 128L
    );
    private static readonly CatalogDefinition<IReadOnlyDictionary<int, int>> s_researchDatacores = new(
        "agents/research-datacores",
        (catalog, _) =>
            catalog
                .Definitions.Items.Values.Where(type =>
                    type.GroupId == ResearchRules.DatacoreGroup && catalog.Read(CatalogData.Client.Types, (long)type.Id)?.Published == 1
                )
                .OrderBy(type => type.Id)
                .GroupBy(type => (int)catalog.FittingAttributes(type.Id).GetValueOrDefault(DogmaAttributeId.RequiredSkill1))
                .Where(group => group.Key > 0)
                .ToDictionary(group => group.Key, group => group.First().Id)
    );

    private T ResearchOperation<T>(long pilotId, Func<WorldAction, CatalogDbContext, Pilot, long, T> action)
    {
        return LiveWorld.Execute(
            database.Path,
            () =>
            {
                using var ownership = LiveWorld.Find(database.Path)?.Enter();
                using var movement = database.EnterMovement();
                using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
                var world = transaction.World;
                using CatalogDbContext catalog = new(database.CatalogPath);
                long now = Now;
                training.Advance(world, pilotId, now);
                var result = action(world, catalog, ResidentPilot(world, pilotId), now);
                world.SaveChanges();
                transaction.Commit();
                return result;
            },
            "Research"
        );
    }

    private CatalogAgent ResearchAgent(long id)
    {
        var agent = Agent(id);
        if (agent.Type != AgentType.Research || agent.StationId is null || agent.Level is < 1 or > 5)
        {
            throw new InvalidDataException("Choose a station research agent.");
        }
        return agent;
    }

    internal static IReadOnlyList<ResearchField> ResearchFields(CatalogDbContext catalog, long agentId) =>
        catalog.Definition(s_researchFields, checked((int)agentId));

    internal static bool HasResearchAccess(WorldAction world, CatalogDbContext catalog, Pilot pilot, CatalogAgent agent) =>
        Access(world, catalog, pilot, agent).Allowed;

    private static IReadOnlyList<ResearchField> LoadResearchFields(CatalogDbContext catalog, int agentId)
    {
        var fields = catalog.Read(CatalogData.Client.Npccharacters, agentId)?.Skills ?? [];
        var datacores = catalog.Definitions.GlobalDefinition(s_researchDatacores);
        return fields
            .Where(f => f.TypeID is > 0 and <= int.MaxValue)
            .Select(f => (int)f.TypeID!.Value)
            .Distinct()
            .Order()
            .Where(catalog.Definitions.Items.ContainsKey)
            .Select(id => new ResearchField(id, catalog.Definitions.Items[id].Name, datacores.TryGetValue(id, out int type) ? type : null))
            .ToImmutableArray();
    }

    public IReadOnlyList<ResearchField> Fields(long pilotId, long agentId) =>
        ResearchOperation(
            pilotId,
            (world, catalog, pilot, _) =>
            {
                var agent = ResearchAgent(agentId);
                RequireStation(pilot, agent.StationId);
                RequireAgentAccess(world, catalog, pilot, agent);
                if (Skill(world, catalog, pilot, ResearchRules.Science) < 5)
                {
                    throw new InvalidDataException("Research requires Science V.");
                }
                return ResearchFields(catalog, agentId).Where(f => Skill(world, catalog, pilot, f.SkillId) >= agent.Level).ToArray();
            }
        );

    private static ResearchProject SettleResearch(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        CatalogAgent agent,
        SavedResearchProject row,
        long now
    )
    {
        row.Points = ResearchRules.Accrue(row.Points, row.PointsPerDay, row.SettledAt, now);
        row.SettledAt = Math.Max(row.SettledAt, now);
        row.PointsPerDay = ResearchRate(world, catalog, pilot, agent, row);
        return new(row.Id, agent, row.SkillId, row.Points, row.PointsPerDay);
    }

    private static double ResearchRate(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        CatalogAgent agent,
        SavedResearchProject row
    ) =>
        ResearchRules.PointsPerDay(
            agent.Level,
            Skill(world, catalog, pilot, row.SkillId),
            Skill(world, catalog, pilot, MissionSocialSkill.Negotiation),
            Access(world, catalog, pilot, agent).Agent
        );

    public IReadOnlyList<ResearchProject> Research(long pilotId, long? agentId = null) =>
        ResearchOperation(
            pilotId,
            (world, catalog, pilot, now) =>
                world
                    .Filter<SavedResearchProject>(p => p.PilotId == pilotId && (agentId == null || p.AgentId == agentId))
                    .OrderBy(p => p.AgentId)
                    .ToArray()
                    .Select(row =>
                    {
                        var agent = ResearchAgent(row.AgentId);
                        var settled = SettleResearch(world, catalog, pilot, agent, row, now);
                        return settled with
                        {
                            CanRequestMission =
                                agentId.HasValue
                                && world is EconomyTransaction
                                && ResearchMissionCatalog.Supports(agent)
                                && Access(world, catalog, pilot, agent).Allowed
                                && ResearchMissionAvailable(LatestResearchMission(world, pilotId, row.AgentId), now),
                        };
                    })
                    .ToArray()
        );

    public ResearchProject StartResearch(long pilotId, long agentId, int skillId) =>
        ResearchOperation(
            pilotId,
            (world, catalog, pilot, now) => StartResearch(world, catalog, pilot, ResearchAgent(agentId), skillId, now)
        );

    internal static ResearchProject StartResearch(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        CatalogAgent agent,
        int skill,
        long now
    )
    {
        RequireStation(pilot, agent.StationId);
        RequireAgentAccess(world, catalog, pilot, agent);
        if (
            Skill(world, catalog, pilot, ResearchRules.Science) < 5
            || Skill(world, catalog, pilot, skill) < agent.Level
            || !ResearchFields(catalog, agent.Id).Any(field => field.SkillId == skill)
        )
        {
            throw new InvalidDataException("Train Science V and an offered science field to the agent's level.");
        }
        int capacity = 1 + Skill(world, catalog, pilot, ResearchRules.ProjectManagement);
        var projects = world.Filter<SavedResearchProject>(project => project.PilotId == pilot.Id);
        if (projects.Any(project => project.AgentId == agent.Id) || projects.Count() >= capacity)
        {
            throw new InvalidDataException("This agent is already researching or your Research Project Management limit is reached.");
        }
        var row = world
            .Add(
                new SavedResearchProject
                {
                    PilotId = pilot.Id,
                    AgentId = agent.Id,
                    SkillId = skill,
                    SettledAt = now,
                }
            )
            .Entity;
        var snapshot = SettleResearch(world, catalog, pilot, agent, row, now);
        world.SaveChanges();
        return snapshot with { Id = row.Id };
    }

    private static SavedResearchProject ResearchRow(WorldAction world, long pilotId, long agentId, long projectId) =>
        world.Filter<SavedResearchProject>(p => p.PilotId == pilotId && p.AgentId == agentId && p.Id == projectId).SingleOrDefault()
        ?? throw new InvalidDataException("This research action is stale. Reopen the agent conversation.");

    public MissionState? CancelResearch(long pilotId, long agentId, long projectId) =>
        ResearchOperation(
            pilotId,
            (world, catalog, pilot, now) =>
            {
                RequireStation(pilot, ResearchAgent(agentId).StationId);
                world.Remove(ResearchRow(world, pilotId, agentId, projectId));
                return CloseResearchMission(world, catalog, pilotId, agentId, now);
            }
        );

    public DatacorePurchase BuyDatacores(long pilotId, long agentId, long projectId, int quantity) =>
        ResearchOperation(
            pilotId,
            (world, catalog, pilot, now) => BuyDatacores(world, catalog, pilot, ResearchAgent(agentId), projectId, quantity, now)
        );

    internal static DatacorePurchase BuyDatacores(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        CatalogAgent agent,
        long projectId,
        int quantity,
        long now,
        int? sponsor = null
    )
    {
        RequireStation(pilot, agent.StationId);
        var row = ResearchRow(world, pilot.Id, agent.Id, projectId);
        _ = SettleResearch(world, catalog, pilot, agent, row, now);
        long cost = ResearchRules.PurchaseCost(row.Points, quantity);
        int type =
            ResearchFields(catalog, agent.Id).SingleOrDefault(field => field.SkillId == row.SkillId)?.DatacoreTypeId
            ?? throw new InvalidDataException("This research field has no catalog datacore.");
        long balance;
        if (sponsor is int corporation)
        {
            balance = CorporationWalletStore.Pay(world, corporation, agent.Id, projectId, WalletEntryType.DatacorePurchase, -cost, now);
        }
        else
        {
            var wallet = world.Set<PilotWallet>().Find(pilot.Id);
            if (wallet is null || wallet.Cents < cost)
            {
                throw new InvalidDataException("Insufficient ISK to buy datacores.");
            }
            balance = wallet.Cents -= cost;
            world.Add(
                new SavedServicePayment
                {
                    PilotId = pilot.Id,
                    CounterpartyId = agent.Id,
                    ContextId = projectId,
                    Kind = WalletEntryType.DatacorePurchase,
                    AmountCents = -cost,
                    BalanceCents = balance,
                    Time = now,
                }
            );
        }
        var item = InventoryPersistence.MergeOutput(
            world,
            catalog,
            pilot.Id,
            agent.StationId!.Value,
            (int)InventoryFlag.Hangar,
            type,
            quantity
        );
        row.Points -= (double)quantity * ResearchRules.PointsPerDatacore;
        return new(balance, item);
    }
}
