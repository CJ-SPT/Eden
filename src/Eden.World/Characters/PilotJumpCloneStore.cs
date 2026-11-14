using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.World.Characters;

[Injectable]
internal sealed class PilotJumpCloneStore(WorldDatabase database, SqliteWorld lookup, TimeProvider clock, PilotTrainingStore training)
    : IPilotJumpClones
{
    private static Pilot Pilot(WorldAction world, long id) => world.Pilots.Find(id) ?? throw new InvalidDataException("Unknown pilot.");

    private static CatalogStationDefinition Station(CatalogDbContext catalog, int id) =>
        catalog.FindStation(id) ?? throw new InvalidDataException("Unknown jump clone station.");

    private static CatalogStationDefinition CurrentStation(CatalogDbContext catalog, Pilot pilot, bool facility)
    {
        if (
            pilot.ActiveShip.Position is not null
            || pilot.ActiveShip.StructureId.HasValue
            || pilot.ActiveShip.OwnerId != pilot.Id
            || pilot.ActiveShip.StoredInId is not null
        )
        {
            throw new InvalidDataException("Use jump clones while docked in an NPC station.");
        }
        var station = Station(catalog, pilot.ActiveShip.StationId);
        if (facility && !StationCapabilities.Has(catalog, station.OperationId, StationService.JumpCloneFacility))
        {
            throw new InvalidDataException("This station has no jump clone facility.");
        }
        // Pochven installation requires standings that Eden does not implement.
        JumpCloneRules.CheckLocation(station.SolarSystemId, station.RegionId);
        return station;
    }

    private static JumpCloneSkills Skills(WorldAction world, CatalogDbContext catalog, Pilot pilot)
    {
        if (
            !world
                .Filter<PilotSkill>(s =>
                    s.PilotId == pilot.Id
                    && s.Points > 0
                    && (
                        s.TypeId == (int)InfomorphSkill.Psychology
                        || s.TypeId == (int)InfomorphSkill.AdvancedPsychology
                        || s.TypeId == (int)InfomorphSkill.ElitePsychology
                        || s.TypeId == (int)InfomorphSkill.Synchronizing
                    )
                )
                .Any()
        )
        {
            return new(0, 0, 0, 0);
        }
        var alpha =
            pilot.Account.CloneGrade == AccountCloneGrade.Alpha
                ? catalog
                    .Require(CatalogData.Client.Clonegrades, pilot.RaceId.ToString(CultureInfo.InvariantCulture))
                    .Skills!.Value.ToDictionary(s => s.TypeID!.Value, s => s.Level!.Value)
                : null;
        int Level(InfomorphSkill skill)
        {
            int trained = PilotSkillReader.Level(world, catalog, pilot.Id, (int)skill);
            return alpha is null ? trained : Math.Min(trained, checked((int)alpha.GetValueOrDefault((int)skill)));
        }
        return new(
            Level(InfomorphSkill.Psychology),
            Level(InfomorphSkill.AdvancedPsychology),
            Level(InfomorphSkill.ElitePsychology),
            Level(InfomorphSkill.Synchronizing)
        );
    }

    public JumpCloneSkills ReadCloneSkills(long pilotId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return WorldActionScope.Inspect(database, world => Skills(world, catalog, Pilot(world, pilotId)));
    }

    public JumpCloneSnapshot Read(long pilotId) =>
        WorldActionScope.Inspect<JumpCloneSnapshot>(
            database,
            world =>
            {
                _ = Pilot(world, pilotId);
                var bodies = world
                    .Filter<SavedJumpClone>(c => c.PilotId == pilotId && !c.Active)
                    .OrderBy(c => c.Id)
                    .Select(c => new JumpClone(c.Id, c.StationId!.Value, c.Name))
                    .ToArray();
                var ids = bodies.Select(c => c.Id).ToArray();
                return new(bodies, world.Set<SavedJumpCloneTimer>().Find(pilotId)?.Time)
                {
                    Implants = world
                        .Filter<SavedPilotImplant>(implant => Enumerable.Contains(ids, implant.CloneId))
                        .OrderBy(implant => implant.CloneId)
                        .ThenBy(implant => implant.Slot)
                        .AsEnumerable()
                        .Select(implant => implant.Snapshot())
                        .ToArray(),
                };
            },
            consistentFileRead: true
        );

    public long Price(long pilotId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        _ = WorldActionScope.Inspect(database, world => CurrentStation(catalog, Pilot(world, pilotId), facility: false));
        return JumpCloneRules.FeeCents;
    }

    private static int? InstallLimit(WorldAction world, CatalogDbContext catalog, Pilot pilot)
    {
        _ = CurrentStation(catalog, pilot, facility: true);
        int capacity = Skills(world, catalog, pilot).Capacity;
        return world.Filter<SavedJumpClone>(c => c.PilotId == pilot.Id && !c.Active).Count() >= capacity ? capacity : null;
    }

    public int? InstallLimit(long pilotId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return WorldActionScope.Inspect(database, world => InstallLimit(world, catalog, Pilot(world, pilotId)));
    }

    private SavedJumpClonePayment Pay(
        WorldAction world,
        Pilot pilot,
        CatalogStationDefinition station,
        JumpClonePaymentKind kind,
        long quote,
        long now
    )
    {
        var wallet = world.Set<PilotWallet>().Find(pilot.Id);
        long balance = JumpCloneRules.Pay(wallet?.Cents ?? 0, quote);
        wallet!.Cents = balance;
        return world
            .Add(
                new SavedJumpClonePayment
                {
                    PilotId = pilot.Id,
                    StationId = station.Id,
                    OperatorId = station.OwnerId,
                    Kind = kind,
                    AmountCents = JumpCloneRules.FeeCents,
                    BalanceCents = balance,
                    Time = now,
                }
            )
            .Entity;
    }

    public JumpCloneChange Install(long pilotId)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        if (InstallLimit(world, catalog, pilot) is not null)
        {
            throw new InvalidDataException("Your trained Infomorph skills and account status do not allow another jump clone.");
        }
        var station = CurrentStation(catalog, pilot, facility: true);
        var payment = Pay(
            world,
            pilot,
            station,
            JumpClonePaymentKind.Installation,
            JumpCloneRules.FeeCents,
            clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()
        );
        var clone = world.Add(new SavedJumpClone { PilotId = pilotId, StationId = station.Id }).Entity;
        world.SaveChanges();
        payment.CloneId = clone.Id;
        world.SaveChanges();
        transaction.Commit();
        return new(payment.BalanceCents, station.Id);
    }

    private static SavedJumpClone Owned(WorldAction world, long pilotId, long cloneId) =>
        world.Set<SavedJumpClone>().Find(cloneId) is { Active: false } clone && clone.PilotId == pilotId
            ? clone
            : throw new InvalidDataException("That installed jump clone does not belong to you or is no longer available.");

    public void Rename(long pilotId, long cloneId, string name)
    {
        string value = JumpCloneRules.Name(name);
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        Owned(world, pilotId, cloneId).Name = value;
        world.SaveChanges();
        transaction.Commit();
    }

    public int Destroy(long pilotId, long cloneId)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        var clone = Owned(world, pilotId, cloneId);
        int station = clone.StationId!.Value;
        world.Remove(clone);
        world.SaveChanges();
        transaction.Commit();
        return station;
    }

    internal static void ResetActive(WorldDbContext world, long pilotId)
    {
        world.RemoveRange(world.Set<SavedJumpClone>().Where(c => c.PilotId == pilotId && c.Active));
    }

    internal static void ResetActive(WorldAction world, long pilotId)
    {
        world.RemoveRange(world.Filter<SavedJumpClone>(c => c.PilotId == pilotId && c.Active).ToArray());
    }

    public JumpCloneTransfer Jump(long pilotId, long expectedShipId, int stationId, long cloneId, long quotedCents)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        var source = CurrentStation(catalog, pilot, facility: false);
        var old = pilot.ActiveShip;
        if (old.Id != expectedShipId || catalog.FittingType(old.TypeId)?.GroupId != FleetDefinitions.CapsuleGroup)
        {
            throw new InvalidDataException("Leave your ship and jump from your current capsule.");
        }
        var incoming = Owned(world, pilotId, cloneId);
        if (incoming.StationId != stationId)
        {
            throw new InvalidDataException("The jump clone is not at that destination.");
        }
        var destination = Station(catalog, stationId);
        JumpCloneRules.CheckLocation(destination.SolarSystemId, destination.RegionId);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var timer = world.Set<SavedJumpCloneTimer>().Find(pilotId);
        JumpCloneRules.CheckTimer(source.Id == stationId, timer?.Time, Skills(world, catalog, pilot), now);
        var payment = Pay(world, pilot, source, JumpClonePaymentKind.Activation, quotedCents, now);
        var type =
            (
                catalog.FittingType(FleetDefinitions.CapsuleType) is { GroupId: FleetDefinitions.CapsuleGroup } replacement
                    ? replacement
                    : null
            ) ?? throw new InvalidDataException("The catalog has no replacement capsule.");
        var previous = PilotFleetStore.Read(catalog, old);
        var capsule = world
            .Add(
                new Ship
                {
                    Id = world.NextItemId(),
                    OwnerId = pilotId,
                    TypeId = type.Id,
                    StationId = stationId,
                    Name = type.Name,
                }
            )
            .Entity;
        training.ChangeAttributes(
            world,
            pilotId,
            () =>
            {
                var outgoing =
                    world.Filter<SavedJumpClone>(c => c.PilotId == pilotId && c.Active).SingleOrDefault()
                    ?? world.Add(new SavedJumpClone { PilotId = pilotId }).Entity;
                outgoing.Active = false;
                outgoing.StationId = source.Id;
                // Release the filtered unique active-body key before activating the target.
                world.SaveChanges();
                incoming.Active = true;
                incoming.StationId = null;
            }
        );
        if (source.Id != stationId)
        {
            timer ??= world.Add(new SavedJumpCloneTimer { PilotId = pilotId }).Entity;
            timer.Time = now;
        }
        payment.CloneId = cloneId;
        payment.DestinationStationId = stationId;
        payment.OldShipId = old.Id;
        payment.NewShipId = capsule.Id;
        pilot.ActiveShip = capsule;
        pilot.ActiveShipId = capsule.Id;
        world.SaveChanges();
        ShipStoragePersistence.Destroy(world, old);
        world.SaveChanges();
        var item = PilotFleetStore.Read(catalog, capsule);
        var activePilot = lookup.FindPilot(world, pilot.AccountId, pilotId)!;
        transaction.Commit();
        return new(activePilot, [new(previous with { Quantity = 0 }, previous), new(item, null)], payment.BalanceCents, source.Id);
    }
}
