using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Storage;

namespace Eden.World.Corporations;

[Injectable]
internal sealed class CorporationBillingStore(WorldDatabase database) : ICorporationBilling
{
    public CorporationBillPaymentSettings Read(long actorId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var corporation = Authorize(world, actorId, false);
        var saved = world.Set<SavedCorporationBillPaymentSettings>().Find(corporation.Id);
        var settings = saved is null ? null : JsonSerializer.Deserialize<CorporationBillPaymentSettings>(saved.DataJson);
        var rules = BillTypes()
            .Select(id => new CorporationBillPaymentRule(id, settings?.Rules.Any(r => r.BillTypeId == id && r.Enabled) == true));
        return new(corporation.Id, settings?.DivisionKey ?? CorporationWalletRules.MasterKey, rules.ToArray());
    }

    public void Save(long actorId, CorporationBillPaymentSettings settings)
    {
        CorporationWalletRules.CheckKey(settings.DivisionKey);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
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
        var saved =
            world.Set<SavedCorporationBillPaymentSettings>().Find(corporation.Id)
            ?? world.Add(new SavedCorporationBillPaymentSettings { CorporationId = corporation.Id }).Entity;
        saved.DataJson = JsonSerializer.Serialize(settings);
        world.SaveChanges();
        transaction.Commit();
    }

    private int[] BillTypes()
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return catalog
            .Set<SourceRecord>()
            .Where(r => r.Dataset == "client/billtypes")
            .Select(r => r.Key)
            .AsEnumerable()
            .Select(key => int.Parse(key, CultureInfo.InvariantCulture))
            .Order()
            .ToArray();
    }

    private static SavedCorporation Authorize(WorldDbContext world, long actorId, bool write)
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
