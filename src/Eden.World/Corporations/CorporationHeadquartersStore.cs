using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Corporations;

[Injectable]
internal sealed class CorporationHeadquartersStore(WorldDatabase database) : ICorporationHeadquarters
{
    public void MoveHere(long actorId)
    {
        var resident =
            LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Headquarters changes require an active resident world.");
        using var world = resident.Economy.Begin(TransactionParticipants.None);
        var corporation = CorporationRoleStore.Current(world, actorId);
        CorporationRoleStore.RequireDirector(world, actorId, corporation);
        var pilot = world.Pilots.Find(actorId)!;
        if (pilot.ActiveShip is not { Position: null, StructureId: null } ship)
        {
            throw new InvalidDataException("Dock at a station with a corporation office before moving headquarters.");
        }
        var office =
            world
                .Set<SavedCorporationOffice>()
                .SingleOrDefault(row => row.CorporationId == corporation.Id && row.StationId == ship.StationId)
            ?? throw new InvalidDataException("Your corporation must rent an office at this station before moving headquarters here.");
        if (corporation.StationId == office.StationId && corporation.SystemId == office.SystemId)
        {
            return;
        }
        var change = new CorporationHeadquartersChange(corporation.StationId, corporation.SystemId, office.StationId, office.SystemId);
        corporation.StationId = office.StationId;
        corporation.SystemId = office.SystemId;
        var details = corporation.Snapshot(0).Details;
        string json = JsonSerializer.Serialize(new CorporationAdministrationChange(corporation.Id, details, details, Headquarters: change));
        foreach (long member in world.Pilots.Where(row => row.CorporationId == corporation.Id).Select(row => row.Id))
        {
            world.Add(new SavedCorporationChange { PilotId = member, AdministrationJson = json });
        }
        world.Commit();
    }
}
