using Eden.Data.World.Characters;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.World.Fighters;

[Injectable]
internal sealed class PilotFighterDeckStore(WorldDatabase database) : IPilotFighterDeck
{
    private LiveWorld Live =>
        LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Fighter decks require the resident world.");

    public FighterDeckSnapshot Read(long pilotId, long shipId) =>
        LiveWorld
            .Find(database.Path)
            ?.Economy.Inspect(
                world =>
                    world.Pilots.Find(pilotId)?.ActiveShipId == shipId
                        ? new FighterDeckSnapshot(
                            TubeItems(world, pilotId, shipId).OrderBy(item => item.FlagId).Select(Tube).ToArray(),
                            Deployed(world, pilotId, shipId).ToArray()
                        )
                        : new FighterDeckSnapshot([], []),
                new(pilots: [pilotId], ships: [shipId])
            )
        ?? new([], []);

    public IReadOnlyList<FighterLaunch> Launch(long pilotId, long shipId, IReadOnlyList<int> flags, long now)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (flags.Count == 0 || flags.Distinct().Count() != flags.Count)
        {
            throw new InvalidDataException("Choose distinct launch tubes.");
        }
        var pilot = RequireDeck(action, catalog, pilotId, shipId, flags[0]);
        var position = pilot.ActiveShip.Position ?? throw new InvalidDataException("Launch fighters from your active ship in space.");
        action.RequireUncloaked(shipId);
        var hull = PilotFittingStore.ShipAttributes(action, catalog, pilot);
        if (PilotHealthStore.Read(action, shipId, ShipHealth.DefenseFrom(hull), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot launch fighters.");
        }
        var loaded = TubeItems(action, pilotId, shipId).ToDictionary(item => item.FlagId);
        int system = position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
        var launches = new List<FighterLaunch>(flags.Count);
        foreach (int flag in flags)
        {
            if (!loaded.TryGetValue(flag, out var item))
            {
                throw new InvalidDataException("Choose only loaded fighter tubes.");
            }
            PilotSkillReader.CheckRequirements(action, catalog, pilotId, item.TypeId);
            var profile = DroneCatalog.Skilled(action, catalog, pilotId, item.TypeId, shipId);
            var before = PilotInventoryStore.Read(catalog, item);
            item.LocationId = system;
            var drone =
                action.Set<WorldDrone>().Find(item.Id)
                ?? action
                    .Add(
                        new WorldDrone
                        {
                            ItemId = item.Id,
                            PilotId = pilotId,
                            Shield = profile.Defense.Shield,
                        }
                    )
                    .Entity;
            double clearance = (catalog.RequireItem(pilot.ActiveShip.TypeId).Radius ?? 0) + profile.Radius + 300;
            drone.ControllerShipId = shipId;
            drone.SystemId = system;
            drone.CorporationId = pilot.CorporationId;
            drone.Activity = DroneActivity.Idle;
            drone.TargetId = shipId;
            (drone.X, drone.Y, drone.Z) = (position.X + clearance, position.Y, position.Z + launches.Count * 2 * profile.Radius);
            (drone.VelocityX, drone.VelocityY, drone.VelocityZ) = (0, 0, 0);
            drone.UpdatedAt = now;
            drone.CommandedAt = now;
            drone.ShotAt = 0;
            drone.ShotTargetId = 0;
            drone.SpeedLimit = profile.OrbitSpeed;
            drone.Orbiting = true;
            var change = new InventoryChange(PilotInventoryStore.Read(catalog, item), before);
            launches.Add(new(new(flag, item.Id, item.TypeId, item.Quantity, drone.Activity), change));
        }
        action.Commit();
        return launches;
    }

    private static IEnumerable<FighterInSpace> Deployed(WorldAction world, long pilotId, long shipId) =>
        world
            .Filter<WorldDrone>(drone => drone.PilotId == pilotId && drone.ControllerShipId == shipId && drone.SystemId != null)
            .AsEnumerable()
            .Select(drone => (Drone: drone, Item: world.Set<OwnedItem>().Find(drone.ItemId)))
            .Where(pair => pair.Item is not null && FighterDeckRules.IsTube(pair.Item.FlagId))
            .OrderBy(pair => pair.Item!.FlagId)
            .Select(pair => new FighterInSpace(pair.Item!.FlagId, pair.Item.Id, pair.Item.TypeId, pair.Item.Quantity, pair.Drone.Activity));

