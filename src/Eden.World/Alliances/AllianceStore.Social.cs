using System.Text.Json;
using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Chat;
using Eden.Gameplay.Corporations;
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Alliances;

internal sealed partial class AllianceStore
{
    private const CorporationRole MotdRole = CorporationRole.Director;

    public AllianceSocialBook Read(long actorId)
    {
        using var coherent = Resident.EnterPersistence("resident alliance social read", TransactionParticipants.None);
        int alliance = Resident.Economy.Inspect(world => MemberAlliance(world, actorId));
        return Social(alliance);
    }

    public long Revision(int allianceId) =>
        Resident.Economy.Inspect(world => world.Set<SavedAllianceSocial>().Find(allianceId)?.Revision ?? 0);

    private AllianceSocialBook Social(int allianceId) =>
        LiveReadModels
            .Find(database.Path)!
            .AllianceSocial(
                allianceId,
                () =>
                    Resident.Economy.Inspect(world =>
                    {
                        var state = Social(world, allianceId);
                        return state with
                        {
                            Contacts = Array.AsReadOnly(state.Contacts.ToArray()),
                            Labels = Array.AsReadOnly(state.Labels.ToArray()),
                            Bulletins = Array.AsReadOnly(state.Bulletins.ToArray()),
                            Events = Array.AsReadOnly(
                                state
                                    .Events.Select(entry => entry with { Attendance = Array.AsReadOnly(entry.Attendance.ToArray()) })
                                    .ToArray()
                            ),
                        };
                    })
            );

    private static AllianceSocialBook Social(WorldAction world, int allianceId) =>
        world.Set<SavedAllianceSocial>().Find(allianceId) is { } row
            ? JsonSerializer.Deserialize<AllianceSocialBook>(row.DataJson)!
            : AllianceSocialBook.Empty(allianceId);

    internal static int MemberAlliance(WorldAction world, long actorId)
    {
        var pilot = world.Pilots.Find(actorId) ?? throw new InvalidDataException("Unknown alliance member.");
        return CorporationAlliance(world, pilot.CorporationId).Id;
    }

    private static SavedAlliance CorporationAlliance(WorldAction world, int corporationId) =>
        Active(
            world,
            world.Set<SavedAllianceMember>().Find(corporationId)?.AllianceId
                ?? throw new InvalidDataException("Your corporation is not in an alliance.")
        );

    public double? Standing(int allianceId, long contactId) =>
        Social(allianceId).Contacts.SingleOrDefault(c => c.ContactId == contactId)?.Standing;

    private void ChangeSocial(
        long actorId,
        Func<WorldAction, AllianceSocialBook, AllianceSocialBook> change,
        int? expected = null,
        CorporationRole role = CorporationRole.Director,
        bool executorOnly = true
    )
    {
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var corporation = CorporationRoleStore.Current(world, actorId);
        var alliance = CorporationAlliance(world, corporation.Id);
        if (expected.HasValue && expected != alliance.Id)
        {
            throw new InvalidDataException("Choose your current alliance.");
        }
        if (executorOnly && alliance.ExecutorCorporationId != corporation.Id)
        {
            throw new InvalidDataException("Alliance executor authority is required.");
        }
        if (!CorporationRoleStore.Has(world, actorId, corporation, role))
        {
            throw new InvalidDataException("You do not have the corporation role required for this alliance action.");
        }
        var state = Social(world, alliance.Id);
        SaveSocial(world, state, change(world, state));
        world.Commit();
    }

    private static void SaveSocial(WorldAction world, AllianceSocialBook before, AllianceSocialBook value)
    {
        var after = value with { Revision = checked(before.Revision + 1) };
        var row =
            world.Set<SavedAllianceSocial>().Find(before.AllianceId)
            ?? world.Add(new SavedAllianceSocial { AllianceId = before.AllianceId }).Entity;
        row.Revision = after.Revision;
        row.DataJson = JsonSerializer.Serialize(after);
    }

    private void CheckTargets(WorldAction world, int allianceId, IReadOnlyList<long> contacts)
    {
        ContactRules.CheckContacts(allianceId, contacts);
        foreach (long id in contacts)
        {
            if (
                world.PilotExists(id)
                || id is > 0 and <= int.MaxValue
                    && (
                        world.Set<SavedCorporation>().Find((int)id) is not null
                        || world.Set<SavedAlliance>().Find((int)id) is not null
                        || database.Catalog.NpcCorporations.ContainsKey((int)id)
                    )
            )
            {
                continue;
            }
            throw new InvalidDataException("Choose known pilots, corporations or alliances as contacts.");
        }
    }

