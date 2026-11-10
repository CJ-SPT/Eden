namespace Eden.Gameplay.Market;

public interface IMarketDemandMaintenance
{
    int AdvanceDemand(int limit = PersonalMarketRules.MaintenanceBatch);
}
