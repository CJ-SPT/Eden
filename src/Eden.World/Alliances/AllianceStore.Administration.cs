using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Data.World.Economy;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Alliances;

internal sealed partial class AllianceStore
{
    public AllianceCreation Create(long actorId, AllianceDetails details)
    {
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var result = Create(world, catalog, actorId, details, Now);
        world.Commit();
        return result;
    }

    internal AllianceCreation Create(WorldAction world, CatalogDbContext catalog, long actorId, AllianceDetails details, long now)
    {
        details = AllianceRules.Validate(details);
        var corporation = CorporationRoleStore.Current(world, actorId);
        double rank = catalog.FittingAttributes(AllianceRules.EmpireControlSkill).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant);
        if (rank <= 0)
        {
            throw new InvalidDataException("Missing Empire Control catalog data.");
        }
        long points = world.Set<PilotSkill>().Find(actorId, AllianceRules.EmpireControlSkill)?.Points ?? 0;
        int level = new TrainedSkill(AllianceRules.EmpireControlSkill, points, rank).Level;
        var wallet = world.Set<PilotWallet>().Find(actorId);
        long balance = AllianceRules.FormationBalance(
            corporation.CeoId == actorId && corporation.ClosedAt is null,
            world.Set<SavedAllianceMember>().Find(corporation.Id) is not null,
            level,
            wallet?.Cents ?? 0
        );
        if (IdentityTaken(world, catalog, details))
        {
            throw new InvalidDataException("That owner name or ticker is already taken.");
        }
        int id;
        do
        {
            id = checked((int)world.Next("WorldAlliance", AllianceRules.FirstAllianceId - 1));
        } while (world.Set<InventoryOwner>().Find((long)id) is not null);
        world.Add(new InventoryOwner { Id = id });
        world.Add(
            new SavedAlliance
            {
                Id = id,
                Name = details.Name,
                Ticker = details.Ticker,
                Description = details.Description,
                Url = details.Url,
                CreatorId = actorId,
                CreatorCorporationId = corporation.Id,
                ExecutorCorporationId = corporation.Id,
                CreatedAt = now,
            }
        );
        world.Add(
            new SavedAllianceMember
            {
                CorporationId = corporation.Id,
                AllianceId = id,
                JoinedAt = now,
                ChosenExecutorId = corporation.Id,
            }
        );
        world.Add(
            new SavedAllianceHistory
            {
                Id = world.Next("WorldAllianceHistory"),
                CorporationId = corporation.Id,
                AllianceId = id,
                StartedAt = now,
            }
        );
        wallet!.Cents = balance;
        world.Add(
            new SavedServicePayment
            {
                PilotId = actorId,
                CounterpartyId = id,
                ContextId = id,
                Kind = WalletEntryType.AllianceRegistration,
                AmountCents = -AllianceRules.CreationFeeCents,
                BalanceCents = balance,
                Time = now,
            }
        );
        var result = Snapshot(world, id)!;
        Issue(world, id, now, now + AllianceBillingRules.MaintenancePeriod.Ticks);
        Record(world, new(0, id, corporation.Id, After: result, MemberAfter: new(corporation.Id, id, now, corporation.Id)));
        world.AfterCommit(database.NotifyAccessGroupsChanged);
        return new(result, balance);
    }

    internal static bool IdentityTaken(WorldAction world, CatalogDbContext catalog, AllianceDetails details) =>
        world.Set<SavedAlliance>().AsEnumerable().Any(row => Matches(row.Name, row.Ticker, details))
        || world.Set<SavedCorporation>().AsEnumerable().Any(row => Matches(row.Name, row.Ticker, details))
        || catalog.NpcCorporationNameExists(details.Name);

    private static bool Matches(string name, string ticker, AllianceDetails details) =>
        string.Equals(name, details.Name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(ticker, details.Ticker, StringComparison.OrdinalIgnoreCase);

    public void Update(long actorId, string description, string url)
    {
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var alliance = Executor(world, actorId);
        var before = Snapshot(world, alliance.Id)!;
        var details = AllianceRules.Validate(new(alliance.Name, alliance.Ticker, description, url));
        if (alliance.Description == details.Description && alliance.Url == details.Url)
        {
            return;
        }
        alliance.Description = details.Description;
        alliance.Url = details.Url;
        Record(world, new(0, alliance.Id, 0, Before: before, After: Snapshot(world, alliance.Id)));
        world.Commit();
    }

    public IReadOnlyList<string> SuggestTickers(string name)
    {
        string ticker = new(name.ToUpperInvariant().Where(char.IsLetterOrDigit).Take(AllianceRules.MaximumTickerLength).ToArray());
        return ticker.Length == 0 || List().Any(row => row.Details.Ticker == ticker) ? [] : [ticker];
    }

    internal static SavedAlliance Executor(WorldAction world, long actorId)
    {
        var corporation = CorporationRoleStore.Current(world, actorId);
        CorporationRoleStore.RequireDirector(world, actorId, corporation);
        int allianceId =
            world.Set<SavedAllianceMember>().Find(corporation.Id)?.AllianceId
            ?? throw new InvalidDataException("Your corporation is not in an alliance.");
        var alliance = world.Set<SavedAlliance>().Find(allianceId)!;
        if (alliance.ClosedAt.HasValue || alliance.ExecutorCorporationId != corporation.Id)
        {
            throw new InvalidDataException("Alliance executor authority is required.");
        }
        return alliance;
    }
}
