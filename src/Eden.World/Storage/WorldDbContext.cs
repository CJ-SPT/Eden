using Eden.Data.World.Bookmarks;
using Eden.Data.World.Calendar;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Contracts;
using Eden.Data.World.Docking;
using Eden.Data.World.Fittings;
using Eden.Data.World.Industry;
using Eden.Data.World.Insurance;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Mail;
using Eden.Data.World.Market;
using Eden.Data.World.Mining;
using Eden.Data.World.Propulsion;
using Eden.Data.World.Redemption;
using Eden.Data.World.Repair;
using Eden.Data.World.Skills;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.World.Bookmarks;
using Eden.World.Calendar;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Configuration;
using Eden.World.Contracts;
using Eden.World.Docking;
using Eden.World.Drones;
using Eden.World.Economy;
using Eden.World.Fittings;
using Eden.World.Industry;
using Eden.World.Insurance;
using Eden.World.Inventory;
using Eden.World.Loot;
using Eden.World.Mail;
using Eden.World.Market;
using Eden.World.Mining;
using Eden.World.Propulsion;
using Eden.World.Redemption;
using Eden.World.Repair;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Thermal;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage;

public sealed partial class WorldDbContext(string path) : DbContext
{
    private readonly WorldConfigurationSlot? _configuration = string.IsNullOrEmpty(path) ? null : WorldConfigurationSlot.For(path);
    internal Eden.Gameplay.Configuration.WorldSettings Settings => _configuration?.Settings ?? WorldConfigurationSlot.Defaults;
    internal string WorldPath => path;
    public DbSet<Pilot> Pilots => Set<Pilot>();
    public DbSet<Ship> Ships => Set<Ship>();
    public DbSet<FlightPosition> Positions => Set<FlightPosition>();

    internal bool ReadsResidentNpcTimer { get; init; }

    private readonly SqliteConnection? _snapshot;

