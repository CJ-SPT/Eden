using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Characters;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Combat;

[Injectable(InjectionType.Scoped)]
internal sealed class SmartbombHandler(TimeProvider clock, IPilotSmartbombs smartbombs, CharacterInteractionHistoryHandler history)
    : IModuleEffectSource
{
    internal const string EffectName = "empWave";
    private const string Graphic = "effects.EMPWave";

    private sealed record Cycle(DogmaActiveEffect Effect, WeaponContact Contact, long ReadyAt, bool Stopping = false);

    private readonly Dictionary<long, Cycle> _cycles = [];

    private long Now() => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycles.TryGetValue(module, out var cycle) && cycle.Effect.ShipId == pilot.ShipId ? cycle.Effect : null;

    public void CancelModule(PilotCallContext context, long module)
    {
        if (_cycles.Remove(module, out var cycle))
        {
            long now = Now();
            context.Notifications.Add(new ShipEffectChanged(cycle.Effect, now, false, false, now));
            if (context.Pilot.ShipId == cycle.Effect.ShipId)
            {
                context.Flight.SetWeaponEffect(cycle.Contact, false);
            }
        }
    }

    internal void Cancel(PilotCallContext context)
    {
        foreach (long module in _cycles.Keys.ToArray())
        {
            CancelModule(context, module);
        }
    }

    internal void Advance(PilotCallContext context)
    {
        if (!context.Selected || _cycles.Count == 0)
        {
            return;
        }
        long now = Now();
        foreach (var (module, cycle) in _cycles.ToArray())
        {
            if (cycle.Effect.ShipId != context.Pilot.ShipId || !context.Flight.CanTarget)
            {
                CancelModule(context, module);
                continue;
            }
            if (now < cycle.ReadyAt)
            {
                continue;
            }
            if (!cycle.Stopping && cycle.Effect.Repeats > 0)
            {
                try
                {
                    Pulse(context, module, cycle.Effect.Repeats - 1, now, cycle);
                    continue;
                }
                catch (Exception error) when (error is InvalidDataException or OverflowException) { }
            }
            CancelModule(context, module);
        }
    }

    internal PilotCallResult Call(PilotCallContext context, string method, object?[] args)
    {
        if (method == "Deactivate" && args is [long stopping, _])
        {
            if (_cycles.TryGetValue(stopping, out var cycle))
            {
                _cycles[stopping] = cycle with { Stopping = true };
            }
            return PilotCallResult.None;
        }
        if (method != "Activate" || args is not [long module, _, var target, long repeats])
        {
            return PilotCallResult.Unsupported;
        }
        if (!context.Flight.CanTarget || target is not null && !Equals(target, context.Pilot.ShipId) || repeats is < 0 or > int.MaxValue)
        {
            return new PilotCallResult.Failure("Activate a smartbomb without a target while in space outside warp.");
        }
        if (_cycles.ContainsKey(module))
        {
            return PilotCallResult.None;
        }
        try
        {
            context.Flight.Checkpoint();
            Pulse(context, module, repeats, Now());
            return PilotCallResult.None;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private void Pulse(PilotCallContext context, long module, long repeats, long now, Cycle? previous = null)
    {
        var pilot = context.Pilot;
        var result = smartbombs.Pulse(pilot.PilotId, pilot.ShipId, module, context.Flight.SmartbombCandidates(), now);
        var fitted = result.Module;
        DogmaActiveEffect effect = new(
            module,
            pilot.PilotId,
            pilot.ShipId,
            null,
            DogmaEffect.Smartbomb,
            now,
            fitted.DurationMilliseconds,
            repeats
        );
        WeaponContact contact = new(module, fitted.TypeId, 0, null, now, fitted.DurationMilliseconds, Graphic);
        _cycles[module] = new(effect, contact, result.ReadyAt);
        if (previous is null)
        {
            context.Notifications.Add(new ShipEffectChanged(effect, now, true, true));
            context.Flight.SetWeaponEffect(contact, true);
        }
        else
        {
            context.Flight.RepeatWeaponEffect(previous.Contact, contact);
        }
        foreach (var hit in result.Hits)
        {
            context.NotificationsBeforeFlight.Add(
                new DamageMessageNotification(
                    new OutgoingCombatMessage(hit.Applied, CombatMessages.HitQuality(1), hit.Health.ShipId, fitted.TypeId, fitted.Damage)
                )
            );
            context.Flight.NotifyHealth(hit.Health);
        }
        if (result.Hits.Any(hit => hit.Health.Remaining.Destroyed))
        {
            history.NotifyDestruction(context);
            context.Flight.RefreshWrecks();
        }
        if (result.Hits.Count > 0)
        {
            context.Flight.RefreshNpcs();
            context.Flight.RefreshDrones();
        }
        if (result.InterceptedMissiles)
        {
            context.Flight.RefreshMissiles();
        }
        if (result.Hits.LastOrDefault(hit => hit.Reward is not null)?.Reward is { } reward)
        {
            context.Notifications.Add(
                new AccountBalanceChanged(pilot.PilotId, reward.BalanceCents / (double)MarketDefinitions.CentsPerIsk)
            );
        }
        context.Notifications.Add(
            new ModuleAttributesChanged([
                new(pilot.PilotId, new DogmaItemKey.Item(pilot.ShipId), DogmaAttributeId.CapacitorCharge, now, result.Capacitor),
            ])
        );
    }
}
