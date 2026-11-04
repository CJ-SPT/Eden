namespace Eden.Gameplay.Market;

public sealed record LoyaltyBalance(int IssuerCorporationId, long Points);

public interface IPilotLoyaltyWallet
{
    IReadOnlyList<LoyaltyBalance> Read(long pilotId);
}
