using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Mining;
using Eden.Data.World.Repair;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Combat.Weapons;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Mining;
using Eden.World.Repair;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    private sealed record BankHeatProfile(ResidentMap<long, CombatWeapon> Weapons, ResidentMap<long, double> Absorption);

    // Bank membership and fitted definitions are replaced on a fit reload.
    // Keep this cache tied to that generation rather than a module ID alone.
    private readonly ConditionalWeakTable<LiveTurretDefinition, BankHeatProfile> _bankHeat = new();

    private BankHeatProfile OverloadedTurrets(LiveCombatShip ship, IReadOnlyList<long> members)
    {
        var first = ship.Turrets.GetValueOrDefault(members[0]) ?? throw new InvalidDataException("Use a fitted turret.");
        if (
            _bankHeat.TryGetValue(first.Definition, out var cached)
            && members.Count == cached.Weapons.Count
            && members.All(cached.Weapons.ContainsKey)
        )
        {
            return cached;
        }
        var profile = WorldActionScope.Inspect(database, world => BuildOverloadedTurrets(world, ship, members), consistentFileRead: true);
        _bankHeat.AddOrUpdate(first.Definition, profile);
        return profile;
    }

    private BankHeatProfile BuildOverloadedTurrets(WorldAction world, LiveCombatShip ship, IReadOnlyList<long> members)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Filter<Pilot>(p => p.Id == ship.PilotId).Single();
        var fit = FittingCalculation.Read(world, catalog, pilot, overloadedModules: members.ToHashSet());
        BankHeatProfile profile = new([], []);
        foreach (long member in members)
        {
            var item = fit.Modules.Single(m => m.Item.Id == member).Item;
            var values = PilotCombatStore.ModuleAttributes(world, catalog, pilot.Id, PilotInventoryStore.Read(catalog, item), fit);
            var turret = ship.Turrets[member].Definition;
            var damage = turret.ChargeAttributes;
            DamageVector vector = new(
                damage.GetValueOrDefault(DogmaAttributeId.EmDamage),
                damage.GetValueOrDefault(DogmaAttributeId.ExplosiveDamage),
                damage.GetValueOrDefault(DogmaAttributeId.KineticDamage),
                damage.GetValueOrDefault(DogmaAttributeId.ThermalDamage)
            );
            profile.Weapons.Add(
                member,
                turret.Weapon with
                {
                    Damage = vector.Scale(values[DogmaAttributeId.DamageMultiplier]),
                    CycleMilliseconds = values[DogmaAttributeId.WeaponCycleTime],
                }
            );
            profile.Absorption.Add(member, values[DogmaAttributeId.HeatAbsorption]);
        }
        return profile;
    }

    private LiveCombatShip LoadShip(long pilotId, long now) =>
        owner.Economy.Read(world => LoadShipResident(world, pilotId, now), new(pilots: [pilotId]));

    private LiveCombatShip ReloadShip(LiveCombatShip ship, long now) =>
        owner.Economy.Read(world => ReloadShipResident(world, ship, now), new(pilots: [ship.PilotId], ships: [ship.ShipId]));

    private LiveCombatNpc? LoadNpc(long id, long now) =>
        owner.Economy.Inspect(world => LoadNpcResident(world, id, now), new(resources: [new(SharedSimulationResource.Npc, id)]));

    private void LoadNpcs(IReadOnlyList<long> ids, long now)
    {
        if (ids.Count == 0)
        {
            return;
        }
        var loaded = owner.Economy.Inspect(
            world => ids.Select(id => LoadNpcResident(world, id, now)).OfType<LiveCombatNpc>().ToArray(),
            new(resources: ids.Select(id => new SimulationResource(SharedSimulationResource.Npc, id)))
        );
        foreach (var npc in loaded)
        {
            SetNpc(npc);
            _savedNpcs[npc.Id] = npc;
            _staleNpcs.Remove(npc.Id);
        }
    }

    private LiveCombatNpc HydrateNpc(
        CatalogDbContext catalog,
        WorldNpc npc,
        WorldNpcMiningMember? mining,
        IReadOnlyList<ElectronicWarfareContact> effects,
        IReadOnlyDictionary<long, CombatParty> contributions,
        long now
    )
    {
        var profile = _simulationProfiles.Read(catalog, npc.TypeId);
        var type = profile.Type;
        var attributes = profile.Attributes;
        var defense = profile.Defense;
        double missileRange = 0;
        if (attributes.GetValueOrDefault(DogmaAttributeId.EntityMissileType) is > 0 and var missile)
        {
            int chargeId = checked((int)missile);
            var charge = PilotFittingStore.ReadAttributes(catalog, chargeId);
            missileRange = NpcMissileRules.Drive(MissileChargeCatalog.Drive(catalog, chargeId, charge), attributes).NominalRange;
        }
        attributes = PlayerEwarRules.NpcAttributes(profile.Attributes, effects, missileRange);
        return new(
            npc.Id,
            npc.TypeId,
            npc.SystemId,
            NpcMotion.Position(npc),
            NpcMotion.Velocity(npc),
            type.Radius ?? 0,
            attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
            new(
                defense,
                new(npc.ShieldCharge, Math.Max(0, defense.Armor - npc.ArmorDamage), Math.Max(0, defense.Structure - npc.StructureDamage)),
                npc.UpdatedAt,
                ArmorDamage: npc.ArmorDamage,
                StructureDamage: npc.StructureDamage
            ),
            npc.DestroyedAt is null && npc.RetiredAt is null,
            contributions,
            npc.Role,
            npc.EncounterId,
            attributes.GetValueOrDefault(
                DogmaAttributeId.EntityAttackDelayMinimum,
                npc.Role == NpcRole.Pirate ? 0 : NpcGuardRules.ReactionMilliseconds
            ),
            npc.CorporationId,
            npc.FactionId,
            NpcMotion.Home(npc, catalog),
            attributes
        )
        {
            AcquisitionRange = npc.AcquisitionRange,
            Navigation = npc.Navigation,
            MotionAt = npc.MotionAt,
            Mass = type.Mass ?? 0,
            GroupId = type.GroupId,
            CategoryId = type.CategoryId,
            Name = npc.Name ?? type.Name,
            AnchorKind = npc.AnchorKind,
            AnchorId = npc.BeltId,
            StandingThresholds = profile.StandingThresholds,
            MissileRange = missileRange,
            RepairProfile = NpcRepairRules.Profile(attributes),
            RepairState = new(npc.ShieldRepairAt, npc.ArmorRepairAt, npc.RepairRevision),
            ElectronicWarfare = profile.ElectronicWarfare,
            IncomingElectronicWarfare = effects,
            Transit = mining?.Transit(npc),
            Activity = mining?.Activity(
                npc,
                attributes.GetValueOrDefault(DogmaAttributeId.BehaviorMiningDuration),
                now,
                checked((int)attributes.GetValueOrDefault(DogmaAttributeId.EntityTurretType))
            ),
        };
    }

    private static CombatParty ReadContribution(SavedCombatDamage damage) =>
        (JsonSerializer.Deserialize<CombatParty>(damage.PartyJson) ?? throw new InvalidDataException("Invalid saved attacker.")) with
        {
            Damage = damage.Damage,
        };
}
