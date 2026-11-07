using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Market;
using Eden.Protocol.Characters;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Combat;

[Injectable(InjectionType.Scoped)]
internal sealed class PilotDamageMonitor(
    PilotDamageSignal signal,
    ShipLossCoordinator loss,
    CharacterInteractionHistoryHandler history,
    TimeProvider clock
) : IDisposable
{
    private const int MaximumPendingMessages = 512;
    private readonly Queue<PilotDamage> _pending = new();
    private long _pilotId;

    internal void Enter(PilotCallContext context)
    {
        Dispose();
        _pilotId = context.Pilot.PilotId;
        signal.Applied += Receive;
    }

    private void Receive(PilotDamage damage)
    {
        lock (_pending)
        {
            if (damage.PilotId != _pilotId)
            {
                return;
            }
            if (_pending.Count == MaximumPendingMessages)
            {
                _pending.Dequeue();
            }
            _pending.Enqueue(damage);
        }
    }

    internal void Advance(PilotCallContext context)
    {
        PilotDamage[] pending;
        lock (_pending)
        {
            pending = _pending.ToArray();
            _pending.Clear();
        }
        // Never hold the inbox lock while loss handling enters world persistence.
        foreach (var damage in pending)
        {
            var shot = damage.Shot;
            if (!context.Selected || shot.Health.ShipId != context.Pilot.ShipId)
            {
                continue;
            }
            context.NotificationsBeforeFlight.Add(new DamageMessageNotification(new PilotIncomingCombatMessage(damage)));
            context.Flight.NotifyHealth(shot.Health);
            context.Notifications.Add(
                new ModuleAttributesChanged([
                    Attribute(DogmaAttributeId.ShieldCharge, shot.Health.Remaining.Shield),
                    Attribute(DogmaAttributeId.ArmorDamage, shot.Health.Defense.Armor - shot.Health.Remaining.Armor),
                    Attribute(DogmaAttributeId.Damage, shot.Health.Defense.Structure - shot.Health.Remaining.Structure),
                ])
            );
            if (damage.Capsule is { } capsule)
            {
                history.NotifyDestruction(context);
                if (damage.Insurance is { } payout)
                {
                    context.Notifications.Add(
                        new AccountBalanceChanged(context.Pilot.PilotId, payout.BalanceCents / (double)MarketDefinitions.CentsPerIsk)
                    );
                }
                loss.Replace(context, capsule);
            }
        }

        ModuleAttributeChange Attribute(DogmaAttributeId attribute, double value) =>
            new(
                context.Pilot.PilotId,
                new DogmaItemKey.Item(context.Pilot.ShipId),
                attribute,
                clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
                value
            );
    }

    public void Dispose()
    {
        signal.Applied -= Receive;
        lock (_pending)
        {
            _pilotId = 0;
            _pending.Clear();
        }
    }
}
