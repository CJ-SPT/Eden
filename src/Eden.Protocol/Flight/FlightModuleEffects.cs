using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Protocol.Combat;

namespace Eden.Protocol.Flight;

internal sealed record FlightPropulsionChange(PropulsionContact? Previous, PropulsionContact? Current);

internal sealed class FlightModuleEffects(FlightProperties flight, FittedSpaceModule[]? modules, FlightEffects effects)
{
    internal FittedSpaceModule[]? Modules { get; private set; } = modules;
    internal MiningContact[] Mining { get; private set; } = [];
    internal WeaponContact[] Weapons { get; private set; } = [];
    internal ShieldBoostContact[] ShieldBoosts { get; private set; } = [];
    internal RemoteRepairCycle[] RemoteRepairs { get; private set; } = [];
    internal CapacitorCycle[] CapacitorEffects { get; private set; } = [];
    internal LocalRepairContact[] LocalRepairs { get; private set; } = [];
    internal HardenerContact[] Hardeners { get; private set; } = [];
    internal SupportContact[] Support { get; private set; } = [];
    internal ElectronicWarfareSpaceContact[] ElectronicWarfare { get; private set; } = [];
    internal TackleContact[] TackleEffects { get; private set; } = [];
    internal TackleEffect[] IncomingTackle { get; private set; } = [];
    internal bool WarpBlocked { get; private set; }
    internal bool MwdBlocked { get; private set; }
    internal PropulsionContact? Propulsion { get; private set; }
    internal FlightProperties BaseFlight { get; private set; } = flight;

    internal FlightProperties Flight =>
        (MwdBlocked && Propulsion?.Module.Effect == DogmaEffect.Microwarpdrive ? null : Propulsion)?.Module.Apply(BaseFlight) ?? BaseFlight;

    internal void SetBaseFlight(FlightProperties flight) => BaseFlight = flight;

    internal void SetBaseSpeed(double speed) => BaseFlight = BaseFlight with { MaxSpeed = (float)speed };

    internal FlightPropulsionChange SetPropulsion(PropulsionContact? effect)
    {
        var change = new FlightPropulsionChange(Propulsion, effect);
        Propulsion = effect;
        return change;
    }

    internal void ReplayPropulsion(long shipId, FlightPropulsionChange change)
    {
        if (change.Previous is not null)
        {
            effects.PropulsionEffect(shipId, change.Previous, false);
        }
        if (change.Current is not null)
        {
            effects.PropulsionEffect(shipId, change.Current, true);
        }
    }

    internal void AdoptHull(FlightProperties flight)
    {
        BaseFlight = flight;
        Modules = [];
        ClearEffects();
    }

    internal void ClearEffects()
    {
        Propulsion = null;
        Mining = [];
        Weapons = [];
        ShieldBoosts = [];
        LocalRepairs = [];
        RemoteRepairs = [];
        CapacitorEffects = [];
        Hardeners = [];
        Support = [];
        TackleEffects = [];
        ElectronicWarfare = [];
        IncomingTackle = [];
        WarpBlocked = false;
        MwdBlocked = false;
    }

    internal void ReconcileReleasedTackle(IPilotTackle? tackle, long pilotId)
    {
        if (tackle is not null && IncomingTackle.Length > 0)
        {
            BaseFlight = BaseFlight with
            {
                MaxSpeed = (float)tackle.ShipAttributes(pilotId).GetValueOrDefault(DogmaAttributeId.MaxVelocity),
            };
        }
    }

    internal void SetMiningEffect(long shipId, MiningContact effect, bool active)
    {
        Mining = Mining.Where(m => m.ModuleId != effect.ModuleId).Concat(active ? [effect] : Array.Empty<MiningContact>()).ToArray();
        effects.MiningEffect(shipId, effect, active);
    }

    internal void SetElectronicWarfareEffect(long shipId, ElectronicWarfareSpaceContact effect, bool active)
    {
        ElectronicWarfare = ElectronicWarfare
            .Where(e => e.Module.ItemId != effect.Module.ItemId)
            .Concat(active ? [effect] : Array.Empty<ElectronicWarfareSpaceContact>())
            .ToArray();
        effects.ElectronicWarfareEffect(shipId, effect, active);
    }

    internal void SetTackleEffect(long shipId, TackleContact effect, bool active)
    {
        TackleEffects = TackleEffects
            .Where(t => t.Module.ItemId != effect.Module.ItemId)
            .Concat(active ? [effect] : Array.Empty<TackleContact>())
            .ToArray();
        effects.TackleEffect(shipId, effect, active);
    }

    internal void SetWeaponEffect(long shipId, WeaponContact effect, bool active)
    {
        Weapons = Weapons.Where(w => w.ModuleId != effect.ModuleId).Concat(active ? [effect] : Array.Empty<WeaponContact>()).ToArray();
        effects.WeaponEffect(shipId, effect, active);
    }