    public void SetStanding(long actorId, IReadOnlyList<long> contacts, double standing, bool directorOnly = false)
    {
        ContactRules.CheckStanding(standing);
        ChangeSocial(
            actorId,
            (world, state) =>
            {
                CheckTargets(world, state.AllianceId, contacts);
                var entries = state.Contacts.ToDictionary(c => c.ContactId);
                foreach (long id in contacts)
                {
                    entries[id] = new(id, standing, entries.GetValueOrDefault(id)?.LabelMask ?? 0);
                }
                return state with { Contacts = entries.Values.OrderBy(c => c.ContactId).ToArray() };
            },
            role: directorOnly ? CorporationRole.Director : CorporationRole.Diplomat
        );
    }

    public void DeleteContacts(long actorId, IReadOnlyList<long> contacts, bool directorOnly = false) =>
        ChangeSocial(
            actorId,
            (world, state) =>
            {
                CheckTargets(world, state.AllianceId, contacts);
                return state with { Contacts = state.Contacts.Where(c => !contacts.Contains(c.ContactId)).ToArray() };
            },
            role: directorOnly ? CorporationRole.Director : CorporationRole.Diplomat
        );

    public long CreateLabel(long actorId, string name, int? color)
    {
        ContactRules.CheckLabelName(name);
        ContactRules.CheckLabelColor(color);
        long id = 0;
        ChangeSocial(
            actorId,
            (_, state) =>
            {
                CheckLabelName(state, name, null);
                var used = state.Labels.Select(l => l.Id).ToHashSet();
                id = Enumerable.Range(0, ContactRules.MaximumLabels).Select(bit => 1L << bit).FirstOrDefault(bit => !used.Contains(bit));
                if (id == 0)
                {
                    throw new InvalidDataException("No alliance contact labels remain available.");
                }
                return state with { Labels = state.Labels.Append(new(id, name.Trim(), color)).ToArray() };
            },
            role: CorporationRole.Diplomat
        );
        return id;
    }

