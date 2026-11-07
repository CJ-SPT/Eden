using Eden.Data.Market;
using Eden.Data.Redemption;
using Eden.Gameplay.Market;
using Eden.Gameplay.Redemption;
using Eden.Infrastructure.Registration;
using Eden.World.Redemption;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Market;

[Injectable]
internal sealed class PilotLoyaltyWalletStore(WorldDatabase database) : IPilotLoyaltyWallet
{
    public IReadOnlyList<LoyaltyBalance> Read(long pilotId)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("read.loyalty");
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
