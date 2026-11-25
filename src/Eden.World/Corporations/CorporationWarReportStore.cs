using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Economy;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

[Injectable]
internal sealed class CorporationWarReportStore(WorldDatabase database) : ICorporationWarReports
{
    private LiveWorld Resident => LiveWorld.Find(database.Path) ?? throw new InvalidDataException("War reports require a running world.");

    public CorporationWarStatistics Read(long warId) =>
        Resident.Economy.Inspect(world =>
        {
            if (world.Set<SavedCorporationWar>().Find(warId) is not { } war)
            {
                return new CorporationWarStatistics(
                    new Dictionary<int, int>(),
                    new Dictionary<int, decimal>(),
                    new Dictionary<int, WarReportGroupLosses>()
                );
            }
            Dictionary<int, int> ships = [];
            Dictionary<int, decimal> isk = [];
            Dictionary<int, WarReportGroupLosses> groups = [];
            foreach (var row in ((EconomyTransaction)world).ReportedWarKills(warId, war.AttackerId))
            {
                int credited = row.AttackerLoss ? war.DefenderId : war.AttackerId;
                ships[credited] = ships.GetValueOrDefault(credited) + 1;
                isk[credited] = isk.GetValueOrDefault(credited) + row.IskLost;
                var before = groups.GetValueOrDefault(row.GroupId) ?? new(0, 0, 0, 0);
                groups[row.GroupId] = row.AttackerLoss
                    ? before with
                    {
                        AttackerShipLoss = before.AttackerShipLoss + 1,
                        AttackerIskLoss = before.AttackerIskLoss + row.IskLost,
                    }
                    : before with
                    {
                        DefenderShipLoss = before.DefenderShipLoss + 1,
                        DefenderIskLoss = before.DefenderIskLoss + row.IskLost,
                    };
            }
            return new(ships, isk, groups);
        });

    public IReadOnlyList<CorporationWarKill> Kills(long warId, int? ownerId, WarReportGroup? group) =>
        Resident.Economy.Inspect(world =>
        {
            if (world.Set<SavedCorporationWar>().Find(warId) is not { } war)
            {
                return Array.Empty<CorporationWarKill>();
            }
            bool? attackerLoss =
                ownerId == war.AttackerId ? false
                : ownerId == war.DefenderId || ownerId is int ally && world.Set<SavedCorporationWarAlly>().Find(warId, ally) is not null
                    ? true
                : null;
            return ((EconomyTransaction)world)
                .ReportedWarKills(warId, war.AttackerId)
                .Where(row => (attackerLoss is null || row.AttackerLoss == attackerLoss) && (group is null || row.Group == group))
                .OrderByDescending(row => row.Time)
                .ThenByDescending(row => row.Id)
                .Select(row => Snapshot(row.WarId, row.Report, row.IskLost, row.IskDestroyed))
                .ToArray();
        });

    public CorporationWarKill? Kill(long? warId, long killId, string hash) =>
        Resident.Economy.Inspect(world =>
        {
            if (world.Set<SavedCorporationWarKill>().Find(killId) is not { } row || warId is long expected && row.WarId != expected)
            {
                return null;
            }
            var snapshot = Snapshot(row.WarId, row.Report, row.IskLost, row.IskDestroyed);
            return string.Equals(hash, WarReportRules.Hash(snapshot.Report), StringComparison.OrdinalIgnoreCase) ? snapshot : null;
        });

    private static CorporationWarKill Snapshot(long warId, string report, decimal iskLost, decimal iskDestroyed) =>
        new(
            warId,
            JsonSerializer.Deserialize<ShipCombatReport>(report) ?? throw new InvalidDataException("Invalid saved war combat report."),
            iskLost,
            iskDestroyed
        );

    internal static void Save(WorldAction world, CatalogDbContext catalog, ShipCombatReport report, CorporationWarStore.LossWar war)
    {
        if (report.Victim.ShipTypeId is not int hullId || catalog.FittingType(hullId) is not { } hull)
        {
            return;
        }
        var reference = MarketValuations.ReferencePrices(catalog);
        decimal hullValue = Price(hullId);
        var items = Value(report.Items);
        world.Add(
            new SavedCorporationWarKill
            {
                Id = report.Id,
                WarId = war.Id,
                VictimOrganizationId = war.VictimOrganizationId,
                AttackerLoss = war.AttackerLoss,
                GroupId = hull.GroupId,
                Group = WarReportRules.Group((InventoryCategory)hull.CategoryId, hull.GroupId),
                Time = report.Time,
                IskLost = hullValue + items.Lost,
                IskDestroyed = hullValue + items.Destroyed,
                Report = JsonSerializer.Serialize(report),
            }
        );

        decimal Price(int type) =>
            EconomicStore.AdjustedPrice(world, type) is > 0 and var adjusted ? adjusted
            : reference.TryGetValue(type, out decimal quoted) ? quoted
            : InsuranceRules.CatalogValue(catalog.Read(CatalogData.Client.Types, (long)type)?.BasePrice)
                / (decimal)MarketDefinitions.CentsPerIsk;

        (decimal Lost, decimal Destroyed) Value(IReadOnlyList<CombatLostItem> contents)
        {
            decimal lost = 0;
            decimal destroyed = 0;
            foreach (var item in contents)
            {
                decimal price = Price(item.TypeId);
                var nested = Value(item.Contents);
                lost += nested.Lost + price * ((long)item.Dropped + item.Destroyed);
                destroyed += nested.Destroyed + price * item.Destroyed;
            }
            return (lost, destroyed);
        }
    }
}
