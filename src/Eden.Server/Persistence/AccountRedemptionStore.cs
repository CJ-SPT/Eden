using Eden.Gameplay.Inventory;
using Eden.Gameplay.Redemption;
using Eden.Gameplay.Skills;
using Eden.Protocol;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public enum RedeemTokenState
{
    Pending,
    Claimed,
    Trashed,
}

public sealed class SavedRedeemToken
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public string Reference { get; set; } = "";
    public int TypeId { get; set; }
    public int Quantity { get; set; }
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public int? StationId { get; set; }
    public long CreatedAt { get; set; }
    public long? ExpiresAt { get; set; }
    public RedeemTokenState State { get; set; }
    public long? FinishedAt { get; set; }
    public long? PilotId { get; set; }
    public long? ItemId { get; set; }
    public long? ReversedAt { get; set; }

    public RedeemToken Snapshot() => new(Id, TypeId, Quantity, Label, Description, StationId, CreatedAt, ExpiresAt);
}

[Injectable]
internal sealed class AccountRedemptionStore(WorldDatabase database, TimeProvider clock) : IAccountRedemption
{
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public RedeemToken Grant(RedeemGrant grant)
    {
        RedemptionRules.Check(grant);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        Account(world, grant.AccountId);
        var prior = world.Set<SavedRedeemToken>().SingleOrDefault(t => t.AccountId == grant.AccountId && t.Reference == grant.Reference);
        if (prior is not null)
        {
            if (
                prior.TypeId != grant.TypeId
                || prior.Quantity != grant.Quantity
                || prior.Label != grant.Label
                || prior.Description != grant.Description
                || prior.StationId != grant.StationId
                || prior.ExpiresAt != grant.ExpiresAt
            )
            {
                throw new InvalidDataException("This grant reference already has a different reward.");
            }
            return prior.Snapshot();
        }
        long now = Now;
        if (grant.ExpiresAt <= now)
        {
            throw new InvalidDataException("The redeem grant must expire in the future.");
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        CheckType(catalog, grant.TypeId);
        if ((SkillPointRewards.Amount(grant.TypeId) > 0 || CurrencyRewardRules.Kind(grant.TypeId).HasValue) && grant.StationId.HasValue)
        {
            throw new InvalidDataException("Automatic rewards have no fixed station.");
        }
        if (grant.StationId is int station)
        {
            CheckStation(catalog, station);
        }
        var token = world
            .Add(
                new SavedRedeemToken
                {
                    AccountId = grant.AccountId,
                    Reference = grant.Reference,
                    TypeId = grant.TypeId,
                    Quantity = grant.Quantity,
                    Label = grant.Label,
                    Description = grant.Description,
                    StationId = grant.StationId,
                    CreatedAt = now,
                    ExpiresAt = grant.ExpiresAt,
                }
            )
            .Entity;
        world.SaveChanges();
        transaction.Commit();
        return token.Snapshot();
    }

    public IReadOnlyList<RedeemToken> Read(long accountId)
    {
        using WorldDbContext world = new(database.Path);
        Account(world, accountId);
        long now = Now;
        return world
            .Set<SavedRedeemToken>()
            .AsNoTracking()
            .Where(t => t.AccountId == accountId && t.State == RedeemTokenState.Pending && (t.ExpiresAt == null || t.ExpiresAt > now))
            .OrderBy(t => t.Id)
            .AsEnumerable()
            .Select(t => t.Snapshot())
            .ToArray();
    }

    public RedeemResult Claim(long accountId, long pilotId, IReadOnlyList<RedeemSelection> tokens, bool useHomeStation)
    {
        RedemptionRules.CheckBatch(tokens.Select(t => t.TokenId).ToArray());
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var pilot = Pilot(world, accountId, pilotId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        int? destination =
            useHomeStation ? PilotHomeStationStore.Read(world, catalog, pilot).Id
            : pilot.ActiveShip.Position is null ? pilot.ActiveShip.StationId
            : null;
        long now = Now;
        List<RedeemedItem> items = [];
        List<RedeemFailure> errors = [];
        List<RedeemedCurrency> currencies = [];
        long skillPoints = 0;
        long? freeSkillPoints = null;
        long? freePointRevision = null;
        TrainingState? training = null;
        foreach (var selected in tokens)
        {
            var token = world.Set<SavedRedeemToken>().Find(selected.TokenId);
            if (
                token is null
                || token.AccountId != accountId
                || token.State != RedeemTokenState.Pending
                || token.TypeId != selected.TypeId
                || token.ExpiresAt <= now
            )
            {
                errors.Add(new(selected.TokenId, selected.TypeId, "This redeem token is unavailable or expired."));
                continue;
            }
            long reward = SkillPointRewards.Amount(token.TypeId);
            if (CurrencyRewardRules.Kind(token.TypeId) is { } currency)
            {
                CheckType(catalog, token.TypeId);
                currencies.Add(PilotLoyaltyWalletStore.Credit(world, pilotId, token, currency, now));
                token.State = RedeemTokenState.Claimed;
                token.FinishedAt = now;
                token.PilotId = pilotId;
                continue;
            }
            if (reward > 0)
            {
                CheckType(catalog, token.TypeId);
                var credit = new PilotTrainingStore(database, clock).Credit(world, pilotId, checked(reward * token.Quantity), now);
                skillPoints = checked(skillPoints + reward * token.Quantity);
                freeSkillPoints = credit.Remaining;
                freePointRevision = credit.Revision;
                training = new(
                    credit.Training.Queue,
                    (training?.ChangedSkills ?? []).Concat(credit.Training.ChangedSkills).Distinct().ToArray()
                );
                token.State = RedeemTokenState.Claimed;
                token.FinishedAt = now;
                token.PilotId = pilotId;
                continue;
            }
            if (destination is null)
            {
                errors.Add(new(token.Id, token.TypeId, "Dock the destination pilot or choose its home station."));
                continue;
            }
            int station = token.StationId ?? destination.Value;
            InventoryItem snapshot;
            OwnedItem item = new()
            {
                Id = InventoryPersistence.NextId(world),
                OwnerId = pilotId,
                LocationId = station,
                FlagId = (int)InventoryFlag.Hangar,
                TypeId = token.TypeId,
                Quantity = token.Quantity,
            };
            try
            {
                CheckType(catalog, token.TypeId);
                CheckStation(catalog, station);
                snapshot = PilotInventoryStore.Read(catalog, item);
            }
            catch (InvalidDataException error)
            {
                errors.Add(new(token.Id, token.TypeId, error.Message));
                continue;
            }
            world.Add(item);
            token.State = RedeemTokenState.Claimed;
            token.FinishedAt = now;
            token.PilotId = pilotId;
            token.ItemId = item.Id;
            items.Add(new(token.Id, snapshot));
        }
        world.SaveChanges();
        transaction.Commit();
        return new(pilotId, items, errors, skillPoints, freeSkillPoints, training, freePointRevision) { Currencies = currencies };
    }

    public void Trash(long accountId, IReadOnlyList<long> tokens)
    {
        RedemptionRules.CheckBatch(tokens);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        Account(world, accountId);
        long now = Now;
        foreach (long id in tokens)
        {
            var token = world.Set<SavedRedeemToken>().Find(id);
            if (token is null || token.AccountId != accountId || token.State != RedeemTokenState.Pending || token.ExpiresAt <= now)
            {
                throw new InvalidDataException("This redeem token is unavailable or expired.");
            }
            token.State = RedeemTokenState.Trashed;
            token.FinishedAt = now;
        }
        world.SaveChanges();
        transaction.Commit();
    }

    public InventoryChange Reverse(long accountId, long pilotId, long itemId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var pilot = Pilot(world, accountId, pilotId);
        var item = world.Set<OwnedItem>().Find(itemId);
        var original = world
            .Set<SavedRedeemToken>()
            .SingleOrDefault(t =>
                t.AccountId == accountId && t.ItemId == itemId && t.State == RedeemTokenState.Claimed && t.ReversedAt == null
            );
        long now = Now;
        if (
            original is null
            || original.ExpiresAt <= now
            || item is null
            || original.PilotId != pilotId
            || item.OwnerId != pilotId
            || item.TypeId != original.TypeId
            || item.Quantity != original.Quantity
            || item.Singleton
            || item.Online
            || pilot.ActiveShip.Position is not null
            || item.LocationId != pilot.ActiveShip.StationId
            || item.FlagId != (int)InventoryFlag.Hangar
        )
        {
            throw new InvalidDataException(
                "Reverse only an intact redeemed stack in your current station hangar before its token expires."
            );
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var previous = PilotInventoryStore.Read(catalog, item);
        original.ReversedAt = now;
        world.Remove(item);
        world.Add(
            new SavedRedeemToken
            {
                AccountId = accountId,
                Reference = $"#reverse/{original.Id}",
                TypeId = original.TypeId,
                Quantity = original.Quantity,
                Label = original.Label,
                Description = original.Description,
                StationId = original.StationId,
                CreatedAt = now,
                ExpiresAt = original.ExpiresAt,
            }
        );
        world.SaveChanges();
        transaction.Commit();
        return new(previous with { Quantity = 0 }, previous);
    }

    private static void Account(WorldDbContext world, long accountId)
    {
        if (!world.Set<Account>().Any(a => a.Id == accountId))
        {
            throw new InvalidDataException("Unknown redeem account.");
        }
    }

    private static Pilot Pilot(WorldDbContext world, long accountId, long pilotId) =>
        world
            .Pilots.Include(p => p.ActiveShip)
                .ThenInclude(s => s.Position)
            .SingleOrDefault(p => p.Id == pilotId && p.AccountId == accountId)
        ?? throw new InvalidDataException("Choose a character on this account.");

    private static void CheckStation(CatalogDbContext catalog, int id)
    {
        if (!catalog.Set<Station>().Any(s => s.Id == id))
        {
            throw new InvalidDataException("The redeem station is unavailable.");
        }
    }

    private static void CheckType(CatalogDbContext catalog, int id)
    {
        var type = catalog.Set<ItemType>().Include(t => t.Group).SingleOrDefault(t => t.Id == id);
        if (type is not null && (SkillPointRewards.Amount(id) > 0 || CurrencyRewardRules.Kind(id).HasValue))
        {
            return;
        }
        if (type is null || !RedemptionRules.Deliverable((InventoryCategory)type.Group.CategoryId))
        {
            throw new InvalidDataException("This reward requires a redemption mechanic that is not implemented yet.");
        }
        _ = PilotInventoryStore.Volume(catalog, id);
    }
}
