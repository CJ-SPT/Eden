using System.Globalization;
using System.Text.Json;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Skills;
using Eden.Protocol;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class SavedCorporation
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Ticker { get; set; } = "";
    public string DetailsJson { get; set; } = "";
    public long CeoId { get; set; }
    public long CreatorId { get; set; }
    public int StationId { get; set; }
    public int SystemId { get; set; }
    public long CreatedAt { get; set; }
    public int MemberLimit { get; set; }
    public long BalanceCents { get; set; }

    public PlayerCorporation Snapshot(int members) =>
        new(
            Id,
            JsonSerializer.Deserialize<CorporationDetails>(DetailsJson)!,
            CeoId,
            CreatorId,
            StationId,
            SystemId,
            CreatedAt,
            MemberLimit,
            members
        );
}

public sealed class SavedCorporationEmployment
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public int CorporationId { get; set; }
    public long StartedAt { get; set; }
}

public sealed class SavedCorporationPayment
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public int CorporationId { get; set; }
    public long Time { get; set; }
    public long BalanceCents { get; set; }

    public CorporationPayment Snapshot() => new(Id, PilotId, CorporationId, Time, BalanceCents);
}

[Injectable]
internal sealed class PlayerCorporationStore(WorldDatabase database, TimeProvider clock) : IPlayerCorporations
{
    public PlayerCorporation? Read(int corporationId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        return world.Set<SavedCorporation>().Find(corporationId)?.Snapshot(world.Pilots.Count(p => p.CorporationId == corporationId));
    }

    public CorporationIdentity Identity(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown pilot.");
        var corporation = world.Set<SavedCorporation>().Find(pilot.CorporationId);
        string ticker;
        if (corporation is not null)
        {
            ticker = corporation.Ticker;
        }
        else
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            ticker =
                catalog
                    .Set<SourceRecord>()
                    .Find("client/npccorporations", pilot.CorporationId.ToString(CultureInfo.InvariantCulture))
                    ?.Read<ClientNpccorporations>()
                    .TickerName
                ?? "";
        }
        long joined =
            world
                .Set<SavedCorporationEmployment>()
                .Where(e => e.PilotId == pilotId)
                .OrderByDescending(e => e.Id)
                .Select(e => (long?)e.StartedAt)
                .FirstOrDefault()
            ?? pilot.CreatedAt;
        return new(pilot.CorporationId, ticker, corporation?.CeoId == pilotId ? CorporationRules.CeoRoles : 0, joined);
    }

    public IReadOnlyList<CorporationEmployment> Employment(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown pilot.");
        var history = world
            .Set<SavedCorporationEmployment>()
            .Where(e => e.PilotId == pilotId)
            .OrderByDescending(e => e.Id)
            .Select(e => new CorporationEmployment(e.CorporationId, e.StartedAt))
            .ToArray();
        return history.Length == 0 ? [new(pilot.CorporationId, pilot.CreatedAt)] : history;
    }

    public IReadOnlyList<CorporationMember> Members(long viewerId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var viewer = world.Pilots.Find(viewerId) ?? throw new InvalidDataException("Unknown pilot.");
        long? ceo = world.Set<SavedCorporation>().Find(viewer.CorporationId)?.CeoId;
        return world
            .Pilots.Where(p => p.CorporationId == viewer.CorporationId)
            .OrderBy(p => p.Id)
            .Select(p => new CorporationMember(
                p.Id,
                p.Name,
                p.CorporationId,
                world
                    .Set<SavedCorporationEmployment>()
                    .Where(e => e.PilotId == p.Id)
                    .OrderByDescending(e => e.Id)
                    .Select(e => (long?)e.StartedAt)
                    .FirstOrDefault()
                    ?? p.CreatedAt,
                p.Id == ceo ? CorporationRules.CeoRoles : 0
            ))
            .ToArray();
    }

    public CorporationCreation Create(long pilotId, CorporationDetails details)
    {
        details = CorporationRules.Validate(details);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot =
            world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.Id == pilotId)
            ?? throw new InvalidDataException("Unknown pilot.");
        double rank =
            catalog
                .Set<TypeAttribute>()
                .Where(a => a.ItemTypeId == CorporationRules.ManagementSkill && a.AttributeId == (int)DogmaAttributeId.SkillTimeConstant)
                .Select(a => (double?)a.Value)
                .SingleOrDefault()
            ?? throw new InvalidDataException("Missing Corporation Management catalog data.");
        long points = world.Set<PilotSkill>().Find(pilotId, CorporationRules.ManagementSkill)?.Points ?? 0;
        int level = new TrainedSkill(CorporationRules.ManagementSkill, points, rank).Level;
        var wallet = world.Set<PilotWallet>().Find(pilotId);
        long balance = CorporationRules.Pay(
            wallet?.Cents ?? 0,
            pilot.ActiveShip.Position is null,
            world.Set<SavedCorporation>().Any(c => c.CeoId == pilotId),
            level
        );
        if (
            world
                .Set<SavedCorporation>()
                .Any(c =>
                    EF.Functions.Collate(c.Name, "NOCASE") == details.Name || EF.Functions.Collate(c.Ticker, "NOCASE") == details.Ticker
                )
            || catalog.Set<NpcCorporation>().Any(c => EF.Functions.Collate(c.Name, "NOCASE") == details.Name)
            || catalog
                .Set<SourceRecord>()
                .Where(r => r.Dataset == "client/npccorporations")
                .AsEnumerable()
                .Any(r => string.Equals(r.Read<ClientNpccorporations>().TickerName, details.Ticker, StringComparison.OrdinalIgnoreCase))
        )
        {
            throw new InvalidDataException("That corporation name or ticker is already taken.");
        }
        var station = catalog.Set<Station>().Single(s => s.Id == pilot.ActiveShip.StationId);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        int id = checked((world.Set<SavedCorporation>().Max(c => (int?)c.Id) ?? CorporationRules.FirstCorporationId - 1) + 1);
        var corporation = world
            .Add(
                new SavedCorporation
                {
                    Id = id,
                    Name = details.Name,
                    Ticker = details.Ticker,
                    DetailsJson = JsonSerializer.Serialize(details),
                    CeoId = pilotId,
                    CreatorId = pilotId,
                    StationId = station.Id,
                    SystemId = station.SolarSystemId,
                    CreatedAt = now,
                    MemberLimit = level * CorporationRules.MembersPerManagementLevel,
                }
            )
            .Entity;
        CorporationMembershipStore.Move(world, pilot, id, now);
        CorporationMembershipStore.WithdrawOtherApplications(world, pilotId, null, now);
        wallet!.Cents = balance;
        world.Add(
            new SavedCorporationPayment
            {
                PilotId = pilotId,
                CorporationId = id,
                Time = now,
                BalanceCents = balance,
            }
        );
        world.SaveChanges();
        transaction.Commit();
        return new(corporation.Snapshot(1), balance);
    }

    public IReadOnlyList<string> SuggestTickers(string name)
    {
        string letters = new(name.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).Take(CorporationRules.MaximumTickerLength).ToArray());
        if (letters.Length == 0)
        {
            return [];
        }
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var used = world
            .Set<SavedCorporation>()
            .Select(c => c.Ticker)
            .AsEnumerable()
            .Concat(
                catalog
                    .Set<SourceRecord>()
                    .Where(r => r.Dataset == "client/npccorporations")
                    .AsEnumerable()
                    .Select(r => r.Read<ClientNpccorporations>().TickerName ?? "")
            )
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return used.Contains(letters) ? [] : [letters];
    }
}
