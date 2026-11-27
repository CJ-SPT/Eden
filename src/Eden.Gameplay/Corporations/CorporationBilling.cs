namespace Eden.Gameplay.Corporations;

public sealed record CorporationBillPaymentRule(int BillTypeId, bool Enabled);

public sealed record CorporationBillPaymentSettings(int CorporationId, int DivisionKey, IReadOnlyList<CorporationBillPaymentRule> Rules);

public sealed record AllianceMaintenancePaymentRule(int AllianceId, bool Enabled);

public sealed record CorporationBillPaymentSnapshot(CorporationBillPaymentSettings Corporation, int? MaintenanceAllianceId);

public interface ICorporationBilling
{
    CorporationBillPaymentSettings Read(long actorId);
    CorporationBillPaymentSnapshot ReadAutomaticPayment(long actorId);
    void Save(long actorId, CorporationBillPaymentSettings settings, AllianceMaintenancePaymentRule? maintenance = null);
    IReadOnlyList<CorporationWarInvoice> WarInvoices(long actorId, bool alliance = false);
    bool PayWarInvoice(long actorId, long billId, int divisionKey, bool alliance = false);
}
