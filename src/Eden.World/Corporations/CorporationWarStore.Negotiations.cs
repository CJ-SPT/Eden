using Eden.Data.World.Corporations;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.World.Corporations;

internal sealed partial class CorporationWarStore
{
    // Build 3662168 Diplomatic Relations dogma attributes.
    private const DogmaAttributeId AllyBaseCostAttribute = (DogmaAttributeId)1820;
    private const DogmaAttributeId AllyCostSkillBonusAttribute = (DogmaAttributeId)1821;
    private const long MaximumNegotiationCents = 9_007_199_254_740_991L;

    public IReadOnlyList<CorporationWar> WarsRequiringAssistance()
    {
        long now = Now;
        return LiveWorld
                .Find(database.Path)
                ?.Economy.Inspect(world =>
                    world
                        .Filter<SavedCorporationWar>(war => war.OpenForAllies && war.EndingAt == null && war.BillDueAt > now)
                        .AsEnumerable()
                        .OrderByDescending(war => war.Id)
                        .Take(200)
                        .Select(war => Snapshot(world, war, now))
                        .ToArray()
                )
            ?? [];
    }

    public long AllyBaseCost(long pilotId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return LiveWorld.Find(database.Path)?.Economy.Inspect(world => AllyBaseCost(world, catalog, pilotId))
            ?? CorporationWarRules.DefaultAllyBaseCostCents;
    }

    private static long AllyBaseCost(WorldAction world, CatalogDbContext catalog, long pilotId)
    {
        var attributes = catalog.FittingAttributes(CorporationWarRules.DiplomaticRelationsSkill);
        double baseIsk = attributes.GetValueOrDefault(AllyBaseCostAttribute, CorporationWarRules.DefaultAllyBaseCostCents / 100d);
        double modifier = attributes.GetValueOrDefault(AllyCostSkillBonusAttribute);
        int level = PilotSkillReader.Level(world, catalog, pilotId, CorporationWarRules.DiplomaticRelationsSkill);
        return checked((long)Math.Round(baseIsk * Math.Max(0, 1 + modifier * level / 100), MidpointRounding.AwayFromZero) * 100);
    }

    public IReadOnlyList<CorporationWarNegotiation> Negotiations(long pilotId)
    {
        long now = Now;
        return Resident.Economy.Inspect(world =>
        {
            if (Negotiator(world, pilotId) is not { } actor)
            {
                return [];
            }
            int owner = actor.Owner;
            return world
                .Filter<SavedCorporationWarNegotiation>(row =>
                    row.State == CorporationWarNegotiationState.Pending && (row.FromOwnerId == owner || row.ToOwnerId == owner)
                )
                .AsEnumerable()
                .Where(row => Pending(world, row, now))
                .OrderByDescending(row => row.Id)
                .Select(Snapshot)
                .ToArray();
        });
    }

    public CorporationWarNegotiation? Negotiation(long pilotId, long id)
    {
        long now = Now;
        return Resident.Economy.Inspect(world =>
        {
            if (world.Set<SavedCorporationWarNegotiation>().Find(id) is not { } row)
            {
                return null;
            }
            var (owner, _) = Authorization(world, pilotId);
            if (owner != row.FromOwnerId && owner != row.ToOwnerId)
            {
                throw new InvalidDataException("Only the organizations negotiating this offer may read it.");
            }
            return row.State != CorporationWarNegotiationState.Pending || Pending(world, row, now) ? Snapshot(row) : null;
        });
    }

    public void SetOpenForAllies(long pilotId, long warId, bool open)
    {
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var (owner, _) = Authorization(world, pilotId);
        var war = OpenNegotiationWar(world, warId, Now);
        if (war.Mutual || war.DefenderId != owner)
        {
            throw new InvalidDataException("Only the defender in a declared war can advertise for allies.");
        }
        if (war.OpenForAllies != open)
        {
            war.OpenForAllies = open;
            world.Commit();
        }
    }

