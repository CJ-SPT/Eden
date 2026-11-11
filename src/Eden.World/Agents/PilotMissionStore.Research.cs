using System.Collections.Immutable;
using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Economy;
using Eden.Data.World.Market;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    private static readonly CatalogDefinition<IReadOnlyList<ResearchField>> s_researchFields = new(
        "agents/research-fields",
        LoadResearchFields,
        Estimate: fields => 128 + fields.Count * 128L
    );

    private T ResearchOperation<T>(long pilotId, Func<WorldDbContext, CatalogDbContext, Pilot, long, T> action)
    {
        return LiveWorld.Execute(
            database.Path,
            () =>
            {
                using var ownership = LiveWorld.Find(database.Path)?.Enter();
                using var movement = database.EnterMovement();
                using WorldDbContext world = new(database.Path);
                using CatalogDbContext catalog = new(database.CatalogPath);
                using var transaction = world.Database.BeginTransaction();
                long now = Now;
                training.Advance(world, pilotId, now);
                var result = action(world, catalog, Pilot(world, pilotId), now);
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

    private static IReadOnlyList<ResearchField> ResearchFields(CatalogDbContext catalog, long agentId) =>
        catalog.Definition(s_researchFields, checked((int)agentId));

    private static IReadOnlyList<ResearchField> LoadResearchFields(CatalogDbContext catalog, int agentId)
    {
        var fields = catalog.Read(CatalogData.Client.Npccharacters, agentId)?.Skills ?? [];
        var datacores = catalog
            .Definitions.Items.Values.Where(t =>
                t.GroupId == ResearchRules.DatacoreGroup && catalog.Read(CatalogData.Client.Types, (long)t.Id)?.Published == 1
            )
            .OrderBy(t => t.Id)
            .Select(t => new
            {
                Type = t.Id,
                Skill = (int)catalog.FittingAttributes(t.Id).GetValueOrDefault(DogmaAttributeId.RequiredSkill1),
            })
            .Where(t => t.Skill > 0)
            .GroupBy(t => t.Skill)
            .ToDictionary(g => g.Key, g => g.First().Type);
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

    private ResearchProject SettleResearch(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, SavedResearchProject row, long now)
    {
        var agent = ResearchAgent(row.AgentId);
        row.Points = ResearchRules.Accrue(row.Points, row.PointsPerDay, row.SettledAt, now);
        row.SettledAt = Math.Max(row.SettledAt, now);
        row.PointsPerDay = ResearchRules.PointsPerDay(
            agent.Level,
            Skill(world, catalog, pilot, row.SkillId),
            Skill(world, catalog, pilot, MissionSocialSkill.Negotiation),
            Access(world, catalog, pilot, agent).Agent
        );
        return new(row.Id, agent, row.SkillId, row.Points, row.PointsPerDay);
    }

    public IReadOnlyList<ResearchProject> Research(long pilotId, long? agentId = null) =>
        ResearchOperation(
            pilotId,
            (world, catalog, pilot, now) =>
                world
                    .Set<SavedResearchProject>()
                    .Where(p => p.PilotId == pilotId && (agentId == null || p.AgentId == agentId))
                    .OrderBy(p => p.AgentId)
                    .ToArray()
                    .Select(row => SettleResearch(world, catalog, pilot, row, now))
                    .ToArray()
        );

    public ResearchProject StartResearch(long pilotId, long agentId, int skillId) =>
        ResearchOperation(
            pilotId,
            (world, catalog, pilot, now) =>
            {
                var agent = ResearchAgent(agentId);
                RequireStation(pilot, agent.StationId);
                RequireAgentAccess(world, catalog, pilot, agent);
                if (
                    Skill(world, catalog, pilot, ResearchRules.Science) < 5
                    || Skill(world, catalog, pilot, skillId) < agent.Level
                    || !ResearchFields(catalog, agentId).Any(f => f.SkillId == skillId)
                )
                {
                    throw new InvalidDataException("Train Science V and an offered science field to the agent's level.");
                }
                int capacity = 1 + Skill(world, catalog, pilot, ResearchRules.ProjectManagement);
                if (
                    world.Set<SavedResearchProject>().Any(p => p.PilotId == pilotId && p.AgentId == agentId)
                    || world.Set<SavedResearchProject>().Count(p => p.PilotId == pilotId) >= capacity
                )
                {
                    throw new InvalidDataException(
                        "This agent is already researching or your Research Project Management limit is reached."
                    );
                }
                var row = world
                    .Add(
                        new SavedResearchProject
                        {
                            PilotId = pilotId,
                            AgentId = agentId,
                            SkillId = skillId,
                            SettledAt = now,
                        }
                    )
                    .Entity;
                var snapshot = SettleResearch(world, catalog, pilot, row, now);
                world.SaveChanges();
                return snapshot with { Id = row.Id };
            }
        );

    private static SavedResearchProject ResearchRow(WorldDbContext world, long pilotId, long agentId, long projectId) =>
        world.Set<SavedResearchProject>().SingleOrDefault(p => p.PilotId == pilotId && p.AgentId == agentId && p.Id == projectId)
        ?? throw new InvalidDataException("This research action is stale. Reopen the agent conversation.");

    public void CancelResearch(long pilotId, long agentId, long projectId) =>
        ResearchOperation(
            pilotId,
            (world, _, pilot, _) =>
            {
                RequireStation(pilot, ResearchAgent(agentId).StationId);
                world.Remove(ResearchRow(world, pilotId, agentId, projectId));
                return true;
            }
        );

    public DatacorePurchase BuyDatacores(long pilotId, long agentId, long projectId, int quantity) =>
        ResearchOperation(
            pilotId,
            (world, catalog, pilot, now) =>
            {
                var agent = ResearchAgent(agentId);
                RequireStation(pilot, agent.StationId);
                var row = ResearchRow(world, pilotId, agentId, projectId);
                _ = SettleResearch(world, catalog, pilot, row, now);
                long cost = ResearchRules.PurchaseCost(row.Points, quantity);
                int type =
                    ResearchFields(catalog, agentId).SingleOrDefault(f => f.SkillId == row.SkillId)?.DatacoreTypeId
                    ?? throw new InvalidDataException("This research field has no catalog datacore.");
                var wallet = world.Set<PilotWallet>().Find(pilotId);
                if (wallet is null || wallet.Cents < cost)
                {
                    throw new InvalidDataException("Insufficient ISK to buy datacores.");
                }
                var item = InventoryPersistence.MergeOutput(
                    world,
                    catalog,
                    pilotId,
                    agent.StationId!.Value,
                    (int)InventoryFlag.Hangar,
                    type,
                    quantity
                );
                wallet.Cents -= cost;
                row.Points -= (double)quantity * ResearchRules.PointsPerDatacore;
                world.Add(
                    new SavedServicePayment
                    {
                        PilotId = pilotId,
                        CounterpartyId = agentId,
                        ContextId = projectId,
                        Kind = WalletEntryType.DatacorePurchase,
                        AmountCents = -cost,
                        BalanceCents = wallet.Cents,
                        Time = now,
                    }
                );
                return new DatacorePurchase(wallet.Cents, item);
            }
        );
}
