using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
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

    public bool RestrictsCorpMails(long actorId) =>
        LiveWorld
            .Find(database.Path)
            ?.Economy.Inspect(world =>
            {
                var actor = world.Pilots.Find(actorId) ?? throw new InvalidDataException("Unknown pilot.");
                return world.Set<SavedCorporation>().Find(actor.CorporationId) is { ClosedAt: null } corporation
                    && corporation.Snapshot(0).Details.RestrictCorpMails;
            })
        ?? false;

    public void SetCorpMailRestriction(long actorId, bool restricted)
    {
        var world =
            LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Corporation mail settings require the resident world.");
        using var action = world.Economy.Begin(TransactionParticipants.None);
        var corporation = CorporationRoleStore.Current(action, actorId);
        CorporationRoleStore.RequireDirector(action, actorId, corporation);
        var details = corporation.Snapshot(0).Details;
        if (corporation.ClosedAt.HasValue)
        {
            throw new InvalidDataException("This corporation is closed.");
        }
        if (details.RestrictCorpMails != restricted)
        {
            corporation.DetailsJson = JsonSerializer.Serialize(details with { RestrictCorpMails = restricted });
        }
        action.Commit();
    }

    private void Change(long actorId, Func<CorporationDetails, CorporationDetails> update)
    {
        using WorldActionScope transaction = new(database, TransactionParticipants.None);
        var world = transaction.World;
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
        transaction.Commit();
    }
}
