using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.FactionWarfare;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Structures;

namespace Eden.World.Drones;

internal sealed partial class PilotDroneStore
{
    public void SetSecondaryAbility(
        long pilotId,
        long shipId,
        IReadOnlyList<long> squadrons,
        long? targetId,
        bool locked,
        long now,
        SpacePosition? point = null
    )
    {
        CheckIds(squadrons);
        if (squadrons.Count > FighterDeckRules.MaximumTubes || targetId is not null && (!locked || point is not null))
        {
            throw new InvalidDataException("Lock a target before using the fighter ability.");
        }
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Fighter abilities require the resident world.");
        using var world = live.Economy.Begin(new(pilots: [pilotId], ships: targetId is long id ? [shipId, id] : [shipId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = InSpace(world, pilotId, requireDroneHull: false);
        var hull = catalog.RequireItem(pilot.ActiveShip.TypeId);
        if (pilot.ActiveShipId != shipId || !FighterDeckRules.Supports(hull.GroupId, hull.Id))
        {
            throw new InvalidDataException("Use the fighter controls on your active carrier.");
        }
        world.RequireUncloaked(shipId);
        if (targetId is not null || point is not null)
        {
            Eden.World.Flight.PilotCapitalTravelStore.RequireOffense(world, shipId, now);
            var defense = ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(world, catalog, pilot));
            if (PilotHealthStore.Read(world, shipId, defense, now).Remaining.Destroyed)
            {
                throw new InvalidDataException("A destroyed ship cannot command fighter abilities.");
            }
        }
        int system = CrimewatchPersistence.System(catalog, pilot.ActiveShip);
        var origin = Position(pilot.ActiveShip.Position!);
        var target = targetId is long targetItem ? ResolveFighterAbilityTarget(world, catalog, pilot, targetItem, system, now) : null;
        bool destroyed = false;
        foreach (long squadron in squadrons)
        {
            var row = world.Set<WorldDrone>().Find(squadron);
            if (
                row is null
                || row.PilotId != pilotId
                || row.ControllerShipId != shipId
                || row.SystemId != system
                || world.Set<OwnedItem>().Find(squadron) is not { } item
                || item.OwnerId != pilotId
                || item.LocationId != system
                || !FighterDeckRules.IsTube(item.FlagId)
                || origin.DistanceTo(new(row.X, row.Y, row.Z)) > DroneRules.ShutdownDistance
            )
            {
                throw new InvalidDataException("Select deployed squadrons controlled by this ship.");
            }
            var contact = Contact(world, catalog, row, now);
            var work = ReadWork(row);
            if (contact.Profile.Bomb is { } bomb)
            {
                if (targetId is not null)
                {
                    throw new InvalidDataException("Aim the fighter bombs at a point in space.");
                }
                SetFighterBomb(world, catalog, pilot, row, contact, work, bomb, point, system, now);
                continue;
            }
            if (point is not null)
            {
                throw new InvalidDataException("That fighter ability requires a locked target.");
            }
            if (contact.Profile.Tackle is { } tackle)
            {
                SetFighterTackle(world, catalog, pilot, row, contact, work, tackle, target, system, now);
                continue;
            }
            var salvo = contact.Profile.Salvo ?? throw new InvalidDataException("That fighter has no supported secondary ability.");
            var cycle = (work.FighterSalvo ?? FighterSalvoCycle.Full(salvo.Charges, salvo.RearmMilliseconds)).Refresh(now);
            if (target is null)
            {
                StopFighterChargeCycle(row, work, cycle, now);
                continue;
            }
            RequireFighterCharge(row, cycle, now);
            if (
                !PlayerEwarRules.CanTarget(PlayerEwarStore.Active(world, row.ItemId), target.Id)
                || contact.Position.DistanceTo(target.Position) - target.Radius - contact.Profile.Radius > salvo.Range
            )
            {
                throw new InvalidDataException("The target is outside the fighter salvo's effective range or lock.");
            }
            cycle = cycle.Fire(now, salvo.DurationMilliseconds);
            WriteWork(row, work with { FighterSalvo = cycle });
            row.ShotAt = now;
            row.ShotTargetId = target.Id;
            if (!destroyed)
            {
                destroyed = ApplySalvo(world, catalog, pilot, row, contact, salvo, target, system, now, cycle.EndsAt);
            }
        }
        if (destroyed)
        {
            world.AfterCommit(security.Publish);
        }
        world.Commit();
    }

    private static void RequireFighterCharge(WorldDrone drone, FighterSalvoCycle cycle, long now)
    {
        if (cycle.EndsAt > now || cycle.Charges == 0 || drone.Activity == DroneActivity.ReturningToBay)
        {
            throw new InvalidDataException("The fighter weapon is active, rearming or returning to its tube.");
        }
    }

    private static void StopFighterChargeCycle(WorldDrone drone, DroneWork work, FighterSalvoCycle cycle, long now)
    {
        if (!cycle.Active(now))
        {
            throw new InvalidDataException("The fighter weapon is no longer active.");
        }
        WriteWork(drone, work with { FighterSalvo = cycle with { StartedAt = 0 } });
    }

    private sealed record FighterAbilityTarget(
        long Id,
        SpacePosition Position,
        SpacePosition Velocity,
        double Radius,
        IReadOnlyDictionary<DogmaAttributeId, double> Attributes,
        Pilot? Player = null,
        AggressionDecision? Decision = null,
        WorldNpc? Npc = null,
        SavedStructure? Structure = null,
        FighterCompanionTarget? Companion = null
    );

    private FighterAbilityTarget ResolveFighterAbilityTarget(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        long target,
        int system,
        long now
    )
    {
        if (FighterTarget(world, catalog, pilot, target, system, now) is { } companion)
        {
            var decision = CrimewatchPersistence.CheckPlayer(world, catalog, pilot, companion.Owner, system, now);
            return new(
                target,
                companion.Contact.Position,
                companion.Contact.Velocity,
                companion.Contact.Profile.Radius,
                catalog.FittingAttributes(companion.Contact.Profile.TypeId),
                Decision: decision,
                Companion: companion
            );
        }
        if (
            world.Set<WorldNpc>().Find(target) is { DestroyedAt: null, RetiredAt: null } npc
            && npc.SystemId == system
            && npc.Navigation != NpcNavigationState.Warping
            && MilitiaHubStore.CanAttack(world, npc, pilot)
        )
        {
            var attributes = PlayerEwarStore.NpcAttributes(world, target, PilotFittingStore.ReadAttributes(catalog, npc.TypeId));
            return new(
                target,
                NpcMotion.Position(npc),
                NpcMotion.Velocity(npc),
                catalog.RequireItem(npc.TypeId).Radius ?? 0,
                attributes,
                Npc: npc
            );
        }
        if (StructureCombatTargets.Find(world, target) is { } structure && structure.SystemId == system)
        {
            StructureCombatTargets.Check(world, catalog, structure, now, pilot.Id);
            return new(
                target,
                StructureCombatTargets.Position(structure),
                SpacePosition.Zero,
                catalog.RequireItem(structure.TypeId).Radius ?? 0,
                catalog.FittingAttributes(structure.TypeId),
                Structure: structure
            );
        }
        if (FindVictim(world, catalog, pilot, target, system, now) is { } victim && FindContact(world, victim, system, now) is { } contact)
        {
            var decision = CrimewatchPersistence.CheckPlayer(world, catalog, pilot, victim, system, now);
            var attributes = PilotFittingStore.ShipAttributes(world, catalog, victim, incursionTime: now);
            return new(
                target,
                contact.Position,
                contact.Velocity,
                catalog.RequireItem(victim.ActiveShip.TypeId).Radius ?? 0,
                attributes,
                victim,
                decision
            );
        }
        throw new InvalidDataException("The fighter ability target is unavailable.");
    }

    private bool ApplySalvo(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        WorldDrone row,
        DroneContact contact,
        FighterSalvo salvo,
        FighterAbilityTarget target,
        int system,
        long now,
        long end
    )
    {
        double application = MissileRules.Application(
            salvo.Warhead,
            Math.Max(
                1,
                target.Attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius)
                    * (target.Companion?.Contact.MobilitySignatureMultiplier ?? 1)
            ),
            target.Velocity.Length
        );
        double resistance = salvo.TargetResistance is { } id
            ? target.Attributes.GetValueOrDefault(id, catalog.Read(CatalogData.Client.Dogmaattributes, (long)id)?.DefaultValue ?? 1)
            : 1;
        var damage = salvo.Warhead.Damage.Scale(contact.SquadronSize * application * resistance);
        if (target.Companion is { } companion)
        {
            CrimewatchPersistence.Player(world, catalog, pilot.Id, companion.Owner.Id, system, target.Decision!, now, end);
            return SmartbombStore.DamageDrone(world, companion.Row, companion.Contact, damage, now).Health.Remaining.Destroyed;
        }
        if (target.Player is { } victim)
        {
            CrimewatchPersistence.Player(world, catalog, pilot.Id, victim.Id, system, target.Decision!, now, end);
            var hit = ApplyPilotDamage(
                world,
                catalog,
                pilot,
                victim,
                row,
                contact.Profile with
                {
                    Damage = salvo.Warhead.Damage,
                },
                target.Attributes,
                damage,
                now
            );
            world.AfterCommit(() => damageEvents.Publish(hit.Damage));
            return hit.Hit.Health.Remaining.Destroyed;
        }
        if (target.Npc is { } npc)
        {
            CrimewatchPersistence.Npc(world, pilot.Id, system, end);
            var hit = NpcDamagePersistence.Apply(
                world,
                catalog,
                npc,
                pilot,
                pilot.ActiveShipId,
                target.Attributes,
                damage,
                now,
                contact.Profile.TypeId
            );
            return hit.Health.Remaining.Destroyed;
        }
        StructureCombatTargets.Aggress(world, pilot.Id, target.Structure!.Id, system, now, end);
        var attacker = PilotInteractionHistoryStore.Player(world, pilot, pilot.ActiveShipId, contact.Profile.TypeId);
        return StructureCombatTargets.Apply(world, catalog, target.Structure!, damage, now, attacker).Health.Remaining.Destroyed;
    }
}
