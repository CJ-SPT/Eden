using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Corporations;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Corporations;

[Injectable]
internal sealed class PlayerCorporationStore(WorldDatabase database, TimeProvider clock) : IPlayerCorporations
{
    public PlayerCorporation? Read(int corporationId) =>
        LiveReadModels.Find(database.Path) is { } resident
            ? resident.Corporation(corporationId, () => ReadCorporation(corporationId))
            : ReadCorporation(corporationId);

    private PlayerCorporation? ReadCorporation(int corporationId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        return world.Set<SavedCorporation>().Find(corporationId)?.Snapshot(world.Pilots.Count(p => p.CorporationId == corporationId));
    }

    public CorporationIdentity Identity(long pilotId) =>
        LiveReadModels.Find(database.Path)?.CorporationIdentity(pilotId, () => ReadIdentity(pilotId)) ?? ReadIdentity(pilotId);

    private CorporationIdentity ReadIdentity(long pilotId)
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
            ticker = catalog.Read(CatalogData.Client.Npccorporations, (long)pilot.CorporationId)?.TickerName ?? "";
        }
        long joined =
            world
                .Set<SavedCorporationEmployment>()
                .Where(e => e.PilotId == pilotId)
                .OrderByDescending(e => e.Id)
                .Select(e => (long?)e.StartedAt)
                .FirstOrDefault()
            ?? pilot.CreatedAt;
        var access = CorporationRoleStore.Effective(world, pilotId, corporation);
        var ship = world.Ships.Include(s => s.Position).Single(s => s.Id == pilot.ActiveShipId);
        long currentRoles = access.Roles.At(ship.Position is null ? ship.StationId : null, corporation?.StationId ?? 0, access.BaseId);
        return new(
            pilot.CorporationId,
            ticker,
            currentRoles,
            joined,
            access,
            CorporationWalletStore.SelectedKey(world, pilotId, corporation)
        );
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
        var corporation = world.Set<SavedCorporation>().Find(viewer.CorporationId);
        return world
            .Pilots.Where(p => p.CorporationId == viewer.CorporationId)
            .OrderBy(p => p.Id)
            .AsEnumerable()
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
                CorporationRoleStore.Effective(world, p.Id, corporation).Roles.Roles,
                CorporationRoleStore.Effective(world, p.Id, corporation)
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
        double rank = catalog
            .FittingAttributes(CorporationRules.ManagementSkill)
            .TryGetValue(DogmaAttributeId.SkillTimeConstant, out double value)
            ? value
            : throw new InvalidDataException("Missing Corporation Management catalog data.");
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
            || catalog.NpcCorporationNameExists(details.Name)
            || catalog
                .Records(CatalogData.Client.Npccorporations)
                .Values.Any(corporation => string.Equals(corporation.TickerName, details.Ticker, StringComparison.OrdinalIgnoreCase))
        )
        {
            throw new InvalidDataException("That corporation name or ticker is already taken.");
        }
        if (pilot.ActiveShip.StructureId.HasValue)
        {
            throw new InvalidDataException("Establish a new corporation's headquarters from an NPC station.");
        }
        var station = catalog.RequireStation(pilot.ActiveShip.StationId);
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
        CorporationShareStore.Issue(world, id);
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
            .Concat(catalog.Records(CatalogData.Client.Npccorporations).Values.Select(corporation => corporation.TickerName ?? ""))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return used.Contains(letters) ? [] : [letters];
    }
}
