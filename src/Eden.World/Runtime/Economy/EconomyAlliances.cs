namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private void RegisterAlliances()
    {
        RegisterAlliance();
        RegisterAllianceMember();
        RegisterAllianceApplication();
        RegisterAllianceHistory();
        RegisterAllianceChange();
        RegisterAllianceBill();
        RegisterAllianceLifecycle();
        RegisterAllianceSocial();
    }
}
