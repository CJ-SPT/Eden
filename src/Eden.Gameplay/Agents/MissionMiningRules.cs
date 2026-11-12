namespace Eden.Gameplay.Agents;

public static class MissionMiningRules
{
    // Private mission deposits follow the public mining site's reserved deposit range.
    public const long FirstDepositId = 8_400_000_000_000;
    public const long EndDepositId = 8_500_000_000_000;
    public const int DepositsPerMission = 100;

    public static bool IsDeposit(long id) => id >= FirstDepositId && id < EndDepositId;

    public static long DepositId(long mission, int slot)
    {
        long id = checked(FirstDepositId + mission * DepositsPerMission + slot);
        if (!IsDeposit(id) || mission <= 0 || slot is < 0 or >= DepositsPerMission)
        {
            throw new InvalidDataException("Mission deposit exceeds its reserved identity range.");
        }
        return id;
    }

    public static long MissionId(long deposit) => (deposit - FirstDepositId) / DepositsPerMission;
}
