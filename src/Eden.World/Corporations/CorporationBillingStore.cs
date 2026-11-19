using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

[Injectable]
internal sealed class CorporationBillingStore(WorldDatabase database) : ICorporationBilling
{
    public CorporationBillPaymentSettings Read(long actorId)
    {
        return WorldActionScope.Inspect<CorporationBillPaymentSettings>(
            database,
            world =>
            {
                var corporation = Authorize(world, actorId, false);
                var settings = Saved(world, corporation.Id);
                var rules = BillTypes()
                    .Select(id => new CorporationBillPaymentRule(id, settings?.Rules.Any(r => r.BillTypeId == id && r.Enabled) == true));
                return new(corporation.Id, settings?.DivisionKey ?? CorporationWalletRules.MasterKey, rules.ToArray());
            }
        );
    }

    public void Save(long actorId, CorporationBillPaymentSettings settings)
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
        Store(world, settings);
        transaction.Commit();
    }

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
