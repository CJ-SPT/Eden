using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

[Injectable]
internal sealed class CorporationBillingStore(WorldDatabase database, TimeProvider clock) : ICorporationBilling
{
    public IReadOnlyList<CorporationWarInvoice> WarInvoices(long actorId, bool alliance = false)
    {
        var resident = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("War bills require an active resident world.");
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        return resident.Economy.Inspect<IReadOnlyList<CorporationWarInvoice>>(world =>
        {
            var corporation = Authorize(world, actorId, false);
            int debtor = corporation.Id;
            int payer = corporation.Id;
            if (alliance)
            {
                debtor = world.Set<SavedAllianceMember>().Find(corporation.Id)?.AllianceId ?? 0;
                if (world.Set<SavedAlliance>().Find(debtor) is not { ClosedAt: null } owner)
                {
                    throw new InvalidDataException("Alliance financial access is required for bills.");
                }
                if (owner.ExecutorCorporationId is not int executor)
                {
                    return [];
                }
                payer = executor;
            }
            return CorporationWarStore.Invoices(world, payer, now).Where(invoice => invoice.DebtorId == debtor).ToArray();
        });
    }

    public bool PayWarInvoice(long actorId, long billId, int divisionKey, bool alliance = false)
    {
        CorporationWalletRules.CheckKey(divisionKey);
        var resident = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("War bills require an active resident world.");
        using var world = resident.Economy.Begin(TransactionParticipants.None);
        _ = Authorize(world, actorId, true);
        if (!CorporationWarStore.PayInvoice(world, actorId, billId, divisionKey, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(), alliance))
        {
            return false;
        }
        world.Commit();
        return true;
    }

    public CorporationBillPaymentSettings Read(long actorId) => ReadAutomaticPayment(actorId).Corporation;

    public CorporationBillPaymentSnapshot ReadAutomaticPayment(long actorId)
    {
        return WorldActionScope.Inspect<CorporationBillPaymentSnapshot>(
            database,
            world =>
            {
                var corporation = Authorize(world, actorId, false);
                var settings = Saved(world, corporation.Id);
                var rules = BillTypes()
                    .Select(id => new CorporationBillPaymentRule(id, settings?.Rules.Any(r => r.BillTypeId == id && r.Enabled) == true));
                return new(
                    new(corporation.Id, settings?.DivisionKey ?? CorporationWalletRules.MasterKey, rules.ToArray()),
                    MaintenanceAlliance(world, corporation.Id)
                );
            }
        );
    }

    public void Save(long actorId, CorporationBillPaymentSettings settings, AllianceMaintenancePaymentRule? maintenance = null)
    {
        CorporationWalletRules.CheckKey(settings.DivisionKey);
        using WorldActionScope transaction = new(database, TransactionParticipants.None);
        var world = transaction.World;
        var corporation = Authorize(world, actorId, true);
        var billTypes = BillTypes();
        if (
            settings.CorporationId != corporation.Id
            || settings.Rules.Select(r => r.BillTypeId).Distinct().Count() != settings.Rules.Count
            || settings.Rules.Any(r => !billTypes.Contains(r.BillTypeId))
        )
        {
            throw new InvalidDataException("Choose your corporation and known bill types.");
        }
        if (!CorporationWalletRules.CanTake(CorporationRoleStore.Effective(world, actorId, corporation).Roles, settings.DivisionKey))
        {
            throw new InvalidDataException("Withdrawal access is required for the automatic payment division.");
        }
        if (maintenance is not null)
        {
            if (MaintenanceAlliance(world, corporation.Id) != maintenance.AllianceId)
            {
                throw new InvalidDataException("Choose the alliance your corporation currently executes.");
            }
            var rules = settings.Rules.Where(rule => rule.BillTypeId != AllianceBillingRules.MaintenanceBillType);
            settings = settings with { Rules = [.. rules, new(AllianceBillingRules.MaintenanceBillType, maintenance.Enabled)] };
        }
        Store(world, settings);
        transaction.Commit();
    }

    private static int? MaintenanceAlliance(WorldAction world, int corporationId) =>
        world.Set<SavedAllianceMember>().Find(corporationId) is { } member
        && world.Set<SavedAlliance>().Find(member.AllianceId) is { ClosedAt: null } alliance
        && alliance.ExecutorCorporationId == corporationId
            ? member.AllianceId
            : null;

    internal static CorporationBillPaymentSettings? Saved(WorldAction world, int corporation) =>
        world.Set<SavedCorporationBillPaymentSettings>().Find(corporation) is { } row
            ? JsonSerializer.Deserialize<CorporationBillPaymentSettings>(row.DataJson)
            : null;

    internal static bool DefaultAllianceMaintenance(WorldAction world, long actorId)
    {
        var corporation = Authorize(world, actorId, true);
        var settings = Saved(world, corporation.Id) ?? new(corporation.Id, CorporationWalletRules.MasterKey, []);
        if (
            settings.Rules.All(rule => rule.BillTypeId != AllianceBillingRules.MaintenanceBillType)
            && CorporationWalletRules.CanTake(CorporationRoleStore.Effective(world, actorId, corporation).Roles, settings.DivisionKey)
        )
        {
            Store(world, settings with { Rules = [.. settings.Rules, new(AllianceBillingRules.MaintenanceBillType, true)] });
            return true;
        }
        return false;
    }

    private static void Store(WorldAction world, CorporationBillPaymentSettings settings)
    {
        var saved =
            world.Set<SavedCorporationBillPaymentSettings>().Find(settings.CorporationId)
            ?? world.Add(new SavedCorporationBillPaymentSettings { CorporationId = settings.CorporationId }).Entity;
        saved.DataJson = JsonSerializer.Serialize(settings);
    }

    private int[] BillTypes()
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return catalog.Records(CatalogData.Client.Billtypes).Keys.Select(id => checked((int)id)).Order().ToArray();
    }

    private static SavedCorporation Authorize(WorldAction world, long actorId, bool write)
    {
        var corporation = CorporationRoleStore.Current(world, actorId);
        var required = write ? CorporationRole.Accountant : CorporationRole.Accountant | CorporationRole.JuniorAccountant;
        if (!CorporationRoleStore.Has(world, actorId, corporation, required))
        {
            throw new InvalidDataException("Corporation financial access is required for bills.");
        }
        return corporation;
    }
}