    internal void RepeatWeaponEffect(long shipId, WeaponContact previous, WeaponContact current)
    {
        Weapons = Weapons.Where(w => w.ModuleId != current.ModuleId).Append(current).ToArray();
        effects.WeaponEffect(shipId, previous, false);
        effects.WeaponEffect(shipId, current, true);
    }

    internal void SetRemoteRepairEffect(long shipId, RemoteRepairCycle repair, bool active)
    {
        RemoteRepairs = RemoteRepairs
            .Where(r => r.Module.ItemId != repair.Module.ItemId)
            .Concat(active ? [repair] : Array.Empty<RemoteRepairCycle>())
            .ToArray();
        effects.RemoteRepairEffect(shipId, repair, active);
    }

    internal void SetCapacitorEffect(CapacitorCycle effect, bool active)
    {
        CapacitorEffects = CapacitorEffects
            .Where(c => c.Module.ItemId != effect.Module.ItemId)
            .Concat(active ? [effect] : Array.Empty<CapacitorCycle>())
            .ToArray();
        effects.CapacitorEffect(effect, active);
    }

    internal void SetLocalRepairEffect(long shipId, LocalRepairContact effect, bool active)
    {
        LocalRepairs = LocalRepairs
            .Where(r => r.ModuleId != effect.ModuleId)
            .Concat(active ? [effect] : Array.Empty<LocalRepairContact>())
            .ToArray();
        effects.LocalRepairEffect(shipId, effect, active);
    }

    internal void SetHardenerEffect(long shipId, HardenerContact effect, bool active)
    {
        Hardeners = Hardeners
            .Where(h => h.Module.ItemId != effect.Module.ItemId)
            .Concat(active ? [effect] : Array.Empty<HardenerContact>())
            .ToArray();
        effects.HardenerEffect(shipId, effect, active);
    }

    internal void SetSupportEffect(long shipId, SupportContact effect, bool active)
    {
        Support = Support
            .Where(h => h.Module.ItemId != effect.Module.ItemId)
            .Concat(active ? [effect] : Array.Empty<SupportContact>())
            .ToArray();
        effects.SupportEffect(shipId, effect, active);
    }

    internal void SetShieldBoostEffect(long shipId, ShieldBoostContact effect, bool active)
    {
        ShieldBoosts = ShieldBoosts
            .Where(b => b.ModuleId != effect.ModuleId)
            .Concat(active ? [effect] : Array.Empty<ShieldBoostContact>())
            .ToArray();
        effects.ShieldBoostEffect(shipId, effect, active);
    }

    internal void RefreshModules(IReadOnlyList<InventoryItem> items) =>
        Modules = items
            .Where(i => InventoryDefinitions.IsHighSlot(i.FlagId))
            .Select(i => new FittedSpaceModule(i.Id, i.TypeId, i.FlagId))
            .ToArray();

    internal DockedPilot RefreshFitting(DockedPilot pilot, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        BaseFlight = BaseFlight with
        {
            MaxSpeed = (float)attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity),
            WarpSpeed =
                attributes.GetValueOrDefault(DogmaAttributeId.BaseWarpSpeed)
                * attributes.GetValueOrDefault(DogmaAttributeId.WarpSpeedMultiplier),
            Mass = attributes.GetValueOrDefault(DogmaAttributeId.Mass),
            Agility = (float)attributes.GetValueOrDefault(DogmaAttributeId.Agility),
        };
        return pilot with
        {
            Ship = pilot.Ship with
            {
                CargoCapacity = attributes.GetValueOrDefault(DogmaAttributeId.CargoCapacity),
                Hull = new(
                    attributes.GetValueOrDefault(DogmaAttributeId.StructureHitPoints),
                    attributes.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity),
                    attributes.GetValueOrDefault(DogmaAttributeId.CapacitorRechargeTime),
                    attributes.GetValueOrDefault(DogmaAttributeId.ShieldCapacity),
                    attributes.GetValueOrDefault(DogmaAttributeId.ArmorHitPoints),
                    attributes.GetValueOrDefault(DogmaAttributeId.ShieldRechargeTime)
                ),
            },
        };
    }

    internal bool RefreshTackle(IPilotTackle tackle, DockedPilot pilot, long now, bool force, bool aligning)
    {
        tackle.Sweep(now);
        var incoming = tackle.Incoming(pilot.ShipId).ToArray();
        if (!force && IncomingTackle.SequenceEqual(incoming) && !aligning)
        {
            return false;
        }
        IncomingTackle = incoming;
        var attributes = tackle.ShipAttributes(pilot.PilotId);
        WarpBlocked = attributes.GetValueOrDefault(DogmaAttributeId.WarpScrambleStatus) > 0;
        MwdBlocked = incoming.Any(t => t.Module.MwdBlock > 0);
        BaseFlight = BaseFlight with { MaxSpeed = (float)attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity) };
        return true;
    }
}
