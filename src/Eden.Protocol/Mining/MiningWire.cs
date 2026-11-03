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
                ["quantity_removed"] = Yield.Extracted,
                ["hasRewards"] = false,
                ["amountWasted"] = 0,
                ["amountCritBonus"] = Yield.CriticalBonus,
                ["itemID"] = AsteroidId,
                ["moduleItemID"] = ModuleId,
            },
        ];
}