    public void CreateAllyOffer(long pilotId, long warId, int defenderId, long amountCents, string description)
    {
        CheckOfferTerms(amountCents, description);
        long now = Now;
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var (owner, _) = Authorization(world, pilotId);
        var war = OpenNegotiationWar(world, warId, now);
        if (defenderId != war.DefenderId)
        {
            throw new InvalidDataException("Choose the defender of this war.");
        }
        CheckAlly(world, war, owner, now);
        var offer = CreateOffer(world, war, CorporationWarNegotiationKind.AllyOffer, owner, defenderId, amountCents, description, now);
        CorporationWarNotifications.Negotiation(world, inbox, Snapshot(offer), pilotId, now);
        world.Commit();
    }

    public void CreateSurrenderOffer(long pilotId, long warId, long amountCents, string description)
    {
        CheckOfferTerms(amountCents, description);
        long now = Now;
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var (owner, payer) = Authorization(world, pilotId);
        var war = OpenNegotiationWar(world, warId, now);
        if (owner != war.AttackerId && owner != war.DefenderId)
        {
            throw new InvalidDataException("Only the two principal organizations may offer surrender.");
        }
        int recipient = owner == war.AttackerId ? war.DefenderId : war.AttackerId;
        var offer = CreateOffer(world, war, CorporationWarNegotiationKind.Surrender, owner, recipient, amountCents, description, now);
        if (amountCents > 0)
        {
            offer.PayerCorporationId = payer.Id;
            offer.PayerDivisionKey = CorporationWalletStore.SelectedKey(world, pilotId, payer);
        }
        CorporationWarNotifications.Negotiation(world, inbox, Snapshot(offer), pilotId, now);
        world.Commit();
    }

    public void ResolveNegotiation(long pilotId, long id, CorporationWarNegotiationKind kind, CorporationWarNegotiationAction action)
    {
        long now = Now;
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var (owner, payer) = Authorization(world, pilotId);
        if (
            world.Set<SavedCorporationWarNegotiation>().Find(id) is not { } offer
            || offer.Kind != kind
            || owner != (action == CorporationWarNegotiationAction.Retract ? offer.FromOwnerId : offer.ToOwnerId)
        )
        {
            throw new InvalidDataException("This organization cannot resolve the war offer.");
        }
        if (offer.State == Resolution(action))
        {
            return;
        }
        if (offer.State != CorporationWarNegotiationState.Pending || !Pending(world, offer, now))
        {
            throw new InvalidDataException("The war offer is no longer pending.");
        }

        CorporationWarAlly? ally = null;
        long? warEndsAt = null;
        switch (action)
        {
            case CorporationWarNegotiationAction.Accept:
                var war = OpenNegotiationWar(world, offer.WarId, now);
                if (kind == CorporationWarNegotiationKind.AllyOffer)
                {
                    ally = AcceptAlly(world, war, offer, payer, pilotId, now);
                }
                else
                {
                    AcceptSurrender(world, war, offer, payer, now);
                    warEndsAt = war.EndsAt;
                }
                offer.AcceptedAt = now;
                break;
            case CorporationWarNegotiationAction.Decline:
                offer.DeclinedAt = now;
                break;
            case CorporationWarNegotiationAction.Retract:
                offer.RetractedAt = now;
                break;
        }
        offer.State = Resolution(action);
        CorporationWarNotifications.Negotiation(world, inbox, Snapshot(offer), pilotId, now, ally, warEndsAt);
        world.Commit();
    }

