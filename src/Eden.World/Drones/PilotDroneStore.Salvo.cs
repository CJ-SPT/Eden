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
    public void SetSalvo(long pilotId, long shipId, IReadOnlyList<long> squadrons, long? targetId, bool locked, long now)
    {
        CheckIds(squadrons);
        if (squadrons.Count > FighterDeckRules.MaximumTubes || targetId is not null && !locked)
        {
            throw new InvalidDataException("Lock a target before firing a fighter salvo.");
        }
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Fighter salvos require the resident world.");
        using var world = live.Economy.Begin(new(pilots: [pilotId], ships: targetId is long id ? [shipId, id] : [shipId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = InSpace(world, pilotId, requireDroneHull: false);
        var hull = catalog.RequireItem(pilot.ActiveShip.TypeId);
        if (pilot.ActiveShipId != shipId || !FighterDeckRules.Supports(hull.GroupId, hull.Id))
        {
            throw new InvalidDataException("Use the fighter controls on your active carrier.");
        }
        world.RequireUncloaked(shipId);
        int system = CrimewatchPersistence.System(catalog, pilot.ActiveShip);
        var origin = Position(pilot.ActiveShip.Position!);
        var target = targetId is long targetItem ? ResolveSalvoTarget(world, catalog, pilot, targetItem, system, now) : null;
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
            var salvo = contact.Profile.Salvo ?? throw new InvalidDataException("That fighter has no supported salvo.");
            var work = ReadWork(row);
            var cycle = (work.FighterSalvo ?? FighterSalvoCycle.Full(salvo)).Refresh(now);
            if (target is null)
            {
                if (!cycle.Active(now))
                {
                    throw new InvalidDataException("The fighter salvo is no longer active.");
                }
                WriteWork(row, work with { FighterSalvo = cycle with { StartedAt = 0 } });
                continue;
            }
            if (cycle.EndsAt > now || cycle.Charges == 0 || row.Activity == DroneActivity.ReturningToBay)
            {
                throw new InvalidDataException("The fighter salvo is active, rearming or returning to its tube.");
            }
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

    private sealed record SalvoTarget(
        long Id,
        SpacePosition Position,
        SpacePosition Velocity,
        double Radius,
        IReadOnlyDictionary<DogmaAttributeId, double> Attributes,
        Pilot? Player = null,
        AggressionDecision? Decision = null,
        WorldNpc? Npc = null,
        SavedStructure? Structure = null
    );

    private SalvoTarget ResolveSalvoTarget(WorldAction world, CatalogDbContext catalog, Pilot pilot, long target, int system, long now)
    {
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
            StructureCombatTargets.Check(world, catalog, structure, now);
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
            var attributes = PilotFittingStore.ShipAttributes(world, catalog, victim);
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
        throw new InvalidDataException("The fighter salvo target is unavailable.");
    }

    private bool ApplySalvo(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        WorldDrone row,
        DroneContact contact,
        FighterSalvo salvo,
        SalvoTarget target,
        int system,
        long now,
        long end
    )
    {
        double application = MissileRules.Application(
            salvo.Warhead,
            Math.Max(1, target.Attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius)),
            target.Velocity.Length
        );
        double resistance = salvo.TargetResistance is { } id
            ? target.Attributes.GetValueOrDefault(id, catalog.Read(CatalogData.Client.Dogmaattributes, (long)id)?.DefaultValue ?? 1)
            : 1;
        var damage = salvo.Warhead.Damage.Scale(contact.SquadronSize * application * resistance);
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
        StructureCombatTargets.Aggress(world, pilot.Id, system, now, end);
        return StructureCombatTargets.Apply(world, catalog, target.Structure!, damage, now).Health.Remaining.Destroyed;
    }
}
