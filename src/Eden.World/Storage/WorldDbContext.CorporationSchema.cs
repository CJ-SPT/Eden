using Eden.Data.Characters;
using Eden.Data.Combat;
using Eden.Data.Corporations;
using Eden.Data.Inventory;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Corporations;
using Eden.World.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage;

public sealed partial class WorldDbContext
{
    private static void ConfigureCorporations(ModelBuilder model)
    {
        model
            .Entity<SavedCorporationOffice>()
            .ToTable("WorldCorporationOffice", t => t.HasCheckConstraint("CK_CorporationOffice_Fee", "FeeCents>=0"))
            .HasKey(o => o.Id);
        model
            .Entity<SavedCorporationOffice>()
            .HasOne<SavedCorporation>()
            .WithMany()
            .HasForeignKey(o => o.CorporationId)
            .OnDelete(DeleteBehavior.Restrict);
        model.Entity<SavedCorporationOffice>().HasIndex(o => new { o.CorporationId, o.StationId }).IsUnique();
        model
            .Entity<SavedCorporationOfficeSettings>()
            .ToTable(
                "WorldCorporationOfficeSettings",
                t =>
                {
                    t.HasCheckConstraint("CK_CorporationOfficeSettings_Id", "Id=1");
                    t.HasCheckConstraint("CK_CorporationOfficeSettings_Fee", "RentalFeeCents>=0");
                    t.HasCheckConstraint("CK_CorporationOfficeSettings_Slots", "OfficeSlots>0");
                }
            )
            .HasKey(s => s.Id);

        model.Entity<SavedCorporationShareIssue>().ToTable("WorldCorporationShareIssue").HasKey(i => i.CorporationId);
        model.Entity<SavedCorporationShareIssue>().HasOne<SavedCorporation>().WithMany().HasForeignKey(i => i.CorporationId);
        model
            .Entity<SavedCorporationShareHolding>()
            .ToTable("WorldCorporationShareHolding")
            .HasKey(h => new { h.CorporationId, h.ShareholderId });
        model.Entity<SavedCorporationShareHolding>().HasOne<SavedCorporationShareIssue>().WithMany().HasForeignKey(h => h.CorporationId);
        model
            .Entity<SavedCorporationShareHolding>()
            .HasOne<InventoryOwner>()
            .WithMany()
            .HasForeignKey(h => h.ShareholderId)
            .OnDelete(DeleteBehavior.Restrict);
        model.Entity<SavedCorporationShareTransfer>().ToTable("WorldCorporationShareTransfer").HasKey(t => t.Id);
        model.Entity<SavedCorporationShareTransfer>().HasOne<SavedCorporationShareIssue>().WithMany().HasForeignKey(t => t.CorporationId);
        model.Entity<SavedCorporationBillPaymentSettings>().ToTable("WorldCorporationBillPaymentSettings").HasKey(s => s.CorporationId);
        model.Entity<SavedCorporationBillPaymentSettings>().HasOne<SavedCorporation>().WithMany().HasForeignKey(s => s.CorporationId);
        model
            .Entity<SavedCorporationLoyaltyBalance>()
            .ToTable("WorldCorporationLoyaltyWallet")
            .HasKey(w => new { w.CorporationId, w.IssuerCorporationId });
        model.Entity<SavedCorporationLoyaltyBalance>().HasOne<SavedCorporation>().WithMany().HasForeignKey(w => w.CorporationId);
        model.Entity<SavedCorporationLoyaltyTransfer>().ToTable("WorldCorporationLoyaltyTransfer").HasKey(t => t.Id);
        model.Entity<SavedCorporationWallet>().ToTable("WorldCorporationWallet").HasKey(w => new { w.CorporationId, w.Key });
        model.Entity<SavedCorporationWallet>().HasOne<SavedCorporation>().WithMany().HasForeignKey(w => w.CorporationId);
        model.Entity<SavedCorporationWalletSelection>().ToTable("WorldCorporationWalletSelection").HasKey(w => w.PilotId);
        model.Entity<SavedCorporationWalletSelection>().HasOne<Pilot>().WithMany().HasForeignKey(w => w.PilotId);
        model.Entity<SavedWalletLedger>().ToTable("WorldWalletLedger").HasKey(e => e.Id);
        model
            .Entity<SavedWalletLedger>()
            .HasIndex(e => new
            {
                e.OwnerId,
                e.Key,
                e.Id,
            });
        model.Entity<NpcBounty>().Property(b => b.TaxCents).HasDefaultValue(0);
        model.Entity<SavedCorporationApplication>().ToTable("WorldCorporationApplication").HasKey(a => a.Id);
        model.Entity<SavedCorporationMemberAccess>().ToTable("WorldCorporationMemberAccess").HasKey(m => m.PilotId);
        model.Entity<SavedCorporationMemberAccess>().HasOne<Pilot>().WithMany().HasForeignKey(m => m.PilotId);
        model.Entity<SavedCorporationTitle>().ToTable("WorldCorporationTitle").HasKey(t => new { t.CorporationId, t.TitleId });
        model.Entity<SavedCorporationTitle>().HasOne<SavedCorporation>().WithMany().HasForeignKey(t => t.CorporationId);
        model.Entity<SavedCorporationApplication>().HasOne<Pilot>().WithMany().HasForeignKey(a => a.PilotId);
        model.Entity<SavedCorporationApplication>().HasOne<SavedCorporation>().WithMany().HasForeignKey(a => a.CorporationId);
        model.Entity<SavedCorporationApplication>().HasIndex(a => new { a.PilotId, a.CorporationId });
        model.Entity<SavedCorporationChange>().ToTable("WorldCorporationChange").HasKey(c => c.Id);
        model.Entity<SavedCorporationChange>().HasOne<Pilot>().WithMany().HasForeignKey(c => c.PilotId);
        model.Entity<SavedCorporationChange>().HasIndex(c => new { c.PilotId, c.Id });
        model.Entity<SavedCorporation>().ToTable("WorldCorporation").HasKey(c => c.Id);
        model.Entity<SavedCorporation>().Property(c => c.Id).ValueGeneratedNever();
        model.Entity<SavedCorporation>().Property(c => c.Name).UseCollation("NOCASE");
        model.Entity<SavedCorporation>().Property(c => c.Ticker).UseCollation("NOCASE");
        model.Entity<SavedCorporation>().HasIndex(c => c.Name).IsUnique();
        model.Entity<SavedCorporation>().HasIndex(c => c.Ticker).IsUnique();
        model.Entity<SavedCorporation>().HasIndex(c => c.CeoId).IsUnique();
        model.Entity<SavedCorporation>().HasOne<Pilot>().WithMany().HasForeignKey(c => c.CeoId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<SavedCorporation>().HasOne<Pilot>().WithMany().HasForeignKey(c => c.CreatorId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<SavedCorporationEmployment>().ToTable("WorldCorporationEmployment").HasKey(e => e.Id);
        model.Entity<SavedCorporationEmployment>().HasIndex(e => new { e.PilotId, e.Id });
        model.Entity<SavedCorporationEmployment>().HasOne<Pilot>().WithMany().HasForeignKey(e => e.PilotId);
        model.Entity<SavedCorporationPayment>().ToTable("WorldCorporationPayment").HasKey(p => p.Id);
        model.Entity<SavedCorporationPayment>().HasOne<Pilot>().WithMany().HasForeignKey(p => p.PilotId);
    }
}
