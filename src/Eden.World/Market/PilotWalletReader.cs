using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Contracts;
using Eden.Data.World.Corporations;
using Eden.Data.World.Economy;
using Eden.Data.World.Insurance;
using Eden.Data.World.Market;
using Eden.Gameplay.Market;
using Eden.Gameplay.Redemption;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Contracts;
using Eden.World.Corporations;
using Eden.World.Economy;
using Eden.World.Insurance;
using Eden.World.Repair;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Market;

[Injectable]
internal sealed class PilotWalletReader(WorldDatabase database) : IPilotWalletReader
{
    public long Balance(long pilotId)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("read.wallet");
        return LiveWorld.Find(database.Path) is { SqlConnections: 0 } live ? live.WalletBalance(pilotId) : BalanceSql(pilotId);
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
            .Records(CatalogData.Client.Accountingentrytypes)
            .Select(r =>
            {
                var type = r.Value;
                return new WalletEntryDefinition(checked((int)r.Key), type.EntryTypeNameID ?? 0, type.Name ?? "", type.Description ?? "");
            })
            .OrderBy(t => t.Id)
            .ToArray();
    }

    public WalletJournal Journal(long pilotId, HistoryPeriod? period = null)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        long from = period?.From ?? 0;
        long until = period?.Until ?? long.MaxValue;
        return new(
            PilotMarketStore.Transactions(world, pilotId, period),
            PilotCombatStore.Rewards(world, pilotId, period),
            PilotRepairStore.Payments(world, pilotId, period),
            world
                .Set<TestingWalletGrant>()
                .AsNoTracking()
                .Where(g => g.PilotId == pilotId && g.Time >= from && g.Time < until)
                .Select(g => new WalletGrant(g.PilotId, g.AmountCents, g.BalanceCents, g.Time))
                .ToArray(),
            world
                .Set<SavedInsuranceLedger>()
                .Where(entry => entry.PilotId == pilotId && entry.Time >= from && entry.Time < until)
                .OrderBy(entry => entry.Id)
                .AsEnumerable()
                .Select(entry => entry.Snapshot())
                .ToArray(),
            world
                .Set<SavedHomeStationPayment>()
                .Where(payment => payment.PilotId == pilotId && payment.Time >= from && payment.Time < until)
                .OrderBy(payment => payment.Id)
                .AsEnumerable()
                .Select(payment => payment.Snapshot())
                .ToArray(),
            world
                .Set<SavedJumpClonePayment>()
                .Where(payment => payment.PilotId == pilotId && payment.Time >= from && payment.Time < until)
                .OrderBy(payment => payment.Id)
                .AsEnumerable()
                .Select(payment => payment.Snapshot())
                .ToArray(),
            world
                .Set<SavedContractPayment>()
                .Where(payment => payment.PilotId == pilotId && payment.Time >= from && payment.Time < until)
                .OrderBy(payment => payment.Id)
                .AsEnumerable()
                .Select(payment => payment.Snapshot())
                .ToArray(),
            world
                .Set<SavedRedeemedCurrency>()
                .Where(entry =>
                    entry.PilotId == pilotId && entry.Time >= from && entry.Time < until && entry.Kind == RedeemCurrencyKind.Isk
                )
                .OrderByDescending(entry => entry.TokenId)
                .AsEnumerable()
                .Select(entry => entry.Snapshot())
                .ToArray(),
            world
                .Set<SavedCorporationPayment>()
                .Where(p => p.PilotId == pilotId && p.Time >= from && p.Time < until)
                .OrderBy(p => p.Id)
                .AsEnumerable()
                .Select(p => p.Snapshot())
                .ToArray(),
            world
                .Set<SavedMarketPayment>()
                .AsNoTracking()
                .Where(p => p.PilotId == pilotId && p.Time >= from && p.Time < until)
                .OrderBy(p => p.Id)
                .AsEnumerable()
                .Select(p => p.Snapshot())
                .ToArray(),
            world
                .Set<SavedServicePayment>()
                .AsNoTracking()
                .Where(p => p.PilotId == pilotId && p.Time >= from && p.Time < until)
                .OrderBy(p => p.Id)
                .AsEnumerable()
                .Select(p => p.Snapshot())
                .ToArray()
        );
    }
}
