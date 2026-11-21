using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.FactionWarfare;

[Injectable(InjectionType.Singleton)]
internal sealed class MilitiaNavyStore(WorldDatabase database, NpcProfileCatalog profiles) : IMilitiaNavy
{
    private static readonly MilitiaNavyUpdate Empty = new();

    private enum Order
    {
        Start,
        End,
        Deploy,
        Withdraw,
        Resume,
    }

    private sealed record Target(long ShipId, int SystemId, MilitiaFaction Faction, double Security, SpacePosition Position, double Radius);

    private sealed record Work(Order Order, Target? Target, TransactionParticipants Participants);

    public MilitiaNavyUpdate Advance(long pilotId, bool canEngage, long now)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return Empty;
        }
        using var owner = live.EnterPersistence("militia navy pursuit", TransactionParticipants.None);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var work = live.Economy.Inspect(world => Plan(world, catalog, pilotId, canEngage, now));
        if (work is null)
        {
            return Empty;
        }

        using var action = live.Economy.Begin(work.Participants);
        var pursuit = action.Set<SavedMilitiaNavyPursuit>().Find(pilotId);
        var target = work.Target;
        MilitiaNavyUpdate result = Empty;
        switch (work.Order)
        {
            case Order.Start:
                pursuit ??= action.Add(new SavedMilitiaNavyPursuit { PilotId = pilotId }).Entity;
                bool changed = RetireWing(action, catalog, pursuit, now);
                pursuit.ShipId = target!.ShipId;
                pursuit.SystemId = target.SystemId;
                pursuit.Faction = target.Faction;
                pursuit.RespondAt = checked(now + MilitiaNavyRules.ResponseDelay(target.Security).Ticks);
                result = new(target.Faction, changed);
                break;
            case Order.End:
                result = new(SceneChanged: RetireWing(action, catalog, pursuit!, now));
                action.Remove(pursuit!);
                break;
            case Order.Deploy:
                Deploy(action, catalog, pursuit!, target!, now);
                result = new(SceneChanged: true);
                break;
            case Order.Withdraw:
                result = new(SceneChanged: RetireWing(action, catalog, pursuit!, now));
                pursuit!.RespondAt = checked(now + MilitiaNavyRules.RedeployDelay.Ticks);
                break;
            case Order.Resume:
                Resume(action, pursuit!, now);
                result = new(SceneChanged: true);
                break;
        }
        action.Commit();
        return result;
    }

    private static Work? Plan(WorldAction world, CatalogDbContext catalog, long pilotId, bool canEngage, long now)
    {
        var pursuit = world.Set<SavedMilitiaNavyPursuit>().Find(pilotId);
        var pilot = world.Pilots.Find(pilotId);
        var target = pilot is null ? null : HostileSystem(world, catalog, pilot);
        if (target is null)
        {
            return pursuit is null ? null : new(Order.End, null, Participants(pursuit, null));
        }
        if (pursuit is null || pursuit.ShipId != target.ShipId || pursuit.SystemId != target.SystemId || pursuit.Faction != target.Faction)
        {
            return new(Order.Start, target, Participants(pursuit, target));
        }
        if (pursuit.FrigateId is null && pursuit.CruiserId is null)
        {
            return canEngage && pursuit.RespondAt <= now ? new(Order.Deploy, target, Participants(pursuit, target)) : null;
        }
        var frigate = Responder(world, pursuit, pursuit.FrigateId);
        var cruiser = Responder(world, pursuit, pursuit.CruiserId);
        if (!canEngage || frigate is null && cruiser is null || !OnGrid(frigate, target.Position) && !OnGrid(cruiser, target.Position))
        {
            return new(Order.Withdraw, target, Participants(pursuit, target));
        }
        if (NeedsAggression(world, frigate, target.ShipId) || NeedsAggression(world, cruiser, target.ShipId))
        {
            return new(Order.Resume, target, Participants(pursuit, target));
        }
        return null;
    }

    private static Target? HostileSystem(WorldAction world, CatalogDbContext catalog, Pilot pilot)
    {
        if (pilot.ActiveShip.Position is not { } position || MilitiaEnlistmentStore.Effective(world, pilot) is not { } affinity)
        {
            return null;
        }
        var ship = pilot.ActiveShip;
        var hull = catalog.RequireItem(ship.TypeId);
        int system = CrimewatchPersistence.System(catalog, ship);
        var definition = catalog.Read(CatalogData.Client.Systems, (long)system);
        var faction = definition?.FactionID is > 0 and <= int.MaxValue and var factionId ? (MilitiaFaction?)factionId : null;
        double security = definition?.SecurityStatus ?? 0;
        return MilitiaNavyRules.Responds(affinity, faction, security, hull.GroupId == FleetDefinitions.CapsuleGroup)
            ? new(ship.Id, system, faction!.Value, security, new(position.X, position.Y, position.Z), hull.Radius ?? 0)
            : null;
    }

    private static bool OnGrid(WorldNpc? npc, SpacePosition target) =>
        npc is { HomeX: double x, HomeY: double y, HomeZ: double z }
        && new SpacePosition(x, y, z).DistanceTo(target) <= MilitiaNavyRules.ClosePursuitRange;

    private static bool NeedsAggression(WorldAction world, WorldNpc? npc, long ship) =>
        npc is not null && world.Set<NpcAggression>().Find(npc.Id)?.TargetShipId != ship;

    private static WorldNpc? Responder(WorldAction world, SavedMilitiaNavyPursuit pursuit, long? id) =>
        id is long npcId
        && world.Set<WorldNpc>().Find(npcId) is { Role: NpcRole.MilitiaNavy, DestroyedAt: null, RetiredAt: null } npc
        && npc.SystemId == pursuit.SystemId
        && npc.EncounterId == -pursuit.PilotId
            ? npc
            : null;

    private static TransactionParticipants Participants(SavedMilitiaNavyPursuit? pursuit, Target? target) =>
        new(
            ships: new[] { pursuit?.ShipId, target?.ShipId }.OfType<long>(),
            resources: new[] { pursuit?.FrigateId, pursuit?.CruiserId }
                .OfType<long>()
                .Select(id => new SimulationResource(SharedSimulationResource.Npc, id))
        );

    private void Deploy(WorldAction world, CatalogDbContext catalog, SavedMilitiaNavyPursuit pursuit, Target target, long now)
    {
        int faction = (int)target.Faction;
        var roster = NpcGuardRules.Roster(faction) ?? throw new InvalidDataException("The local faction navy has no catalog roster.");
        int corporation = checked((int)(catalog.Read(CatalogData.Client.Factions, (long)faction)?.CorporationID ?? 0));
        if (corporation <= 0)
        {
            throw new InvalidDataException("The local faction navy corporation is unavailable.");
        }
        pursuit.FrigateId = Spawn(roster.Frigate, 0, -1);
        pursuit.CruiserId = Spawn(roster.Cruiser, 1, 1);
        Resume(world, pursuit, now);

        long Spawn(NpcGuardType type, int slot, int direction)
        {
            var profile = profiles.Read(catalog, (int)type);
            if (
                profile.Defense.Structure <= 0
                || !NpcPopulationRules.HasDirectWeapon(profile.Attributes)
                || profile.ElectronicWarfare.Any(ability => ability.Kind == NpcEwarKind.WarpDisruption)
            )
            {
                throw new InvalidDataException("The faction navy hull lacks a supported pursuit profile.");
            }
            double separation = target.Radius + (profile.Type.Radius ?? 0) + MilitiaNavyRules.FormationSpacing;
            var at = target.Position + new SpacePosition(direction * separation, 0, 0);
            // Static encounters and CONCORD use positive IDs. A pilot's negative ID
            // gives their two navy slots a stable key across ships and systems.
            var npc = world
                .Add(
                    new WorldNpc
                    {
                        SystemId = target.SystemId,
                        TypeId = (int)type,
                        Role = NpcRole.MilitiaNavy,
                        EncounterId = -pursuit.PilotId,
                        SpawnSlot = slot,
                        FactionId = faction,
                        CorporationId = corporation,
                        X = at.X,
                        Y = at.Y,
                        Z = at.Z,
                        HomeX = target.Position.X,
                        HomeY = target.Position.Y,
                        HomeZ = target.Position.Z,
                        ShieldCharge = profile.Defense.Shield,
                        UpdatedAt = now,
                    }
                )
                .Entity;
            world.SaveChanges();
            return npc.Id;
        }
    }

    private static void Resume(WorldAction world, SavedMilitiaNavyPursuit pursuit, long now)
    {
        foreach (
            var npc in new[]
            {
                Responder(world, pursuit, pursuit.FrigateId),
                Responder(world, pursuit, pursuit.CruiserId),
            }.OfType<WorldNpc>()
        )
        {
            if (world.Set<NpcAggression>().Find(npc.Id) is null)
            {
                world.Add(NpcAggression.Start(npc.Id, pursuit.ShipId, NpcGuardRules.ReactionMilliseconds, now));
            }
        }
    }

    private static bool RetireWing(WorldAction world, CatalogDbContext catalog, SavedMilitiaNavyPursuit pursuit, long now)
    {
        bool changed = false;
        foreach (long id in new[] { pursuit.FrigateId, pursuit.CruiserId }.OfType<long>())
        {
            NpcCombatStore.Disengage(world, catalog, pursuit.ShipId, id);
            world.RemoveRange(world.Filter<WorldNpcEwar>(effect => effect.NpcId == id));
            if (Responder(world, pursuit, id) is { } npc)
            {
                npc.RetiredAt = now;
                changed = true;
            }
        }
        pursuit.FrigateId = null;
        pursuit.CruiserId = null;
        return changed;
    }

    public void Leave(long pilotId, long now)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return;
        }
        using var owner = live.EnterPersistence("militia navy departure", TransactionParticipants.None);
        var participants = live.Economy.Inspect(world =>
            world.Set<SavedMilitiaNavyPursuit>().Find(pilotId) is { } pursuit ? Participants(pursuit, null) : null
        );
        if (participants is null)
        {
            return;
        }
        using var action = live.Economy.Begin(participants);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pursuit = action.Set<SavedMilitiaNavyPursuit>().Find(pilotId)!;
        RetireWing(action, catalog, pursuit, now);
        action.Remove(pursuit);
        action.Commit();
    }

    public void Sweep(long now)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return;
        }
        using var owner = live.EnterPersistence("militia navy cleanup", TransactionParticipants.None);
        var departures = live.Economy.Inspect(world =>
            world
                .Set<SavedMilitiaNavyPursuit>()
                .AsEnumerable()
                .Where(pursuit =>
                    world.Pilots.Find(pursuit.PilotId) is not { } pilot
                    || pilot.ActiveShipId != pursuit.ShipId
                    || pilot.ActiveShip.Position is not { } position
                    || position.SystemId is int system && system != pursuit.SystemId
                    || !MilitiaRules.AreEnemies(MilitiaEnlistmentStore.Effective(world, pilot), pursuit.Faction)
                    || world.Set<SavedCharacterDeletion>().Find(pursuit.PilotId)?.DeletedAt is not null
                    || (world.Set<SavedLogoff>().Find(pursuit.PilotId)?.ExpiresAt ?? long.MaxValue) <= now
                )
                .Select(pursuit => pursuit.PilotId)
                .ToArray()
        );
        foreach (long pilot in departures)
        {
            Leave(pilot, now);
        }
    }
}
