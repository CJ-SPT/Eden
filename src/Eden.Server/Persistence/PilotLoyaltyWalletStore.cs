using Eden.Gameplay.Market;
using Eden.Gameplay.Redemption;
using Eden.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[PrimaryKey(nameof(PilotId), nameof(IssuerCorporationId))]
public sealed class SavedLoyaltyBalance
{
    public long PilotId { get; set; }
    public int IssuerCorporationId { get; set; }
    public long Points { get; set; }
}

public sealed class SavedRedeemedCurrency
{
    public long TokenId { get; set; }
    public long PilotId { get; set; }
    public RedeemCurrencyKind Kind { get; set; }
    public long Amount { get; set; }
    public long Before { get; set; }
    public long After { get; set; }
    public long Time { get; set; }

    public RedeemedCurrency Snapshot() => new(TokenId, PilotId, Kind, Amount, Before, After, Time);
}

[Injectable]
internal sealed class PilotLoyaltyWalletStore(WorldDatabase database) : IPilotLoyaltyWallet
{
    public IReadOnlyList<LoyaltyBalance> Read(long pilotId)
    {
        using var phase = Eden.Protocol.PilotSessions.SessionPerformance.Phase("read.loyalty");
        return LiveReadModels.Find(database.Path) is { } resident ? resident.Loyalty(pilotId, () => ReadSql(pilotId)) : ReadSql(pilotId);
    }

    private IReadOnlyList<LoyaltyBalance> ReadSql(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        if (!world.Pilots.Any(p => p.Id == pilotId))
        {
            throw new InvalidDataException("Unknown loyalty wallet owner.");
        }
        var rows = world
            .Set<SavedLoyaltyBalance>()
            .AsNoTracking()
            .Where(b => b.PilotId == pilotId)
            .OrderBy(b => b.IssuerCorporationId)
            .ToArray();
        if (rows.Any(b => b.Points < 0 || b.IssuerCorporationId <= 0))
        {
            throw new InvalidDataException("Invalid saved loyalty balance.");
        }
        return rows.Where(b => b.Points > 0).Select(b => new LoyaltyBalance(b.IssuerCorporationId, b.Points)).ToArray();
    }

    internal static RedeemedCurrency Credit(WorldDbContext world, long pilotId, SavedRedeemToken token, RedeemCurrencyKind kind, long now)
    {
        long amount = CurrencyRewardRules.Amount(kind, token.Quantity);
        long before;
        long after;
        if (kind == RedeemCurrencyKind.Isk)
        {
            var wallet = world.Set<PilotWallet>().Find(pilotId) ?? world.Add(new PilotWallet { PilotId = pilotId }).Entity;
            before = wallet.Cents;
            after = CurrencyRewardRules.Credit(before, amount);
            wallet.Cents = after;
        }
        else
        {
            var wallet =
                world.Set<SavedLoyaltyBalance>().Find(pilotId, CurrencyRewardRules.ParagonCorporation)
                ?? world
                    .Add(new SavedLoyaltyBalance { PilotId = pilotId, IssuerCorporationId = CurrencyRewardRules.ParagonCorporation })
                    .Entity;
            before = wallet.Points;
            after = CurrencyRewardRules.Credit(before, amount);
            wallet.Points = after;
        }
        var saved = world
            .Add(
                new SavedRedeemedCurrency
                {
                    TokenId = token.Id,
                    PilotId = pilotId,
                    Kind = kind,
                    Amount = amount,
                    Before = before,
                    After = after,
                    Time = now,
                }
            )
            .Entity;
        return saved.Snapshot();
    }
}
