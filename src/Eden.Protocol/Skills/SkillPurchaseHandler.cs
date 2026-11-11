using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Skills;

[Injectable]
internal sealed class SkillPurchaseHandler(IPilotSkillPurchases purchases, IPilotSkillReader skills, TimeProvider clock)
    : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "@skills" || method != "PurchaseSkills")
        {
            return PilotCallResult.Unsupported;
        }
        if (
            args is not [List<object?> ids]
            || ids.Any(id => id is not long value || value is <= 0 or > int.MaxValue)
            || keywords?.Count > 0
        )
        {
            return new PilotCallResult.Failure("Expected a list of skill type IDs.");
        }
        try
        {
            var purchase = purchases.Purchase(context.Pilot.PilotId, ids.Cast<long>().Select(id => (int)id).ToArray());
            if (purchase.TypeIds.Count > 0)
            {
                var updates = new BlueMap<int, TrainedSkillReply>(
                    skills
                        .ReadSkills(context.Pilot.PilotId)
                        .Where(skill => purchase.TypeIds.Contains(skill.TypeId))
                        .ToDictionary(skill => skill.TypeId, skill => new TrainedSkillReply(skill))
                );
                context.Notifications.Add(new ServerSkillsChanged(updates, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()));
                context.Notifications.Add(
                    new AccountBalanceChanged(context.Pilot.PilotId, (double)purchase.BalanceCents / MarketDefinitions.CentsPerIsk)
                );
            }
            return new BlueList<int>(purchase.TypeIds);
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