    internal WorldDbContext(SqliteConnection snapshot)
        : this(string.Empty)
    {
        _snapshot = snapshot;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        if (_snapshot is not null)
        {
            // Detached checkpoint projection uses the same model and SQLite rules,
            // without consulting or invalidating the advancing resident world.
            options.TracePerformance().UseSqlite(_snapshot, contextOwnsConnection: false);
            return;
        }
        options
            .AddInterceptors(new FitTransactions(this), new CombatPersistenceBridge(path), new CombatTransactionBridge(path))
            .TracePerformance()
            .UseSqlite(WorldConnections.Create(path), contextOwnsConnection: true);
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        Eden.World.Structures.StructureSchema.Configure(model);
        Eden.World.Agents.MissionSchema.Configure(model);
        Eden.World.Capsuleers.TrafficSchema.Configure(model);
        Eden.World.Information.InformationSchema.Configure(model);
        Eden.World.Exploration.CombatAnomalySchema.Configure(model);
        Eden.World.Exploration.ProbeScanningSchema.Configure(model);
        Eden.World.Exploration.HackingSchema.Configure(model);
        ModuleConditionSchema.Configure(model);
        SpatialNavigationSchema.Configure(model);
        Eden.World.Capsuleers.CapsuleerCommunityStore.Configure(model);
        Eden.World.Capsuleers.CapsuleerPopulationStore.Configure(model);
        ShipHeatSchema.Configure(model);
        NaniteRepairSchema.Configure(model);
        WorldConfigurationSchema.Configure(model);
        model.Entity<SavedCharacterDeletion>().ToTable("WorldCharacterDeletion").HasKey(d => d.PilotId);
        model.Entity<SavedCharacterDeletion>().HasOne<Pilot>().WithMany().HasForeignKey(d => d.PilotId);
        model.Entity<WorldPlayerEwar>().ToTable("WorldPlayerEwar").HasKey(e => e.ModuleId);
        model.Entity<WorldPlayerEwar>().HasOne<OwnedItem>().WithMany().HasForeignKey(e => e.ModuleId);
        model.Entity<WorldPlayerEwar>().HasOne<Pilot>().WithMany().HasForeignKey(e => e.PilotId);
        model.Entity<WorldPlayerEwar>().HasOne<Ship>().WithMany().HasForeignKey(e => e.SourceShipId);
        model.Entity<WorldPlayerEwar>().HasIndex(e => new { e.TargetId, e.TargetKind });
        model.Entity<WorldRemoteRepair>().ToTable("WorldRemoteRepair").HasKey(r => r.ModuleId);
        model.Entity<WorldRemoteRepair>().HasOne<OwnedItem>().WithMany().HasForeignKey(r => r.ModuleId);
        model.Entity<WorldRemoteRepair>().HasOne<Pilot>().WithMany().HasForeignKey(r => r.PilotId);
        model.Entity<WorldRemoteRepair>().HasOne<Ship>().WithMany().HasForeignKey(r => r.SourceShipId);
        model.Entity<WorldRemoteRepair>().HasOne<Ship>().WithMany().HasForeignKey(r => r.TargetId);
        model.Entity<WorldRemoteRepair>().HasIndex(r => r.TargetId);
        model.Entity<WorldRemoteAssistance>().ToTable("WorldRemoteAssistance").HasKey(r => r.ModuleId);
        model.Entity<WorldRemoteAssistance>().HasOne<OwnedItem>().WithMany().HasForeignKey(r => r.ModuleId);
        model.Entity<WorldRemoteAssistance>().HasOne<Pilot>().WithMany().HasForeignKey(r => r.PilotId);
        model.Entity<WorldRemoteAssistance>().HasOne<Ship>().WithMany().HasForeignKey(r => r.SourceShipId);
        model.Entity<WorldRemoteAssistance>().HasOne<Ship>().WithMany().HasForeignKey(r => r.TargetId);
        model.Entity<WorldRemoteAssistance>().HasIndex(r => r.TargetId);
        EconomicSchema.Configure(model);
        CrimewatchSchema.Configure(model);
        ConcordSchema.Configure(model);
        DroneSchema.Configure(model);
        HistorySchema.Configure(model);
        ConfigureInteractionHistory(model);
        ConfigureCorporations(model);
        model.Entity<SavedFittingRecord>().ToTable("WorldSavedFitting").HasKey(f => f.Id);
        model.Entity<SavedFittingRecord>().HasOne<Pilot>().WithMany().HasForeignKey(f => f.OwnerId);
        model.Entity<SavedFittingRecord>().HasIndex(f => f.OwnerId);
        model.Entity<InventoryOwner>().ToTable("WorldInventoryOwner").HasKey(o => o.Id);
        model.Entity<InventoryOwner>().Property(o => o.Id).ValueGeneratedNever();
        model.Entity<SavedWeaponLink>().ToTable("WorldWeaponLink").HasKey(l => l.SlaveId);
        model.Entity<SavedWeaponLink>().HasOne<OwnedItem>().WithMany().HasForeignKey(l => l.MasterId);
        model.Entity<SavedWeaponLink>().HasOne<OwnedItem>().WithMany().HasForeignKey(l => l.SlaveId);
        model.Entity<SavedWeaponLink>().HasOne<Ship>().WithMany().HasForeignKey(l => l.ShipId);
        model.Entity<WorldNpcEncounter>().ToTable("WorldNpcEncounter").HasKey(e => e.Id);
        model.Entity<WorldNpcEncounter>().Property(e => e.Id).ValueGeneratedNever();
        model.Entity<WorldNpcEncounter>().HasIndex(e => e.SystemId);
        model.Entity<WorldNpcMiningOperation>().ToTable("WorldNpcMiningOperation").HasKey(o => o.EncounterId);
        model.Entity<WorldNpcMiningOperation>().HasOne<WorldNpcEncounter>().WithMany().HasForeignKey(o => o.EncounterId);
        model.Entity<WorldNpcMiningOperation>().HasIndex(o => o.SystemId);
        model.Entity<WorldNpcMiningMember>().ToTable("WorldNpcMiningMember").HasKey(m => m.NpcId);
        model.Entity<WorldNpcMiningMember>().HasOne<WorldNpc>().WithMany().HasForeignKey(m => m.NpcId);
        model.Entity<WorldNpcMiningMember>().HasOne<WorldNpcMiningOperation>().WithMany().HasForeignKey(m => m.EncounterId);
        model.Entity<WorldNpcMiningMember>().HasIndex(m => m.EncounterId);
        model
            .Entity<WorldNpcMiningCargo>()
            .ToTable("WorldNpcMiningCargo", t => t.HasCheckConstraint("CK_NpcMiningCargo_Quantity", "Quantity>=0"))
            .HasKey(c => new { c.NpcId, c.TypeId });
        model.Entity<WorldNpcMiningCargo>().HasOne<WorldNpc>().WithMany().HasForeignKey(c => c.NpcId);
        model.Entity<SavedPilotNote>().ToTable("WorldPilotNote").HasKey(note => new { note.PilotId, note.TargetId });
        model.Entity<SavedPilotNote>().HasOne<Pilot>().WithMany().HasForeignKey(note => note.PilotId);
        model.Entity<SavedLoyaltyBalance>().ToTable("WorldLoyaltyWallet").HasOne<Pilot>().WithMany().HasForeignKey(b => b.PilotId);
        model.Entity<SavedRedeemedCurrency>().ToTable("WorldRedeemedCurrency").HasKey(c => c.TokenId);
        model.Entity<SavedRedeemedCurrency>().HasOne<SavedRedeemToken>().WithMany().HasForeignKey(c => c.TokenId);
        model.Entity<SavedRedeemedCurrency>().HasOne<Pilot>().WithMany().HasForeignKey(c => c.PilotId);
        model.Entity<SavedRedeemedCurrency>().HasIndex(c => new { c.PilotId, c.Time });
        model.Entity<SavedFreeSkillPoints>().ToTable("WorldFreeSkillPoints").HasKey(s => s.PilotId);
        model.Entity<SavedFreeSkillPoints>().HasOne<Pilot>().WithMany().HasForeignKey(s => s.PilotId);
        model.Entity<SavedRedeemToken>().ToTable("WorldRedeemToken").HasKey(t => t.Id);
        model.Entity<SavedRedeemToken>().HasOne<Account>().WithMany().HasForeignKey(t => t.AccountId);
        model.Entity<SavedRedeemToken>().HasIndex(t => new { t.AccountId, t.Reference }).IsUnique();
        model.Entity<SavedSecurityStatus>().ToTable("WorldPilotSecurity").HasKey(s => s.PilotId);
        model.Entity<SavedSecurityStatus>().HasOne<Pilot>().WithMany().HasForeignKey(s => s.PilotId);
        model.Entity<PendingSecurityReward>().ToTable("WorldSecurityPending").HasKey(s => s.PilotId);
        model.Entity<PendingSecurityReward>().HasOne<Pilot>().WithMany().HasForeignKey(s => s.PilotId);
        model.Entity<SavedSecurityTransaction>().ToTable("WorldSecurityTransaction").HasKey(s => s.Id);
        model.Entity<SavedSecurityTransaction>().HasOne<Pilot>().WithMany().HasForeignKey(s => s.PilotId);
        model.Entity<SavedSecurityTransaction>().HasIndex(s => new { s.PilotId, s.Id });
        model.Entity<SavedShipDirt>().ToTable("WorldShipDirt").HasKey(s => s.ShipId);
        model.Entity<SavedShipDirt>().HasOne<Ship>().WithMany().HasForeignKey(s => s.ShipId);
        model.Entity<SavedContract>().ToTable("WorldContract").HasKey(c => c.Id);
        model.Entity<SavedContract>().Property(c => c.Type).HasDefaultValue(Eden.Gameplay.Contracts.ContractType.ItemExchange);
        model.Entity<SavedContract>().HasIndex(c => c.PackageId).IsUnique();
        model.Entity<SavedContract>().HasOne<Pilot>().WithMany().HasForeignKey(c => c.IssuerId);
        model.Entity<SavedContract>().HasOne<Pilot>().WithMany().HasForeignKey(c => c.AssigneeId).OnDelete(DeleteBehavior.NoAction);
        model.Entity<SavedContract>().HasOne<Pilot>().WithMany().HasForeignKey(c => c.AcceptorId).OnDelete(DeleteBehavior.NoAction);
        model.Entity<SavedContract>().HasIndex(c => new { c.IssuerId, c.Status });
        model.Entity<SavedContract>().HasIndex(c => new { c.AssigneeId, c.Status });
        model.Entity<SavedContractBid>().ToTable("WorldContractBid").HasKey(b => b.Id);
        model.Entity<SavedContractBid>().HasOne<SavedContract>().WithMany().HasForeignKey(b => b.ContractId);
        model.Entity<SavedContractBid>().HasOne<Pilot>().WithMany().HasForeignKey(b => b.BidderId);
        model.Entity<SavedContractBid>().HasIndex(b => new { b.ContractId, b.Id });
        model.Entity<SavedContractBid>().HasIndex(b => new { b.BidderId, b.ContractId });
        model.Entity<SavedContractItem>().ToTable("WorldContractItem").HasKey(i => i.Id);
        model.Entity<SavedContractItem>().HasOne<SavedContract>().WithMany().HasForeignKey(i => i.ContractId);
        model.Entity<SavedContractEscrow>().ToTable("WorldContractEscrow").HasKey(e => e.ItemId);
        model.Entity<SavedContractEscrow>().Property(e => e.ItemId).ValueGeneratedNever();
        model.Entity<SavedContractEscrow>().HasOne<SavedContract>().WithMany().HasForeignKey(e => e.ContractId);
        model.Entity<SavedContractPayment>().ToTable("WorldContractPayment").HasKey(p => p.Id);
        model.Entity<SavedContractPayment>().HasOne<Pilot>().WithMany().HasForeignKey(p => p.PilotId);
        model.Entity<SavedContractPayment>().HasOne<SavedContract>().WithMany().HasForeignKey(p => p.ContractId);
        model.Entity<SavedContractPayment>().HasIndex(p => new { p.PilotId, p.Id });
        model.Entity<SavedContractChange>().ToTable("WorldContractChange").HasKey(c => c.Id);
        model.Entity<SavedContractChange>().HasOne<Pilot>().WithMany().HasForeignKey(c => c.PilotId);
        model.Entity<SavedContractChange>().HasOne<SavedContract>().WithMany().HasForeignKey(c => c.ContractId);
        model.Entity<SavedContractChange>().HasIndex(c => new { c.PilotId, c.Id });
        model.Entity<OwnedItem>().HasQueryFilter(i => !Set<SavedContractEscrow>().Any(e => e.ItemId == i.Id));
        model.Entity<Ship>().HasQueryFilter(s => !Set<SavedContractEscrow>().Any(e => e.ItemId == s.Id));
        model.Entity<SavedInboxNotification>().ToTable("WorldInboxNotification").HasKey(n => n.Id);
        model.Entity<SavedInboxNotification>().HasOne<Pilot>().WithMany().HasForeignKey(n => n.ReceiverId);
        model.Entity<SavedInboxNotification>().HasIndex(n => new { n.ReceiverId, n.Id });
        model.Entity<SavedCalendarEvent>().ToTable("WorldCalendarEvent").HasKey(e => e.Id);
        model.Entity<SavedCalendarEvent>().HasOne<Pilot>().WithMany().HasForeignKey(e => e.OwnerId);
        model.Entity<SavedCalendarEvent>().HasIndex(e => e.Time);
        model.Entity<SavedCalendarParticipant>().ToTable("WorldCalendarParticipant").HasKey(p => new { p.EventId, p.PilotId });
        model.Entity<SavedCalendarParticipant>().HasOne<SavedCalendarEvent>().WithMany().HasForeignKey(p => p.EventId);
        model.Entity<SavedCalendarParticipant>().HasOne<Pilot>().WithMany().HasForeignKey(p => p.PilotId);
        model.Entity<SavedCalendarChange>().ToTable("WorldCalendarChange").HasKey(c => c.Id);
        model.Entity<SavedCalendarChange>().HasOne<SavedCalendarEvent>().WithMany().HasForeignKey(c => c.EventId);
        model.Entity<SavedCalendarChange>().HasOne<Pilot>().WithMany().HasForeignKey(c => c.PilotId);
        model.Entity<SavedCalendarChange>().HasIndex(c => new { c.PilotId, c.Id });
        model.Entity<SavedJumpClone>().ToTable("WorldJumpClone").HasKey(clone => clone.Id);
        model.Entity<SavedJumpClone>().HasOne<Pilot>().WithMany().HasForeignKey(clone => clone.PilotId);
        model.Entity<SavedJumpClone>().HasIndex(clone => clone.PilotId).IsUnique().HasFilter("Active = 1");
        model
            .Entity<SavedJumpClone>()
            .ToTable(table =>
                table.HasCheckConstraint(
                    "CK_JumpClone_Location",
                    "(Active = 1 AND StationId IS NULL) OR (Active = 0 AND StationId IS NOT NULL)"
                )
            );
        model.Entity<SavedMailingList>().ToTable("WorldMailingList").HasKey(list => list.Id);
        model.Entity<SavedMailingList>().HasIndex(list => list.Key).IsUnique().HasFilter("Deleted = 0");
        model.Entity<SavedMailingListMember>().ToTable("WorldMailingListMember");
        model.Entity<SavedMailingListMember>().HasOne<SavedMailingList>().WithMany().HasForeignKey(member => member.ListId);
        model.Entity<SavedMailingListMember>().HasOne<Pilot>().WithMany().HasForeignKey(member => member.PilotId);
        model.Entity<SavedMailingListAccess>().ToTable("WorldMailingListAccess");
        model.Entity<SavedMailingListAccess>().HasOne<SavedMailingList>().WithMany().HasForeignKey(access => access.ListId);
        model.Entity<SavedMailingListChange>().ToTable("WorldMailingListChange").HasKey(change => change.Id);
        model.Entity<SavedMailingListChange>().HasOne<Pilot>().WithMany().HasForeignKey(change => change.PilotId);
        model.Entity<SavedMailingListChange>().HasIndex(change => new { change.PilotId, change.Id });
        model.Entity<SavedMail>().ToTable("WorldMail").HasKey(mail => mail.Id);
        model.Entity<SavedMail>().HasIndex(mail => new { mail.SenderId, mail.SentAt });
        model.Entity<SavedMail>().HasIndex(mail => mail.ListId);
        model.Entity<SavedMailEntry>().ToTable("WorldMailEntry").HasOne<Pilot>().WithMany().HasForeignKey(entry => entry.PilotId);
        model.Entity<SavedMailEntry>().HasOne<SavedMail>().WithMany().HasForeignKey(entry => entry.MessageId);
        model.Entity<SavedMailLabel>().ToTable("WorldMailLabel").HasOne<Pilot>().WithMany().HasForeignKey(label => label.PilotId);
        model.Entity<SavedMailChange>().ToTable("WorldMailChange").HasKey(change => change.Id);
        model.Entity<SavedMailChange>().HasOne<Pilot>().WithMany().HasForeignKey(change => change.PilotId);
        model.Entity<SavedMailChange>().HasIndex(change => new { change.PilotId, change.Id });
        model.Entity<SavedSkillExtraction>().ToTable("WorldSkillExtraction").HasKey(extraction => extraction.Id);
        model.Entity<SavedSkillExtraction>().HasOne<Pilot>().WithMany().HasForeignKey(extraction => extraction.PilotId);
        model.Entity<SavedSkillExtraction>().HasIndex(extraction => new { extraction.PilotId, extraction.Id });
        model.Entity<SavedSkillInjectorState>().ToTable("WorldSkillInjectorState").HasKey(state => state.PilotId);
        model.Entity<SavedSkillInjectorState>().HasOne<Pilot>().WithMany().HasForeignKey(state => state.PilotId);
        model.Entity<SavedSkillInjection>().ToTable("WorldSkillInjection").HasKey(injection => injection.Id);
        model.Entity<SavedSkillInjection>().HasOne<Pilot>().WithMany().HasForeignKey(injection => injection.PilotId);
        model.Entity<SavedSkillInjection>().HasIndex(injection => new { injection.PilotId, injection.Time });
        model.Entity<SavedBooster>().ToTable("WorldBooster").HasKey(booster => booster.ItemId);
        model.Entity<SavedBooster>().HasOne<Pilot>().WithMany().HasForeignKey(booster => booster.PilotId);
        model.Entity<SavedBooster>().HasIndex(booster => new { booster.PilotId, booster.Slot }).IsUnique();
        model.Entity<SavedBoosterState>().ToTable("WorldBoosterState").HasKey(state => state.PilotId);
        model.Entity<SavedBoosterState>().HasOne<Pilot>().WithMany().HasForeignKey(state => state.PilotId);
        model.Entity<SavedImplantState>().ToTable("WorldImplantState").HasKey(state => state.PilotId);
        model.Entity<SavedImplantState>().HasOne<Pilot>().WithMany().HasForeignKey(state => state.PilotId);
        model.Entity<SavedPilotImplant>().ToTable("WorldPilotImplant").HasKey(implant => implant.ItemId);
        model.Entity<SavedPilotImplant>().HasOne<SavedJumpClone>().WithMany().HasForeignKey(implant => implant.CloneId);
        model.Entity<SavedPilotImplant>().HasIndex(implant => new { implant.CloneId, implant.Slot }).IsUnique();
        model.Entity<SavedJumpCloneTimer>().ToTable("WorldJumpCloneTimer").HasKey(timer => timer.PilotId);
        model.Entity<SavedJumpCloneTimer>().HasOne<Pilot>().WithMany().HasForeignKey(timer => timer.PilotId);
        model.Entity<SavedJumpClonePayment>().ToTable("WorldJumpClonePayment").HasKey(payment => payment.Id);
        model.Entity<SavedJumpClonePayment>().HasOne<Pilot>().WithMany().HasForeignKey(payment => payment.PilotId);
        model.Entity<SavedAgentContact>().ToTable("WorldAgentContact").HasKey(contact => new { contact.PilotId, contact.AgentId });
        model.Entity<SavedAgentContact>().HasOne<Pilot>().WithMany().HasForeignKey(contact => contact.PilotId);
        model.Entity<SavedHomeStation>().ToTable("WorldHomeStation").HasKey(home => home.PilotId);
        model.Entity<SavedHomeStation>().HasOne<Pilot>().WithMany().HasForeignKey(home => home.PilotId);
        model.Entity<SavedHomeStationPayment>().ToTable("WorldHomeStationPayment").HasKey(payment => payment.Id);
        model.Entity<SavedHomeStationPayment>().HasOne<Pilot>().WithMany().HasForeignKey(payment => payment.PilotId);
        model.Entity<SavedHomeCloneTransfer>().ToTable("WorldHomeCloneTransfer").HasKey(transfer => transfer.Id);
        model.Entity<SavedHomeCloneTransfer>().HasOne<Pilot>().WithMany().HasForeignKey(transfer => transfer.PilotId);
        model.Entity<SavedHomeCloneTransfer>().HasIndex(transfer => transfer.OldShipId).IsUnique();
        model.Entity<SavedTaskCounter>().ToTable("WorldTaskCounter").HasKey(counter => new { counter.PilotId, counter.Name });
        model.Entity<SavedTaskCounter>().HasOne<Pilot>().WithMany().HasForeignKey(counter => counter.PilotId);
        model.Entity<SavedCompletedTask>().ToTable("WorldCompletedTask").HasKey(task => new { task.PilotId, task.Id });
        model.Entity<SavedCompletedTask>().HasOne<Pilot>().WithMany().HasForeignKey(task => task.PilotId);
        model.Entity<SavedInsurancePolicy>().ToTable("WorldInsurancePolicy").HasKey(p => p.Id);
        model.Entity<SavedInsurancePolicy>().HasOne<Pilot>().WithMany().HasForeignKey(p => p.PilotId);
        // Policies and ledger entries retain ship identities after hull deletion.
        model.Entity<SavedInsurancePolicy>().HasIndex(p => p.ShipId).IsUnique().HasFilter("Status = 0");
        model.Entity<SavedInsuranceLedger>().ToTable("WorldInsuranceLedger").HasKey(entry => entry.Id);
        model.Entity<SavedInsuranceLedger>().HasOne<Pilot>().WithMany().HasForeignKey(entry => entry.PilotId);
        model
            .Entity<SavedInsuranceLedger>()
            .HasOne<SavedInsurancePolicy>()
            .WithMany()
            .HasForeignKey(entry => entry.PolicyId)
            .OnDelete(DeleteBehavior.Restrict);
        model.Entity<SavedInsuranceLedger>().HasIndex(entry => entry.ShipId).IsUnique().HasFilter("IsPayout = 1");
        model.Entity<SavedBookmarkFolder>().ToTable("WorldBookmarkFolder").HasKey(f => f.Id);
        model.Entity<SavedBookmarkFolder>().HasOne<Pilot>().WithMany().HasForeignKey(f => f.PilotId);
        model.Entity<SavedBookmarkSubfolder>().ToTable("WorldBookmarkSubfolder").HasKey(s => s.Id);
        model.Entity<SavedBookmarkSubfolder>().HasAlternateKey(s => new { s.Id, s.FolderId });
        model.Entity<SavedBookmarkSubfolder>().HasOne<SavedBookmarkFolder>().WithMany().HasForeignKey(s => s.FolderId);
        model.Entity<SavedBookmark>().ToTable("WorldBookmark").HasKey(b => b.Id);
        model.Entity<SavedBookmark>().HasOne<SavedBookmarkFolder>().WithMany().HasForeignKey(b => b.FolderId);
        model.Entity<SavedBookmark>().HasOne<Pilot>().WithMany().HasForeignKey(b => b.CreatorId);
        model
            .Entity<SavedBookmark>()
            .HasOne<SavedBookmarkSubfolder>()
            .WithMany()
            .HasForeignKey(b => new { b.SubfolderId, b.FolderId })
            .HasPrincipalKey(s => new { s.Id, s.FolderId })
            .OnDelete(DeleteBehavior.Restrict);
        model.Entity<SavedContact>().ToTable("WorldContact").HasKey(c => new { c.PilotId, c.ContactId });
        model.Entity<SavedContact>().HasOne<Pilot>().WithMany().HasForeignKey(c => c.PilotId);
        model.Entity<SavedContact>().HasOne<Pilot>().WithMany().HasForeignKey(c => c.ContactId);
        model.Entity<SavedBlockedContact>().ToTable("WorldBlockedContact").HasKey(c => new { c.PilotId, c.ContactId });
        model.Entity<SavedBlockedContact>().HasOne<Pilot>().WithMany().HasForeignKey(c => c.PilotId);
        model.Entity<SavedBlockedContact>().HasOne<Pilot>().WithMany().HasForeignKey(c => c.ContactId);
        model.Entity<SavedContactLabel>().ToTable("WorldContactLabel").HasKey(l => new { l.PilotId, l.Id });
        model.Entity<SavedContactLabel>().HasOne<Pilot>().WithMany().HasForeignKey(l => l.PilotId);
        model.Entity<SavedContactLabel>().HasIndex(l => new { l.PilotId, l.Name }).IsUnique();
        model
            .Entity<SavedCharacterSetting>()
            .ToTable(
                "WorldCharacterSetting",
                table => table.HasCheckConstraint("CK_CharacterSetting_Value", "(Text IS NULL) <> (Binary IS NULL)")
            )
            .HasKey(s => new { s.PilotId, s.Key });
        model.Entity<SavedCharacterSetting>().HasOne<Pilot>().WithMany().HasForeignKey(s => s.PilotId);
        model.Entity<SavedCharacterSheet>().ToTable("WorldCharacterSheet").HasKey(s => s.PilotId);
        model.Entity<SavedCharacterSheet>().HasOne<Pilot>().WithMany().HasForeignKey(s => s.PilotId);
        model.Entity<WorldTackle>().ToTable("WorldTackle").HasKey(t => t.ModuleId);
        model.Entity<WorldTackle>().HasOne<OwnedItem>().WithMany().HasForeignKey(t => t.ModuleId);
        model.Entity<WorldTackle>().HasOne<Pilot>().WithMany().HasForeignKey(t => t.PilotId);
        model.Entity<WorldTackle>().HasOne<Ship>().WithMany().HasForeignKey(t => t.SourceShipId);
        // Targets may be ships or NPCs. Their availability is checked by the owning operation.
        model.Entity<WorldTackle>().HasIndex(t => new { t.TargetId, t.TargetKind });
        model.Entity<WorldMissile>().ToTable("WorldMissile").HasKey(m => m.Id);
        model.Entity<WorldMissile>().Property(m => m.Id).ValueGeneratedOnAdd();
        model.Entity<WorldMissile>().HasOne<Pilot>().WithMany().HasForeignKey(m => m.PilotId);
        // SourceKind and TargetKind discriminate player and NPC identities.
        // Operations validate availability; historical identities outlive hulls.
        // A launched projectile retains its module identity after destruction
        // or unfitting; ModuleId deliberately has no inventory foreign key.
        model
            .Entity<WorldMissile>()
            .HasIndex(m => new
            {
                m.SystemId,
                m.Status,
                m.UpdatedAt,
            });
        model.Entity<WorldMissile>().HasIndex(m => new { m.PilotId, m.Reported });
        model
            .Entity<IndustryJob>()
            .ToTable(
                "WorldIndustryJob",
                t =>
                    t.HasCheckConstraint(
                        "CK_IndustryJob_Values",
                        "Runs > 0 AND OutputQuantity > 0 AND DurationSeconds > 0 AND EndTime > StartTime"
                    )
            );
        model.Entity<SavedContractItem>().Property(i => i.BlueprintRuns).HasDefaultValue(-1);
        model.Entity<SavedContractItem>().Property(i => i.MaterialEfficiency).HasDefaultValue(0);
        model.Entity<SavedContractItem>().Property(i => i.TimeEfficiency).HasDefaultValue(0);
        model.Entity<IndustryJob>().Property(j => j.Activity).HasDefaultValue(IndustryActivity.Manufacturing);
        model.Entity<IndustryJob>().Property(j => j.LicensedRuns).HasDefaultValue(0);
        model.Entity<IndustryJob>().Property(j => j.BlueprintCopy).HasDefaultValue(false);
        model.Entity<IndustryJob>().Property(j => j.MaterialEfficiency).HasDefaultValue(0);
        model.Entity<IndustryJob>().Property(j => j.TimeEfficiency).HasDefaultValue(0);
        model.Entity<IndustryJob>().Property(j => j.OutputRuns).HasDefaultValue(0);
        model.Entity<IndustryJob>().Property(j => j.Probability).HasDefaultValue(1d);
        model.Entity<IndustryJob>().HasOne<Pilot>().WithMany().HasForeignKey(j => j.PilotId);
        model.Entity<IndustryJob>().HasIndex(j => new { j.PilotId, j.Status });
        model.Entity<IndustryJob>().HasIndex(j => j.BlueprintId);
        model
            .Entity<WorldWreck>()
            .ToTable(
                "WorldWreck",
                t =>
                    t.HasCheckConstraint(
                        "CK_Wreck_Source",
                        "(NpcId IS NOT NULL AND ShipId IS NULL) OR (NpcId IS NULL AND ShipId IS NOT NULL)"
                    )
            )
            .HasKey(w => w.Id);
        model.Entity<WorldWreck>().Property(w => w.Id).ValueGeneratedNever();
        model.Entity<WorldWreck>().HasOne<WorldNpc>().WithMany().HasForeignKey(w => w.NpcId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<WorldWreck>().HasOne<ShipLoss>().WithMany().HasForeignKey(w => w.ShipId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<WorldWreck>().HasOne<Pilot>().WithMany().HasForeignKey(w => w.OwnerId);
        model.Entity<WorldWreck>().HasIndex(w => w.NpcId).IsUnique();
        model.Entity<WorldWreck>().HasIndex(w => w.ShipId).IsUnique();
        model.Entity<WorldWreck>().HasIndex(w => new { w.SystemId, w.ExpiresAt });
        model.Entity<StationRepairPayment>().ToTable("WorldRepairPayment").HasKey(p => p.Id);
        model.Entity<StationRepairPayment>().HasOne<Pilot>().WithMany().HasForeignKey(p => p.PilotId);
        model.Entity<StationRepairPayment>().HasIndex(p => new { p.PilotId, p.Time });
        model.Entity<NpcAggression>().ToTable("WorldNpcAggression").HasKey(a => a.NpcId);
        model.Entity<NpcAggression>().HasOne<WorldNpc>().WithMany().HasForeignKey(a => a.NpcId);
        model.Entity<NpcAggression>().HasOne<Ship>().WithMany().HasForeignKey(a => a.TargetShipId);
        model.Entity<NpcAggression>().HasIndex(a => a.TargetShipId);
        model.Entity<WorldNpcEwar>().ToTable("WorldNpcEwar").HasKey(e => new { e.NpcId, e.Kind });
        model.Entity<WorldNpcEwar>().HasOne<NpcAggression>().WithMany().HasForeignKey(e => e.NpcId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<WorldNpcEwar>().HasOne<Ship>().WithMany().HasForeignKey(e => e.TargetShipId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<WorldNpcEwar>().HasIndex(e => e.TargetShipId);
        model.Entity<ShipLoss>().ToTable("WorldShipLoss").HasKey(l => l.ShipId);
        model.Entity<ShipLoss>().Property(l => l.ShipId).ValueGeneratedNever();
        model.Entity<ShipLoss>().Property(l => l.StoredShipsJson).HasDefaultValue("[]");
        model.Entity<ShipLoss>().HasOne<Pilot>().WithMany().HasForeignKey(l => l.PilotId);
        model.Entity<ShipLoss>().HasOne<WorldNpc>().WithMany().HasForeignKey(l => l.NpcId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ShipLoss>().HasIndex(l => new { l.PilotId, l.Time });
        // Retain the historical table name; version 33 adds the repaired layer
        // without replacing already paid armor schedules.
        model
            .Entity<PendingLocalRepair>()
            .ToTable("WorldArmorRepair", t => t.HasCheckConstraint("CK_ArmorRepair_Amount", "Amount > 0"))
            .HasKey(r => r.ModuleId);
        model.Entity<PendingLocalRepair>().Property(r => r.Layer).HasDefaultValue(RepairLayer.Armor);
        model.Entity<PendingLocalRepair>().HasOne<OwnedItem>().WithMany().HasForeignKey(r => r.ModuleId);
        model.Entity<PendingLocalRepair>().HasOne<Ship>().WithMany().HasForeignKey(r => r.ShipId);
        model.Entity<PendingLocalRepair>().HasIndex(r => new { r.ShipId, r.CompletesAt });
        model.Entity<ActiveFittedModule>().ToTable("WorldActiveModule").HasKey(h => h.ModuleId);
        model.Entity<ActiveFittedModule>().Property(h => h.ExpiresAt).HasDefaultValue(0L);
        model.Entity<ActiveFittedModule>().HasIndex(h => h.ExpiresAt).HasFilter("ExpiresAt>0");
        model.Entity<ActiveFittedModule>().HasOne<OwnedItem>().WithMany().HasForeignKey(h => h.ModuleId);
        model.Entity<ActiveFittedModule>().HasOne<Ship>().WithMany().HasForeignKey(h => h.ShipId);
        model.Entity<ModuleCooldown>().ToTable("WorldWeaponCooldown").HasKey(w => w.ModuleId);
        model.Entity<ModuleCooldown>().HasOne<OwnedItem>().WithMany().HasForeignKey(w => w.ModuleId);
        model
            .Entity<NpcBounty>()
            .ToTable("WorldNpcBounty", t => t.HasCheckConstraint("CK_NpcBounty_Amounts", "AmountCents > 0 AND BalanceCents >= 0"));
        model.Entity<NpcBounty>().HasOne<Pilot>().WithMany().HasForeignKey(b => b.PilotId);
        model.Entity<NpcBounty>().HasOne<WorldNpc>().WithMany().HasForeignKey(b => b.NpcId);
        model.Entity<NpcBounty>().HasIndex(b => b.NpcId).IsUnique();
        model.Entity<NpcBounty>().HasIndex(b => new { b.PilotId, b.Time });
        model
            .Entity<WorldNpc>()
            .ToTable(
                "WorldNpc",
                t => t.HasCheckConstraint("CK_Npc_Health", "ShieldCharge >= 0 AND ArmorDamage >= 0 AND StructureDamage >= 0")
            );
        model.Entity<WorldNpc>().HasIndex(n => n.BeltId);
        model.Entity<WorldNpc>().Property(n => n.ShieldRepairAt).HasDefaultValue(0L);
        model.Entity<WorldNpc>().Property(n => n.ArmorRepairAt).HasDefaultValue(0L);
        model.Entity<WorldNpc>().Property(n => n.RepairRevision).HasDefaultValue(0L);
        model
            .Entity<WorldNpcEnergy>()
            .ToTable("WorldNpcEnergy", t => t.HasCheckConstraint("CK_NpcEnergy_Charge", "Charge>=0"))
            .HasKey(n => n.NpcId);
        model.Entity<WorldNpcEnergy>().HasOne<WorldNpc>().WithMany().HasForeignKey(n => n.NpcId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<WorldNpc>().HasIndex(n => n.SystemId);
        model.Entity<WorldNpc>().HasIndex(n => n.EncounterId);
        model
            .Entity<WorldNpc>()
            .HasIndex(n => new { n.EncounterId, n.SpawnSlot })
            .IsUnique()
            .HasFilter("Role<>0 AND DestroyedAt IS NULL AND RetiredAt IS NULL");
        model
            .Entity<WorldNpc>()
            .HasIndex(n => new
            {
                n.SystemId,
                n.RetiredAt,
                n.DestroyedAt,
            });
        model
            .Entity<ShipHealthState>()
            .ToTable(
                "WorldShipHealth",
                t => t.HasCheckConstraint("CK_ShipHealth_Values", "ShieldCharge >= 0 AND ArmorDamage >= 0 AND StructureDamage >= 0")
            )
            .HasKey(s => s.ShipId);
        model.Entity<ShipHealthState>().HasOne<Ship>().WithMany().HasForeignKey(s => s.ShipId);
        model
            .Entity<PilotWallet>()
            .ToTable("WorldWallet", t => t.HasCheckConstraint("CK_Wallet_Cents", "Cents >= 0"))
            .HasKey(w => w.PilotId);
        model.Entity<PilotWallet>().HasOne<Pilot>().WithMany().HasForeignKey(w => w.PilotId);
        model.Entity<MarketStation>().ToTable("WorldMarketStation").HasKey(s => s.StationId);
        model.Entity<MarketStation>().Property(s => s.SeedVersion).HasDefaultValue(0);
        model
            .Entity<TestingWalletGrant>()
            .ToTable("WorldTestingGrant", t => t.HasCheckConstraint("CK_TestingGrant_Amounts", "AmountCents > 0 AND BalanceCents >= 0"))
            .HasKey(g => g.PilotId);
        model.Entity<TestingWalletGrant>().HasOne<Pilot>().WithMany().HasForeignKey(g => g.PilotId);
        model
            .Entity<NpcMarketOrder>()
            .ToTable(
                "WorldMarketOrder",
                t =>
                {
                    t.HasCheckConstraint("CK_MarketOrder_Price", "PriceCents > 0");
                    t.HasCheckConstraint("CK_MarketOrder_Quantity", "Remaining >= 0 AND Entered > 0 AND Remaining <= Entered");
                }
            );
        model
            .Entity<NpcMarketOrder>()
            .HasIndex(o => new
            {
                o.StationId,
                o.TypeId,
                o.Bid,
                o.Kind,
            })
            .IsUnique();
        model.Entity<NpcMarketOrder>().HasIndex(o => o.RegionId);
        model.Entity<NpcMarketOrder>().Property(o => o.Kind).HasDefaultValue(Eden.Gameplay.Market.NpcMarketKind.Development);
        model
            .Entity<NpcMarketOrder>()
            .HasIndex(o => new
            {
                o.RegionId,
                o.TypeId,
                o.Bid,
                o.PriceCents,
            });
        model
            .Entity<MarketExecution>()
            .ToTable(
                "WorldMarketTrade",
                t =>
                {
                    t.HasCheckConstraint(
                        "CK_MarketTrade_Amounts",
                        "PriceCents > 0 AND Quantity > 0 AND TaxCents >= 0 AND BalanceCents >= 0"
                    );
                }
            );
        model.Entity<MarketExecution>().HasOne<Pilot>().WithMany().HasForeignKey(t => t.PilotId);
        model.Entity<MarketExecution>().HasIndex(t => new { t.PilotId, t.Time });
        model
            .Entity<ShipEnergy>()
            .ToTable("WorldShipEnergy", t => t.HasCheckConstraint("CK_ShipEnergy_Charge", "Charge >= 0"))
            .HasKey(e => e.ShipId);
        model.Entity<ShipEnergy>().HasOne<Ship>().WithMany().HasForeignKey(e => e.ShipId);
        model.Entity<WorldAsteroid>().ToTable("WorldAsteroid", t => t.HasCheckConstraint("CK_Asteroid_Quantity", "Quantity >= 0"));
        model.Entity<WorldAsteroid>().Property(a => a.Id).ValueGeneratedNever();
        model.Entity<WorldAsteroid>().HasIndex(a => a.SystemId);
        model.Entity<WorldAsteroid>().HasIndex(a => a.BeltId);
        Eden.World.Mining.MiningSiteSchema.Configure(model);
        model.Entity<OwnedItem>().ToTable("WorldItem", t => t.HasCheckConstraint("CK_Item_Quantity", "Quantity > 0"));
        model.Entity<OwnedItem>().Property(i => i.CrystalDamage).HasDefaultValue(0d);
        model.Entity<OwnedItem>().Property(i => i.MaterialEfficiency).HasDefaultValue(0);
        model.Entity<OwnedItem>().Property(i => i.TimeEfficiency).HasDefaultValue(0);
        model.Entity<OwnedItem>().Property(i => i.BlueprintRuns).HasDefaultValue(-1);
        model.Entity<OwnedItem>().Property(i => i.Singleton).HasDefaultValue(false);
        model.Entity<OwnedItem>().Property(i => i.Online).HasDefaultValue(false);
        model.Entity<OwnedItem>().HasOne<InventoryOwner>().WithMany().HasForeignKey(i => i.OwnerId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<OwnedItem>().HasIndex(i => new { i.OwnerId, i.LocationId });
        model.Entity<OwnedItem>().HasIndex(i => i.LocationId);
        model.Entity<Account>().ToTable("WorldAccount");
        model.Entity<Account>().Property(a => a.CloneGrade).HasDefaultValue(AccountCloneGrade.Alpha);
        model.Entity<Pilot>().ToTable("WorldPilot");
        model.Entity<Pilot>().Property(p => p.SafetyLevel).HasDefaultValue(ShipSafetyLevel.Full);
        // Defaults preserve the identities previously returned by character selection.
        model.Entity<Account>().Property(a => a.UserName).HasDefaultValue("Eden");
        PersonalMarketSchema.Configure(model);
        RegionalMarketSchema.Configure(model);
        model.Entity<Account>().Property(a => a.CreatedAt).HasDefaultValue(134000000000000000L);
        model.Entity<Pilot>().Property(p => p.CreatedAt).HasDefaultValue(134000000000000000L);
        model.Entity<Pilot>().Property(p => p.RaceId).HasDefaultValue(1);
        model.Entity<Pilot>().Property(p => p.BloodlineId).HasDefaultValue(1);
        model.Entity<Pilot>().Property(p => p.GenderId).HasDefaultValue(1).HasSentinel(-1);
        model.Entity<Pilot>().Property(p => p.AncestryId).HasDefaultValue(10);
        model.Entity<Pilot>().Property(p => p.SchoolId).HasDefaultValue(19);
        model.Entity<PilotSkill>().ToTable("WorldPilotSkill", t => t.HasCheckConstraint("CK_PilotSkill_Points", "Points >= 0"));
        model.Entity<PilotSkill>().HasOne<Pilot>().WithMany().HasForeignKey(s => s.PilotId);
        model.Entity<QueuedLevel>().ToTable("WorldSkillQueue").HasOne<Pilot>().WithMany().HasForeignKey(e => e.PilotId);
        model.Entity<TrainingLog>().ToTable("WorldSkillHistory").HasOne<Pilot>().WithMany().HasForeignKey(e => e.PilotId);
        model.Entity<TrainingLog>().HasIndex(e => new { e.PilotId, e.Time });
        model.Entity<Ship>().ToTable("WorldShip");
        model.Entity<Ship>().Property(s => s.StorageFlagId).HasDefaultValue((int)InventoryFlag.Hangar);
        model.Entity<Ship>().HasIndex(s => new { s.OwnerId, s.StoredInId });
        model.Entity<FlightPosition>().ToTable("WorldPosition").HasKey(p => p.ShipId);
        model.Entity<Pilot>().HasIndex(p => p.AccountId);
        model.Entity<Pilot>().Property(p => p.Name).UseCollation("NOCASE");
        model.Entity<Pilot>().HasIndex(p => p.Name).IsUnique();
        model
            .Entity<Pilot>()
            .HasOne(p => p.ActiveShip)
            .WithMany()
            .HasForeignKey(p => new { p.ActiveShipId, p.Id })
            .HasPrincipalKey(s => new { s.Id, s.OwnerId })
            .OnDelete(DeleteBehavior.Restrict);
        model.Entity<Ship>().HasOne<InventoryOwner>().WithMany().HasForeignKey(s => s.OwnerId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Ship>().HasOne(s => s.Position).WithOne().HasForeignKey<FlightPosition>(p => p.ShipId);
    }
}