    private static void CheckLabelName(AllianceSocialBook state, string name, long? except)
    {
        if (state.Labels.Any(l => l.Id != except && string.Equals(l.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("An alliance contact label already has this name.");
        }
    }

    public void EditLabel(long actorId, long labelId, string? name, int? color)
    {
        ContactRules.CheckLabelId(labelId);
        ContactRules.CheckLabelColor(color);
        if (name is not null)
        {
            ContactRules.CheckLabelName(name);
        }
        if (name is null && color is null)
        {
            throw new InvalidDataException("Choose a label change.");
        }
        ChangeSocial(
            actorId,
            (_, state) =>
            {
                var before =
                    state.Labels.SingleOrDefault(l => l.Id == labelId) ?? throw new InvalidDataException("Unknown alliance label.");
                CheckLabelName(state, name ?? before.Name, labelId);
                return state with
                {
                    Labels = state
                        .Labels.Select(l => l.Id == labelId ? l with { Name = name?.Trim() ?? l.Name, Color = color ?? l.Color } : l)
                        .ToArray(),
                };
            },
            role: CorporationRole.Diplomat
        );
    }

    public void DeleteLabel(long actorId, long labelId)
    {
        ContactRules.CheckLabelId(labelId);
        ChangeSocial(
            actorId,
            (_, state) =>
            {
                if (!state.Labels.Any(l => l.Id == labelId))
                {
                    throw new InvalidDataException("Unknown alliance label.");
                }
                return state with
                {
                    Labels = state.Labels.Where(l => l.Id != labelId).ToArray(),
                    Contacts = state.Contacts.Select(c => c with { LabelMask = c.LabelMask & ~labelId }).ToArray(),
                };
            },
            role: CorporationRole.Diplomat
        );
    }

    public void SetLabels(long actorId, IReadOnlyList<long> contacts, long mask, bool assign) =>
        ChangeSocial(
            actorId,
            (world, state) =>
            {
                CheckTargets(world, state.AllianceId, contacts);
                long allowed = state.Labels.Aggregate(0L, (value, label) => value | label.Id);
                if (mask <= 0 || (mask & ~allowed) != 0 || contacts.Any(id => !state.Contacts.Any(c => c.ContactId == id)))
                {
                    throw new InvalidDataException("Choose existing alliance contacts and labels.");
                }
                return state with
                {
                    Contacts = state
                        .Contacts.Select(c =>
                            contacts.Contains(c.ContactId) ? c with { LabelMask = assign ? c.LabelMask | mask : c.LabelMask & ~mask } : c
                        )
                        .ToArray(),
                };
            },
            role: CorporationRole.Diplomat
        );

    public long SaveBulletin(long actorId, string title, string body, long? id, long? editedAt)
    {
        CorporationCommunicationRules.CheckBulletin(title, body);
        long saved = 0;
        ChangeSocial(
            actorId,
            (world, state) =>
            {
                var rows = state.Bulletins.Select((bulletin, order) => bulletin with { Order = order }).ToList();
                if (id is long existing)
                {
                    int index = rows.FindIndex(b => b.Id == existing);
                    if (index < 0 || rows[index].EditedAt != editedAt)
                    {
                        throw new InvalidDataException("The alliance bulletin changed. Reload before editing.");
                    }
                    saved = existing;
                    rows[index] = rows[index] with
                    {
                        Title = title,
                        Body = body,
                        EditorId = actorId,
                        EditedAt = Math.Max(Now, rows[index].EditedAt + 1),
                    };
                }
                else
                {
                    if (editedAt.HasValue || rows.Count >= AllianceSocialRules.MaximumBulletins)
                    {
                        throw new InvalidDataException("An alliance can have at most ten bulletins.");
                    }
                    saved = world.Next("AllianceBulletin");
                    rows.Add(new(saved, title, body, actorId, Now, rows.Count));
                }
                return state with { Bulletins = rows.ToArray() };
            },
            role: id.HasValue ? CorporationRole.ChatManager : CorporationRole.Director,
            executorOnly: false
        );
        return saved;
    }

    public void DeleteBulletin(long actorId, long id) =>
        ChangeSocial(
            actorId,
            (_, state) =>
            {
                if (!state.Bulletins.Any(b => b.Id == id))
                {
                    throw new InvalidDataException("Unknown alliance bulletin.");
                }
                return state with
                {
                    Bulletins = state
                        .Bulletins.Where(b => b.Id != id)
                        .Select((bulletin, order) => bulletin with { Order = order })
                        .ToArray(),
                };
            },
            role: CorporationRole.ChatManager,
            executorOnly: false
        );

    public void OrderBulletins(long actorId, IReadOnlyList<long> ids) =>
        ChangeSocial(
            actorId,
            (_, state) =>
            {
                if (
                    ids.Count != state.Bulletins.Count
                    || ids.Distinct().Count() != ids.Count
                    || ids.Any(id => !state.Bulletins.Any(b => b.Id == id))
                )
                {
                    throw new InvalidDataException("Supply every current bulletin once.");
                }
                var ranks = ids.Select((id, rank) => (id, rank)).ToDictionary(pair => pair.id, pair => pair.rank);
                return state with
                {
                    Bulletins = state.Bulletins.Select(b => b with { Order = ranks[b.Id] }).OrderBy(b => b.Order).ToArray(),
                };
            },
            executorOnly: false
        );

    public string ReadMotd(int allianceId) => Social(allianceId).Motd;

    public IReadOnlyList<long> MotdEditors(int allianceId, IReadOnlyList<long> occupants) =>
        Resident.Economy.Inspect(world =>
        {
            if (
                world.Set<SavedAlliance>().Find(allianceId) is not { ClosedAt: null, ExecutorCorporationId: int executor }
                || world.Set<SavedAllianceMember>().Find(executor)?.AllianceId != allianceId
                || world.Set<SavedCorporation>().Find(executor) is not { ClosedAt: null } corporation
            )
            {
                return (IReadOnlyList<long>)[];
            }
            return occupants.Where(pilot => CorporationRoleStore.Has(world, pilot, corporation, MotdRole)).Distinct().ToArray();
        });

    public void SetMotd(long actorId, int allianceId, string text)
    {
        ChatRules.CheckMotd(text);
        ChangeSocial(actorId, (_, state) => state with { Motd = text }, allianceId, MotdRole);
    }
}
