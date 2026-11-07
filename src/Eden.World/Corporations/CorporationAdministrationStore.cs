using System.Text.Json;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Storage;

namespace Eden.World.Corporations;

[Injectable]
internal sealed class CorporationAdministrationStore(WorldDatabase database) : ICorporationAdministration
{
    public void Update(long actorId, CorporationProperties properties) =>
        Change(
            actorId,
            details =>
                details with
                {
                    Description = properties.Description,
                    Url = properties.Url,
                    TaxRate = properties.TaxRate,
                    ApplicationsEnabled = properties.IsRecruiting,
                    LoyaltyPointTaxRate = properties.LoyaltyPointTaxRate,
                }
        );

    public void UpdateLogo(long actorId, CorporationLogo logo) => Change(actorId, details => details with { Logo = logo });

    public void UpdateDivisions(long actorId, CorporationDivisions divisions) =>
        Change(actorId, details => details with { Divisions = divisions });

    private void Change(long actorId, Func<CorporationDetails, CorporationDetails> update)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var actor = world.Pilots.Find(actorId) ?? throw new InvalidDataException("Unknown pilot.");
        var corporation =
            world.Set<SavedCorporation>().Find(actor.CorporationId)
            ?? throw new InvalidDataException("Join a player corporation before editing its details.");
        CorporationRoleStore.RequireDirector(world, actorId, corporation);
        var before = corporation.Snapshot(0).Details;
        var after = CorporationRules.Validate(update(before));
        if (before == after)
        {
            return;
        }
        corporation.DetailsJson = JsonSerializer.Serialize(after);
        string change = JsonSerializer.Serialize(new CorporationAdministrationChange(corporation.Id, before, after));
        foreach (long member in world.Pilots.Where(p => p.CorporationId == corporation.Id).Select(p => p.Id))
        {
            world.Add(new SavedCorporationChange { PilotId = member, AdministrationJson = change });
        }
        world.SaveChanges();
        transaction.Commit();
    }
}
