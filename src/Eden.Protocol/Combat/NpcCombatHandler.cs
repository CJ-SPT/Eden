using System.Security.Cryptography;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Repair;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Combat;

[Injectable(InjectionType.Scoped)]
internal sealed class NpcCombatHandler(TimeProvider clock, INpcCombat combat, SharedTargetLocks locks,
    MiningHandler mining, CombatHandler weapons, ShieldBoostHandler shields, PropulsionHandler propulsion, SharedSpace space) : IDisposable
{
    private const int ProbabilityResolution = 1_000_000;
    private long _shipId;
    private long[] _aggressors = [];

    public void Enter(PilotCallContext context)
    {
        // Reconnecting starts without an offline fight or overdue burst. Local
        // detection can establish a fresh fight with a new reaction interval.
        combat.Disengage(context.Pilot.ShipId);
        _shipId = context.Pilot.ShipId;
    }

    private void Clear()
    {
        foreach (long npc in _aggressors) { locks.Remove(npc, _shipId); }
        if (_shipId != 0) { combat.Disengage(_shipId); }
        _aggressors = [];
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected) { return; }
        if (_shipId != context.Pilot.ShipId)
        {
            Clear();
            _shipId = context.Pilot.ShipId;
        }
        if (!context.Flight.CanTarget || context.Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup)
        {
            Clear();
            return;
        }
        if (context.Pilot.SystemId == NpcEncounterDefinitions.PerimeterSystem)
        {
            combat.Acquire(space.NpcTargets(context.Pilot.SystemId), clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        }
        foreach (long npc in combat.Aggressors(_shipId))
        {
            if (!context.Flight.VisibleNpcs.Any(n => n.Id == npc))
            {
                combat.Disengage(_shipId, npc);
                continue;
            }
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            var shot = combat.Fire(context.Pilot.PilotId, _shipId, npc, context.Flight.Velocity, now,
                RandomNumberGenerator.GetInt32(ProbabilityResolution) / (double)ProbabilityResolution);
            if (shot is null) { continue; }
            context.NotificationsBeforeFlight.Add(("OnDamageMessage", [CombatMessages.Incoming(shot)]));
            context.Flight.NotifyNpcWeapon(shot.NpcId, shot.Weapon);
            context.Flight.NotifyHealth(shot.Health);
            context.Notifications.Add(("OnModuleAttributeChanges", [new List<object?>
            {
                Attribute(DogmaAttributeId.ShieldCharge, shot.Health.Remaining.Shield),
                Attribute(DogmaAttributeId.ArmorDamage, shot.Health.Defense.Armor - shot.Health.Remaining.Armor),
                Attribute(DogmaAttributeId.Damage, shot.Health.Defense.Structure - shot.Health.Remaining.Structure),
            }]));
            if (shot.Capsule is { } capsule)
            {
                context.Flight.BeginShipLoss();
                mining.Cancel(context);
                weapons.Cancel(context);
                shields.Cancel(context);
                propulsion.Cancel(context);
                // Wreck.Prepare links launcherID to the still-loaded hull for
                // rotation and explosion timing before its terminal removal.
                context.Flight.RefreshWrecks();
                context.ReplaceDestroyedShip(capsule);
                Clear();
                _shipId = capsule.ShipId;
                break;
            }

            object?[] Attribute(DogmaAttributeId attribute, double value) =>
                ["OnModuleAttributeChange", context.Pilot.PilotId, shot.Health.ShipId, (int)attribute, now, value, null, now];
        }
        var current = combat.Aggressors(_shipId).ToArray();
        foreach (long npc in _aggressors.Except(current)) { locks.Remove(npc, _shipId); }
        foreach (long npc in current.Except(_aggressors)) { locks.Add(npc, _shipId); }
        _aggressors = current;
        context.Flight.RefreshNpcs();
    }

    public void Dispose() => Clear();
}
