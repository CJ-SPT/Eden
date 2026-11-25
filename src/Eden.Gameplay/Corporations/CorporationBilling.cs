namespace Eden.Gameplay.Corporations;

public sealed record CorporationBillPaymentRule(int BillTypeId, bool Enabled);

public sealed record CorporationBillPaymentSettings(int CorporationId, int DivisionKey, IReadOnlyList<CorporationBillPaymentRule> Rules);

public interface ICorporationBilling
{
    CorporationBillPaymentSettings Read(long actorId);
    void Save(long actorId, CorporationBillPaymentSettings settings);
    IReadOnlyList<CorporationWarInvoice> WarInvoices(long actorId, bool alliance = false);
    bool PayWarInvoice(long actorId, long billId, int divisionKey, bool alliance = false);
}
