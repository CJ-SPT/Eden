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
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Characters;

[Injectable]
internal sealed class PilotJumpCloneStore(WorldDatabase database, SqliteWorld lookup, TimeProvider clock, PilotTrainingStore training)
    : IPilotJumpClones
{
    private static Pilot Pilot(WorldDbContext world, long id) =>
        world.Pilots.Include(p => p.Account).Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.Id == id)
        ?? throw new InvalidDataException("Unknown pilot.");

    private static Station Station(CatalogDbContext catalog, int id) =>
        catalog.Set<Station>().Include(s => s.SolarSystem).ThenInclude(s => s.Constellation).SingleOrDefault(s => s.Id == id)
        ?? throw new InvalidDataException("Unknown jump clone station.");

    private static Station CurrentStation(CatalogDbContext catalog, Pilot pilot, bool facility)
    {
        if (pilot.ActiveShip.Position is not null || pilot.ActiveShip.OwnerId != pilot.Id || pilot.ActiveShip.StoredInId is not null)
        {
            throw new InvalidDataException("Use jump clones while docked in an NPC station.");
        }
        var station = Station(catalog, pilot.ActiveShip.StationId);
        if (facility && !StationCapabilities.Has(catalog, station.OperationId, StationService.JumpCloneFacility))
        {
            throw new InvalidDataException("This station has no jump clone facility.");
        }
        // Pochven installation requires standings that Eden does not implement.
        JumpCloneRules.CheckLocation(station.SolarSystemId, station.SolarSystem.Constellation.RegionId);
        return station;
    }

    private static JumpCloneSkills Skills(WorldDbContext world, CatalogDbContext catalog, Pilot pilot)
    {
        if (
            !world
                .Set<PilotSkill>()
                .Any(s =>
                    s.PilotId == pilot.Id
                    && s.Points > 0
                    && (
                        s.TypeId == (int)InfomorphSkill.Psychology
                        || s.TypeId == (int)InfomorphSkill.AdvancedPsychology
                        || s.TypeId == (int)InfomorphSkill.ElitePsychology
                        || s.TypeId == (int)InfomorphSkill.Synchronizing
                    )
                )
        )
        {
            return new(0, 0, 0, 0);
        }
        var alpha =
            pilot.Account.CloneGrade == AccountCloneGrade.Alpha
                ? CatalogData
                    .Client.Clonegrades.Get(catalog, pilot.RaceId.ToString(CultureInfo.InvariantCulture))
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
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return Skills(world, catalog, Pilot(world, pilotId));
    }

    public JumpCloneSnapshot Read(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        _ = Pilot(world, pilotId);
        return new(
            world
                .Set<SavedJumpClone>()
                .Where(c => c.PilotId == pilotId && !c.Active)
                .OrderBy(c => c.Id)
                .Select(c => new JumpClone(c.Id, c.StationId!.Value, c.Name))
                .ToArray(),
            world.Set<SavedJumpCloneTimer>().Find(pilotId)?.Time
        )
        {
            Implants = (
                from implant in world.Set<SavedPilotImplant>()
                join body in world.Set<SavedJumpClone>() on implant.CloneId equals body.Id
                where body.PilotId == pilotId && !body.Active
                orderby body.Id, implant.Slot
                select implant
            )
                .AsEnumerable()
                .Select(i => i.Snapshot())
                .ToArray(),
        };
    }

    public long Price(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        _ = CurrentStation(catalog, Pilot(world, pilotId), facility: false);
        return JumpCloneRules.FeeCents;
    }

    private static int? InstallLimit(WorldDbContext world, CatalogDbContext catalog, Pilot pilot)
    {
        _ = CurrentStation(catalog, pilot, facility: true);
        int capacity = Skills(world, catalog, pilot).Capacity;
        return world.Set<SavedJumpClone>().Count(c => c.PilotId == pilot.Id && !c.Active) >= capacity ? capacity : null;
    }

    public int? InstallLimit(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return InstallLimit(world, catalog, Pilot(world, pilotId));
    }

    private SavedJumpClonePayment Pay(WorldDbContext world, Pilot pilot, Station station, JumpClonePaymentKind kind, long quote, long now)
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
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
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

    private static SavedJumpClone Owned(WorldDbContext world, long pilotId, long cloneId) =>
        world.Set<SavedJumpClone>().SingleOrDefault(c => c.Id == cloneId && c.PilotId == pilotId && !c.Active)
        ?? throw new InvalidDataException("That installed jump clone does not belong to you or is no longer available.");

    public void Rename(long pilotId, long cloneId, string name)
    {
        string value = JumpCloneRules.Name(name);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        Owned(world, pilotId, cloneId).Name = value;
        world.SaveChanges();
        transaction.Commit();
    }

    public int Destroy(long pilotId, long cloneId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
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

    public JumpCloneTransfer Jump(long pilotId, long expectedShipId, int stationId, long cloneId, long quotedCents)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        var source = CurrentStation(catalog, pilot, facility: false);
        var old = pilot.ActiveShip;
        if (old.Id != expectedShipId || catalog.Set<ItemType>().Find(old.TypeId)?.GroupId != FleetDefinitions.CapsuleGroup)
        {
            throw new InvalidDataException("Leave your ship and jump from your current capsule.");
        }
        var incoming = Owned(world, pilotId, cloneId);
        if (incoming.StationId != stationId)
        {
            throw new InvalidDataException("The jump clone is not at that destination.");
        }
        var destination = Station(catalog, stationId);
        JumpCloneRules.CheckLocation(destination.SolarSystemId, destination.SolarSystem.Constellation.RegionId);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var timer = world.Set<SavedJumpCloneTimer>().Find(pilotId);
        JumpCloneRules.CheckTimer(source.Id == stationId, timer?.Time, Skills(world, catalog, pilot), now);
        var payment = Pay(world, pilot, source, JumpClonePaymentKind.Activation, quotedCents, now);
        var type =
            catalog.Set<ItemType>().SingleOrDefault(t => t.Id == FleetDefinitions.CapsuleType && t.GroupId == FleetDefinitions.CapsuleGroup)
            ?? throw new InvalidDataException("The catalog has no replacement capsule.");
        var previous = PilotFleetStore.Read(catalog, old);
        var capsule = world
            .Add(
                new Ship
                {
                    Id = InventoryPersistence.NextId(world),
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
                    world.Set<SavedJumpClone>().SingleOrDefault(c => c.PilotId == pilotId && c.Active)
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
        transaction.Commit();
        return new(
            lookup.FindPilot(pilot.AccountId, pilotId)!,
            [new(previous with { Quantity = 0 }, previous), new(item, null)],
            payment.BalanceCents,
            source.Id
        );
    }
}