    public FighterTubeChange Load(long pilotId, long shipId, long stackId, int flag)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = RequireDeck(action, catalog, pilotId, shipId, flag);
        var stack = InventoryPersistence.OwnedStack(action, pilotId, stackId);
        if (
            stack is not { Singleton: false, Quantity: > 0 }
            || stack.LocationId != shipId
            || stack.FlagId != (int)InventoryFlag.FighterBay
            || catalog.FittingType(stack.TypeId)?.CategoryId != (int)InventoryCategory.Fighter
            || catalog.Read(CatalogData.Client.Types, (long)stack.TypeId)?.Published != 1
        )
        {
            throw new InvalidDataException("Load an owned fighter stack from the current ship's fighter bay.");
        }
        var attributes = catalog.FittingAttributes(stack.TypeId);
        int maximum = checked((int)attributes.GetValueOrDefault(DogmaAttributeId.FighterSquadronMaximum));
        if (maximum <= 0 || FighterDeckRules.Class(attributes) is not { } kind)
        {
            throw new InvalidDataException("This fighter type has no usable squadron data.");
        }
        PilotSkillReader.CheckRequirements(action, catalog, pilotId, stack.TypeId);
        var deployed = Deployed(action, pilotId, shipId).ToArray();
        if (deployed.Any(squad => squad.Flag == flag))
        {
            throw new InvalidDataException("Recall this tube's fighter squadron before loading it.");
        }
        var loaded = TubeItems(action, pilotId, shipId);
        var squadron = loaded.SingleOrDefault(item => item.FlagId == flag);
        if (squadron is not null && squadron.TypeId != stack.TypeId)
        {
            throw new InvalidDataException("Unload the other fighter type before filling this tube.");
        }
        var hull = PilotFittingStore.ShipAttributes(action, catalog, pilot);
        int slots = FighterDeckRules.Bays(kind, hull);
        if (
            squadron is null
            && loaded
                .Select(item => item.TypeId)
                .Concat(deployed.Select(squad => squad.TypeId))
                .Count(type => FighterDeckRules.Class(catalog.FittingAttributes(type)) == kind) >= slots
        )
        {
            throw new InvalidDataException("The carrier has no free slot for this fighter class.");
        }
        int moved = Math.Min(stack.Quantity, maximum - (squadron?.Quantity ?? 0));
        if (moved <= 0)
        {
            throw new InvalidDataException("The fighter squadron is already full.");
        }

        var previous = PilotInventoryStore.Read(catalog, stack);
        IReadOnlyList<InventoryChange> changes;
        if (squadron is not null)
        {
            var previousTube = PilotInventoryStore.Read(catalog, squadron);
            squadron.Quantity += moved;
            stack.Quantity -= moved;
            if (stack.Quantity == 0)
            {
                action.Remove(stack);
            }
            changes =
            [
                new(previous with { Quantity = stack.Quantity }, previous),
                new(PilotInventoryStore.Read(catalog, squadron), previousTube),
            ];
        }
        else if (moved < stack.Quantity)
        {
            changes = InventoryPersistence.SplitTransfer(action, catalog, stack, previous, shipId, flag, moved);
        }
        else
        {
            stack.FlagId = flag;
            changes = [new(PilotInventoryStore.Read(catalog, stack), previous)];
        }
        var current = changes.Last().Item;
        action.Commit();
        return new(flag, new(flag, current.Id, current.TypeId, current.Quantity), changes);
    }

    public FighterTubeChange Unload(long pilotId, long shipId, int flag)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        _ = RequireDeck(action, catalog, pilotId, shipId, flag);
        var squadron =
            TubeItems(action, pilotId, shipId).SingleOrDefault(item => item.FlagId == flag)
            ?? throw new InvalidDataException("There is no landed fighter squadron in this tube.");
        var previous = PilotInventoryStore.Read(catalog, squadron);
        const int bay = (int)InventoryFlag.FighterBay;
        double free =
            PilotInventoryStore.HoldCapacity(action, catalog, pilotId, shipId, bay)
            - InventoryPersistence.UsedVolume(action, catalog, pilotId, shipId, bay);
        double required = ShipHoldRules.Definition(bay)!.RequiredCapacity(previous, squadron.Quantity);
        if (free < required)
        {
            throw new InvalidDataException("There is insufficient space in the fighter bay.");
        }
        squadron.FlagId = bay;
        var updated = PilotInventoryStore.Read(catalog, squadron);
        action.Commit();
        return new(flag, null, [new(updated, previous)]);
    }

    private static OwnedItem[] TubeItems(WorldAction world, long pilotId, long shipId) =>
        world
            .Filter<OwnedItem>(item =>
                item.LocationId == shipId
                && item.OwnerId == pilotId
                && item.FlagId >= FighterDeckRules.FirstTube
                && item.FlagId < FighterDeckRules.FirstTube + FighterDeckRules.MaximumTubes
            )
            .ToArray();

    private static FighterTube Tube(OwnedItem item) => new(item.FlagId, item.Id, item.TypeId, item.Quantity);

    private static Pilot RequireDeck(WorldAction world, CatalogDbContext catalog, long pilotId, long shipId, int flag)
    {
        var pilot = world.Pilots.Find(pilotId);
        if (
            pilot?.ActiveShipId != shipId
            || !FighterDeckRules.IsTube(flag)
            || !FighterDeckRules.Supports(catalog.RequireItem(pilot.ActiveShip.TypeId).GroupId)
            || flag - FighterDeckRules.FirstTube
                >= catalog.FittingAttributes(pilot.ActiveShip.TypeId).GetValueOrDefault(DogmaAttributeId.FighterTubes)
        )
        {
            throw new InvalidDataException("Choose a valid fighter tube on your active carrier.");
        }
        return pilot;
    }
}
