using System.Globalization;
using System.Text.Json;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Redemption;
using Eden.Gameplay.Skills;
using Microsoft.EntityFrameworkCore;

namespace Eden.Data.World.Corporations;

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

public sealed class SavedCorporationApplication
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public int CorporationId { get; set; }
    public string Text { get; set; } = "";
    public CorporationApplicationStatus Status { get; set; }
    public long AppliedAt { get; set; }
    public long UpdatedAt { get; set; }
    public string Response { get; set; } = "";

    public CorporationApplication Snapshot() => new(Id, PilotId, CorporationId, Text, Status, AppliedAt, UpdatedAt, Response);
}

public sealed class SavedCorporationBillPaymentSettings
{
    public int CorporationId { get; set; }
    public string DataJson { get; set; } = "";
}

public sealed class SavedCorporationChange
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public string? ApplicationJson { get; set; }
    public string? MembershipJson { get; set; }
    public string? AdministrationJson { get; set; }
    public string? RoleJson { get; set; }
    public string? TitleJson { get; set; }
    public string? ShareJson { get; set; }
    public string? StorageJson { get; set; }

    public CorporationChange Snapshot() =>
        new(
            Id,
            ApplicationJson is null ? null : JsonSerializer.Deserialize<CorporationApplication>(ApplicationJson),
            MembershipJson is null ? null : JsonSerializer.Deserialize<CorporationMemberChange>(MembershipJson),
            AdministrationJson is null ? null : JsonSerializer.Deserialize<CorporationAdministrationChange>(AdministrationJson),
            RoleJson is null ? null : JsonSerializer.Deserialize<CorporationRoleChange>(RoleJson),
            TitleJson is null ? null : JsonSerializer.Deserialize<CorporationTitleChange>(TitleJson),
            ShareJson is null ? null : JsonSerializer.Deserialize<CorporationShareChange>(ShareJson),
            StorageJson is null ? null : JsonSerializer.Deserialize<CorporationStorageChange>(StorageJson)
        );
}

public sealed class SavedCorporationEmployment
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public int CorporationId { get; set; }
    public long StartedAt { get; set; }
}

public sealed class SavedCorporationLoyaltyBalance
{
    public int CorporationId { get; set; }
    public int IssuerCorporationId { get; set; }
    public long Points { get; set; }
}

public sealed class SavedCorporationLoyaltyTransfer
{
    public long Id { get; set; }
    public long SourceOwnerId { get; set; }
    public int RecipientCorporationId { get; set; }
    public int IssuerCorporationId { get; set; }
    public long Points { get; set; }
    public long SourceBefore { get; set; }
    public long SourceAfter { get; set; }
    public long RecipientBefore { get; set; }
    public long RecipientAfter { get; set; }
    public long Time { get; set; }

    public CorporationLoyaltyTransfer Snapshot() =>
        new(
            Id,
            SourceOwnerId,
            RecipientCorporationId,
            IssuerCorporationId,
            Points,
            SourceBefore,
            SourceAfter,
            RecipientBefore,
            RecipientAfter,
            Time
        );
}

public sealed class SavedCorporationMemberAccess
{
    public long PilotId { get; set; }
    public int CorporationId { get; set; }
    public string DataJson { get; set; } = "";
}

public sealed class SavedCorporationOffice
{
    public long Id { get; set; }
    public int CorporationId { get; set; }
    public int StationId { get; set; }
    public int SystemId { get; set; }
    public long RentedAt { get; set; }
    public long FeeCents { get; set; }

    public CorporationOffice Snapshot() => new(Id, CorporationId, StationId, SystemId, RentedAt, FeeCents);
}

public sealed class SavedCorporationOfficeSettings
{
    public int Id { get; set; }
    public long RentalFeeCents { get; set; }
    public int OfficeSlots { get; set; }
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

public sealed class SavedCorporationShareHolding
{
    public int CorporationId { get; set; }
    public long ShareholderId { get; set; }
    public long Shares { get; set; }
}

public sealed class SavedCorporationShareIssue
{
    public int CorporationId { get; set; }
    public long Shares { get; set; }
}

public sealed class SavedCorporationShareTransfer
{
    public long Id { get; set; }
    public int CorporationId { get; set; }
    public long ActorId { get; set; }
    public long FromOwnerId { get; set; }
    public long ToOwnerId { get; set; }
    public long Shares { get; set; }
    public long Time { get; set; }
}

public sealed class SavedCorporationTitle
{
    public int CorporationId { get; set; }
    public long TitleId { get; set; }
    public string DataJson { get; set; } = "";

    public CorporationTitle Snapshot() => JsonSerializer.Deserialize<CorporationTitle>(DataJson)!;
}

public sealed class SavedCorporationWallet
{
    public int CorporationId { get; set; }
    public int Key { get; set; }
    public long BalanceCents { get; set; }
}

public sealed class SavedCorporationWalletSelection
{
    public long PilotId { get; set; }
    public int CorporationId { get; set; }
    public int Key { get; set; } = CorporationWalletRules.MasterKey;
}

public sealed class SavedWalletLedger
{
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public int Key { get; set; }
    public long Time { get; set; }
    public long CounterpartyId { get; set; }
    public long AmountCents { get; set; }
    public long BalanceCents { get; set; }
    public WalletEntryType EntryType { get; set; }
    public long ReferenceId { get; set; }
    public string Reason { get; set; } = "";

    public CorporationWalletEntry Snapshot() =>
        new(Id, OwnerId, Key, Time, CounterpartyId, AmountCents, BalanceCents, EntryType, ReferenceId, Reason);
}
