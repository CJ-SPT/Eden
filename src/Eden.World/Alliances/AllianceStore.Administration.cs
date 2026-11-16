using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Data.World.Economy;
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
        details = AllianceRules.Validate(details);
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var corporation = CorporationRoleStore.Current(world, actorId);
        using CatalogDbContext catalog = new(database.CatalogPath);
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
        if (
            world
                .Set<SavedAlliance>()
                .AsEnumerable()
                .Any(row =>
                    string.Equals(row.Name, details.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(row.Ticker, details.Ticker, StringComparison.OrdinalIgnoreCase)
                )
            || world
                .Set<SavedCorporation>()
                .AsEnumerable()
                .Any(row =>
                    string.Equals(row.Name, details.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(row.Ticker, details.Ticker, StringComparison.OrdinalIgnoreCase)
                )
            || catalog.NpcCorporationNameExists(details.Name)
        )
        {
            throw new InvalidDataException("That owner name or ticker is already taken.");
        }
        int id = checked((int)world.Next("WorldAlliance", AllianceRules.FirstAllianceId - 1));
        long now = Now;
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
        world.Commit();
        database.NotifyAccessGroupsChanged();
        return new(result, balance);
    }

    public void Update(long actorId, string description, string url)
    {
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var alliance = Executor(world, actorId);
        var details = AllianceRules.Validate(new(alliance.Name, alliance.Ticker, description, url));
        alliance.Description = details.Description;
        alliance.Url = details.Url;
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