    private CorporationWarAlly AcceptAlly(
        WorldAction world,
        SavedCorporationWar war,
        SavedCorporationWarNegotiation offer,
        SavedCorporation payer,
        long pilotId,
        long now
    )
    {
        CheckAlly(world, war, offer.FromOwnerId, now);
        var recipient = Payer(world, offer.FromOwnerId) ?? throw new InvalidDataException("The ally has no active wallet.");
        using CatalogDbContext catalog = new(database.CatalogPath);
        int otherAllies = world.Filter<SavedCorporationWarAlly>(row => row.WarId == war.Id && row.EndsAt > now).Count();
        long fee = otherAllies == 0 ? 0 : checked(AllyBaseCost(world, catalog, pilotId) * (1L << Math.Min(otherAllies - 1, 20)));
        TransferNegotiation(world, offer, payer, recipient, CorporationWalletStore.SelectedKey(world, pilotId, payer), fee, now);

        var row =
            world.Set<SavedCorporationWarAlly>().Find(war.Id, offer.FromOwnerId)
            ?? world
                .Add(
                    new SavedCorporationWarAlly
                    {
                        WarId = war.Id,
                        OwnerId = offer.FromOwnerId,
                        AttackerId = war.AttackerId,
                    }
                )
                .Entity;
        row.StartsAt = checked(now + CorporationWarRules.AllyWarmup);
        row.EndsAt = checked(now + CorporationWarRules.AllyLifetime);
        return new(row.OwnerId, row.StartsAt, row.EndsAt);
    }

    private void AcceptSurrender(
        WorldAction world,
        SavedCorporationWar war,
        SavedCorporationWarNegotiation offer,
        SavedCorporation recipient,
        long now
    )
    {
        if (offer.AmountCents > 0)
        {
            var payer = Payer(world, offer.FromOwnerId);
            if (payer is null || payer.Id != offer.PayerCorporationId || offer.PayerDivisionKey is not int key)
            {
                throw new InvalidDataException("The offer's original corporation wallet is no longer authorized to pay.");
            }
            TransferNegotiation(world, offer, payer, recipient, key, 0, now);
        }
        End(world, war, CorporationWarEndReason.Surrender, now, now);
    }

    private static void TransferNegotiation(
        WorldAction world,
        SavedCorporationWarNegotiation offer,
        SavedCorporation payer,
        SavedCorporation recipient,
        int division,
        long concordFee,
        long now
    )
    {
        long amount = offer.AmountCents;
        if (amount == 0 && concordFee == 0)
        {
            return;
        }
        long balance = CorporationWalletStore.ReadBalance(world, payer, division);
        if (payer.Id == recipient.Id || balance < amount || balance - amount < concordFee)
        {
            throw new InvalidDataException("The paying corporation wallet cannot cover this agreement.");
        }
        long receivingBalance = CorporationWalletStore.ReadBalance(world, recipient, CorporationWalletRules.MasterKey);
        if (receivingBalance > long.MaxValue - amount)
        {
            throw new InvalidDataException("The receiving corporation wallet cannot hold this payment.");
        }

        bool ally = offer.Kind == CorporationWarNegotiationKind.AllyOffer;
        long payerOwner = ally ? offer.ToOwnerId : offer.FromOwnerId;
        long recipientOwner = ally ? offer.FromOwnerId : offer.ToOwnerId;
        var type = WalletEntryType.PlayerDonation;
        if (amount > 0)
        {
            string details = ally ? "War ally agreement" : "War surrender agreement";
            balance -= amount;
            receivingBalance += amount;
            CorporationWalletStore.WriteBalance(world, recipient, CorporationWalletRules.MasterKey, receivingBalance);
            CorporationWalletStore.AddEntry(world, payer.Id, division, now, recipientOwner, -amount, balance, type, offer.WarId, details);
            CorporationWalletStore.AddEntry(
                world,
                recipient.Id,
                CorporationWalletRules.MasterKey,
                now,
                payerOwner,
                amount,
                receivingBalance,
                type,
                offer.WarId,
                details
            );
        }
        if (concordFee > 0)
        {
            balance -= concordFee;
            CorporationWalletStore.AddEntry(
                world,
                payer.Id,
                division,
                now,
                AllianceBillingRules.ConcordCorporationId,
                -concordFee,
                balance,
                type,
                offer.WarId,
                "CONCORD ally registration fee"
            );
        }
        CorporationWalletStore.WriteBalance(world, payer, division, balance);
    }

