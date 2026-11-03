using Eden.Gameplay.Mining;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Mining;

internal sealed record OreMined(MiningYield Yield, long AsteroidId, long ModuleId) : PilotNotification("OnOreMined")
{
    public override object?[] Arguments =>
        [
            new Dictionary<string, object?>
            {
                ["oreType"] = Yield.TypeId,
                ["quantity_added"] = Yield.Extracted + Yield.CriticalBonus,
                // The native scanner adds amountWasted to quantity_removed itself.
                ["quantity_removed"] = Yield.Extracted,
                ["hasRewards"] = false,
                ["amountWasted"] = Yield.Residue,
                ["amountCritBonus"] = Yield.CriticalBonus,
                ["itemID"] = AsteroidId,
                ["moduleItemID"] = ModuleId,
            },
        ];
}
