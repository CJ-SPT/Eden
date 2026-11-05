using System.Globalization;
using Eden.Gameplay.Market;
using Eden.Gameplay.Redemption;
using Eden.Protocol;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotWalletReader(WorldDatabase database) : IPilotWalletReader
{
    public long Balance(long pilotId)
    {
        using var phase = Eden.Protocol.PilotSessions.SessionPerformance.Phase("read.wallet");
        return LiveReadModels.Find(database.Path) is { } resident
            ? resident.Wallet(pilotId, () => BalanceSql(pilotId))
            : BalanceSql(pilotId);
    }

    private long BalanceSql(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        return world.Set<PilotWallet>().Find(pilotId)?.Cents ?? 0;
    }

    public IReadOnlyList<WalletEntryDefinition> EntryTypes()
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return catalog
            .Set<SourceRecord>()
            .Where(r => r.Dataset == "client/accountingentrytypes")
            .AsEnumerable()
            .Select(r =>
            {
                var type = r.Read<ClientAccountingentrytypes>();
                return new WalletEntryDefinition(
                    int.Parse(r.Key, CultureInfo.InvariantCulture),
                    type.EntryTypeNameID ?? 0,
                    type.Name ?? "",
                    type.Description ?? ""
                );
            })
            .OrderBy(t => t.Id)
            .ToArray();
    }

    public WalletJournal Journal(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        return new(
            PilotMarketStore.Transactions(world, pilotId),
            PilotCombatStore.Rewards(world, pilotId),
            PilotRepairStore.Payments(world, pilotId),
            world
                .Set<TestingWalletGrant>()
                .AsNoTracking()
                .Where(g => g.PilotId == pilotId)
                .Select(g => new WalletGrant(g.PilotId, g.AmountCents, g.BalanceCents, g.Time))
                .ToArray(),
            world
                .Set<SavedInsuranceLedger>()
                .Where(entry => entry.PilotId == pilotId)
                .OrderBy(entry => entry.Id)
                .AsEnumerable()
                .Select(entry => entry.Snapshot())
                .ToArray(),
            world
                .Set<SavedHomeStationPayment>()
                .Where(payment => payment.PilotId == pilotId)
                .OrderBy(payment => payment.Id)
                .AsEnumerable()
                .Select(payment => payment.Snapshot())
                .ToArray(),
            world
                .Set<SavedJumpClonePayment>()
                .Where(payment => payment.PilotId == pilotId)
                .OrderBy(payment => payment.Id)
                .AsEnumerable()
                .Select(payment => payment.Snapshot())
                .ToArray(),
            world
                .Set<SavedContractPayment>()
                .Where(payment => payment.PilotId == pilotId)
                .OrderBy(payment => payment.Id)
                .AsEnumerable()
                .Select(payment => payment.Snapshot())
                .ToArray(),
            world
                .Set<SavedRedeemedCurrency>()
                .Where(entry => entry.PilotId == pilotId && entry.Kind == RedeemCurrencyKind.Isk)
                .OrderByDescending(entry => entry.TokenId)
                .AsEnumerable()
                .Select(entry => entry.Snapshot())
                .ToArray()
        );
    }
}