    private static (int Owner, SavedCorporation Payer)? Negotiator(WorldAction world, long pilotId)
    {
        if (world.Pilots.Find(pilotId) is not { } actor || world.Set<SavedCorporation>().Find(actor.CorporationId) is not { } corporation)
        {
            return null;
        }
        int owner = Organization(world, corporation.Id);
        return Payer(world, owner)?.Id == corporation.Id && CorporationRoleStore.Has(world, pilotId, corporation, CorporationRole.Director)
            ? (owner, corporation)
            : null;
    }

    private SavedCorporationWar OpenNegotiationWar(WorldAction world, long warId, long now)
    {
        var war = world.Set<SavedCorporationWar>().Find(warId) ?? throw new InvalidDataException("Choose an existing war.");
        Settle(world, war, now);
        if (war.EndingAt is not null)
        {
            throw new InvalidDataException("This war is already ending.");
        }
        return war;
    }

    private static void CheckAlly(WorldAction world, SavedCorporationWar war, int owner, long now)
    {
        if (
            war.Mutual
            || owner == war.AttackerId
            || owner == war.DefenderId
            || EligibilityLoss(world, owner, now) is not null
            || world.Set<SavedCorporationWarAlly>().Find(war.Id, owner)?.EndsAt > now
            || HasActiveWar(world, war.AttackerId, owner, now)
        )
        {
            throw new InvalidDataException("Only another eligible organization can join this war as an ally.");
        }
    }

    private static void CheckOfferTerms(long amount, string description)
    {
        if (amount is < 0 or > MaximumNegotiationCents || description.Length > CorporationWarRules.MaximumNegotiationDescriptionLength)
        {
            throw new InvalidDataException("Enter a valid offer amount and description.");
        }
    }

    private static SavedCorporationWarNegotiation CreateOffer(
        WorldAction world,
        SavedCorporationWar war,
        CorporationWarNegotiationKind kind,
        int from,
        int to,
        long amount,
        string description,
        long now
    )
    {
        if (
            world
                .Filter<SavedCorporationWarNegotiation>(row =>
                    row.WarId == war.Id && row.Kind == kind && row.FromOwnerId == from && row.ToOwnerId == to
                )
                .AsEnumerable()
                .Any(row => row.State == CorporationWarNegotiationState.Pending && Pending(world, row, now))
        )
        {
            throw new InvalidDataException("This organization already has a pending offer to the recipient.");
        }
        return world
            .Add(
                new SavedCorporationWarNegotiation
                {
                    Id = world.Next(CorporationWarSchema.NegotiationTable),
                    WarId = war.Id,
                    Kind = kind,
                    FromOwnerId = from,
                    ToOwnerId = to,
                    AttackerId = war.AttackerId,
                    DefenderId = war.DefenderId,
                    AmountCents = amount,
                    Description = description,
                    CreatedAt = now,
                }
            )
            .Entity;
    }

    private static bool Pending(WorldAction world, SavedCorporationWarNegotiation offer, long now) =>
        offer.CreatedAt > now - CorporationWarRules.NegotiationLifetime
        && world.Set<SavedCorporationWar>().Find(offer.WarId) is { EndingAt: null } war
        && war.AttackerId == offer.AttackerId
        && war.DefenderId == offer.DefenderId
        && (war.Mutual || war.BillDueAt > now);

    private static CorporationWarNegotiationState Resolution(CorporationWarNegotiationAction action) =>
        action switch
        {
            CorporationWarNegotiationAction.Accept => CorporationWarNegotiationState.Accepted,
            CorporationWarNegotiationAction.Decline => CorporationWarNegotiationState.Declined,
            CorporationWarNegotiationAction.Retract => CorporationWarNegotiationState.Retracted,
            _ => throw new InvalidDataException("Choose a supported war negotiation action."),
        };

    private static CorporationWarNegotiation Snapshot(SavedCorporationWarNegotiation offer) =>
        new(
            offer.Id,
            offer.WarId,
            offer.Kind,
            offer.FromOwnerId,
            offer.ToOwnerId,
            offer.AttackerId,
            offer.DefenderId,
            offer.AmountCents,
            offer.Description,
            offer.State,
            offer.CreatedAt,
            offer.AcceptedAt,
            offer.DeclinedAt,
            offer.RetractedAt
        );
}
