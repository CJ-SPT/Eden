using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

[Injectable]
internal sealed class CorporationCommunicationStore(WorldDatabase database, TimeProvider clock) : ICorporationCommunications
{
    private LiveWorld Resident => LiveWorld.Find(database.Path) ?? throw new InvalidDataException("The world is not running.");
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public CorporationCommunications Read(long memberId) =>
        Resident.Economy.Inspect(world =>
        {
            var pilot = world.Pilots.Find(memberId) ?? throw new InvalidDataException("Unknown pilot.");
            return world.Set<SavedCorporation>().Find(pilot.CorporationId)?.Snapshot(0).Details.Communications
                ?? CorporationCommunications.Empty;
        });

    public long SaveBulletin(long actorId, string title, string body, long? id, long? editedAt)
    {
        CorporationCommunicationRules.CheckBulletin(title, body);
        long savedId = 0;
        Change(
            actorId,
            CorporationRole.ChatManager,
            state =>
            {
                var rows = state.Bulletins.ToList();
                if (id is long existing)
                {
                    int index = rows.FindIndex(row => row.Id == existing);
                    if (index < 0 || rows[index].EditedAt != editedAt)
                    {
                        throw new InvalidDataException("The bulletin changed. Reload it before saving.");
                    }
                    var before = rows[index];
                    rows[index] = before with
                    {
                        Title = title,
                        Body = body,
                        EditorId = actorId,
                        EditedAt = Math.Max(Now, before.EditedAt + 1),
                    };
                    savedId = existing;
                    return state with { Bulletins = rows.ToArray() };
                }
                if (editedAt.HasValue || rows.Count >= CorporationCommunicationRules.MaximumBulletins)
                {
                    throw new InvalidDataException("A corporation can have at most ten bulletins.");
                }
                savedId = state.NextBulletinId;
                rows.Add(new(savedId, title, body, actorId, Now, rows.Count));
                return state with { Bulletins = rows.ToArray(), NextBulletinId = checked(savedId + 1) };
            }
        );
        return savedId;
    }

    public void DeleteBulletin(long actorId, long id) =>
        Change(
            actorId,
            CorporationRole.ChatManager,
            state =>
            {
                if (!state.Bulletins.Any(row => row.Id == id))
                {
                    throw new InvalidDataException("Unknown corporation bulletin.");
                }
                return state with { Bulletins = state.Bulletins.Where(row => row.Id != id).ToArray() };
            }
        );

    public void OrderBulletins(long actorId, IReadOnlyList<long> ids) =>
        Change(
            actorId,
            CorporationRole.ChatManager,
            state =>
            {
                if (
                    ids.Count != state.Bulletins.Count
                    || ids.Distinct().Count() != ids.Count
                    || ids.Any(id => !state.Bulletins.Any(b => b.Id == id))
                )
                {
                    throw new InvalidDataException("Reload the bulletin list before changing its order.");
                }
                return state with
                {
                    Bulletins = ids.Select((id, order) => state.Bulletins.Single(b => b.Id == id) with { Order = order }).ToArray(),
                };
            }
        );

    public void SetWelcomeMail(long actorId, string body)
    {
        CorporationCommunicationRules.CheckWelcomeMail(body);
        Change(
            actorId,
            CorporationRole.PersonnelManager,
            state =>
                state.WelcomeMail.Body == body
                    ? state
                    : state with
                    {
                        WelcomeMail = new(body, actorId, Math.Max(Now, (state.WelcomeMail.EditedAt ?? 0) + 1)),
                    }
        );
    }

    private void Change(long actorId, CorporationRole role, Func<CorporationCommunications, CorporationCommunications> update)
    {
        using var transaction = Resident.Economy.Begin(TransactionParticipants.None);
        var corporation = Current(transaction, actorId);
        if (!CorporationRoleStore.Has(transaction, actorId, corporation, role))
        {
            throw new InvalidDataException("You do not have the corporation role required for this action.");
        }
        var before = corporation.Snapshot(0).Details;
        var previous = before.Communications ?? CorporationCommunications.Empty;
        var next = update(previous);
        if (next == previous)
        {
            return;
        }
        var after = before with { Communications = next };
        corporation.DetailsJson = JsonSerializer.Serialize(after);
        // Welcome-mail changes use the existing durable corporation event stream.
        if (previous.WelcomeMail != next.WelcomeMail)
        {
            string change = JsonSerializer.Serialize(new CorporationAdministrationChange(corporation.Id, before, after));
            foreach (long member in transaction.Pilots.Where(p => p.CorporationId == corporation.Id).Select(p => p.Id))
            {
                transaction.Add(new SavedCorporationChange { PilotId = member, AdministrationJson = change });
            }
        }
        transaction.Commit();
    }

    private static SavedCorporation Current(WorldAction world, long actorId)
    {
        var pilot = world.Pilots.Find(actorId) ?? throw new InvalidDataException("Unknown pilot.");
        return world.Set<SavedCorporation>().Find(pilot.CorporationId)
            ?? throw new InvalidDataException("Join a player corporation first.");
    }
}
