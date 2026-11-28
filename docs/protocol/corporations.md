# Corporations

[Documentation](../README.md) / [Protocol index](../protocol.md)

Creation, membership, administration, roles, wallets, ownership and shares.

These records preserve milestone evidence and historical observations. Dates, schema versions, test results and live acceptance apply to the recorded change; they do not establish acceptance of later changes.

## Contents

- [Corporation and alliance UI audit](#corporation-and-alliance-ui-audit)
- [Corporation office asset search](#corporation-office-asset-search)
- [Corporation Kill Reports](#corporation-kill-reports)
- [Corporation medals](#corporation-medals)
- [Medal award notifications](#medal-award-notifications)
- [Corporation mail restrictions](#corporation-mail-restrictions)
- [Corporation and alliance wars](#corporation-and-alliance-wars)
- [Defender allies, surrender and War Reports](#defender-allies-surrender-and-war-reports)
- [Edit member private identity reply, November 16, 2026](#edit-member-private-identity-reply-november-16-2026)
- [Ordinary member auto-removal, November 15, 2026](#ordinary-member-auto-removal-november-15-2026)
- [Blueprint lock and unlock votes, November 15, 2026](#blueprint-lock-and-unlock-votes-november-15-2026)
- [CEO elections, November 15, 2026](#ceo-elections-november-15-2026)
- [Administration and inventory blockers, November 15, 2026](#administration-and-inventory-blockers-november-15-2026)
- [Corporation Show Info market activity latency, November 15, 2026](#corporation-show-info-market-activity-latency-november-15-2026)
- [Bulk member removal, November 15, 2026](#bulk-member-removal-november-15-2026)
- [Member-expulsion votes, November 15, 2026](#member-expulsion-votes-november-15-2026)
- [General and share-creation votes, November 15, 2026](#general-and-share-creation-votes-november-15-2026)
- [Corporation closure, November 15, 2026](#corporation-closure-november-15-2026)
- [Dividends and CEO succession, November 15, 2026](#dividends-and-ceo-succession-november-15-2026)
- [Member search and tracking, November 14, 2026](#member-search-and-tracking-november-14-2026)
- [Recruitment advertisements, November 14, 2026](#recruitment-advertisements-november-14-2026)
- [Bulletins and welcome mail, November 14, 2026](#bulletins-and-welcome-mail-november-14-2026)
- [Corporation title row decoding, November 14, 2026](#corporation-title-row-decoding-november-14-2026)
- [Administration Role Management](#administration-role-management)
- [Corporation construction failure capture, November 14, 2026](#corporation-construction-failure-capture-november-14-2026)
- [Direct invitations and corporation window reads, November 14, 2026](#direct-invitations-and-corporation-window-reads-november-14-2026)
- [Corporation registry lifetime and application replies](#corporation-registry-lifetime-and-application-replies)
- [Current recruitment filters and inbox notices](#current-recruitment-filters-and-inbox-notices)
- [Corporation contacts and standings, November 11, 2026](#corporation-contacts-and-standings--november-11-2026)

- [Corporation Show Info window audit, November 9, 2026](#corporation-show-info-window-audit-november-9-2026)

- [Public corporation Show Info, November 9, 2026](#public-corporation-show-info-november-9-2026)

- [Corporation offices and shared storage, November 6, 2026](#corporation-offices-and-shared-storage-november-6-2026)

- [Corporation share holdings and native wallet panels](#corporation-share-holdings-and-native-wallet-panels)
- [Corporation Bills and Market Transactions panel calls](#corporation-bills-and-market-transactions-panel-calls)
- [Corporation ISK header permission correction](#corporation-isk-header-permission-correction)
- [Corporation inventory ownership storage](#corporation-inventory-ownership-storage)
- [Corporate LP and EverMark wallets](#corporate-lp-and-evermark-wallets)
- [Shared corporation ISK wallets and bounty taxation](#shared-corporation-isk-wallets-and-bounty-taxation)
- [Delegated corporation roles and titles](#delegated-corporation-roles-and-titles)
- [Persistent corporation administration](#persistent-corporation-administration)
- [Update corporation with CEO skills](#update-corporation-with-ceo-skills)
- [Player corporation character-selection logo correction](#player-corporation-character-selection-logo-correction)
- [Corporation creation CEO refresh correction](#corporation-creation-ceo-refresh-correction)
- [Corporation Details faction-enlistment service correction](#corporation-details-faction-enlistment-service-correction)
- [Corporation window voting-capability startup correction](#corporation-window-voting-capability-startup-correction)
- [Player corporation applications and membership](#player-corporation-applications-and-membership)
- [Player corporations: creation foundation](#player-corporations-creation-foundation)

## Corporation and alliance UI audit

This audit started at Eden `ad061bf0` and fixed 15 defects. The native UI call sites below were inspected as bytecode in Windows build
3662168, archive SHA-256 `37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.
The separate [alliance findings](alliances.md#alliance-contacts-applications-and-calendar-audit) record shared contacts,
application processing, and the calendar.

### Corporation membership and voting

- Accepting a recruitment offer now checks the pilot's authority to leave their existing player corporation, including
  the role-removal cooldown. The same pending offer can be accepted when that restriction expires.
- A pilot can remove their own roles without accidentally clearing their base. Changing their own base, member title,
  division, or squadron still requires Personnel Manager authority.
- Viewing another corporation's votes accepts the native `GetVoteCasesByCorporation(corporationID, 0)` call.
  `GetVotes(voteCaseID)` resolves the case's corporation and returns the viewer's ballots, subject to the same view rules.
  The one-argument case-list call and the two-argument ballot call remain supported.

In build 3662168, `base_corporation.pyj::RemoveAllRoles` supplies a None base while zeroing the role masks.
`voting.pyj::CorporationVotes` uses the two native voting call shapes. `corp_ui_votes.pyj::GetOpenVoteSubContent` uses those
ballots to render the viewer's vote. EVE.js 261108.1 references are `corpRegistryRuntime.js` (`41d691387ff4`) and
`voteManagerService.js` (`a494d91c7d13`). [CCP corporation votes](https://support.eveonline.com/hc/en-us/articles/203268841-Corporation-Votes)

### Corporate hangars and wallet

- Deliver to Corp Member works from an ordinary local NPC office division when the caller has its Hangar Take role.
  Assembled containers also require Container Take. The transaction supports full stacks and partial quantities, and publishes
  the resulting inventory changes.
- Manual office unrenting requires Director or CEO authority. Rent Office permits renting.
- Whole-ISK amounts keep exact cents through transfer validation and accounting, including integer amounts beyond the
  exact range of a double.
- Assets → Deliveries lists NPC stations with corporate Market Deliveries, including stations where the corporation has
  no office. Authorized users can expand each location and search by category, group, type and minimum stack quantity.
  A caller without report authority receives an empty location list so the panel can display its own role hint.

`invItemFunctions.pyj::_DeliverToCorpMemberID` sends the source flag at offsets 284–305.
`OfficesPanel._load_buttons` distinguishes the rent and unrent actions. `transferMoneyWnd.pyj::Confirm` forwards the
amount to both wallet transfer routes at offsets 109–179. EVE.js 261108.1 references are `corporationDelivery.js`
(`604f4904a84a`) and `officeManagerService.js` (`4ec93d6ba0f0`).
[CCP corporation roles](https://support.eveonline.com/hc/en-us/articles/203217712-Roles-Listing)

`corp_ui_accounts.pyj::CorpAccounts.ShowAssets` requests `deliveries` before its role hint.
`GetLocationData` reads `solarsystemID` and `itemCount`, and `GetSubContent` extends the returned rowset's virtual
columns. The location summary uses the native delivery descriptor confirmed by EVE.js `corpAssetState.js` (`787dda9c937e`).

### War panels

- The War HQ panel lists finished wars at the structure. The structure icon uses the current war list to decide when
  to show its active HQ marker.
- An open mutual-war invitation list refreshes when the pilot's corporation becomes part of an alliance and the list
  changes. The notification compares the old view with the current invitation list before recording the new view.

`panelWarHQ.pyj::WarHQ.GetWars` calls `GetWarsForStructure` and `_LoadWars` renders every returned war.
`warMutalWarInvite.pyj::OnMutualWarsUpdated` reloads the list on the event forwarded by `mutualWarInvites.pyj`.
EVE.js 261108.1 references are `warsInfoMgrService.js` (`eb8b1ded394f`), `warRuntimeState.js` (`7dd95ae03e39`),
and `mutualWarInviteMgrService.js` (`96eb8bfaa332`). [CCP wars](https://support.eveonline.com/hc/en-us/articles/115004152745-Wars)

### Automated validation

On baseline `ad061bf0`, all 17 focused cases reproduced the defects in 10.20 seconds. With the fixes, all 17 passed in
15.12 seconds. The adjacent selection passed 315 cases in 92.27 seconds. The solution built in 0.88 seconds with zero
warnings or errors. The catalog setup and formatting policies passed 8 and 9 cases respectively.
The formatting check passed in an isolated checkout in 39.23 seconds, checking 3,342 files and the 140-column limit.

<details>
<summary>Test filters</summary>

Prefix each suffix with `Eden.Protocol.Tests.` and join the `FullyQualifiedName` selectors with `|`.
The focused selection uses `=`.

```text
Alliances.CorporationWarWireTests.NativeWarHeadquartersPanelRetainsFinishedWarsAfterTheActiveMarkerClears
Alliances.CorporationWarWireTests.NativeMutualInvitationPanelRefreshesWhenItsOrganizationChanges
Alliances.CorporationWarInheritanceTests.AcceptedAllianceApplicationSurvivesAnInterveningMutualWarAndJoinsAfterCooldown
Alliances.AllianceSocialTests.FormerPilotStopsReceivingAllianceContactUpdatesBeforeSessionAffiliationRefresh
Alliances.AllianceSocialTests.AllianceContactLabelBatchSkipsContactsMissingFromTheSharedBook
Alliances.AllianceMembershipTests.RepeatedAllianceApplicationDeletionSucceedsWithoutPublishingAnotherChange
Alliances.AllianceSocialTests.RepeatedCalendarCancellationKeepsTheCommittedAllianceBook
PilotSessions.CorporationDeliveriesTests.NativeOfficeDeliveryUsesLocalDivisionTakeRightsAndPublishesInventoryChanges
PilotSessions.CorporationStorageTests.NativeUnrentOfficeRequiresDirectorEvenWhenMemberRentedTheOffice
PilotSessions.CorporationWalletTests.IntegerWalletTransferPreservesExactCentsAboveDoublePrecision
PilotSessions.CorporationMembershipTests.AcceptingARecruitmentOfferHonorsTheCurrentCorporationsRoleCooldown
PilotSessions.CorporationRoleTests.SelfRoleRemovalPreservesMemberDetailsAndCannotEditThem
PilotSessions.CorporationVotingTests.NativeForeignVoteListAcceptsTheAllStatusesArgument
PilotSessions.CorporationVotingTests.NativeGetVotesResolvesTheCorporationFromTheCaseAndReturnsOnlyTheCallersBallots
PilotSessions.CorporationStorageTests.NativeMarketDeliveryAssetReportsListExpandAndSearchAcrossStations
PilotSessions.CorporationStorageTests.NativeMarketDeliveryAssetReportsRespectCurrentFinancialAuthority
```

The adjacent selection uses `~`.

```text
Alliances.
PilotSessions.CorporationAdministrationTests
PilotSessions.CorporationMembershipTests
PilotSessions.CorporationRecruitmentTests
PilotSessions.CorporationRoleTests
PilotSessions.CorporationLeadershipTests
PilotSessions.CorporationMemberAuditTests
PilotSessions.CorporationVotingTests
PilotSessions.CorporationCeoElectionTests
PilotSessions.CorporationBlueprintVoteTests
PilotSessions.CorporationExpulsionVoteTests
PilotSessions.CorporationRemovalQueueTests
PilotSessions.CorporationBulkRemovalTests
PilotSessions.CorporationDeliveriesTests
PilotSessions.CorporationStorageTests
PilotSessions.CorporationWalletTests
PilotSessions.CorporationWalletStartupTests
PilotSessions.CorporationFinancialPanelTests
PilotSessions.CorporationLoyaltyTests
PilotSessions.CorporationContactsTests
PilotSessions.CorporationCalendarTests
PilotSessions.CorporationClientDiagnosticsTests
Market.CorporationMarket
```

</details>

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

1. Open another corporation's Votes panel, expand a case and inspect its options.
2. Open Corporation Assets → Deliveries, expand a remote station and search for a delivered stack.
3. Keep the mutual-war invitation list open while a corporation enters an alliance.

## Corporation office asset search

The Corporation Assets search can find rented NPC-station offices by item category, group, type and minimum
stack quantity. Blank filters list the corporation's offices. With filters set, each matching station appears
once. An assembled ship counts as one item. Results reflect current resident office contents and reopen from
the same checkpointed inventory.

The CEO, Directors, Accountants and Junior Accountants can search and open the matching office asset reports.
The report grants read access to the ordinary divisions. The Goals Deliveries hangar uses its own query role,
and moving items requires the ordinary office hangar rights. The server checks the current corporation and
effective roles on each request, including after a role change.

Inert inspection of build **3662168**, archive SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`, finds the call
`corpmgr.SearchAssets(which, categoryID, groupID, typeID, minimumQuantity)` in
`corp_ui_accounts.CorpAccounts.Search`. The pinned client reads `locationID` and expands the selected
station using `GetAssetInventoryForLocation`. EVE.js **261108.1** confirms the financial authority check and
64-bit `locationID` `CRowset` in `corpmgrService.js` and `corpAssetState.js`
(SHA-256 `0ceac2c9c0982f00e714a1f17700acc8a968ffc4ef9c83ccb763b51d8bd1368f` and
`787dda9c937e554650a7d69c9cf3cd67b25b6e97bb7c855d2580e1cf88113af5`).
[CCP corporation roles](https://support.eveonline.com/hc/en-us/articles/203217712-Roles-Listing) ·
[EVE University corporation logistics](https://wiki.eveuniversity.org/Corporation_logistics)

`GetAssetInventoryForLocation` returns a native `CRowset` for both the Offices tab and office search results.
In build **3662168**, `CorpAccounts.GetSubContent` line 496 extends `items.header.virtual` with group and
category getters before showing populated results. `GetSubContentDivision` line 565 also reads that header.
A plain list passed the empty-results branch but raised an attribute error for populated offices. The response
uses the existing inventory DBRow descriptor with stack-size and singleton getters and respects current
office-division permissions on each read. REA extraction evidence
`ev_2cea7aefe7ddcfbbf062c37f6cd5403bbc7cb61948f13c7c05b788be6704e819` identifies the pinned
`corp_ui_accounts.pyj`. The pinned Carbon `CRowset` constructor stores the supplied header, and EVE.js
`corpmgrService.js` uses a `CRowset` for the same detail call.

The rowset correction passed four exact cases covering populated and empty details, office search, a remote assembled
ship, and current division authority. The combined 22-case feature run built in 21.52 seconds and tested in 22.90 seconds.

**Automated validation.** On `81a2764d` plus the search change,
`CorporationStorageTests.NativeOfficeAssetSearchFiltersResidentStacksAndStoredShipsAcrossCheckpoint`
and `CorporationStorageTests.NativeOfficeAssetSearchRequiresCurrentFinancialAuthorityAndPermitsOpeningResults`
passed in 5.18 seconds of combined case time (7.93 seconds for the runner). They cover native rows, stack and
ship filtering, zero database commands and connections on warm searches, checkpoint reopen, role revocation
and Goals Deliveries visibility. The affected `CorporationStorageTests`, `CorporationRoleTests`,
`PilotInventoryTests` and `InventoryOwnershipTests` selection passed all 46 cases in 13.96 seconds of runner time.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Search for an assembled ship and a stack in different offices, then
expand each matching result while using a character with the Junior Accountant role.

## Corporation Kill Reports

**Corporation → General → Kill Reports** shows recent capsuleer ship and capsule kills and losses for the selected pilot's
corporation. A kill is credited to the final-blow pilot's corporation, and a loss is credited to the victim's corporation.
NPC-inflicted capsuleer losses appear in the loss view. Attribution uses the saved combat parties, and the canonical loss
record supplies one kill ID even when several pilots have copies in their personal logs. The resident economy indexes
existing combat reports by corporation and side and restores the index from checkpoints. Structure reports are available
through the War Report for attributed structure losses.

Inert inspection of build **3662168**, archive SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`, confirms the navigation in
`corporationWindow.pyj` and the `corpRegistry.GetRecentKills` and `GetRecentLosses` calls in `base_corporation.pyj`.
`corp_ui_killreports.pyj` sends a page size of 25 and a Python None or an exclusive kill ID. The same archive's
`crowset.pyj` accepts the descriptor and rows in its `CRowset` constructor. Both replies use a corporation-scoped cached
`CRowset` like the EVE.js **261108.1** reference and share Eden's combat report fields and killmail XML.
EVE.js reference files are `corpRegistryRuntime.js` (SHA-256
`41d691387ff45dce3cf4680da40291557b868f0b3f589234d0eb244b77c4e35c`) and `killmailState.js` (SHA-256
`920fca640077c858230c273b7fdb52bd5869196fd1d5ffad316ad27e871451cd`).
[UniWiki Killmail](https://wiki.eveuniversity.org/Killmail)

### Automated validation

`FullyQualifiedName=Eden.Protocol.Tests.Alliances.CorporationCombatReportTests.NativeCorporationPagesUseCanonicalKillIdsAndSurviveCheckpoint`
passed. It exercises a bound registry call, corporation isolation, native reply fields, exclusive paging, duplicate
personal reports, NPC-inflicted loss and checkpoint reload.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

1. Open **Corporation → General → Kill Reports**, switch between kills and losses, and open a saved report.

## Corporation medals

**Corporation → Members → Decorations** lets CEOs, directors and personnel managers create and award custom medals.
Creating a medal costs 5 million ISK. Each award costs 5 million ISK per selected member, and a member can receive
the same medal again. Both operations use the selected corporation wallet division and request confirmation before
charging it. The resident economy commits the fee, journal entry and medal or award together.

Recipients start with private awards. In **Character Sheet → Decorations**, they can publish awards to Show Info,
make them private or discard them. Discarding removes an award from the character's list and preserves the issuing
corporation's award history. Issuing corporations can review their own catalogue and recipients. Checkpoint schema 230
preserves medal art, award reasons, visibility and corporation attribution. The recipient keeps a medal after leaving
the issuing corporation. The issuing connection receives the catalogue refresh event. Recipients get
[medal award notifications](#medal-award-notifications).

Inert inspection of build **3662168**, archive SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`, confirmed the operations and quote flow in
`medals.pyj`, the layered ribbon and medal triples in `medalribbonranks.pyj` and the statuses and journal references in
`appConst.pyj`. The EVE.js **261108.1** comparison used `corpService.js` (SHA-256
`a9ef788e32125b80e5725d00c919bf837365cb5187954c93068f765cb8212d6a`) for client row shapes.
[CCP Quantum Rise patch notes](https://www.eveonline.com/news/view/patch-notes-for-quantum-rise-1) ·
[UniWiki Managing corporation members](https://wiki.eveuniversity.org/Managing_corporation_members#Decorations)

### Automated validation

`FullyQualifiedName=Eden.Protocol.Tests.Alliances.CorporationMedalTests.NativeMedalsChargeTheSelectedDivisionAndPersistVisibilityAcrossCheckpoint`
passed in 0.43 seconds. It exercises native fee quotes and confirmations, role checks, selected wallet debits, repeat
awards, insufficient-funds rollback, private and public reads, discard, and checkpoint reload.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

1. Create a medal from **Members → Decorations** and confirm the selected corporation wallet division pays the fee.
2. Award the medal to a corporation member and verify that reopening **Decorations** shows the private award.
3. Make the medal public and open the recipient's Show Info from another pilot.

## Medal award notifications

An award records a notification in each recipient's **Corporation** inbox with the medal, issuing corporation and
award reason. The resident transaction commits the notification alongside the award, fee and wallet journal.
Connected recipients also receive `OnMedalIssued`, which refreshes **Decorations**. The regular inbox read and
delivery cursor prevent a fetched notification from appearing twice. Unread notices remain available after a restart.

Build **3662168** `notificationconst.pyj` assigns type 3 to `notificationTypeCharMedalMsg` and places it in the
Corporation group. `notificationUtil.pyj` reads `medalID` and `corpID` for the native formatter. In `medals.pyj`,
`OnMedalIssued` refreshes the recipient's medals and takes no required arguments. The EVE.js **261108.1** reference
`corporationNotifications.js` (SHA-256 `101462c93041e9de1f42ae8707f4e90c0a34a96483fb3d6d55b8cc686dddd3c9`)
also targets the recipient connection with this event.
[CCP Quantum Rise patch notes](https://www.eveonline.com/news/view/patch-notes-for-quantum-rise-1) ·
[UniWiki award flow](https://wiki.eveuniversity.org/Managing_corporation_members#Decorations)

### Automated validation

`FullyQualifiedName=Eden.Protocol.Tests.Alliances.CorporationMedalTests.MedalAwardsNotifyOnlyTheirRecipientsAndUnreadNoticesSurviveCheckpoint`
passed in 4.83 seconds. It verifies recipient-only delivery, the native fields and Decorations refresh, no notice
for quotes or failed awards, and unread notification persistence after an offline award.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

1. Award a medal while its recipient has Decorations open and confirm the view refreshes and the unread notice appears.
2. Award another medal while the recipient is signed out, then verify the notification after sign-in.

## Corporation mail restrictions

CEOs and directors can set **Corp Mail** to **Communications Officers only** on the Corporation home panel.
When enabled, Communications Officers and directors can address a mail to their corporation. The resident send
checks the pilot's current corporation roles before creating the message, recipient copies or delivery events.
If the draft also addresses individuals, a rejected corporation address prevents the entire send. Removing the
restriction restores corporation-addressed sending for all current members. Existing saves default to unrestricted
and the setting persists in the standard corporation details checkpoint.

The pinned build **3662168** `corp_ui_home.pyj` checks `SessionChecker.IsCorpDirector` before showing its toggle.
The read uses `corpRegistry.DoesCorpRestrictCorpMails()` and the toggle sends one Boolean to
`RegisterNewCorpMailRestrictionSettings`. The restricted label identifies Communications Officers, who use the
pinned `corpRoleChatManager` bit. The archive SHA-256 is
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.
The EVE.js **261108.1** reference `corpRegistryRuntime.js` (SHA-256
`41d691387ff45dce3cf4680da40291557b868f0b3f589234d0eb244b77c4e35c`) implements the settings calls.
Eden keeps its existing Boolean read and checks the effective role in the mail transaction.
[UniWiki corporation mail](https://wiki.eveuniversity.org/EVE_Mail#Corp_Mails) ·
[CCP corporation roles](https://support.eveonline.com/hc/en-us/articles/203217712-Roles-Listing)

### Automated validation

`FullyQualifiedName=Eden.Protocol.Tests.Alliances.CorporationMailRestrictionTests.NativeCorporationMailRestrictionChecksCurrentRoleAndSurvivesCheckpoint`
passed in 0.42 seconds. It checks native reads and edits, authorization, atomic rejection of mixed recipients, personal
mail, immediate role changes, and restriction persistence after reopening a checkpoint.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

1. Enable **Communications Officers only** on Corporation Home and try sending to the corporation as an ordinary member.
2. Assign the member the Communications Officer role, send again, and confirm the message appears in the Corp mailbox.

## Corporation and alliance wars

Directors use **Corporation → Wars** to select another eligible organization and one of their own anchored, dockable
high-security Upwell structures as War HQ. Alliance wars use the executor corporation's authority and wallet. Owning a
deployed Upwell structure makes a player corporation eligible, including when it belongs to an alliance. A standard
declaration debits 500 million ISK immediately and issues the next bill. The wallet's Bills panel supports manual payment
and the corporation's existing automatic payment settings. Each renewal has its own invoice and advances the original
billing deadline by seven days.

Hostilities start 24 hours after declaration. Unpaid bills, loss of eligibility or loss of the selected HQ start the
24-hour combat cooldown. Losing the HQ prevents the attacker from redeclaring against the same defender for 14 days.
Mutual invitations are free, expire after 30 days and require both organizations' consent. Either party can retract an
accepted mutual war. A defending corporation's war follows it into an alliance. An aggressor or party to a mutual war
must resolve its war before joining an alliance. A departing corporation inherits its alliance's current wars with their
original combat and billing dates. An inherited offensive war without a usable HQ runs through its paid cycle and
cooldown. Pilots who leave during hostilities can rejoin after seven days or when hostilities end.

Crimewatch treats opposing pilots and eligible Upwell targets as legal during hostilities. The normal weapons and PvP
timers still apply. Neutral remote assistance to a war participant with an active PvP timer is criminal in high security
and suspect in low security. In high security, neutral command bursts skip those participants. War HQs use the shorter
reinforcement window. The resident economy owns war, invitation and invoice state and saves it in checkpoint schema 227.
Declaration and end notifications reach members and shareholders through the saved inbox.

Native service coverage includes declaration, target and HQ selection, public and cached war records, bill payment,
eligibility, peace treaties, mutual invitation settings and war-change notifications. See the follow-up for
[defender allies, surrender and War Reports](#defender-allies-surrender-and-war-reports).

### Source evidence

The original build **3662168** archive has SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.
Static inspection covered `evewar/const.pyj`, `warDeclare.pyj`, `war_cso.pyj`, `mutualWarInvites.pyj`,
`notifications/common/formatters/war.pyj` and `notificationconst.pyj`. The registry cache consumes KeyVal dictionaries
keyed by numeric war ID. Public war history uses a native indexed rowset. `OnWarChanged` includes the updated war,
the participant owner set and a dictionary of old and new field values.

The local EVE.js **261108.1** snapshot was used for independent behavior comparison. SHA-256 references under
`server/src/services/corporation/` follow.

| File | SHA-256 |
| --- | --- |
| `warDeclarationState.js` | `5e0a89c44dd6429034bb0a8261317f0525e57840afcac83890fa57451660fb06` |
| `warRuntimeState.js` | `7dd95ae03e39ce4c226b26a3f6d0802f440b73222d7c944f9183398ebdd065c7` |
| `warNotificationCenter.js` | `a47a1486d61090f49da112ae0a71fd015e340eedb7414a54cbf2fec8f1008842` |

The pinned client and September 22 CCP patch use 500 million ISK per week. The EVE.js snapshot still uses 100 million.
[CCP Wars](https://support.eveonline.com/hc/en-us/articles/115004152745-Wars) ·
[War Eligibility](https://support.eveonline.com/hc/en-us/articles/360001975920-War-Eligibility) ·
[Mutual War](https://support.eveonline.com/hc/en-us/articles/360005659379-Mutual-War) ·
[War Notifications](https://support.eveonline.com/hc/en-us/articles/203279981-War-Notifications) ·
[Version 24.01 patch notes](https://www.eveonline.com/news/view/patch-notes-version-24-01) ·
[CCP Crimewatch changes](https://www.eveonline.com/news/view/patch-notes-for-april-2019-release) ·
[CCP reinforcement changes](https://www.eveonline.com/news/view/patch-notes-version-20-05#h3-structures-deployables) ·
[UniWiki War declarations](https://wiki.eveuniversity.org/War_declarations)

### Automated validation

The final solution build passed in 9.34 seconds with zero warnings and errors. The focused .NET run passed all
294 selected cases in 110.59 seconds, with zero skipped. The filter covered `Alliances`, `CorporationShowInfoTests`,
`CorporationPublicInfoTests`, `CorporationFinancialPanelTests`, `CorporationMembershipTests`, `CrimewatchTests`,
`RemoteAssistanceTests`, `StructureTests`, `StructureAttackTests`, `StructureWeaponTests`, `CommandBurstTests`,
`ServerServicesTests` and the exact migration case `Version226AddsCorporationWarStateAndPreservesTheExistingWorld`.
The final filter excluded both cases of `FutureLogisticsHullSelectorsHaveKnownConsumersWithoutEnablingTheirHullCohort`
after the exact method reproduced the same failures on unchanged main `36e86fae`.

Development started with the exact `CorporationWarTests.DeclarationAndWeeklyInvoiceCommitOnceAndSurviveRestart`
filter. The catalog setup and formatting policy suites passed 8 and 9 cases in 1.388 and 0.018 seconds respectively.
The style, CSharpier, line-width, local-link and diff checks passed. The full .NET suite was outside this validation scope.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

1. Declare a standard war from **Corporation → Wars**, then verify the next bill, the War HQ's Show Info and the defender's inbox.
2. Accept a mutual war invitation and verify a green-safety tackle succeeds after warmup and is blocked again after cooldown.

## Native War Permit status and refresh

Corporation replies use current effective war eligibility for the client's `allowWar` field. For an alliance member
corporation, the alliance can qualify through a structure owned by any current member. The declaration lookup
and command still use legal war organizations and executor authority. An alliance member corporation remains
ineligible as a direct target. The stock context menu also reads the permit when opening an individual member
corporation, so that menu can expose the action even though the wizard and server require the alliance as target.

The native corporation registry read starts monitoring the current corporation's permit. When it changes, Eden
sends one `OnAllowWarUpdated(corpID, allowWar)` to the observing connection. Ordinary polling sends no duplicate,
and a corporation change invalidates the previous observation.

Build **3662168** `warPermit.pyj` resolves an alliance target to its executor corporation before requesting
`corpmgr.GetPublicInfo(corpID).allowWar`. Its own permit is cached after `corpRegistry.GetCorporation` and
refreshed by `OnAllowWarUpdated`. The corresponding REA extraction evidence is
`ev_a564ab886bfdfb61e0997e15195e22e41d2fba7249b39c9b5232be4035e9d777`.
Local EVE.js 261108.1 `warPermitState.js` supplies the event comparison (SHA-256
`1c961a8d93801e75de8ecadabdd75d1e6e75a8f1d2922f7d4a6f0f463aefe749`).
[CCP War Eligibility](https://support.eveonline.com/hc/en-us/articles/360001975920-War-Eligibility)

The two exact `CorporationWarWireTests.NativeWarPermit*` methods passed in the shared feature run
(21.52 seconds to build, 22.90 seconds to test). They cover effective alliance eligibility, direct target
rejection, qualifying changes on another member corporation and duplicate suppression.

## Defender allies, surrender and War Reports

Defenders in a declared war can advertise for assistance. Eligible corporations and alliances can offer assistance on
the defender's side for a quoted one-time payment or for free. The defender can accept or decline, and the offering
organization can retract an outstanding offer. A contract opens legal combat against the aggressor after four hours,
provided the war has started, and expires two weeks after acceptance or when the war ends. Existing safety and third-party
assistance rules use the same war relationship. Accepted fees and CONCORD registration charges settle with the contract
in the same resident transaction.

Either principal can propose surrender for zero ISK or a payment to the recipient. Acceptance transfers the agreed ISK,
starts the 24-hour combat cooldown and sets a 14-day bilateral redeclaration restriction. Expired, declined and private
offers cannot be accepted by another organization. The War inbox records offers, responses and withdrawals for the
appropriate parties. Checkpoint schema 228 preserves negotiations, active ally contracts and war losses.

The public War Report shows ship and structure losses, estimated ISK losses by side, the ship-class graph and a filtered
list of linked killmails. Eden assigns an opposing loss to one active war at destruction and saves its participants and
valuation with the loss. The latest qualifying war wins when a loss qualifies for several wars. The report uses local
market reference prices and catalog prices for available hull and item valuations. Losses from before schema 228 have
no recorded war attribution and do not populate the report retroactively.

### Contract and report evidence

The original build **3662168** archive has SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`. Inert inspection of `war_cso.pyj` and
`warWindows.pyj` verified the negotiation methods, argument order, floating ISK offers and private offer IDs.
`eveMoniker.GetWarStatistic` binds a numeric war ID. The bound `GetBaseInfo` returns the war, kills by
organization, ISK by organization and the ally dictionary. `GetKills` filters by side and ship class.
`GetKillsByGroup` uses raw inventory group IDs, which `cfg.GetShipClassTypeByGroupID` maps to graph classes.
`crimewatch.util.GetKillReportHashValue` uses SHA-1 of the victim pilot, final pilot, victim type and FILETIME fields
in that order. The service checks this hash before returning a linked killmail.

EVE.js **261108.1** references under `server/src/services/` were consulted for independent behavior comparison.

| File | SHA-256 |
| --- | --- |
| `corporation/warNegotiationRuntimeState.js` | `79fb2eb65e51f8fde5530b50ca68e7f4805cbf29ec113e13f528a8b85cd4a1eb` |
| `corporation/warStatisticMgrService.js` | `54d81629f93eafc508d6958f9114614c252371844f4582aeb0096ea8b52e8809` |
| `killmail/killmailState.js` | `920fca640077c858230c273b7fdb52bd5869196fd1d5ffad316ad27e871451cd` |

[CCP Wars](https://support.eveonline.com/hc/en-us/articles/115004152745-Wars) ·
[War Notifications](https://support.eveonline.com/hc/en-us/articles/203279981-War-Notifications) ·
[CCP ally registration fee schedule](https://www.eveonline.com/ko/news/view/inferno-1.1-changes-to-the-war-dec-system) ·
[UniWiki War declarations](https://wiki.eveuniversity.org/War_declarations)

### Automated validation

The integrated filter selected 555 cases across `Alliances`, `CapsuleCombatTests`, `CrimewatchTests`,
`RemoteAssistanceTests`, `PilotCombatTests`, `MissileCombatTests`, `CombatDroneTests`, `ExpandedCombatDroneTests`,
`InsuranceLossTests`, `StructureAttackTests`, `StructureWeaponTests`, `InteractionHistoryPersistenceTests`,
`CharacterInteractionHistoryTests`, `CorporationShowInfoTests`, `CorporationPublicInfoTests`,
`CorporationFinancialPanelTests`, `CommandBurstTests`, `ServerServicesTests`, and the exact schema 226 and 227
war upgrade tests. It passed 554 cases in 305.48 seconds. Two known logistics assertions from the initial wars
milestone were excluded.

The resident case of `FailedCapsuleSettlementRollsBackBodyTrainingHealthPaymentAndNotifications` reproduced its
failure on main `76298346`. The fixture now projects the seeded queue into the in-memory checkpoint base before testing
rollback. Both cases passed the exact retest, followed by 63 passing affected combat and report cases in 25 seconds.
The final solution build had zero warnings or errors. The final 20-case filter covering `CorporationWar`, the exact
capsule rollback and two fatal war loss cases, and the schema 227 upgrade passed in 15.08 seconds. Catalog setup
and formatting policy passed all 8 and 9 cases in 1.382 and 0.016 seconds respectively. The style, CSharpier,
line-width, local-link and diff checks passed. The full .NET suite was outside this validation scope.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

1. Accept a paid ally offer, inspect the defender and aggressor views, and check that combat becomes legal after the ally warmup.
2. Open a War Report after an opposing loss, filter by ship class, and open its killmail.
3. Accept surrender and confirm the payment, the cooldown and the peace treaty in the native window.

## War report side filters and former ally updates

War Report ship counts, ISK totals, ship-class graphs, and filtered lists require the recorded loss side to agree with
the client’s final-blow classifier. The rule also applies to archived reports. A loss with opposing damage and an
incompatible final blow still retains its original killmail and attribution data.

When withdrawing a war from a connected ally’s results, Eden sends the client’s war-removal delta on that connection.
The war continues to appear for organizations that still participate.

Inert analysis of the build 3662168 archive identified the client’s second side filter in
`warReport.pyj` (`_GetKillsScrollList`, offsets 193–286). It uses the final corporation and alliance IDs.
`evewar/util.pyj` handles the existing `warID` delta ending in Python None, and the client’s war lookup skips an
evicted war. The local EVE.js 261108.1 comparison used `warStatisticMgrService.js` and `killmailState.js`.
[UniWiki War Report](https://wiki.eveuniversity.org/War_declarations#War_Report)

### Automated validation

`DefensiveAllianceFormationRemovesWarFromFormerAllyConnection` verifies the decoded removal event for the ally and a
regular war update for the aggressor. `WarReportSideFiltersMatchTheClientBeforeAndAfterCheckpoint` verifies both
report sides, incompatible final blows, direct killmail links, and the same outcomes after reopening a checkpoint.

## Edit member private identity reply, November 16, 2026

The user's screenshot reports **Unsupported local call charMgr.GetPrivateInfo**. Static inspection of
prepared client build **3662168** identifies the caller as `corp_dlg_edit_member.pyj` `LoadChar`, which
requests `GetPrivateInfo(characterID)` before constructing the corporation Edit Member window.
`OnTabGeneral` reads `characterID`, `gender`, `raceID` and `createDateTime` from the returned object;
security status is fetched separately through the existing crimewatch service.

`CharacterSheetHandler` now handles that selected-pilot call and returns a named native Row containing
the saved identity fields plus bloodline and ancestry. It accepts one positive character ID, rejects extra
arguments/keywords, and restricts targets to the selected pilot or their current saved corporation.
The existing typed resident profile/corporation readers supply the reply. No wallet balance, account data,
new persistence path or state mutation is involved. The Row uses the existing explicit NEWOBJ adapter.

The inspected module is `eve/client/script/ui/shared/neocom/corporation/corp_dlg_edit_member.pyj` in
`E:/EdenDev/3662168/tq/code.ccp`; its decompressed SHA-256 is
**7a61ce1c6a7464beecdcb35267c2cbb117de9df1996c1df63f3292450b07a78a**, identical to the prepared client's
stock archive copy. References: [EVE University member management](https://wiki.eveuniversity.org/Managing_corporation_members),
[Carbon Blue Marshal.cpp revision fcbf0c2](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp),
and local evejs **261108.1** `server/src/services/character/charMgrService.js` (SHA-256
**82731fcebb67a53a6723f95464a7ffa838bc98c57344fa6c64b0e3a7a90858f1**). No evejs source was copied.

**Automated validation:** Tool/solution restore and solution build passed with zero warnings/errors;
the build took **9.61 seconds**. Catalog policy (**8 cases**), formatting policy (**9 cases**),
formatting/line-width verification and diff checks passed in **36.87 seconds**. No regression tests were
added or run, at the user's request. Native window behavior remains unverified after the fix.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** After restarting the updated server, open Corporation → Edit Member
and verify its General tab loads.

## Ordinary member auto-removal, November 15, 2026

Ordinary `corpRegistry.KickOutMember` requests for another pilot now defer departure while the subject is in space.
The authorized request records its corporation, requester, exact employment generation, original NPC destination and
request time. Duplicate valid requests preserve the original requester and timestamp; an authorized new request replaces
an already-invalid request. Docked subjects still depart immediately.
`KickOutMembers` commits mixed immediate departures and queued requests together: its native `kicked` list identifies
accepted requests, including subjects waiting to dock; ineligible and unknown targets remain in `notKicked`.
An unexpected failure rolls back all earlier departures, queue entries, employment and notifications in that batch.

The existing one-second corporation maintenance loop checks outstanding resident requests. It removes a docked subject
only if the original employment still matches and current removal permission still allows the action. Departure/rejoin,
closure, new CEO status, new authority-bearing roles/titles or shareholder protection, and loss of the requester's authority cancel
stale requests. Cancellation does not remove the member or emit a departure. Existing self-quit rules, role-removal delays,
shareholder-vote requirements and CEO protection remain unchanged. Self-quitting to the original NPC corporation continues
to take effect immediately, including in space; this milestone changes ordinary expulsions of other members.

`GetPendingAutoKicks()` now combines ordinary requests and sanctioned vote expulsions in one coherent resident read,
deduplicated by subject. It retains the existing Director gate and native list of `(characterID, kickedByCharacterID)`
tuples. Connected subjects retain their corporation until settlement; existing membership, session, count, chat and
bookmark-access notifications then follow the committed departure, without replay on later maintenance cycles.
Opening the panel also advances ready removals. No new client notification or Blue/transport/Destiny format is introduced.

Schema **167** adds `WorldCorporationRemoval` with an explicit resident mapping, corporation index and checkpoint projection.
Only outstanding requests are polled. Idle maintenance looks up their subjects' membership and docking state without
role/share/history scans, write transactions or projection; panel reads and settlement recheck complete permission.
Single removal and removal-permission reads now require the resident world, matching bulk removal and voting; affected
legacy tests were moved to resident worlds. There is no SQL gameplay implementation or offline removal fallback.
Schema creation/upgrades, initial loading and checkpoint projection remain persistence boundaries.
Requests survive restarts and continue waiting for docking. Eden has no scheduled daily-downtime removal sweep yet;
restarting the server does not forcibly expel a subject still in space.

References: [EVE University managing corporation members](https://wiki.eveuniversity.org/Managing_corporation_members)
describes role delays, docked removal and the Auto Removal list; its modern immediate self-quit guidance does not replace
Eden's existing role cooldown policy in this milestone. Inert inspection of pinned build **3662168** checked
`base_corporation.KickOut`, `KickOutMultipleMembers`, `GetPendingAutoKicks` and `corp_ui_member_autokicks.CorpAutoKicks.Load`.
The panel unpacks each pending row into the subject and requester's IDs. Prepared archive SHA-256:
`AC15883C14B8547F4846A74E125A0E30073880C4EDF55E214DCAED804A4A19C6`.
EveJS **261108.1** `corpRegistryRuntime.js` (SHA-256
`41D691387FF45DCE3CF4680DA40291557B868F0B3F589234D0EB244B77C4E35C`) was a behavioral comparison; its immediate
ordinary-removal implementation and stored pending-list stub do not establish Eden's queue semantics. No reference code
was copied, and no client code was executed.

**Automated validation.** Development started with exact filter
`FullyQualifiedName=Eden.Protocol.Tests.PilotSessions.CorporationRemovalQueueTests.NativeKickInSpacePersistsUntilDockingAndPublishesDepartureExactlyOnce`.
Thirteen new cases cover native single/bulk requests, duplicate timestamps, motion preservation, next-dock settlement,
connected notifications, exact employment generations, changed CEO/roles/shares/requester authority, combined sanctioned
and ordinary panel rows, rollback, schema-166 upgrade and checkpoint/reopen. Warmed queue creation, repeated waiting reads
and settlement assert zero SQL commands, connections and fitting calculations. Initial build checks caught test-analyzer
violations, corrected by awaiting checkpoints and using the filtered `Assert.Single` overload. A broader development run
passed 127 of 132 tests; the five failures required resident fixtures, whose affected rerun then passed 19 cases.
The added mixed-batch and duplicate-timestamp checks then passed their two exact filters.
The broader stable sequence then passed **163 tests** in **53.39 s**, tool/solution restore, a zero-warning build,
both Python suites (**8 catalog and 9 formatting tests**), formatting of **2,252 files**, width and diff checks in **113.23 s**.
A final review regression reproduced a lost authorized retry after revoking the original requester. Valid requests remain
idempotent; invalid requests can now be replaced by a newly authorized request. Its exact-method rerun passed.
After this correction and the idle-maintenance lookup refinement, the final sequence passed **164 focused tests** in
**54.09 s**, tool/solution restore, a zero-warning solution build, both Python policy suites, formatting of **2,252 files**,
the 140-column check and `git diff --check` in **102.22 s**. Documentation checks resolved **662 local paths** and verified
the auto-removal topic anchor. The full .NET suite was not run.

The final filter ORs `FullyQualifiedName~` matches for `CorporationRemovalQueueTests`, `CorporationBulkRemovalTests`,
`CorporationMembershipTests`, `CorporationRoleTests`, `CorporationExpulsionVoteTests`, `CorporationCeoElectionTests`,
`CorporationClosureTests`, `CorporationLeadershipTests`, `CorporationMemberAuditTests`, `CorporationOwnerNamesTests`,
`CorporationShareTests`, `CorporationStorageTests`, `SharedBookmarkServiceTests`,
`CorporationTractorAccessUsesCurrentMembershipAndStopsOnDeparture`, `CorporationVotingTests`, `CorporationBlueprintVoteTests`
and `WorldInitializationTests`, plus exact method
`Eden.Protocol.Tests.Market.ResidentEconomyTests.RegisteredResidentRowsCoverEveryScalarColumnOfTheirSavedModels`.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Expel a member in space and inspect Auto Removal; dock that member and verify their
corporation/session refresh and disappearance from the list.

## Blueprint lock and unlock votes, November 15, 2026

Native `voteManager.InsertVoteCase` now accepts type **5** (Lock Blueprint) and **6** (Unlock Blueprint), with item ID,
blueprint type ID and location in the existing binary option payload. A current CEO or Director must select one original
blueprint owned by their corporation, directly in a numbered division of a rented office. Copies, stacks of multiple
originals, installed blueprints, non-blueprints, personal assets, non-office locations, mismatched types/locations and
unauthorized proposals reject atomically. Both the office inventory ID and its station ID identify the same eligible
location; the native lock cache uses station IDs.

Starting lockdown immediately creates a resident hold. The standard weighted vote decides its outcome at the saved
deadline: a strict majority of cast weight makes it permanent without a sanction; defeat, ties and empty votes release
the temporary hold. An unlock vote requires that permanent lock and leaves it in place while open or pending sanction.
A successful unlock requires a separate, current, active CEO sanction within the existing seven-day local sanction
window. Failed or expired unlocks leave the original locked. Duplicate lockdowns and overlapping unlock votes/pending
sanctions reject. An unlock records its source lock's vote ID; it cannot unlock a replacement generation. Consumed
sanctions cannot apply twice, and a later unlock vote may follow a failed or expired one.

Schema **166** adds `WorldCorporationBlueprintLock`, keyed by item ID and indexed by corporation, with an explicit
resident mapping and checkpoint projection. The world owner commits holds, vote state and notifications together.
Inventory commit validation rejects changing a locked original's owner, office, division, type, quantity or original/copy
state, or deleting it, even through a service that does not consult the client lock cache. A rejected mixed withdrawal
rolls back every item and its events. In-place research attributes remain mutable. This milestone does not add corporation
industry jobs; that existing industry boundary remains. No SQL gameplay implementation or offline lock fallback is added.
SQL is limited to schema setup, initial resident loading and checkpoint projection.

`itemLocking.GetItemsByLocation(locationID)` returns an item-keyed dictionary of native KeyVals, and
`GetLockedItemLocations()` returns a native Python set of stations. Both reads use the caller's current resident membership
and division query rights; revocation takes effect immediately. Pending deadlines advance before reading. A named
`OnLockedItemChange(itemID, ownerID, stationID, isLocked)` notification invalidates connected members' native caches through
the existing corporation event cursor. Repeated advancement produces no duplicate transitions. The previous personal-
location-only empty `itemLocking` route is replaced by this owner. NPC members still receive empty committed-state reads.

Sources: [CCP Corporation Votes](https://support.eveonline.com/hc/en-us/articles/203268841-Corporation-Votes) specifies
the immediate temporary lock, automatic successful lockdown and CEO-sanctioned unlock;
[EVE University corporation logistics](https://wiki.eveuniversity.org/Corporation_logistics) describes blueprint governance.
Inert inspection of client build **3662168**, prepared `code.ccp` SHA-256
`AC15883C14B8547F4846A74E125A0E30073880C4EDF55E214DCAED804A4A19C6`, checked
`CorporationVotes.CreateVote`, the pending/active blueprint checks, and `itemLocking.GetLockedItemsByLocation`,
`GetLockedItemLocations`, `IsItemLocked` and `OnLockedItemChange`. EveJS **261108.1** `itemLockingService.js` (SHA-256
`3DC727F916A2D8569709BBF8433B72684DEB49933BE5E3B2AA202771C1B24FFD`) and its recorded voting runtime were behavioral
comparisons. Existing dictionary, KeyVal and Python set adapters use the recorded
[Carbon Blue revision fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp).
No client code execution or game launch was performed.

**Automated validation.** The exact development filter was
`FullyQualifiedName=Eden.Protocol.Tests.PilotSessions.CorporationBlueprintVoteTests.NativeLockAndUnlockVotesPublishLocksAndRequireOneCeoSanctionAfterRestart`.
The 22 new cases cover native requests, item dictionaries and location sets, notifications, current permissions,
immediate holds, weighted rejection/ties, permanent results, unlock consent and expiry, duplicate/stale sanctions,
invalid targets, installed originals, atomic withdrawal rejection, direct resident mutation/deletion rejection, in-place
research metadata, version-165 upgrade, open and permanent lock recovery, and successful checkpoint deletion after unlock.
Measured creation, casting, settlement and reads used zero SQL commands, connections and fitting calculations. Development
corrected a raw CLR set response to the existing native Python-set adapter and wrapped a checkpoint SQL line to meet the
140-column policy. A broader affected run passed **176 of 177 tests** in **50.01 s**. Its existing scalar-column audit
incorrectly compared SQL expressions with saved column names; the audit now normalizes quoted identifiers and the two
nullable-blob expressions before comparing distinct source columns. Its exact-method rerun passed.

The broader filter ORed `FullyQualifiedName~` matches for `CorporationBlueprintVoteTests`, `CorporationVotingTests`,
`CorporationCeoElectionTests`, `CorporationExpulsionVoteTests`, `CorporationStorageTests`, `CorporationRoleTests`,
`CorporationScreenTests`, `CorporationHangarStartupTests`, `CorporationClosureTests`, `PlayerTradeTests`, `PilotInventoryTests`,
`CargoContainerAssemblyTests`, `EconomicProjectionTests`, `ResidentEconomyTests`, `Eden.Protocol.Tests.Industry.IndustryTests`
and `WorldInitializationTests`.

After aligning new-save and upgrade foreign keys, final filter
`FullyQualifiedName~CorporationBlueprintVoteTests|FullyQualifiedName=Eden.Protocol.Tests.Market.ResidentEconomyTests.RegisteredResidentRowsCoverEveryScalarColumnOfTheirSavedModels`
passed **23 tests** in **13.65 s**. The final stable sequence passed tool/solution restore, a zero-warning solution build,
both Python policy suites (**8 catalog and 9 formatting tests**), formatting for **2,245 files**, the 140-column check and
`git diff --check` in **56.97 s**. Documentation checks resolved **659 local paths** and verified the blueprint topic anchor.
The full .NET suite was not run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Propose a lockdown, inspect the locked icon and attempt withdrawal; inspect a successful
unlock under Sanctionable Actions and enact it as CEO; reconnect and inspect the office lock state.

## CEO elections, November 15, 2026

`voteManager.CanRunForCEO()` and native type **0** `InsertVoteCase` now support shareholder-initiated CEO elections.
The candidate must propose themselves, belong to an active player corporation, accept roles, have Corporation Management
injected and personally hold at least **5%** of issued shares. Corporate holdings represented by a CEO do not satisfy the
personal-share requirement. Exact integer comparison avoids overflow and rounding. Forged candidates or auxiliary action
parameters, ineligible members and a second election during an unresolved authority hold reject without opening a case.

Opening the vote suspends the incumbent's effective roles through the existing vote hold, preserving saved assignments
until the outcome is known. This also blocks CEO-only role grants, treasury share representation, sanctions, dividends,
voluntary succession and closure; storing the CEO ID is not sufficient authority. Personal shareholders retain their own
ballots, including the incumbent. Existing weighted, frozen voting rights and strict majority-of-cast-weight rules apply.
Ties and empty or defeated elections restore the incumbent once the saved vote deadline passes. The current CEO can still
read the case while suspended even without personal shares.

A successful vote automatically changes the CEO at its deadline without a sanction. The candidate must still be the same
membership, have the injected skill, accept roles and be free of another authority hold. Departure or departure/rejoin
cannot inherit an old election. The replaced CEO's assigned and title roles are cleared, and the existing role-removal
cooldown begins. Corporation identity and both role changes publish with the vote outcome in one resident transaction.
The new CEO title is immediate; the new CEO's effective authority remains held until **three days after the vote deadline**.
This explicit local delay interprets CCP's unspecified "few days" policy. Late advancement applies already-expired delays
immediately. Saved deadlines survive checkpoint/reopen and callbacks cannot apply succession or activation twice.

All voting reads, mutations, pending expulsions and deadline maintenance now require the active resident world. They no
longer use the legacy SQL action fallback. Existing vote/member-access JSON records gain an optional typed election record;
schema **165** needs no new table or migration, and old general/share/expulsion votes retain their behavior.

References: [EVE University corporation logistics](https://wiki.eveuniversity.org/Corporation_logistics),
[CCP CEO Change](https://support.eveonline.com/hc/en-us/articles/203268861-CEO-Change) and
[CCP CEO and Directors](https://support.eveonline.com/hc/en-us/articles/203279841-CEO-and-Directors). The CCP pages disagree
on exactly 5% ("5% or more" versus "more than 5%"); Eden follows CEO Change and EVE University. Inert inspection of pinned
build **3662168** `CorporationVotes.CanRunForCEO` checks `skills.GetSkill`, not a trained-level threshold, before the
zero-argument remote call. `CorporationVotes.CreateVote` sends the candidate's own ID in the binary option payload.
The prepared archive hash is recorded in the blocker correction below. EveJS **261108.1** `voteRuntimeState.js` (SHA-256
`65D62D89ED4FA4553686A10A457DA863D67280DA9D7963F37C9A01B19A1D1DED`) was a behavioral comparison; its any-share
eligibility and immediate successor authority do not establish Eden's rules. Existing adapters reuse the previously
recorded Carbon Blue revision; no serialization or Destiny shape changed.

**Automated validation.** Development began with exact filter
`FullyQualifiedName=Eden.Protocol.Tests.PilotSessions.CorporationCeoElectionTests.NativeElectionSuspendsIncumbentTransfersLeadershipAndRestoresSuccessorAfterRestart`.
Nine new cases cover native eligibility and proposal calls, personal/share thresholds, injected-skill and role preferences,
incumbent suspension and bypass rejection, personal ballots and ties, failed/empty votes, exact deadlines, late callbacks,
repeated settlement, saved-role clearing, candidate departure/rejoin and checkpoint/reopen. The measured resident
creation/casting/late-settlement path performs zero SQL commands, connections or fitting calculations.
The final sequence passed tool/solution restore, a zero-warning solution build, **117 focused tests** in **36.97 s**,
both Python policy suites (**8 catalog and 9 formatting tests**), formatting for **2,238 files**, the 140-column check and
`git diff --check` in **79.99 s**. Documentation checks resolved **615 local paths** and verified the election anchor.
The final `FullyQualifiedName~` filter joined `CorporationCeoElectionTests`, `CorporationVotingTests`,
`CorporationExpulsionVoteTests`, `CorporationLeadershipTests`, `CorporationRoleTests`, `CorporationMembershipTests`,
`CorporationClosureTests`, `CorporationScreenTests`, `CorporationShareTests`, `CorporationMemberAuditTests`,
`CorporationHeadquartersTests`, `CorporationHangarStartupTests` and `CorporationWalletTests`.
The full .NET suite was not requested or run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Open Run for CEO with an eligible shareholder and inspect role changes on both connected
pilots; inspect the saved vote and successor authority after the delay.

## Administration and inventory blockers, November 15, 2026

The maintainer supplied an Administration panel stuck behind `Unsupported local call corpmgr.AuditMember`, a character
header showing three days in a corporation while its employment row showed one day, an unsupported
`corpStationMgr.MachoBindObject (MoveCorpHQHere)` dialog, and a corporation hangar permission denial while playing its CEO.
The local Death Scout game log dated November 15 also records `invbroker.MachoBindObject (GetInventory)` at 08:30:43 UTC,
followed by the hangar denials at 08:31:26 and 08:31:36 UTC. The precise clicked inventory entry was not established.

`corpmgr.AuditMember(memberID, fromDate, toDate, rowsPerPage)` now returns the native tuple of event and role-history
CRowsets, including both rowsets for empty results. The resident reader requires current Director/CEO or Auditor authority,
filters the inclusive-start/exclusive-end interval, and bounds each stream to the requested page size. Employment records
provide dated corporation creation, join and leave events for the caller's corporation. New member-role edits and effective
title-role changes retain their issuer and timestamp in the existing checkpointed corporation change journal. Searches
work for either the affected member or issuer without counting notification fan-out more than once. Pre-existing role
events lack trustworthy timestamps and issuers and are not fabricated into dated audit records. The native role payload
combines location masks; changes that only move the same permission between locations, vanity-title history and complete
legacy administrative history remain outside this correction. No new schema or SQL gameplay fallback is added.

`GetPublicInfo3.startDateTime` now reports the current corporation membership's latest employment start from the same
resident snapshot as the public profile. Character birth remains `GetPublicInfo.createDateTime`. Rejoining the same
corporation selects the new membership, and checkpoint reopen retains the date. NPC profiles without saved membership
history keep their existing creation-date fallback.

The station-bound zero-argument `MoveCorpHQHere` call now moves headquarters to the actor's current rented office. It
requires current CEO/Director authority and docking at a station. Station/system identifiers and a named administration
event commit together in the resident world, and `OnCorporationChanged` carries the native `stationID` and `solarSystemID`
old/new pairs. Repeated moves to the same headquarters emit no changes; forged locations, missing offices, undocked pilots
and unauthorized actors reject. Office contents and corporation money are unchanged by the move.

Build 3662168 includes **Goals Deliveries (flag 184, division index 14)** in `flagCorpSAGs`. Eden previously recognized only
the seven numbered flags **115–121**: its permission helper rejected flag 184 before considering Director authority, and
its native `ListByFlags` adapter rejected the combined list. The read paths now recognize this native entry with deliveries
query permissions. Invalid flags and unauthorized/revoked members remain rejected. The separate corporation Deliveries
container **10012** now binds and lists real resident station inventory under market-deliveries flag **62**, with current
corporation and query-authority checks on every read. This adds reads, not corporate market trading, project delivery
mutations or withdrawal actions.

Sources: inert inspection of prepared client build **3662168**, `code.ccp` SHA-256
`AC15883C14B8547F4846A74E125A0E30073880C4EDF55E214DCAED804A4A19C6`, particularly
`CorpAuditing.ShowJournal`, `CharacterInfoWindow.construct_header_character`, `StationController.SetHQ`,
`inventorycommon.const`, `BaseCorpContainer` and `invCache.GetInventory`/`invCacheContainer.List`.
[EVE University member management](https://wiki.eveuniversity.org/Managing_corporation_members) describes role auditing;
[corporation logistics](https://wiki.eveuniversity.org/Corporation_logistics) describes moving headquarters to an office.
EveJS **261108.1** `corpmgrService.js` (SHA-256
`0CEAC2C9C0982F00E714A1F17700ACC8A968FFC4EF9C83CCB763B51D8BD1368F`) supplied an older-client response comparison.
Native DBRow/CRowset adapters reuse the recorded [Carbon Blue revision fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp).
No client code was executed and no game was launched by the agent.

**Automated validation.** Exact development filters targeted
`CorporationMemberAuditTests.NativeAuditReturnsTwoRowsetsAndSubsequentCorporationReadsSucceed`,
`CorporationHeadquartersTests.NativeEmbeddedHeadquartersCallPublishesLocationAndPersistsOnce` and
`CorporationHangarStartupTests.CeoAndDirectorCanReadAllNativeHangarsIncludingGoalsAndBindCorporateDeliveries`.
An initial stale exact role-test name matched no tests; subsequent development used the named regressions. Development
corrections fixed an unsupported CLR short in a wire model and assertions that tried to round-trip outbound-only
CRowset/object-reference reductions through the restricted inbound decoder. The inbound decoder was not widened.
The final sequence passed tool/solution restore, the zero-warning solution build, **131 focused tests** in **40.58 s**,
both Python policy suites (**8 catalog and 9 formatting cases**), formatting for **2,236 files**, the 140-column check and
`git diff --check` in **80.50 s**. Documentation checks resolved **614 local paths** and verified the new protocol anchor.
The focused filter joined `CorporationMemberAuditTests`, `CorporationHeadquartersTests`, `CorporationHangarStartupTests`,
`CorporationRoleTests`, `CorporationStorageTests`, `CorporationWalletStartupTests`, `CorporationAdministrationTests`,
`CorporationMembershipTests`, `CorporationShowInfoTests`, `CorporationPublicInfoTests`, `CharacterSheetServiceTests`,
`AgentDirectoryServiceTests`, `DockedServicesTests` and `CorporationScreenTests` with `FullyQualifiedName~` clauses.
Coverage includes native embedded binding, real resident item reads, CEO/Director access and immediate revocation, empty
audit rowsets, timestamp boundaries, no-op and rejected edits, role-history and headquarters checkpoint reopen, membership
departure/rejoin dates and unchanged character birth. The measured audit reader used zero SQL commands, connections or
fitting calculations. The full .NET suite was not requested or run.

**Live acceptance.** Not performed; agent client testing is disabled. Screenshots and the game log establish the original
failures; corrected native rendering and the exact clicked hangar entry remain unverified.

**Optional maintainer checks.** Update/restart the server and reconnect; open Administration/Auditing and compare the
character membership header with employment history; move headquarters and open numbered hangars, Goals Deliveries and Deliveries.

## Bulk member removal, November 15, 2026

`corpRegistry.KickOutMembers(characterIDs)` now returns the native dictionary with Python lists under `kicked` and
`notKicked`. A current CEO or Director can remove eligible members together. Unknown pilots, foreign members, the CEO,
members with effective or grantable roles (including title roles), members within the role-removal cooldown, and protected
shareholders remain unchanged and appear in `notKicked`. Targets are deduplicated in request order. Empty, oversized,
malformed, unauthorized and self-containing requests reject before any changes. The 1,000-target cap is Eden's local
request limit, matching the existing bounded member lookup.

The operation requires the active resident world and uses a single economy transaction under its owner. Membership,
employment, member counts, wallet-selection and member-access cleanup, bookmark access and ordered corporation events
commit together and use the existing checkpoint mechanism. A failed destination lookup rolls back earlier departures
and their notifications. Personal property, money and share holdings stay with the departing member. There is no saved-world
SQL implementation or offline fallback for this call. Original NPC corporation names come from the immutable catalog cache.

This follows the existing ordinary-removal policy, including immediate membership changes for pilots in space. Deferring
ordinary removals until docking is separate remaining work; the docked-removal queue implemented for sanctioned expulsion
votes is unchanged. The new batch call does not claim the full corporation window is complete.

Sources: [EVE University member management](https://wiki.eveuniversity.org/Managing_corporation_members) establishes role
removal and cooldown behavior; [corporation logistics](https://wiki.eveuniversity.org/Corporation_logistics) establishes
shareholder protection. Previously captured inert build **3662168** disassembly of
`base_corporation.KickOutMultipleMembers` confirms the single list argument, Director/self checks and both result keys.
The local EveJS **261108.1** `corpRegistryRuntime.js` comparison (SHA-256
`41D691387FF45DCE3CF4680DA40291557B868F0B3F589234D0EB244B77C4E35C`) agrees on the result shape. Existing dictionary/list
adapters follow [Carbon Blue Marshal.cpp revision fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp),
`ReadObjectDict` and `ReadObjectList`; no codec behavior changes or client-script execution were needed.

**Automated validation.** Development began with the exact filter
`FullyQualifiedName=Eden.Protocol.Tests.PilotSessions.CorporationBulkRemovalTests.NativeBatchReturnsMixedListsAndPersistsMembershipAndOrderedNotifications`.
The ten new cases cover native list/dictionary shapes, real mixed batches and duplicates, repeated calls, malformed and
unselected requests, current Director authority and revocation, role/title/grantable-role/cooldown/shareholder protections,
rollback, ordered notifications and resident checkpoint reopen. The measured resident removal executes zero SQL commands,
opens zero connections and performs zero fitting calculations. Initial development failures corrected a duplicate catalog
fixture insertion, an incomplete notification expectation and an invalid grantable-role fixture value.

The final sequence passed tool/solution restore, a zero-warning solution build, **100 focused .NET tests** in **42.77 s**,
both Python policy suites (**8 catalog and 9 formatting tests**), formatting for **2,226 files**, the 140-column check and
`git diff --check` in **85.79 s**. Documentation checks resolved **656 local paths** and verified the new protocol anchor.
The final filter was
`FullyQualifiedName~CorporationBulkRemovalTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~CorporationRoleTests|FullyQualifiedName~CorporationExpulsionVoteTests|FullyQualifiedName~CorporationLeadershipTests|FullyQualifiedName~CorporationClosureTests|FullyQualifiedName~CorporationScreenTests|FullyQualifiedName~CorporationWalletTests|FullyQualifiedName~SharedBookmarkServiceTests|FullyQualifiedName~SharedBookmarksTests`.
The full .NET suite was not requested or run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Select multiple eligible and protected members, run Expel Members and inspect the result dialog.

## Corporation Show Info market activity latency, November 15, 2026

The reported Royal Khanid Navy window drew its logo, headquarters, and faction membership, with the tabs still blank.
The deployed host held the resident world owner in `corporationSvc.GetCorpInfo` for **18.26 s** at 06:40:31 UTC
and **15.57 s** at 06:42:23 UTC. In pinned client build **3662168**, `Info.UpdateDataCorp` waits for this NPC market
activity reply before the information window constructs its tabs. The pinned catalog confirms Royal Khanid Navy
**1000156**, headquarters **60013933**, and faction **500008**. EveJS **261108.1**
(`server/src/services/corporation/corpService.js`, SHA-256
`A9EF788E32125B80E5725D00C919BF837365CB5187954C93068F765CB8212D6A`) also treats the reply as iterable market
activity. See [EVE University's corporation Show Info path](https://wiki.eveuniversity.org/Datacore_Farming).

The resident query evaluated a deferred join and grouping for each execution. It now collects the corporation's
NPC order IDs once using the owner index, restricts executions with the order index and counterparty ID, and takes
the highest execution ID for each item and direction. The native nine-column Rowset still carries prices in ISK
and Python None for a missing buy or sell. The service reads committed resident state, including after checkpoint
reopen. It reuses the existing [Carbon Blue marshal format](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp).

**Automated validation.** The exact development filter was
`FullyQualifiedName=Eden.Protocol.Tests.PilotSessions.CorporationShowInfoTests.MarketActivityReadDoesNotScaleWithUnrelatedResidentExecutions`.
With 2,000 unrelated executions, the original lookup took **2,604 ms** and allocated **3.32 GB**.
The same warmed lookup after the fix took **0.27 ms** and allocated **38 KB**, with zero SQL commands, connections,
or fitting calculations. The two focused development cases also verified native reply values and reopen behavior.

The final validation sequence completed in **52.44 s**. Restore, the zero-warning solution build, **68 focused .NET
tests** in **11 s**, both Python policy suites (**8 catalog and 9 formatting cases**), and formatting and width
checks for **2,209 files** passed. The final populated lookup took **0.38 ms**. The initial combined run hit a
Windows seed-directory rename error. Moving test data to the repository drive let the run reach the assertions,
where one argument-validation case still used an offline fixture. The final run uses a resident fixture for those calls.

Final filter:
`FullyQualifiedName~CorporationShowInfoTests|FullyQualifiedName~CorporationPublicInfoTests|FullyQualifiedName~CorporationScreenTests|FullyQualifiedName~StationDirectoryTests|FullyQualifiedName~ResidentQueryIndexTests|FullyQualifiedName~Eden.Protocol.Tests.Market.MarketTests.`.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** After updating the server, open Royal Khanid Navy's Show Info and confirm the tabs appear.

## Member-expulsion votes, November 15, 2026

`voteManager.InsertVoteCase` accepts native type 3 (Expel Member), with the target pilot in the binary option parameter.
Opening the vote suspends that member's effective roles, including title and grantable roles, without destroying their saved
assignments. Role edits and voluntary CEO succession cannot bypass the suspension. A failed vote restores authority;
a successful vote retains the suspension until its CEO sanction or the existing seven-day local sanction expiry.
Ordinary expulsion rejects a shareholder whose holding exceeds the average per positive shareholder, using exact integer
comparison. Voluntary departure remains possible after the normal role-removal cooldown.

A sanctioned docked member returns to their original NPC corporation atomically, retaining personal shares and property.
If the member is in space, the sanctioned removal waits until docking. The pending state survives restart and sanction
expiry; `corpRegistry.GetPendingAutoKicks()` exposes native `(characterID, kickedByCharacterID)` tuples to Directors/CEO.
Host maintenance inspects only cached pending vote IDs while waiting, and performs the normal membership/access/bookmark
transition once the member docks. This docking trigger is Eden's local scheduling policy; it does not model Tranquility's
daily downtime. A departure/rejoin cancels the old membership's pending removal and cannot resurrect its old roles.
Existing JSON records gain optional fields; schema 164 remains compatible with earlier saved votes and role assignments.

Sources: [EVE University corporation logistics](https://wiki.eveuniversity.org/Corporation_logistics),
[managing members](https://wiki.eveuniversity.org/Managing_corporation_members) and
[CCP Corporation Votes](https://support.eveonline.com/hc/en-us/articles/203268841-Corporation-Votes).
Inert pinned build **3662168** inspection of `corporation/voting.pyj`, `corp_ui_votes.pyj`,
`base_corporation.pyj.GetPendingAutoKicks` and `corp_ui_member_autokicks.pyj.Load` established wire arguments and row shape.
The archive hash is recorded in the earlier corporation records; no codec behavior changed.

**Automated validation.** Tool/solution restore and solution build passed. The focused filter joining
`CorporationExpulsionVoteTests`, `CorporationVotingTests`, `CorporationRoleTests`, `CorporationMembershipTests`,
`CorporationLeadershipTests`, `CorporationMemberQueryTests`, `CorporationClosureTests` and `CorporationScreenTests`
passed **78 tests in 25 seconds**. Both Python policy suites passed (8 and 9 tests); formatting checked 2,205 files and
the 140-column limit; documentation links and `git diff --check` passed. The final sequence took **68.23 seconds**.
An earlier docking regression exposed a test fixture writing a navigation property instead of using the resident position
operation; it passed after the fixture used `SqliteWorld.SetPosition`. Coverage includes exact share thresholds, invalid
targets, role suspension/restoration, rejected edits, single-use sanctions, native pending rows, docking after restart,
and departure/rejoin. The full .NET suite was not run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Run an expulsion vote and inspect suspended/restored roles. Sanction a passed vote for a
member in space, then verify removal after docking.

The maintainer requested stopping after this feature. CEO elections, blueprint votes and other remaining standalone work
are still pending; this milestone does not claim completion of the entire corporation window.

## General and share-creation votes, November 15, 2026

`voteManager` now serves native voting capability, case lists/single cases, options, the caller's ballots, vote creation,
casting, sanction lists and sanction activation. Case lists are status-keyed dictionaries of native CRowsets, options and
ballots are KeyVal lists, and sanctions are keyed by case ID. NPC members have no voting capability; CEOs, Directors and
represented shareholders can read eligible player-corporation votes. General polls and share-creation votes are enabled.
CEO elections and member/blueprint sanctions are separate follow-on work and reject until implemented.

Schema 164 saves typed vote definitions, deadlines, ownership snapshots, ballots and sanction state. Each share represents
one vote. An option needs strictly more than half of cast weight; ties and empty polls have no winner. Eden freezes the
owner/weight snapshot when a vote opens, so transferring shares cannot multiply their voting power. A current CEO may cast
the rights belonging to their corporation; one owner cannot vote twice, including across CEO changes. These snapshot rules
are local anti-replay policy. New shares require a successful vote and a separate current-CEO sanction. Supply, treasury
holdings, saved action state and notifications commit together; overflow or repeated activation leaves them unchanged.
Public corporation rows report the actual issued supply.

The pinned limits are 1,000 open cases, 20 recent closed cases, 50 options and 30 days. Eden bounds titles/descriptions/options
and uses a seven-day sanction window as a local expiry policy. Host maintenance processes saved deadlines, emits case/action
notifications once, and skips world access until the next known deadline. Restart recomputes that deadline from saved votes.
The existing corporation change cursor carries named vote events, including to personal shareholders and corporate-holder
CEOs outside the issuer. Existing foreign-shareholder reads now authorize those corporate representatives.

Sources: inert build **3662168** inspection of `corporation/voting.pyj`, `corp_ui_votes.pyj`, the native voting constants and
the previously recorded archive hash. [EVE University corporation logistics](https://wiki.eveuniversity.org/Corporation_logistics)
provides the shareholder workflow; [CCP Corporation Votes](https://support.eveonline.com/hc/en-us/articles/203268841-Corporation-Votes)
defines share weights, majority and CEO sanctioning. EveJS 261108.1 was consulted for response-field comparison only;
no implementation was copied. Native CRowsets reuse the existing Market adapter pattern and recorded Carbon Blue revision.

**Automated validation.** Tool/solution restore and solution build passed. The focused filter joining
`CorporationVotingTests`, `CorporationScreenTests`, `CorporationShareTests`, `CorporationLeadershipTests`,
`CorporationPublicInfoTests`, `CorporationClosureTests` and `WorldMigrationTests` passed **61 tests in 21 seconds**.
Both Python policy suites passed (8 and 9 tests); formatting checked 2,202 files and the 140-column limit;
documentation links and `git diff --check` passed. Coverage includes native wire forms, permission failures, frozen weights,
duplicate ballots, exact deadline closure, repeated polling, overflow rollback, sanction expiry, migration and restart.
The full .NET suite was not run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Open Votes as CEO and shareholder. Complete a short poll and sanction a passed share-creation vote.

## Corporation closure, November 15, 2026

The native `ResignFromCEO(None)` flow now closes a corporation when no eligible successor exists. The pinned client asks
for confirmation before that call. The server independently requires its current CEO to be docked and rechecks successor
eligibility inside the transaction. All members return to their first recorded NPC corporations, lose corporation access,
and receive normal membership/session changes. Open applications become rejected, recruitment ads are removed, and rental
notifications invalidate office lists. Any failure rolls back the complete operation, including already moved members.

Schema 163 records closure time while retaining the last CEO, founder, identity, financial history, shares and asset
locations. Closed office records preserve inventory references but no longer consume station rental slots or appear as
tenants. Remaining property and balances stay with the closed corporation and cannot be inherited or withdrawn by former
members. This is Eden's archival policy; it does not transfer or silently destroy assets. The former CEO can found another
corporation. Names may be reused after closure; tickers remain reserved. Both resident uniqueness checks and filtered SQLite
indexes enforce these rules. Public Show Info returns `deleted=True` and the historical CEO ID: the pinned portrait code
still reads that ID without a deleted-state guard.

Sources: inert build **3662168** inspection of `base_corporation.ResignFromCEO` and
`corporationInfoWindow.construct_ceo_picture_info`, using the pinned archive recorded above.
[EVE University formation](https://wiki.eveuniversity.org/Forming_a_corporation) covers closure and identity reuse;
[CCP CEO and Directors](https://support.eveonline.com/hc/en-us/articles/203279841-CEO-and-Directors) describes the no-successor
warning and removal of remaining members. [CCP closure guidance](https://support.eveonline.com/hc/en-us/articles/203209062-Corporation-Alliance-Closure)
states that remaining corporation assets become irrecoverable. Existing Blue adapters and the recorded Carbon revision are unchanged.

**Automated validation.** Tool/solution restore, solution build (zero warnings/errors), both Python policy suites
(8 catalog and 9 formatting tests), formatting verification (2,186 files), and documentation/diff checks passed.
Filter `FullyQualifiedName~CorporationClosureTests|FullyQualifiedName~CorporationLeadershipTests|FullyQualifiedName~CorporationStorageTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~PlayerCorporationTests|FullyQualifiedName~CorporationShowInfoTests|FullyQualifiedName~CharacterDeletionTests`
passed 107 tests in 31 seconds; the required sequence took 72.42 seconds. Additional dependency filter
`FullyQualifiedName~CorporationPublicInfoTests|FullyQualifiedName~WorldMigrationTests` passed 18 tests in 11 seconds.
Regressions exercise file/resident closure, role/session updates, retained property, office slots, successor/permission
checks, application/ad cleanup, transaction rollback, migration and restart. Seven older creation/Show Info session cases
now use a resident test world, matching main's Abyssal session prerequisite. After merging main through `ff5c5495`, the
combined 125-case filter passed 123 and exposed the same fixture issue in two additional public-info session cases.
Both corrected cases passed the exact `FullyQualifiedName~ShowInfoReturnsPublicFieldsThroughTheSessionForNpcAndOtherPlayerCorporations`
filter (4 seconds), with a clean build and formatting verification. No full .NET suite was run.

The final main integration includes the independently developed mutaplasmid schema, which also used version 162 on its
branch. Version 163 identifies the combined schema; both idempotent migrations accept saves from either branch.
With main through `051f4395`, the combined corporation/migration filters above plus `FullyQualifiedName~MutaplasmidTests`
passed **141 tests in 39 seconds**. Tool/solution restore, clean solution build, both Python suites, formatting verification
(2,194 files) and documentation/diff checks all passed; the final sequence took **77.60 seconds**.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Close a disposable corporation with no eligible successor. Inspect its Show Info and former members.

## Dividends and CEO succession, November 15, 2026

`corpRegistry.PayoutDividend` accepts the native integer mode (one for shareholders, zero for members) and a total ISK
amount. Only the current CEO may spend the master wallet this way. Member payments divide equally; shareholder payments
use current positive holdings, including corporation holdings. Eden allocates whole cents by largest remainder with owner
ID as the deterministic tie-breaker. This local rounding policy conserves the total exactly, even at Int64 balance limits.
Debits, all recipient credits and journal reference 38 commit together; recipient overflow rolls everything back.

Despite its name, `GetNumberOfPotentialCEOs` returns a list of eligible member IDs. The native resignation flow first calls
`ResignFromCEO(None)` for that list, then passes the selected member ID. Transfer requires a docked current CEO, current
membership, injected Corporation Management and acceptance of roles. Stored roles remain intact while CEO powers override
them; resignation restores those roles and starts the role-removal cooldown. The saved CEO, per-member role events and
`OnCorporationChanged` CEO pair publish together. The caller refreshes its identity before the reply; other sessions follow
the existing corporation event cursor. No schema change is needed. Closure with no successor remains a separate milestone.

Sources: inert build **3662168** inspection of `corp_ui_home.PayoutDividendForm`, `base_corporation.ResignFromCEO`,
`bco_members.GetNumberOfPotentialCEOs` and `appConst.refCorporationDividendPayment`, using the pinned archive recorded above.
[EVE University corporation formation](https://wiki.eveuniversity.org/Forming_a_corporation) describes succession and closure.
[CCP CEO and Directors](https://support.eveonline.com/hc/en-us/articles/203279841-CEO-and-Directors) explicitly permits an
injected, untrained management skill; this newer clarification takes precedence over the older CEO Change article's level-I
wording. Existing scalar/list/None adapters are unchanged from the recorded Carbon Blue revision.

**Automated validation.** Tool/solution restore, solution build (zero warnings/errors), both Python policy suites
(8 catalog and 9 formatting tests), formatting verification (2,184 files), and documentation/diff checks passed.
After integrating main through `a5c9e350`, filter
`FullyQualifiedName~CorporationLeadershipTests|FullyQualifiedName~CorporationShareTests|FullyQualifiedName~CorporationWalletTests|FullyQualifiedName~CorporationRoleTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~CorporationAdministrationTests`
passed 75 tests in 25 seconds; the complete sequence took 63.01 seconds. Regressions cover native payments and candidate
lists, exact allocation, rollback, permissions, role blocking, repeated succession, notifications and restart persistence.
No full .NET suite was run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Pay a small member/shareholder dividend. Transfer CEO between two docked pilots and inspect roles.

## Member search and tracking, November 14, 2026

`corpRegistry.GetMemberIDsByQuery` parses the pinned list-of-lists query: three values in its first term and four values
(including AND/OR) in subsequent terms. It accepts only the twelve native fields and their permitted operators. Names use
case-insensitive contains, equality, prefix/suffix or bounded SQL-LIKE matching (`%` and `_`); no supplied SQL or regular
expression is executed. The local evaluator gives AND precedence over OR. Empty queries return no IDs. Implied-role
search includes title roles and Director powers; title-name search adds assigned title names and the member's title to
name matching. Results contain only current corporation members. The above-average-shareholder lookup compares individual
member holdings to total issued holdings divided by current membership, using exact integer arithmetic.

Full and simple tracking now return the fields consumed by `CorpMemberTracking.ShowMemberTracking`. Full tracking requires
CEO/Director authority. Simple tracking omits locations, hulls and exact session timestamps. The native `lastOnline` field
is elapsed hours, with a negative value for online members and None for unknown history. Private Abyssal system IDs are
redacted from full tracking. Corporation membership dates, roles, titles and bases come from saved authoritative state.

Schema 161 adds per-pilot login/logout history with resident checkpoint projection and pilot-deletion cleanup. The existing
station-presence lifecycle records selection and disconnect once per transition; routine presence polling does not write
history. Unknown pre-upgrade timestamps stay unknown. If a crash prevents logout recording, offline elapsed time uses the
last recorded session event; it does not invent a shutdown timestamp. Simultaneous connections retain the latest observed
connection timestamps while shared presence determines whether any connection remains online.

Sources: inert build **3662168** inspection of `corp_ui_member_find` operator/property definitions and query construction,
`base_corporation.GetMemberTrackingInfo`, and `corp_ui_member_tracking.ShowMemberTracking/GetLastLoggedOnText`, using the same
pinned archive hash recorded above. [EVE University member management](https://wiki.eveuniversity.org/Managing_corporation_members)
describes role, title and tracking workflows. Local EveJS 261108.1 `corpMemberQueryState.js` was consulted as a behavioral
comparison, including its recorded empty-query observation; no implementation was copied.

**Automated validation.** Tool and solution restore, solution build (zero warnings/errors), both Python policy suites
(8 catalog and 9 formatting tests), formatting verification (2,177 files), and 628 documentation links/anchors passed.
After integrating main through `5a5a9e79`, the filter
`FullyQualifiedName~CorporationMemberQueryTests|FullyQualifiedName~CorporationRoleTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~CorporationShareTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~OnlineStatusTests|FullyQualifiedName~CharacterDeletionTests`
passed 93 tests in 35 seconds; the complete final sequence took 72.67 seconds. Regressions cover native query shapes,
role/title switches, malformed conditions, exact share thresholds, tracking privacy, online/offline transitions,
disconnect cleanup, migration and restart. No full .NET suite was run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Search members by role/title. Compare member tracking as CEO and an ordinary member.

## Recruitment advertisements, November 14, 2026

The standalone recruitment panel now owns all eight pinned `corpRecProxy` calls: create, update, delete, recruiter lookup,
single-ad lookup, corporation public listings, corporation management listings and criteria search. Management returns
native DBRow lists; public listings/search return `(grade, DBRow)` pairs. Single-ad lookup returns one DBRow. Search grades
count matching advertised selections; deterministic ties use ad ID. Eden's matching policy requires overlap in each requested
activity, area, language and time category, all requested other flags, and the requested membership, tax and skill-point limits.
This is a local search policy; no Tranquility ranking formula was available. Alliance exclusion is satisfied by the current
standalone corporation model, which has no alliance membership.

Personnel Managers, Directors and CEOs may manage ads. The master wallet pays the pinned 500,000 ISK creation fee plus
250,000 ISK per day; extensions charge only the daily fee. Editing without extension is free and deletion gives no refund.
Payment, journal reference 86, saved advertisement and member cache invalidation commit together. Unknown/foreign ads,
insufficient funds, malformed masks, nonmembers as recruiters and limit violations leave state unchanged.

Build-3662168 limits are three active ads, 28 days, six recruiters, 40 title characters, 1,000 description characters,
four activities from one activity group, two operating areas and eight active hours. Recruiter lookups exclude pilots who
have since left. Public reads exclude ads at their exact expiry; management can extend an expired ad. Creating a new ad
removes expired entries. Saved global IDs never reuse deleted IDs. Schema 160 adds the advertisement table and resident
checkpoint projection. A typed revision in corporation details uses the existing durable member event stream to emit
the native zero-argument `OnCorporationRecruitmentAdChanged` notification.

Sources: inert inspection of build **3662168** archive SHA-256
**37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**, `base_corporation`, `bco_recruitment`,
`corpRecruitmentContainer`, `recruitmentEntry`, `evecorporation.recruitmentUtil`, `corp_ui_recruitment_const` and `appConst`.
The current native create request has nine positional arguments and omits the older second timezone mask; update has eight
positional arguments plus `minSP` and `otherMask` keywords. Search has eleven positional arguments and fractional tax rates.
[EVE University recruitment](https://wiki.eveuniversity.org/Recruiting_corporation_members) and
[CCP recruitment changes](https://www.eveonline.com/news/view/corporation-recruitment-changes) support the fee, duration and
recruiter workflow. Existing Blue DBRow encoding is reused unchanged.

**Automated validation.** Tool and solution restore, solution build (zero warnings/errors), both Python policy suites
(8 catalog and 9 formatting tests), formatting verification (2,164 files), and 625 documentation links/anchors passed.
The filter `FullyQualifiedName~CorporationRecruitmentTests|FullyQualifiedName~CorporationCommunicationTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~CorporationAdministrationTests|FullyQualifiedName~CorporationRoleTests|FullyQualifiedName~CorporationWalletTests|FullyQualifiedName~WorldMigrationTests`
passed 79 tests in 27 seconds; final validation took 65.02 seconds. Tests cover native requests/replies/notifications,
master-wallet payment and rollback, delegated/foreign-corporation authorization, departed and forged recruiters, search
exclusions, limits, exact expiry, extension, deletion, schema-159 migration and checkpoint/restart identity preservation.
An earlier migration-test failure was corrected by invoking normal world initialization before constructing the resident
service provider. No full .NET suite was run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Publish and edit an ad from Recruitment. Find it from another pilot using the search filters.

## Bulletins and welcome mail, November 14, 2026

The maintainer confirmed that the title-row correction allows Corporation to load, and requested completion of standalone
corporation features. Alliances and wars remain separate. The first completion milestone implements `GetBulletins`,
`AddBulletin`, `DeleteBulletin`, `UpdateBulletinOrder`, `GetCorpWelcomeMail` and `SetCorpWelcomeMail`.

Bulletins use native DBRow replies, the pinned ten-bulletin limit, 100-character titles and 4,000-character bodies.
Editing passes `bulletinID` and `editDateTime` as keywords to `AddBulletin`; stale revisions, missing IDs and incomplete or
duplicate ordering lists are rejected without changes. Editing timestamps advance even within the same clock tick.
Communications Officers (`ChatManager`), Directors and CEOs manage bulletins. Personnel Managers, Directors and CEOs edit
welcome mail, which uses native tagged/plain mail-body limits and `OnCorporationWelcomeMailChanged` cache invalidation.
Bulletin edits follow the native initiating client's `RefreshBulletins`; other clients retain the native 15-minute cache.

Bounded communication state is typed within the existing saved corporation details, so old saves default to empty
bulletins and welcome-mail settings without a schema migration. Operations use the resident world transaction and existing
checkpoint projection. Joining delivers one automated mail from the last editor (or current CEO if unavailable) and a sent
copy in the same transaction as membership; an unset body uses a short standard welcome. Rejected or repeated acceptance cannot produce extra mail. Renaming or editing
other corporation settings preserves communication state.

Sources: build 3662168 archive SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**,
`base_corporation.GetBulletins/AddBulletin/UpdateBulletin`, `corp_ui_bulletins.BulletinsOrder.ApplyCurrentOrder`,
`bco_applications.GetCorpWelcomeMail` and `welcomeMailWindow`. Existing Blue DBRow encoding is unchanged.
[EVE University recruitment](https://wiki.eveuniversity.org/Recruiting_corporation_members) and
[CCP's recruitment changes](https://www.eveonline.com/news/view/corporation-recruitment-changes) describe automatic welcome mail.

Resident corporation change and member-access rows now identify their pilot owners, so committed role and welcome-mail
changes invalidate connected members' system caches. The existing role-refresh session regression exposed this gap when
its fixture was moved to the resident world required by the current session coordinator.

**Automated validation.** Tool and solution restore, solution build (zero warnings/errors), both Python policy suites
(8 catalog and 9 formatting tests), and formatting verification (2,157 files) passed. The focused filter
`FullyQualifiedName~CorporationCommunicationTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~CorporationAdministrationTests|FullyQualifiedName~CorporationScreenTests|FullyQualifiedName~CorporationRoleTests|FullyQualifiedName~MailServiceTests|FullyQualifiedName~MailArrivalContractTests`
passed 81 tests in 29 seconds; the complete final validation took 86.38 seconds. Tests cover CRUD/order/revision conflicts,
permission boundaries, native replies, NPC read-only behavior, limits, notifications, checkpoint/restart and single mail
delivery on joining. Earlier validation caught an extra founder welcome mail and the resident role-cache issue; both were
corrected before this final run. No full .NET suite was run.

**Live acceptance.** Not performed; agent client testing is disabled. The maintainer's successful window-load report applies
to the earlier title-row change, not these newly implemented controls.

**Optional maintainer checks.** Add/edit/reorder a bulletin. Set a welcome mail and accept a corporation invitation on another pilot.

## Administration Role Management

The Administration → Members → Role Management screen loads members through `CorpRolesNew.GetMembersToShow` in
build **3662168**. The local `PagedCorpMembers.PopulatePage` forwards a one-based page to `corpRegistry.GetMembersPaged`.
The response builds the new-style `PagedResultSet` with its native `(collection, totalCount, page, perPage)`
constructor. The response page is zero-based, which is the index `PagedCollection.Add` uses to insert the rows.

`CorpRolesNew._CreateRowsToUpdate` sends the visible role masks in the 14-column `Rowset` even when a member's
title assignment is unchanged. Member rows and change notifications now supply the direct masks the editor can
save. The session and permission checks continue to use effective roles, including roles granted by titles.
Removing a title therefore removes the roles it granted unless they were also explicitly assigned. The save
checks both the direct assignment and effective role change against the actor's current authority.

The pinned archive SHA-256 is `37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.
The native paths are `corporationWindow.construct_admin_members_panels` line 534,
`corp_ui_member_newroles.CorpRolesNew.GetMembersToShow` line 202, `_CreateRowsToUpdate` line 518,
`SaveChanges` line 573, `pagedCollection.PagedResultSet.__init__` line 78, and
`bco_members.GetMyGrantableRoles` lines 246–252. EVE.js **261108.1** uses the same constructor in
`serviceHelpers.buildPagedResultSet` and direct masks in `corpRegistryRuntime.buildMemberRow`.
[Carbon marshal source](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp) ·
[Corporation roles and titles](https://wiki.eveuniversity.org/Managing_corporation_members)

**Automated validation.** Both `CorporationRoleTests.NativeRoleManagementLoadsMemberPagesAndSavesNativeRows`
cases passed in the five-case exact test run. The build took 15.83 seconds and the run took 9.70 seconds. The cases verify the paged
constructor, regular member and director masks, rejection of an unauthorized write, and a saved direct change
whose authority remains distinct from an assigned title.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer check.** Open Administration → Members → Role Management as a CEO, assign a role to a
member who has a different role from a title, save, and verify the two assignments after reopening the panel.

## Corporation title row decoding, November 14, 2026

The maintainer's next run produced `eden-corporation-21084.log`: ordinary corporation panels completed,
then administration construction failed with `UnmarshalError`. The traceback ends in `ObjectCallGPCS.FromPickle`,
called by `bco_titles.GetTitles` at line 73 while `CorpQueryMembersForm` is being constructed. No invitation attempt
appears in this capture. This identifies the title response as the blocker behind the overlapping window controls.

Eden sent each title as legacy Blue INSTANCE (`0x17`) named `util.Row`. Build **3662168** defines
`carbon.common.script.sys.row.Row` as an `object` subclass with slots `header` and `line`; it needs new-style allocation.
The reply now uses the existing NEWOBJ (`0x23`) encoder and the full class name, retaining the tuple of mutable header
and value lists. This lets native `bco_titles.GetTitles` fill blank title names and preserves later field mutations.
The inbound decoder recognizes only this additional explicit class and validates its two equally sized lists with
string column names. It retains inert data and never constructs a Python object.

Sources: original client archive SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**,
`carbon/common/script/sys/row.pyj`, `bco_titles.pyj` and the recorded maintainer traceback.
[Carbon Marshal.cpp at fcbf0c2](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp)
distinguishes legacy instance and protocol-2 object reconstruction; its Python 3 compatibility implementation is not
treated as the pinned Python 2 allocator. A headless check with the pinned `python27.dll` confirms that its
`PyInstance_NewRaw` rejects this native Row class while `Row.__new__` and its actual `__setstate__` succeed.
[EVE University member management](https://wiki.eveuniversity.org/Managing_corporation_members) supplies the gameplay
reference for titles and roles; this repair changes serialization, not role permissions or membership rules.

**Automated validation.** The exact-method registry regression first failed because
the response contained `BlueInstance`. It now checks all 16 title slots, Unicode names, role values and NEWOBJ encoding.
The native-method regression checks blank-name initialization, repeated cached reads, 64-bit roles and row mutation
using pinned code with test doubles for localization and registry access. It does not execute the Blue DLL, transport,
renderer or a game session. Development also corrected two harness assumptions: legacy raw allocation reports
`SystemError`, and extracted functions require an explicit builtins scope. An initial build in the shared checkout was
blocked by unrelated in-progress Abyssal code; validation moved to an isolated worktree.

Final tool restore, solution restore/build (zero warnings/errors), both Python policy suites (8 catalog and 9 formatting
cases), formatting (2,142 files) and 616 documentation links/anchors passed. With the pinned-client test enabled,
`FullyQualifiedName~CorporationRoleTests|FullyQualifiedName~CorporationTitleWireTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~CorporationScreenTests|FullyQualifiedName~BlueMarshalTests|FullyQualifiedName~PilotBindingsTests|FullyQualifiedName~CorporationClientDiagnosticsTests`
passed **108 tests in 30 seconds**. The validation sequence took **97 seconds**. The full .NET suite was not run.

**Live acceptance.** Not performed; agent client testing is disabled. The maintainer's trace establishes the old failure;
the corrected window rendering remains unverified. Bounded diagnostics remain installed for the next run.

**Optional maintainer checks.** Update the server, reconnect and reopen Corporation. If invitations still fail, attempt
one so the retained diagnostic captures that separate path.

## Corporation construction failure capture, November 14, 2026

After updating the server and reconnecting, the maintainer still observed a corporation window with overlapping
kill-report and standings controls, no title and no navigation tabs. The earlier invitation/bulletin change did not
resolve that failure. The latest available game log contained no unsupported-call error; it did not contain a Python
traceback either. This is an unresolved live failure, not accepted window behavior.

In the pinned build 3662168 source identified below, `CorporationWindow.ApplyAttributes` constructs all panels before
building the header tabs and selecting a panel. That ordering is consistent with an exception during construction,
but the screenshot alone does not identify the failing method or response. Reviewed eager reads included voting access,
corporation identity, titles, member roles and offices; no further reply correction was justified by the available evidence.

The signed login adapter now observes window construction, panel selection and `Corporation.InviteToJoinCorp`.
It writes `%TEMP%\eden-corporation-<pid>.log` locally, including installation and begin/complete markers, exception types,
and up to 32 traceback frames. Separate limits retain at most 128 progress and 128 failure records per process.
It records no arguments, exception messages, frame locals or credentials and transmits nothing. Original return values
and exceptions are preserved, including when logging fails. Repeated installation does not stack wrappers.

**Automated validation.** The exact-method diagnostic regression passed through `tools/Test-Feature.ps1`.
Tool restore, solution restore/build (zero warnings/errors), the combined filter
`FullyQualifiedName~CorporationClientDiagnosticsTests|FullyQualifiedName~ConcordOverviewTests|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~PlanetaryClientTests`
passed all six cases, including signed-login evaluation in the pinned Python 2 runtime
(`EDEN_TEST_CLIENT_ROOT=E:/EdenDev/3662168`). Both Python policy suites passed (8 catalog and 9 formatting cases);
formatting verified 2,141 files. The final validation sequence took 35 seconds, with 3 seconds in focused tests.
These checks establish diagnostic behavior, not the cause or correction of the reported window fault.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** After installing this server revision, reconnect and open Corporation once; optionally
repeat the failed invitation. Retain the new `%TEMP%\eden-corporation-*.log` for the next investigation.

## Direct invitations and corporation window reads, November 14, 2026

The maintainer reported that the player corporation window and direct invitations still failed. The November 14
game log records the invitation confirmation at 12:41:06 ET, followed by `Unsupported local call corpRegistry.MachoBindObject`
at 12:41:08 and three further registry failures. That log does not expose the embedded methods, so it cannot establish
which read failed on each window attempt. Inert inspection confirms that `base_corporation.InviteToJoinCorp` calls
`InsertInvitation(characterID)`, which had no Eden handler. `GetCorpBulletins` also called an unhandled `GetBulletins()`.

`InsertInvitation` now authorizes the selected pilot as CEO, Director or Personnel Manager and uses the existing resident
world transaction, saved application rows and checkpoint mechanism. A direct invitation has native status 8 and returns
its application ID. Existing applications become offers without replacing their text; repeated outstanding invitations
return the existing ID without adding events. Invitations remain possible when unsolicited applications are disabled.
Unknown pilots, ordinary members, existing corporation members, CEOs and full corporations are rejected without changes.
The recipient sees the invitation through `GetMyApplications` and the existing `OnCorporationApplicationChanged` updates;
only their acceptance moves membership. Withdrawal/rejection permits a later invitation. This does not add a separate
notification-feed message or automatic welcome mail.

`GetBulletins` returns the native empty list for Eden's currently absent bulletin feature, allowing the read to complete.
Bulletin editing and recruitment advertisements remain separate work. Unsupported binds now include their embedded
method in the client error, for example `corpRegistry.MachoBindObject (UnknownCorporationMethod)`, so another missing
panel call can be identified from the game log.

Sources: build **3662168** original `code.ccp`, SHA-256
**37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**, `base_corporation.InviteToJoinCorp`,
`GetMyOpenInvitations`, `GetCorpBulletins`, `bco_applications` and `corporationWindow` panel construction.
EVE.js **261108.1** `corpRegistryRuntime.Handle_InsertInvitation` was reviewed as a behavioral reference;
its older client target and independent state model were not copied.
[EVE University recruitment](https://wiki.eveuniversity.org/Recruiting_corporation_members) documents direct invitations,
Personnel Manager authority and recipient acceptance. Existing list, integer and object-reference encoding reuses
[Carbon Blue Marshal.cpp at af0d65b](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp).

**Automated validation.** Ten new cases cover native bound invitations, application
reply fields and notifications, consent, repeat suppression, rejection/reinvitation, permissions, malformed requests,
capacity, preserved applicant text, closed unsolicited recruitment, checkpoint/restart persistence and window reads.
The initial invitation test reproduced the unsupported call. An initial test helper attempted to decode an outbound
object reference with the inbound decoder; it was corrected to inspect `BoundPilotReply` and serialize its payload.
Tool restore, solution restore and build passed with zero warnings/errors. The combined filter
`FullyQualifiedName~Corporation|FullyQualifiedName~PilotBindingsTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~HomeStationServiceTests|FullyQualifiedName~CharacterSheetServiceTests`
passed **281 tests in 95 seconds**. Both Python policy suites passed (8 catalog and 9 formatting cases), formatting
verified 2,138 files, and documentation validation checked 616 links/anchors. The final validation sequence took
**134 seconds**. No full .NET suite or live client run was requested or performed.

**Live acceptance.** Not performed; agent client testing is disabled. The reported window's rendering remains unverified.

**Optional maintainer checks.** Restart on the updated server and reopen Corporation. Invite another pilot, then accept
through Recruitment / My Applications. If a panel still fails, the updated game-log error includes its embedded method.

## Current recruitment filters and inbox notices

`GetApplications` and `GetMyApplications` return open applications and decisions updated within the last three calendar
months. The two older-application reads return completed applications whose latest update predates that window.
This keeps recent outcomes in the native status filters after reconnect. Open invitations remain actionable regardless
of age. The window starts at the latest update so a decision on a long-open application appears in the current filters.

The existing corporation application update also writes the relevant Corporation inbox notice in the same transaction.
Applicants receive offers, direct invitations and declines. CEOs, Directors and Personnel Managers receive new
applications, joins, withdrawals and declined offers. A repeated direct invitation or a rejected transition creates
no extra notice. The native event refreshes connected clients after commit, and offline recipients can fetch their
notices after restart.

REA extracted build 3662168's `corp_ui_applications.pyj` and `appConst.pyj` in
`ev_a277ae57a69212e05615a352db9cd929fc550033d0f8c5015ce5e0d317a1d901`. The native current and older views call
different getters before applying the UI's status filters. Extraction
`ev_2aa262d14bc4a73ee7cdfc83fc751bbfe5ead62aacbfcde280b91c94ee8cb28c` identifies the notice constants
**16–18**, **128–130**, **139**, and **142** in `notificationconst.pyj`. The archived EVE.js
`corpApplicationNotifications.js` supplies the native `charID`, `corpID`, `invokingCharID`, `applicationText` and
`customMessage` payloads. CCP describes the recent application window and status filters.
[CCP corporation recruitment changes](https://www.eveonline.com/news/view/corporation-recruitment-changes)

`NativeApplicationFiltersKeepRecentDecisionsAndOpenApplicationsOnFreshReads` and
`RecruitmentPublishesNativeInboxNoticesOnceAndKeepsOfflineNoticesAcrossRestart` passed in the combined 22-case feature run.
The build took 21.52 seconds and tests took 22.90 seconds. The cases cover the exact age boundary, authorized reads,
native push payloads, duplicate suppression, and unread notice retrieval after checkpoint reopen.

## Corporation registry lifetime and application replies

The November 13 screenshot showed a partially constructed corporation window after gate travel. The client game log
recorded `Unsupported local call corpRegistry.MachoBindObject` at 23:09:37 ET. The running server's warning logs did not
identify the bind's embedded method. Read-only inspection confirmed the selected pilot was the CEO of an existing player
corporation.

Build **3662168** `base_corporation.GetCorpRegistry` and `eveMoniker.GetCorpRegistry` cache the registry by `corpid`.
Eden had been releasing the reference on location changes, which left the client holding an expired reference when it
opened corporation panels or queried applications after travel. Registry references now live until corporation change
or pilot logoff. Binds still validate the selected pilot's current corporation. `bco_applications.InsertApplication`
returns the registry reply to `ApplyForMembership`, and `recruitmentEntry.Apply` consumes it as the application ID.
Eden now returns the ID from the committed application.

Inert inspection used the original build archive, SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**.
EVE.js **261108.1** was checked for registry binding and application reply behavior. Eden reuses its existing object
reference and integer encoders. [EVE University recruitment guide](https://wiki.eveuniversity.org/Recruiting_corporation_members) ·
[Carbon Blue `Marshal.cpp` at fcbf0c2e](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp)

**Automated validation.** The targeted regressions reproduced the expired reference and missing application ID.
The corrected cases cover location departure, corporation change, public application dialog queries, submission
through a retained reference, Unicode text, persistence and duplicate rejection.
The combined corporation, session, market and serialization run passed 546 cases in 161 seconds. The filter
included `Corporation`, `PilotBindingsTests`, `PilotServicesTests`, `HomeStationServiceTests`, `CharacterLogoffTests`,
`Eden.Protocol.Tests.Market` and `Eden.Protocol.Tests.Serialization`. Three cases reproduced on base `f964b2ae`
and were excluded from the final run: `RegisteredResidentRowsCoverEveryScalarColumnOfTheirSavedModels`,
`FailedPaymentRollsBackStockGoodsAndHistoryAndRetrySettlesOnce` and
`MaintenanceUsesPricePriorityAndRejectsDuplicateConsumptionAfterRollback`.
Tool and solution restore, build, both Python policy suites and formatting passed in 235 seconds overall.

**Live acceptance.** Not performed because agent client testing is disabled.

**Optional maintainer checks.** After updating the server, reopen Corporation after a gate jump. From an eligible pilot,
apply through a recruiting corporation's Show Info window and check My Applications.

## Corporation contacts and standings — November 11, 2026

Milestone 6 replaces the empty corporate-contact placeholder with authoritative contacts, standings and labels.
`corpRegistry` owns `GetCorporateContacts`, `GetContactList`, `AddCorporateContact`, `EditCorporateContact`,
`RemoveCorporateContacts`, `EditContactsRelationshipID`, `GetLabels`, `CreateLabel`, `EditLabel`, `DeleteLabel`,
`AssignLabels` and `RemoveLabels`. Reads return native dictionaries keyed by contact/label ID, containing
named `utillib.KeyVal` records. Contact fields are `contactID`, floating `relationshipID`, false `inWatchlist` and signed
long `labelMask`; labels retain Unicode `name`, long `labelID`, and nullable `color`. Creation returns the label bit;
mutations return None. Zero is an explicit colour; None leaves an edited field unchanged.

Any current member can read their corporation's book. Mutations require the current CEO, Director or global Diplomat
role, including title-derived roles, checked from saved membership and effective roles inside the transaction. Stale
session roles do not authorize a write. A corporation contact belongs to the organization, so a member may update a
contact for their own character. Targets are known Eden pilots and known player/NPC corporations. Alliance contacts and
unknown owners are not fabricated. Manual contact standings do not change mission-derived NPC standings or standings
in the opposite direction. NPC-corporation books remain empty/read-only. Personal contacts and labels remain separate.

Schema **144** adds corporation-owned contact and label tables with cascading owner references and unique label names.
Existing personal data is preserved. Standings retain the native -10 through +10 range; batches are bounded to 1,024
IDs and validate the entire selection before changing state. Labels occupy the 63 positive signed-long bits. Removing a
label clears its bit on every owned contact in the same transaction; deleting/reusing a bit does not reassign it.
Invalid owners, missing contacts for label assignment, unknown label bits, invalid text/colour, exhausted label capacity
and database failures preserve the prior book and its change revision. No inventory, wallet or ownership operation is
performed by contact management.

Primed sessions monitor an in-memory corporation revision. Unchanged empty/populated monitor polls perform no SQL, open no
connections and calculate no fittings. Committed contact changes reload an authorized snapshot and send
`OnOrganizationContactsUpdated(ownerID, listOfUpdateTuples)`. New/changed standing is `(contactID, standing, None)`,
a mask update is `(contactID, None, mask)`, and deletion is `(contactID, None, None)`. The pinned client applies either
standing **or** mask to an existing row, so simultaneous field changes use separate tuples. Departed members receive
no former-corporation data, including when their session identity has not yet refreshed.

**Native cache limitation:** Addressbook's pinned notification handler refreshes contact standings and assignments but
has no label-definition invalidation event. The editing client changes its own name/colour cache after successful
label operations; another signed-in member may retain old label names/colours until session reset/reconnection. No
unsupported event or remote client-reset call was introduced. Live rendering and cache timing remain unverified.

References: [EVE University corporation diplomacy](https://wiki.eveuniversity.org/Corporation_diplomacy) covers shared,
one-way standings and labels; [corporation roles](https://wiki.eveuniversity.org/Managing_corporation_members) describes
Diplomat/Director authority; [contacts](https://wiki.eveuniversity.org/Contacts) describes contact groups and labels.
Inert build **3662168** inspection covered `addressbookService.GetContacts`, `GetContactLabels`, `AddToAddressBook`,
`EditContacts`, `AssignLabelFromWnd`, `ShowLabelMenuAndManageBtn`, and `OnOrganizationContactsUpdated` from archive
SHA-256 `37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368`; no client code was executed.
Existing Blue dictionary/KeyVal/list/tuple adapters suffice. Codec and transport are unchanged; the
[Carbon Blue serialization reference](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp)
remains revision `fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae`.

EveJs installed source **261108.1**, without Git metadata, was consulted as a behavioral reference and independently
reconciled with the pinned client. Source SHA-256 identifiers:

- `services/corporation/corpRegistryRuntime.js`: `41D691387FF45DCE3CF4680DA40291557B868F0B3F589234D0EB244B77C4E35C`
- `services/corporation/corporationNotifications.js`: `101462C93041E9DE1F42AE8707F4E90C0A34A96483FB3D6D55B8CC686DDDD3C9`
- `services/corporation/contactLabelState.js`: `BB7A2C70CEA505FF3B229C59C5D03CE91A6EB3D1EBF2A6D04CB2E9E8290B2D40`

**Automated validation:** Tool/solution restore and solution build passed with zero warnings/errors. Final filter
`FullyQualifiedName~CorporationContactsTests|FullyQualifiedName~ContactsServiceTests|FullyQualifiedName~PilotContactsTests|FullyQualifiedName~CorporationRoleTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~CorporationScreenTests|FullyQualifiedName~CorporationShowInfoTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~WorldInitializationTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting`
passed **143 cases**, including **15 corporation-contact cases**, zero failed/skipped, in **49.47 seconds** with four test
workers. Both Python policy suites passed (**8 catalog**, **9 formatting**), formatting verification checked **1,716 files**,
and documentation text/link/anchor/whitespace checks passed. Required checks took **79.76 seconds** on base `fa2f8838`
plus this milestone. Coverage includes disk/resident native CRUD and reopen, current role/title/director authority and
revocation, malformed/late-invalid selections, organization/personal isolation, all label bits and quota reuse, two-session
native notification tuple semantics, idle polling, stale-membership privacy, forced transaction rollback and version-143
upgrade preservation. Development began with the exact native CRUD/reopen method (2 cases), then combined the affected
class and persistence methods. No full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** With two corporation members, change a standing and label assignment and inspect the
other member's contact list/overview. Reconnect the second member after editing label names/colours.

## Corporation Show Info window audit, November 9, 2026

The follow-up screenshot reaches the corporation header and then reports
`Unsupported local call corporationSvc.GetCorpInfo`. The earlier `GetPublicInfo` correction fixed the first request,
but did not establish acceptance of the body or tabs. This change traces the pinned build **3662168**
`CorporationInfoWindow`, `Info.UpdateDataCorp`, tab callbacks and their local service adapters before adding the
remaining read routes.

| Window path | Native dependency and implementation |
| --- | --- |
| Header and Attributes | Existing `corpmgr.GetPublicInfo` now also supplies `creatorID` and `loyaltyPointTaxRate`. Saved player founder and LP tax are authoritative; NPC founder retains the existing corporation reply's CEO fallback and NPC LP tax is zero. Existing aggression-settings and permitted-enlistment reads are reused. |
| NPC Market Activity | New `corporationSvc.GetCorpInfo(corporationID)` returns a nine-column native Rowset: `typeID`, `sellPrice`, `sellQuantity`, `sellDate`, `sellStationID`, `buyPrice`, `buyQuantity`, `buyDate`, `buyStationID`. It reports the latest completed NPC sale and purchase per type from Eden's saved executions, converts cents to ISK, and preserves None for a missing side. An untouched market returns an empty Rowset. It does not invent retail activity, seed orders or disclose pilot identities/wallet data. |
| Stations and map highlighting | New `config.GetStationSolarSystemsByOwner(corporationID)` returns sorted, distinct `solarSystemID` rows from the existing station directory. Catalog ownership determines the results; renting an office does not transfer station ownership. The client obtains individual station rows locally through `eveCfg.GetLocationsLocalBySystem`; existing station/location priming and pathfinding routes are reused. |
| Agents | `PanelAgents.GetAgentsOfLevel` uses the existing `agentMgr.GetAgents` directory through the local agents service, then filters by corporation and level. Existing saved personal standings determine availability. No new agent route is required. |
| Standings | Existing personal/corporate contact reads, `standingMgr.GetCharStandings`, `GetCorpStandings` and `GetNPCNPCStandings` supply the local standings service. The existing public corporation lookup supplies alliance affiliation. No new standings route is required. |
| Alliance History | New `allianceRegistry.GetEmploymentRecord(corporationID)` returns the saved player corporation founding row (`allianceID=None`, `startDate=CreatedAt`). The native callback treats the last row as the founding event. NPC founding dates are unavailable, so their result is empty. There are no fabricated alliance memberships. |
| War History and affiliation | New `warsInfoMgr.GetWarsByOwnerID(corporationID)` returns an empty list, and the shared `GetWarsByOwners(set(ownerIDs))` cache read returns an empty list for every requested owner. New `facWarMgr.GetCorporationWarFactionID(corporationID)` returns None for Eden's unenlisted corporations; NPC militia affiliation and faction wars remain local static client data. Eden has no declared player wars. |
| Freelance Projects | The modern tab uses the public gateway rather than a local MachoNet RPC: `eve_public.freelance.project.api.GetAllActiveForCorporationRequest`. The gateway now returns a successful native response with an empty repeated `projects` field. Repository polling requests stats only for cached project IDs; no IDs exist in Eden. Project creation, participation and rewards remain unavailable. |
| Members, application button and shared menu | The local `GetMembersAsEveOwners` adapter primes the caller's existing corporation-scoped `corpRegistry.GetEveOwners`. Application reads/writes already have owners; the window-specific menu extension opens the existing contracts search. These paths retain their existing membership/role restrictions. Opening downstream management windows does not add their deferred gameplay features. |

All new MachoNet reads require character selection and reject malformed IDs, extra arguments and unexpected keywords;
normal `machoVersion` metadata remains router-owned. Single-corporation information reads reject unknown corporations.
Station ownership follows the existing station-directory convention of an empty result for an unknown positive owner.
War cache batches accept native integer sets with at most **1,000** positive Int32 owner IDs and include each requested
owner, so an absent cache entry cannot cause repeated requests. No schema, codec or corporation mutations are added.
The public gateway follows its existing account-independent empty-information response policy.

Evidence: inert inspection of `E:/EdenDev/3662168/.eden-original-code.ccp`, SHA-256
**37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**. In addition to the window and `infosvc`, reviewed
`panelAgents`, `panelStandings`, `panelFreelanceProjects`, `bco_members`, `base_corporation`, `standingsvc`, `facWarSvc`,
`wars/war_cso`, `menusvc.GetMenuFromItemIDTypeID`, `warPermit`, `eveCfg.GetLocationsLocalBySystem`, the freelance repository/messenger/parser, and the native freelance
protobuf message definitions. This reuses [Carbon Blue Marshal.cpp at fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp)
for unchanged Rowset/KeyVal/None/list/dictionary encoding. Gameplay references include
[EVE University's Agents-tab instructions](https://wiki.eveuniversity.org/Datacore_farming) and
[corporation diplomacy and War-tab overview](https://wiki.eveuniversity.org/Corporation_diplomacy).

**Automated validation:** development started with
`.\tools\Test-Feature.ps1 -Filter 'FullyQualifiedName=Eden.Protocol.Tests.PilotSessions.CorporationShowInfoTests.NativeStartupAndAllTabReadsReturnExpectedShapesWithoutUnsupportedCalls'`.
The two session cases passed after correcting test-only assertions to use the existing native set constructor and
decoded dictionary types (build **5.75s**, tests **15.22s**). An initial test build also exposed an invalid test result
type name. The first expanded run passed 17 cases and rejected one market fixture that omitted required order price
and stock; the corrected market/reopen regression passed (build **4.34s**, tests **6.83s**). No codec changes were made.
The final pre-integration filter below passed **65 tests in 20s**; tool/solution restore, solution build (zero
warnings/errors), both Python policy suites (**8 catalog**, **9 formatting**), formatting (**1,509 files**) and width
checks passed in **63.55s**. The session regression exercises startup and tab reads together for NPC and other-player
corporations. Further cases cover malformed requests and selection gating, bounded war-cache sets, authoritative
market rows and absent-side None values, isolation from other corporations/personal activity, saved-world reopen
and the freelance gateway response. After integrating main through **2b4f8a8a** (including **15ad8f3a**'s market
changes), the same focused filter passed **65 tests in 29s**. Tool/solution restore, solution build (zero
warnings/errors), both Python suites (**8 + 9 tests**), formatting (**1,522 files**), width and documentation/diff
checks passed; the integration sequence took **80.71s**. These results cover the combined code, without the unrelated
uncommitted work in the main checkout.

Focused filter:
`FullyQualifiedName~CorporationShowInfoTests|FullyQualifiedName~CorporationPublicInfoTests|FullyQualifiedName~CorporationScreenTests|FullyQualifiedName~PlayerCorporationTests|FullyQualifiedName~CorporationAdministrationTests|FullyQualifiedName~StationDirectoryTests|FullyQualifiedName~AgentDirectoryServiceTests|FullyQualifiedName~CharacterInteractionHistory|FullyQualifiedName~GatewayRequestsTests|FullyQualifiedName~HomeStationServiceTests`.
No full .NET suite was run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Restart the server; open NPC and player Corporation Show Info and visit each tab.
Confirm native layout, localized NPC content and empty War/Freelance displays. The screenshot establishes the original
failure only; rendering, input and client-side tab construction remain unverified.

## Public corporation Show Info, November 9, 2026

The maintainer's screenshot reports `Invalid Packet Destination: Unsupported local call corpmgr.GetPublicInfo`.
In pinned build **3662168**, `CorporationInfoWindow.reconstruct_info_window` requests this reply before constructing
the window. Its header and caption read `corporationID`, `ceoID`, `allianceID`, `stationID`, `description` and `deleted`.
The fallback caption path also assigns `shares` on the reply. `WarPermit.GetWarPermitStatusForCorp` reads `allowWar`;
the bounty window and kill-right display also read `allianceID`.

`CorporationPublicInfoHandler` now serves that call as a named public `utillib.KeyVal`, using existing saved player
corporation and NPC catalog readers. Any selected pilot may read another corporation without membership or roles.
The response includes identity, ticker, CEO, headquarters, description, URL, tax, shares and member count; NPC
descriptions remain localized by the client. `deleted` and `allowWar` are false, and `allianceID` is None, matching
Eden's current corporation capabilities. NPC member count retains the existing directory baseline of zero and player
shares retain the existing corporation reply's initial-share baseline. This does not add alliances, dissolution or
war declarations. Private roles, division names and wallet contents are excluded. Unknown corporations, invalid IDs,
extra arguments and unexpected keywords fail without mutation or notifications. The router preserves selection
gating and accepts its usual `machoVersion` metadata.

Sources: inert inspection of the original pinned archive
`E:/EdenDev/3662168/.eden-original-code.ccp`, SHA-256
**37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**, at the call sites above.
The installed client archives contain overlays; inspection was verified against the preserved original.
[EVE University's corporation window overview](https://wiki.eveuniversity.org/Neocom#Corporation) describes public
corporation information. Existing KeyVal/Unicode/None/boolean adapters reuse
[Carbon Blue Marshal.cpp revision fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp).
No codec, gameplay rule or saved schema changes are required.

**Automated validation:** development started with
`.\tools\Test-Feature.ps1 -Filter 'FullyQualifiedName=Eden.Protocol.Tests.PilotSessions.CorporationPublicInfoTests.ShowInfoReturnsPublicFieldsThroughTheSessionForNpcAndOtherPlayerCorporations'`.
The initial run passed the player case and failed the NPC case because the test catalog lacked NPC metadata; after
adding that fixture metadata, both cases passed (build **20.27s**, tests **17.87s**). Final tool/solution restore and
solution build passed with zero warnings/errors. Filter
`FullyQualifiedName~CorporationPublicInfoTests|FullyQualifiedName~PlayerCorporationTests|FullyQualifiedName~CorporationScreenTests|FullyQualifiedName~CorporationAdministrationTests|FullyQualifiedName~HomeStationServiceTests`
passed **41 tests in 22s**, including five new cases covering real session replies, repeated NPC/other-player reads,
exact header fields, Unicode descriptions, saved-description refresh, selection gating, malformed/unknown IDs,
keyword rejection and unchanged membership. Both Python suites passed (**8 catalog**, **9 formatting**); formatting
verified **1,505 files**, line-width checks passed, and documentation links/whitespace and diff checks passed. The
final required sequence took **90.60s**. No full .NET suite was run; native rendering remains unverified.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Open Corporation Show Info for an NPC corporation and a player corporation; confirm
the header, headquarters and description render without the reported dialog. Other information tabs may have
independent service requirements.

## Corporation offices and shared storage, November 6, 2026

Player corporations can rent a local NPC-station office, deposit materials, equipment and inactive ships into seven divisions, and withdraw to personal hangars or permitted active-ship holds. Assembled hulls withdraw to the station hangar with their fittings and cargo; ship insurance is voided when ownership changes, matching existing contract transfers. Active ships, installed industry blueprints, sealed courier packages, filled standalone containers and ships containing nested hulls are rejected. Deeper container trees require their own ownership lifecycle and are also rejected. Hauling an inactive ship into an office updates its retained station identity.

`officeManager` now owns `GetMyCorporationsOffices` and station-bound `GetCorporationsWithOffices`, `GetEmptyOfficeCount`, `GetPriceQuote(corporationID)`, `RentOffice(cost)`, `UnrentOffice`, `PrimeOfficeItem` and `HasCorpImpoundedItems`. Bind/resolve requests must name the current docked station. Office rows expose `officeID`, `corporationID`, `stationID`, `solarsystemID` and `officeFolderID`. `invbroker.GetInventoryFromId(officeID, passive)` returns a location-scoped office reference; the native numeric default `passive=0` and explicit boolean forms are accepted. Office inventory implements `GetSelfInvItem`, `List(flag=None)`, `ListByFlags(flags=...)`, `Add`, `MultiAdd` and division-scoped `StackAll`. The existing inventory manager's `SplitStack(stationID,itemID,quantity,ownerID)` also accepts an authorized corporation owner. Inventory rows retain the native nine-field DBRow descriptor and explicit assembled quantity. `corpmgr.GetAssetInventory(corporationID,'offices')` and `GetAssetInventoryForLocation(corporationID,stationID,'offices')` expose rented-office summaries and permitted rows, including remote read-only reports.

Every operation checks the saved actor's actual corporation, docking/location and effective current roles, including titles and HQ/base/other-station scopes. Members may donate without query access. Reading requires the division's query bit; withdrawal, division moves, splitting and consolidation require query and take bits. CEO/Director authority covers all seven divisions. Active-ship cargo capacity and hold type restrictions apply to withdrawals. Cached references cannot retain access after leaving, revocation or travel. Other local members receive inventory updates only for currently readable divisions; crossing into a hidden division removes the old row without publishing the destination. Rental changes use the native `OnOfficeRentalChange(corporationID,officeID)` event. Durable change records and asset/payment writes commit together; observer cursors prevent duplicate delivery within a connection.

Schema **97** adds `WorldCorporationOffice`, a unique corporation/station rental, `WorldCorporationOfficeSettings`, and `WorldCorporationChange.StorageJson`. Office IDs occupy the pinned 66,000,000–67,999,999 folder range and retain their allocation high-water mark after unrenting. Inactive corporate hulls use the office as their stored container; startup validates owner, station, division and inactive status. The existing active-hull owner constraint remains enforced. Migration and normal repeated startup preserve preexisting items and saved policy.

**Rental policy and limits:** the pinned catalog supplies station owner/location but no current server-side rental quote or slot capacity. Eden explicitly saves a fallback **10,000 ISK initial fee** and **24 offices per station** in the one-row settings table; these are configurable saved policy, not observed Tranquility prices. Rental authority (`CanRentOffice`, CEO/Director) permits the atomic debit from the corporation master wallet, with native journal entry **13** to the station's NPC owner. Quote changes, duplicate rentals, full stations and insufficient funds fail without payment. This milestone provides persistent initial rentals, not recurring leases: monthly invoices, automatic renewal, impound/recovery, remote unrenting, corporation industry/trading and non-office asset selectors remain separate work. Unrenting requires all divisions empty and gives no refund, so assets cannot be stranded pending impound support.

Sources: inert inspection of pinned build **3662168** `code.ccp`, SHA-256 `37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368`: `officeManager` station/corporation properties, quote/rent/unrent/priming and rental-change paths; `invCache.GetInventoryFromId` defaults and container `List`/`ListByFlags`/`Add`/`MultiAdd`/`StackAll`; `invControllers.StationCorpHangar`/`BaseCorpContainer`; `corp_ui_accounts` and `mapFilterCorpAssets`; `inventorycommon.const` office IDs/type/division flags; and `appConst.refOfficeRentalFee` (**13**). The local catalog confirms Office type **27**, group **16**, category **1**. Consulted the [Carbon repository inventory](https://github.com/orgs/carbonengine/repositories); no corporation gameplay implementation was found in the public repositories reviewed. Existing native DBRow/list/tuple/None serialization reuses [Carbon Blue Marshal.cpp revision fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp). No codec change, client-code execution, game launch or normal runtime save modification occurred. Implementation began at `2eca342` and integrated `31d7bcf` before final validation.

**Automated validation:** tool/solution restore and build passed with zero warnings/errors. Final filter `FullyQualifiedName~CorporationStorageTests|FullyQualifiedName~InventoryOwnershipTests|FullyQualifiedName~CorporationScreenTests|FullyQualifiedName~CorporationRoleTests|FullyQualifiedName~InventoryStackSplitTests|FullyQualifiedName~ShipInventoryRulesTests|FullyQualifiedName~MiningTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~CorporationAdministrationTests|FullyQualifiedName~CorporationWalletTests|FullyQualifiedName~CorporationFinancialPanelTests|FullyQualifiedName~CorporationShareTests|FullyQualifiedName~PlayerCorporationTests|FullyQualifiedName~InventoryPersistenceTests|FullyQualifiedName~PilotInventoryTests|FullyQualifiedName~ProductionInventoryTests|FullyQualifiedName~DockedServicesTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~Eden.Protocol.Tests.Serialization` passed **561 tests** (73 seconds reported by the runner; 76 seconds elapsed including runner startup). Both Python policy suites passed (**3 catalog and 9 formatting cases**); formatting verified **915 files**, and added documentation links, UTF-8/newlines/whitespace and diff checks passed. The required validation sequence took **99 seconds**. The 21 storage cases cover native requests, separate query/take rights for all divisions and station scopes, titles and stale references, local observer filtering, split/consolidation, disk/resident reopen, version-96 migration, saved policy, rental authority/fees/capacity, rejected/late-failing batches and simultaneous withdrawals. Development began with exact filter `FullyQualifiedName=Eden.Protocol.Tests.PilotSessions.CorporationStorageTests.RentalAndLocalNativeInventoryTransfersChargeOnceAndConserveOwnership`. Expanded boundary coverage passed **416 tests in 44 seconds** before the final additional native-default, migration and rollback cases. Earlier failures exposed EF's immutable ship ownership alternate key (corrected using the existing transactional contract pattern) and test expectations for outbound DBRows and native tuple rows; the latter were corrected without extending the receive codec. A direct-constructor mining fixture was updated for the added storage dependency. No full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled. Native office lobby, division rendering and drag/drop feel remain unverified.

**Optional maintainer checks:** rent an office and verify the lobby/hangar divisions; use a second member with query-only then query/take roles to inspect transfer presentation and live updates.

## Corporation share holdings and native wallet panels

The Shares screenshot's `corpRegistry.MachoBindObject` failure concealed the bind's first `GetSharesByShareholder` call. The corporation handler now owns that method, `GetShareholders`, `MoveCompanyShares` and `MovePrivateShares`. Holdings return an ordinary native Rowset that the client indexes by `corporationID`; shareholders return an IndexRowset keyed by `shareholderID`. Both expose `corporationID`, `shareholderID`, `shares` and nullable `shareholderCorporationID` with explicit native row/list forms.

World schema **80** records issued supply, per-owner holdings, transfer history and ordered member notifications. Existing corporations receive their previously advertised **1,000** initial shares in treasury once; creation issues the same treasury holding within the corporation-creation transaction. Subsequent startup preserves distributed shares and zero treasury balances. Transfers conserve the issued supply, require a positive whole amount and a known saved pilot/player-corporation recipient, and commit both holdings, history and pending events together. Company holdings require financial read authority and Director transfer authority in the actor's current corporation. Personal ownership survives leaving the issuer, and personal holders may transfer their own shares. A corporation can own and transfer another issuer's shares. Shareholder-list reads require current financial authority in the issuer or a personal holding in that issuer.

The pinned `bco_shares.OnShareChange_thread` passes dictionary changes directly to `IndexRowset.__setitem__` on its update branch, although that setter accepts a list/Row. Its deletion branch safely evicts a certificate, and a visible Shares panel responds by fetching authoritative holdings again. Notifications therefore invalidate affected certificates using that deletion form instead of sending the incompatible update form. Durable records retain both actual before/after quantities. Current ownership/financial access is checked again before notification delivery, and repeated polling does not repeat events. Shareholders refresh when their tab is selected. Native rendering and event scheduling remain unverified.

Sources: inert build **3662168** `bco_shares`, `sharesPanel.GetScrollList`/`GetEntry`/`OnShareChange`, `corpShareholdersPanel`, `giveSharesDialog.TransferShares`, `bco_base.GetAddRemoveFromChange`, and `eve.common.script.sys.rowset.Rowset.Index`/`IndexRowset`. The pinned archive hash and Carbon Blue revision recorded below remain applicable; this milestone does not change the codec or execute client scripts. Voting, new share issuance, dividends and CEO succession remain separate governance work; existing voting capability remains disabled.

**Automated validation:** seventeen new cases cover the actual bind first-call path, native row fields/indexes, initial supply, company/private and cross-corporation transfers, leave/revocation boundaries, malformed/forged/insufficient/overflow rejection, unchanged ISK, failed-write rollback of holdings/history/events, ordered authorized cache invalidations, disk/resident reopen and one-time schema-79 backfill. Development filter `FullyQualifiedName~CorporationShareTests`, with `xUnit.MaxParallelThreads=4`, passed 17 cases in 4 seconds. The initial nine-pass/one-failure run attempted to decode the outbound bound-reference token with Eden's inbound decoder; the test now asserts that reference directly and decodes the actual bound reply separately, without changing codec behavior. Tool/solution restore and build passed with zero warnings/errors. Final filter `FullyQualifiedName~Corporation|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~InventoryOwnershipTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~CharacterLogoffTests|FullyQualifiedName~Eden.Protocol.Tests.Serialization|FullyQualifiedName~Eden.Protocol.Tests.Hosting`, with `xUnit.MaxParallelThreads=4`, passed **198 tests in 21 seconds**. Both Python suites passed (3 catalog and 9 formatting tests), formatting verified 741 files and diff checks passed. The required sequence took **40.1 seconds**; no full .NET suite was run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** After updating/reconnecting, inspect Owned By Corp and Shareholders; give shares to a second pilot and inspect their personal shares and transfer back.

## Corporation Bills and Market Transactions panel calls

The follow-up screenshots show the now-enabled ISK header, followed by missing `billMgr.GetAutomaticPaySettings` and `marketProxy.CorpGetTransactions` requests. The pinned Bills panels also request `GetCorporationBills`, `GetCorporationBillsReceivable` and `SendAutomaticPaySettings`. These now have authenticated selected-pilot owners. The billing settings reply is the native owner-ID dictionary containing numeric bill-type boolean entries and the string `divisionID`; mixed keys use the existing explicit Blue dictionary adapter. Known bill types come from the pinned catalog. World schema **79** saves the selected division and automatic-payment preferences per corporation. Accountants/Directors can edit with withdrawal access to the selected division; Junior Accountants can read. Owner IDs and permissions are checked against current saved membership on every operation.

This milestone first registered the authorized Wallet endpoints with empty histories. Automatic-payment preferences persist
for future invoice services. `CorpGetTransactions(fromDate, accountKey)` accepts None for either filter and validates explicit
division keys. Its history surface requires financial or Trader authority. Malformed filters, forged settings and
revoked access fail without changing state. The later market milestone populates transaction history for purchases through
the corporation wallet. [Immediate corporation market purchases](industry.md#immediate-corporation-market-purchases)

Sources: inert build **3662168** `corpBillsPayablePanel.PopulateScroll`/`GetBillsPayable`, `corpBillsReceivablePanel.GetBillsReceivable`, `corpBillsAutoPaidPanel.GetBillsPayable`, `corpBillsAutoPaySettingsPanel` settings readers/submission, and `marketSvc.GetCorpMarketTransactions`/`corpMarketTransactionsPanel._GetTransactions`. The pinned archive hash and Carbon Blue revision in the following section remain applicable. The decoder now retains valid numeric/text mixed keys as an explicit inert `BlueDictionary`, matching Carbon `Marshal::ReadObjectDict` and `TY_DICT`. Homogeneous dictionaries keep their existing typed forms; no client execution occurred.

**Automated validation:** six new panel cases and one codec case exercise encoded replies and requests, catalog bill types, all affected read paths, saved settings and disk/resident reopen, current role checks, division withdrawal, forged/malformed rejection and schema-78 upgrade. Development exposed both unsupported raw mixed-key test/reply dictionaries and the decoder's blanket mixed-key rejection. Both boundaries now retain that native shape explicitly. The corrected filter `FullyQualifiedName~CorporationFinancialPanelTests|FullyQualifiedName~BlueMarshalTests`, with `xUnit.MaxParallelThreads=4`, passed 31 tests in 3 seconds. Tool/solution restore and build passed with zero warnings/errors. Final filter `FullyQualifiedName~Corporation|FullyQualifiedName~MarketTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~Eden.Protocol.Tests.Serialization|FullyQualifiedName~Eden.Protocol.Tests.Hosting`, with `xUnit.MaxParallelThreads=4`, passed **203 tests in 23 seconds**. Both Python suites passed (3 catalog and 9 formatting tests), formatting verified 736 files and diff checks passed. The required sequence took **39.5 seconds**; no full .NET suite was run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Open Bills and its settings/payable/receivable tabs; open corporate Market Transactions with All/Master division filters.

## Corporation ISK header permission correction

The reported blank Corporation Wallet ISK header came from the pinned client's local permission gates. Build 3662168 `corpISKTab.Update`, `walletUtil.AmAccountantOrJuniorAccountant`, and `HaveAccessToCorpWalletDivision` test individual Accountant and division-take bits; they do not infer those capabilities from Director. The server correctly authorized Directors, but advertised only their explicit assignments in session fields, so the client disabled ISK before requesting its balance.

Session serialization now expands CEO/Director capabilities to the known global and location-specific role masks. Stored assignments, member administration and grant limits retain their explicit values. Ordinary and limited members receive their existing permissions; Director revocation removes the expanded session capabilities. No balance, role assignment or live save is rewritten.

Evidence: inert disassembly of the pinned `code.ccp` archive, SHA-256 `37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368`. The existing [Carbon Blue reference](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp) at revision `fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae` remains the serialization reference; no codec behavior changes.

**Automated validation:** the two CEO/Director startup regressions failed before the correction. Five new cases check native permission predicates against encoded session packets, all seven division bits, an actual corporate balance reply, unchanged explicit assignments, limited-member isolation and revocation. Tool/solution restore and build passed with zero warnings/errors. Final filter `FullyQualifiedName~Corporation|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~PilotSessionTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting`, with `xUnit.MaxParallelThreads=4`, passed **136 tests in 22 seconds**. Both Python policy suites passed (3 catalog and 9 formatting tests), formatting verified 728 files and diff checks passed. The required sequence took **42.5 seconds**; no full .NET suite was run. Integration with the concurrently published localization correction passed a fresh build, 34 startup/localization/hosting tests in 6 seconds, formatting (730 files) and diff checks.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** After updating and restarting the server, reconnect a CEO/Director pilot and open Corporation Wallet to inspect the ISK header, division selector and journal.

## Corporation inventory ownership storage

World schema **78** replaces inventory/ship pilot-only owner references with a registry of valid pilot and player-corporation owners. Creation triggers register both kinds atomically. The pilot active-ship composite foreign key remains unchanged, so an active hull cannot be handed to a corporation. Personal asset operations retain their owner filters; no corporate read/transfer permission is added by this storage milestone alone. See the [ownership migration notes](../inventory-ownership.md) for table replacement, sequence/index/trigger preservation and rollback behavior.

This is SQLite persistence behavior, not a new Blue or Destiny shape. The existing pinned protocol/source references remain applicable. During development, historical-save tests exposed SQLite ALTER's validation of fitting triggers while the inventory name is temporarily absent; holding legacy ALTER mode during the atomic replacement preserves those other-table triggers. The final foreign-key check precedes commit and both connection settings are restored.

**Automated validation:** five new cases cover registered corporate owners, personal asset isolation, the active-ship constraint, unknown-owner rejection, disk/resident reopen, version-77 table replacement with custom indexes and fitting triggers, and corrupted-owner rollback. Filter `FullyQualifiedName~InventoryOwnershipTests` passed five tests in 3 seconds. The historical migration and immediate corporation tests also passed 22 focused cases after the ALTER correction. An initial broader run stopped with four historical-migration failures: older migrations can leave one inventory table on the current schema and the other pilot-only. The upgrade now validates each target independently and rebuilds both when either is old. The corrected historical/inventory run passed 20 tests in 6 seconds. Final tool/solution restore and build passed with zero warnings/errors. Filter `FullyQualifiedName~InventoryOwnershipTests|FullyQualifiedName~PilotInventoryTests|FullyQualifiedName~InventoryPersistenceTests|FullyQualifiedName~PilotFleetTests|FullyQualifiedName~ShipStorageTests|FullyQualifiedName~ContractAssetTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~Corporation|FullyQualifiedName~Eden.Protocol.Tests.Hosting`, with `xUnit.MaxParallelThreads=4`, passed **176 tests in 37 seconds**. Both Python suites passed (3 catalog and 9 formatting tests), formatting verified 732 files and diff checks passed. The required sequence took **58.3 seconds**; no full .NET suite was run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** None for the storage-only milestone; office and corporation inventory behavior follows separately.

## Corporate LP and EverMark wallets

World schema **77** adds corporate loyalty balances per NPC issuer and a saved transfer ledger. `LPSvc.GetAllMyCorporationWalletLPBalances` returns the pinned list of `(issuerCorporationID, points)` tuples for authorized financial readers; ordinary members/NPC corporations receive an empty list. Accountants/Junior Accountants and Directors may read; corporate transfers require Accountant or Director. `TransferLPFromMyWalletToOtherCorp` donates personal points to a player corporation, and `TransferLPFromMyCorpWalletToOtherCorp` transfers corporate LP to another player corporation. Both sides and the ledger commit together. Unknown actors/issuers/recipients, forged arguments, fractional/nonpositive amounts, insufficient funds and overflow reject without spending points. Donations are gifts and are not taxed as earned LP.

EverMarks use issuer **1000419** (Paragon) on the same balance surface. In standalone scope, personal EverMarks may go only to the pilot's current corporation after **30 days** of continuous current membership. Corporate EverMark transfers require alliance relationships and remain outside the requested standalone scope. The authoritative membership date comes from employment history; `charMgr.GetPrivateInfoOnCorpChange` now returns that date to the selected pilot, preserving None for NPC corporations and rejecting reads for another pilot. This enables the native eligibility check without exposing private membership data.

Personal donors receive native `OnLPChange`; authorized connected corporation observers receive `OnCorpLPChange(reason, issuerCorporationID)` and invalidate their cached balance. Polling does not duplicate an operation's own notification. Existing personal redemption balances remain compatible and survive the upgrade.

Sources: inert build **3662168** `loyaltyPointsWalletSvc._LoadCorporationLPData`/`TransferLPFromCorpToCorp`/`TransferLPFromPlayerToCorp`/`OnCorpLPChange`, personal/corporate donation panels, `personalDonateEMPanel._is_corp_membership_valid`, and `corporation.common.const.FLAG_MIN_ALLIANCE_MEMBERSHIP_DURATION_FOR_EM_DONATION_DEFAULT` (**30**) with the default donation kill switches false. Existing tuple/list/None adapters reuse the pinned Carbon Blue reference recorded below. No client execution or normal save modification occurred.

**Automated validation:** six new cases cover native tuple rows and donation calls, current read/spend permissions, issuer-by-issuer conservation, private membership reads, malformed/forged/insufficient/overflow rejection, exact EverMark membership boundary, ordered cache notifications without repeated polls, failed-write rollback, disk/resident reopen and schema-76 migration. Development filter `FullyQualifiedName~CorporationLoyaltyTests|FullyQualifiedName~LoyaltyWalletServiceTests`, with `xUnit.MaxParallelThreads=4`, passed 12 cases in 5 seconds. The first wider run stopped with 190 passes and one failure: a pre-existing market test resolved the stateless wallet reader from the root provider, revealing an unnecessary dependency on the scoped corporation notification handler. Corporate balance/journal read adaptation now lives directly in the stateless `WalletHandler`; only transfer notifications/monitoring require a connection scope. The corrected filter `FullyQualifiedName~CorporationLoyaltyTests|FullyQualifiedName~CorporationWalletTests|FullyQualifiedName~TestingMarketTests`, with `xUnit.MaxParallelThreads=4`, passed 27 tests in 4 seconds. Tool/solution restore and build passed with zero warnings/errors. Final filter `FullyQualifiedName~Corporation|FullyQualifiedName~LoyaltyWalletServiceTests|FullyQualifiedName~CurrencyRewardTests|FullyQualifiedName~RedemptionServiceTests|FullyQualifiedName~MarketTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting`, with `xUnit.MaxParallelThreads=4`, passed **191 tests in 25 seconds**. Both Python suites passed (3 catalog and 9 formatting tests), formatting verified 727 files, and diff checks passed. The final sequence took **41 seconds**; no full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Donate LP to a corporation and inspect both balances; inspect the EverMark membership eligibility and a second authorized member's balance refresh.

## Shared corporation ISK wallets and bounty taxation

World schema **76** adds six secondary division balances (master remains the existing corporation balance), saved per-member wallet selection, a shared typed transfer/tax ledger and an explicit bounty tax amount. Seven divisions use native keys **1000–1006**. `account.GetWalletDivisionsInfo`, corporate `GetCashBalance` and corporate `GetTransactions` expose authorized balances/history. Accountants and Junior Accountants may view divisions; withdrawal requires that division's Account Can Take bit or Director. Selecting a division with `corpRegistry.SetAccountKey` requires withdrawal access and updates `corpAccountKey` before later calls. Revocation resets an inaccessible selection; leaving clears it.

`account.GiveCash` implements the native personal funding request, including `toAccountKey`; `GiveCashFromCorpAccount` implements withdrawal/internal division transfers with `toAccountKey` and `reason`. Recipients are saved Eden pilots/player corporations. The authenticated selected pilot determines the source owner. Destination accounts, roles, balances, amount precision and overflow are checked inside the transfer transaction; both balances and both journal entries commit together. A failure leaves both sides unchanged. Public deposits convey no balance-read or withdrawal access. Personal and corporation observers receive authorized `OnAccountChange` updates. `WalletHandler` retains sole ownership of both personal/corporate balance and transaction service pairs.

Pirate bounties larger than the pinned **100,000 ISK** threshold pay the stored corporation tax rate to the master wallet, truncated to whole cents. Eden currently settles each NPC kill immediately, so the threshold applies per kill rather than to a retail-style delayed bounty batch. NPC-corporation pilots retain the existing reward behavior. Gross bounty, net personal balance, explicit personal tax debit and corporate tax credit settle with the kill/wreck/history transaction; replaying a destroyed NPC cannot pay again. Transfer entries use native Player Donation (10) and Corporation Account Withdrawal (37), and bounty tax uses Bounty Prize Corporation Tax (92). There are no mission reward/earned-LP sources to tax yet; this milestone does not tax testing grants, redemption tokens, ordinary gifts, market sales or insurance.

Sources: inert build **3662168** `accountsvc.GetWalletDivisionsInfo`/`GetCorpTransactions`, `walletSvc.GetCorpWealth`/`OnAccountChange`, `transferMoneyWnd.Confirm`, `corpDivisionsPanel.GetScrollEntry`/`DoSetAccountKey`, `bco_members.SetAccountKey`, `appConst.minCorporationTaxAmount` and the pinned accounting-entry catalog. `transactionsUtil.GetDerivedTransactions`/`GetTransactionHint` read safe YAML metadata; transfer reasons are escaped JSON under native **DESC**, a compatible subset that keeps player text from becoming derived journal metadata. The existing Carbon Blue revision and pinned client hash below remain applicable; no codec change or client execution occurred.

**Automated validation:** fifteen new cases cover native funding/withdrawal/division/journal shapes, conservation and exact balances, reasons, saved selection, read/take separation, revocation, malformed/forged/insufficient/overflow rejection, failed-write rollback, schema-75 migration, disk/resident reopen, tax threshold/rate boundaries and a fatal NPC settlement. The latter checks gross/net/tax journals and duplicate rejection; a forced tax-journal failure rolls back the NPC death and both wallets. Its journal check returns to the fixture's fully catalogued home station, avoiding unrelated missing asteroid metadata in its combat system. Development filter `FullyQualifiedName~CorporationWalletTests` passed 15 tests in 4 seconds. An initial wider run stopped after 306 passes and three failures: a test resolved the newly scoped wallet handler from the root provider, and two real-time combat tests assumed the first damage message/final packet had a particular direction/type. The scoped fixture was corrected. Those ordering checks now use `TestClock`, identify the outgoing target message explicitly and require the final call reply after the capsule transition while allowing later notifications. The two ordering checks passed five isolated cases on parent **9653397**; the corrected current filter `FullyQualifiedName~FatalShotSendsCombatTextBeforeRemovingTheTargetsClientRecord|FullyQualifiedName~SessionSendsTheFatalMessageAndCapsuleBallBeforeChangingShipId|FullyQualifiedName~FatalRetaliationStopsTheGunLoadsACapsuleAndRemovesTheOldHullForObservers`, with `xUnit.MaxParallelThreads=4`, passed seven cases in 6 seconds. Tool/solution restore and build passed with zero warnings/errors. Final filter `FullyQualifiedName~Corporation|FullyQualifiedName~Wallet|FullyQualifiedName~PilotCombatTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~MissileCombatTests|FullyQualifiedName~WeaponBankTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~Eden.Protocol.Tests.Serialization|FullyQualifiedName~Eden.Protocol.Tests.Hosting`, with `xUnit.MaxParallelThreads=4`, passed **309 tests in 45 seconds**. Both Python suites passed (3 catalog and 9 formatting tests), formatting verified 724 files, and diff checks passed. The final validation sequence took **61 seconds**. No full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Fund a division and transfer ISK to a member; select a division and reopen the wallet; kill a qualifying pirate and inspect the bounty/tax entries.

## Delegated corporation roles and titles

World schema **75** adds saved member access and title records plus ordered role/title events. `corpRegistry.UpdateMember` accepts the pinned 15 arguments; `UpdateMembers` accepts the native 14-column batch. `UpdateTitle`, `UpdateTitles` and `DeleteTitle` persist title roles; `GetTitles` returns sixteen native `util.Row` slots for player corporations and an empty map for NPC corporations. Blank names select the client's localized untitled labels. Batch validation is atomic: an invalid later target or grant rolls back earlier changes.

Role masks use the pinned `appConst.corpRole*` values. Global, HQ, base and other-location assignments and grantable masks are separate. Local masks accept hangar/container permissions; Director and other administrative roles remain global. Delegates may change only bits they can grant and cannot grant further delegation. Only the CEO may appoint/change Directors; CEO roles remain fixed. Titles cannot confer Director. Effective title roles refresh member reads, public title names and selected sessions. Director access enables corporation settings; personnel access enables application review/offers and member removal. Public profile replies expose names only, not grantable masks.

Members may remove their own roles using the exact native `base_corporation.RemoveAllRoles` request and block reassignment. `CanLeaveCurrentCorporation` returns `CrpCantQuitNotInStasis` when roles remain, allowing the home panel to offer that action. Leaving/removal is denied until 24 hours after losing the final assigned/grantable role. The saved timestamp is checked with `TimeProvider`, survives restart and clears with old access when membership changes. A role refresh preserves the current corporation binding; operations recheck current permissions, while a corporation change still releases old bindings.

Sources: inert build **3662168** inspection of `appConst`, `bco_members.UpdateMember`/`UpdateMembers`/`GetMyGrantableRoles`, `corp_ui_member_newroles._CreateRowsToUpdate`, `corp_ui_member_titles.SaveChanges`, `bco_titles.GetTitles`/`OnTitleChanged`, `base_corporation.RemoveAllRoles`, `corp_ui_home.RemoveAllRoles` and `carbon/common/script/sys/row.Row.__getstate__`/`__setstate__`. The same pinned hash and Carbon Blue revision recorded below apply. No client execution or new codec behavior is introduced.

**Automated validation:** eight new cases cover native scalar/bulk/title row shapes, public names, scoped session fields and binding reuse, grant escalation/CEO protection, malformed masks/targets, batch rollback, title edits/deletion, ordered notifications without duplicates, disk/resident reopen, schema-74 migration and the persisted 24-hour boundary with `TestClock`. Development filter `FullyQualifiedName~CorporationRoleTests` passed eight tests in 4 seconds. Existing creation tests now expect sixteen title slots instead of the earlier deferred empty map. Tool/solution restore and build passed with zero warnings/errors. Final filter `FullyQualifiedName~Corporation|FullyQualifiedName~CharacterSheetServiceTests|FullyQualifiedName~HomeStationServiceTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~ChatWorldTests|FullyQualifiedName~ChatServiceTests|FullyQualifiedName~Eden.Protocol.Tests.Serialization|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed **177 tests in 36 seconds**. Both Python suites passed (3 catalog and 9 formatting tests); formatting verified 720 files, and diff checks passed. The complete sequence took **54 seconds**. No full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Assign a title and a limited role to another member; inspect their updated access. Remove all roles and inspect the leave cooldown.

## Division names after a save

Build 3662168's `corp_ui_home.DivisionsForm` opens for the CEO and displays the Master Wallet as a fixed label.
`ApplyDivisionNames` (163–253) forwards the edited names and the other current values to
`base_corporation.UpdateDivisionNames`, which calls `corpRegistry.UpdateDivisionNames` with seven hangar fields
followed by seven wallet fields (9–54). The server already persisted this request and returned the committed names
on a fresh corporation read.

The client applies the live update through `CorporationsO.OnCorporationChanged`. Its shared
`BaseCorpObject.GetAddRemoveFromChange` classifier (64–158) treats an all-None previous side as an insertion and an
all-None new side as a removal. A first rename from localized defaults supplied an insertion-shaped partial delta.
The corporation cache rejected it because the update lacked the complete DBRow (39–91). Clearing every name could
also remove the cached row. Nullable-only updates now carry the unchanged `corporationID` pair, so both the CEO's
and current members' caches apply the changes to the existing row.

The signed archive SHA-256 is `37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.
Local EVE.js 261108.1 `corpRegistryRuntime.js` (`41d691387ff4`) also uses fourteen positional fields.
Its `corporationRuntimeState.js` (`2a324a6c6ac4`) supplies non-null initial strings. Eden leaves unset fields
nullable so the pinned client can use localized labels. [Corporation divisions](https://wiki.eveuniversity.org/Corporation_logistics#Corporation_hangars)

**Automated validation.** `CorporationAdministrationTests.DivisionNamesUpdateLoadedCorporationRowsWhenSavedOrCleared`
passed in the combined five-case exact run. It replays the pinned classifier against the CEO and member updates
after both a first save and a clear, and checks that an ordinary member cannot edit names. The build took 15.83 seconds
and the tests took 9.70 seconds.

## Persistent corporation administration

`corpRegistry.UpdateCorporation` now saves description, website, ISK/LP tax rates and application availability using the pinned five-argument request. `UpdateLogo` saves all seven nullable logo identifiers; `UpdateDivisionNames` saves fourteen nullable names. Identity names/tickers remain immutable. Validation rejects nonfinite/out-of-range rates, oversized/control-bearing fields, negative logo IDs and unauthorized actors. These operations initially require the current CEO; delegated access follows in the roles milestone. Identical edits emit no events.

World schema **74** adds `AdministrationJson` to the existing ordered per-pilot corporation change stream. Detail edits commit with an event for each current member; connected sessions receive `OnCorporationChanged(corporationID, changes)` with explicit before/after field pairs. A polling member sees each edit once in order. Existing JSON details gain an optional typed division-names record, preserving saves without it. Native corporation DBRows return the saved names using the existing shared descriptor. Settings edits neither charge a fee nor change membership/assets.

Sources: inert pinned build **3662168** inspection of `bco_corporations.UpdateCorporation`/`UpdateLogo`/`OnCorporationChanged` and `base_corporation.UpdateDivisionNames`; the pinned `code.ccp` hash and Carbon Blue **fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae** reference from the preceding corrections remain unchanged. Explicit Unicode, tuple, None and notification adapters are reused; no codec change or native client execution occurred.

**Automated validation:** focused tests cover native arguments, unchanged-state rejections, no-op writes, ordered notifications without duplicates, exact saved division fields, disk/resident reopen and migration from schema 73. Development filter `FullyQualifiedName~CorporationAdministrationTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~PlayerCorporationTests` passed 38 tests in 6 seconds. Tool/solution restore and build passed with zero warnings/errors. Final filter `FullyQualifiedName~Corporation|FullyQualifiedName~HomeStationServiceTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed **116 tests in 24 seconds**. Both Python suites passed (3 catalog and 9 formatting tests), formatting verified 715 files, and diff checks passed. The complete validation sequence took **42 seconds**; no full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Edit corporation details/logo/division names, then inspect them from another connected member.

## Update corporation with CEO skills

The pinned corporation details dialog calls `corpRegistry.UpdateCorporationAbilities()` through its bound registry.
It expects a numeric result, then forces a fresh corporation read and displays `memberLimit`. A successful update saves
the current CEO's trained capacity and sends `OnCorporationChanged` with the previous and new limit to corporation members.
Directors cannot apply their skills in place of the CEO. Lowering the limit can block new admissions while existing members
keep their membership. Training by itself leaves the last saved limit in effect.

REA extracted the signed build **3662168** client modules in
`ev_3e5562fa11dbf77c6ba842dfc1dbdf91b2bfdd95064b2b0c93a820d2f889a428`. In
`corp_dlg_editcorpdetails.pyj`, `CorpDetails.UpdateWithSkills` calls with no arguments at offset 15, checks for
`None` at offsets 18–30 and rereads the corporation at offsets 31–58. `bco_corporations.pyj` forwards the
zero-argument call to the registry. The pinned catalog's `corporationMemberBonus` attribute **191** supplies
**20**, **100**, **400**, and **2,000** members per trained level for Corporation Management (**3363**), Megacorp
Management (**3731**), Empire Control (**3732**), and Sovereignty (**12241**). EVE.js
`corporationMemberLimit.js` and `corpRegistryRuntime.js` use the same catalog bonus and return a numeric limit.
[EVE University corporation skill workflow](https://wiki.eveuniversity.org/Forming_a_corporation#Relevant_skills)

`BoundUpdateWithMySkillsRefreshesAndPersistsCapacityForTheActiveCeo` passed both cases in the combined
five-case exact run. It verifies the nested bind, the full **12,600** and zero capacity values, director rejection,
the forced reread, no-op notification suppression, and checkpoint reopen. The build took 15.83 seconds and
tests took 9.70 seconds.

## Player corporation character-selection logo correction

The maintainer's character-selection screenshot showed the NPC-corporation pilot's complete card, while the player-corporation pilot had a name but no skill, wallet or mail information. In pinned build 3662168, `characterSlots.LoadSlot` constructs the corporation logo synchronously before filling those fields. `eveIcon.CorpIcon.DoLayout` reads `shape1`, `shape2`, `shape3`, `color1`, `color2` and `color3` from `cfg.corptickernames`. Eden's `config.GetMultiCorpTickerNamesEx` returned only `corporationID` and `tickerName`, omitting all six attributes. The NPC logo uses a different native path, explaining the different card behavior.

The account-phase ticker reply now uses the pinned eight-column schema: corporation ID, ticker, three shapes and three colors. Saved player-corporation rows supply their actual logo values; unset layers and NPC rows contain explicit None values, matching `eveCfg.LoadCrpTickerNames`. Saved player rows take precedence over roster/visible-pilot shortcuts, which carry only a ticker and previously discarded logo data. Public lookup works before selection, for an off-roster corporation, after selection and after logoff. This changes no world schema, membership or money and does not enable logo editing.

Sources: inert inspection of build **3662168** `characterSlots.LoadSlot`, `eveIcon.GetLogoIcon`/`CorpIcon.DoLayout` and `eveCfg.LoadCrpTickerNames`, from `code.ccp` SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**. Existing tuple/list/None encoding was checked against the previously consulted [Carbon Blue Marshal.cpp revision fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp). No codec change, game launch, client-script execution or normal save modification occurred.

**Automated validation:** four simulated-session regressions cover empty and populated saved logos, immediate reads and a rebuilt service provider/world reopen. Each exercises the CEO and another account, before selection, after selection and after logoff, and checks exact decoded headers/layers, NPC None fields, duplicate/unknown IDs, public owner identity, card skill points/balance and unchanged corporation/payment state. After supplying the test catalog's NPC metadata, all four regressions failed on the old two-column response (3 seconds). The corrected development filter `FullyQualifiedName~CorporationSelectionTests|FullyQualifiedName~StationGuestTests` passed nine tests in 5 seconds. Final tool/solution restore and build passed with zero warnings/errors. Filter `FullyQualifiedName~CorporationSelectionTests|FullyQualifiedName~PlayerCorporationTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~CorporationScreenTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~HomeStationServiceTests|FullyQualifiedName~CharacterSheetServiceTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~Eden.Protocol.Tests.Serialization|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed **132 tests in 23 seconds**. Both Python policy suites passed (3 catalog and 9 formatting tests), formatting verified 711 files, and diff checks passed. The complete validation sequence took **40 seconds**. No full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled. Corrected card rendering and input remain unverified.

**Optional maintainer checks:** Rebuild/restart the server, restart the client to refresh its ticker cache, and inspect/select the existing player-corporation pilot.

## Corporation creation CEO refresh correction

The maintainer reported **Unsupported local call corpRegistry.MachoBindObject** after pressing Submit in Create Corporation. Read-only inspection of the saved world found corporation **98000000**, CEO **90000006**, the pilot's matching membership and exactly one formation payment. The creation timestamp precedes the native game log's bind error. Formation had committed successfully; the failure was during the ensuing CEO UI initialization. The agent did not change that save or repeat creation.

After a corporation change, the native window reconstructs its administration panels. `corporationWindow.construct_admin_members_panels` constructs `CorpTitles`, and `corp_ui_member_titles.CorpTitles.ApplyAttributes` immediately calls `corp.GetTitles`. `bco_titles.TitlesO.GetTitles` binds `corpRegistry.GetTitles` and expects a dictionary whose values are title rows. That read had no Eden handler. When embedded in `MachoBindObject`, its unsupported result was reported under the outer binding method, concealing the missing `GetTitles` call. Both creation through an existing reference and creation embedded in binding reproduce the post-creation error in automated sessions.

`corpRegistry.GetTitles()` now returns an empty native indexed dictionary while delegated titles remain unimplemented. Malformed reads reject and title/member role writes remain unsupported. These same panel constructors call `GetRoleGroupings` and `GetDivisionNames`; the latter reads `division1` through `division7` and `walletDivision2` through `walletDivision7`. NPC and player corporation DBRows now share a **38-column** descriptor, preserving the original 25-column prefix and appending these 13 nullable Unicode fields. Their None values select the native localized defaults. This supplies the expected UI fields without creating shared assets, wallet divisions, role assignments or a new database schema.

Sources: inert inspection of build **3662168**, `code.ccp` SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**, especially `corp_dlg_editcorpdetails.CreateCorp.Submit`, `base_corporation.GetCorpRegistry`, `eveMoniker.GetCorpRegistryEx`, `moniker.Bind`/`MonikeredCall`, `corporationWindow.construct_administration_panels`/`construct_admin_members_panels`, `corp_ui_member_titles.CorpTitles.ApplyAttributes`, `bco_titles.GetTitles` and `bco_corporations.GetDivisionNames`. Existing dictionary, None, object-reference and DBRow adapters reuse the previously consulted [Carbon Blue Marshal.cpp revision fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp). No codec change, client launch or client-script execution was needed.

**Automated validation:** before the correction, filter `FullyQualifiedName~CreationThenNativeCeoRefreshBindsTitlesWithoutRepeatingFormation` failed both cases on the 6588964 behavior with the same unsupported `corpRegistry.MachoBindObject` response (3 seconds). The corrected sequence asserts the exact bound-reference/title-map reply, subsequent roster/member/corporation reads, and one corporation/payment plus the correct employment and wallet state. Bound-reference replies are compared as expected wire bytes because the inbound decoder intentionally does not deserialize outbound object references. Additional coverage checks empty title reads, rejected arguments, disabled mutations and the shared NPC/player division field names/types/defaults. Tool/solution restore and build passed with zero warnings/errors. Filter `FullyQualifiedName~PlayerCorporationTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~CorporationScreenTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~HomeStationServiceTests|FullyQualifiedName~CharacterSheetServiceTests|FullyQualifiedName~Eden.Protocol.Tests.Serialization|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed **123 tests in 16 seconds**. Both Python policy suites passed (3 catalog and 9 formatting tests), formatting verified 710 files, and diff checks passed. The complete validation sequence took **33 seconds**. No full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled. The screenshot and saved state establish the original post-creation failure; corrected native CEO-panel rendering remains unverified.

**Optional maintainer checks:** Rebuild/restart the server, relog and reopen the existing corporation window; inspect its Details and Members panels. The saved corporation already exists, so another formation attempt is unnecessary.

## Corporation Details faction-enlistment service correction

The next reported Details-panel failure was **Unsupported local call fwCharacterEnlistmentMgr.GetCorpAllowedEnlistmentFactions**. Eden already returned the intended empty faction list, but the implementation registered it under `facWarMgr`. The pinned Details panel calls the local `fwEnlistmentSvc.GetCorpAllowedEnlistmentFactions`, whose wrapper forwards to the distinct remote service **fwCharacterEnlistmentMgr** with one corporation ID. The earlier audit stopped at the local method name and missed this final remote-service boundary.

The selected-pilot handler now owns the exact native service/method pair and validates one positive Int32 corporation ID. It returns a native empty **list**, so the Details panel renders no allowed enlistment factions while faction warfare remains unimplemented. Enrollment operations remain unsupported; malformed requests reject without changing corporation membership, roles or assets. No schema or codec changes are introduced.

The expanded automated startup sequence covers selection, voting capability, corporation details, the native enlistment route, structure acceptance, restricted-mail settings, aggression settings and office reads for NPC members and player CEOs. It verifies the actual decoded empty list and surrounding replies, rather than asserting only that a dispatch succeeded. The NPC fixture includes the catalog metadata needed by the real corporation-details reader. An initial expanded test failed on absent NPC fixture metadata; adding that metadata made all eight screen cases pass without changing the reader or relaxing its errors.

Sources: inert inspection of unchanged build **3662168** `code.ccp`, SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**, particularly `corp_ui_home.GetSubContentDetails` and `fwEnlistmentSvc.GetCorpAllowedEnlistmentFactions`. The surrounding Details registry reads were checked at their native call sites. This reuses the existing list adapter and previously consulted [Carbon Blue Marshal.cpp at fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp); no Carbon-owned simulation or serialization behavior changes. No game launch, client-script execution or normal runtime database modification occurred.

**Automated validation:** after rebasing onto logging milestone **d201912**, tool/solution restore and build passed with zero warnings/errors. Filter `FullyQualifiedName~CorporationScreenTests|FullyQualifiedName~PlayerCorporationTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~HomeStationServiceTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed **80 tests in 15 seconds**, including all eight corporation-screen cases. Both Python policy suites passed (3 catalog and 9 formatting tests), formatting verified 710 files, and diff checks passed. The complete sequence took **36 seconds**. No full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled. The screenshot establishes the original routing failure; corrected native Details rendering remains unverified.

**Optional maintainer checks:** Rebuild/restart, reopen the corporation screen and expand Details for an NPC member and a player-corporation CEO.

## Opportunities enlistment and Freelance Jobs startup

The reported Opportunities window showed an unsupported `fwCharacterEnlistmentMgr.GetMyEnlistment` dialog and a separate Freelance Jobs loading error. The Factional Warfare and Pirate Insurgencies providers call `fwEnlistmentSvc.GetMyFaction`, which refreshes its cache through the remote read. The selected-pilot service now answers the zero-argument call with the native three-element tuple of Python None values, in effective, direct, and corporation faction order.

The Freelance Jobs provider separately loads nearby broadcasts, committed projects, and unclaimed rewards. The public gateway now returns HTTP 200 and the corresponding native response type for `GetAllBroadcasted`, `GetAllCommitted`, and `GetAllUnredeemed`. The client treats omitted system entries as having no broadcasts and the default empty next-page token as the end of rewards pagination. Eden has no faction warfare enlistments or freelance projects to publish.

Inert inspection used build **3662168**, `code.ccp` SHA-256 **C317DDF3292AF412752157DC8DC58991F30E39E106AE6B7F0A2F67A77847721D**. The wire contracts come from `fwEnlistmentSvc._UpdateMyEnlistmentState`, the Freelance Projects provider, repository and messenger, and `eve_public/freelance/project/api/requests_pb2.pyj`. EVE.js release **261108.1** (client build **3396210**) was checked as a behavioral reference. [Carbon Blue tuple and None serialization at af0d65b](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp) · [EVE University faction warfare](https://wiki.eveuniversity.org/Faction_Warfare) · [CCP Freelance Jobs](https://support.eveonline.com/hc/en-us/articles/20210930509340-Freelance-Jobs)

**Automated validation.** Five targeted regressions reproduced the two session errors and three gateway 501 responses (11.42 seconds), then passed with the correction (15.03 seconds). The session tests cover NPC and player corporation members, repeat reads, preselection and malformed calls, and unchanged pilot state. Gateway tests assert native response envelopes and empty payloads for representative requests.

The focused filter `FullyQualifiedName~CorporationScreenTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~CorporationShowInfoTests|FullyQualifiedName~PlayerCorporationTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~Eden.Protocol.Tests.Gateway` passed **116 tests** in **62.40 seconds**. Tool and solution restore, solution build with zero warnings or errors, both Python policy suites (8 catalog and 9 formatting tests), formatting verification, and diff checks passed in **101.25 seconds** overall. Validation used automated sessions and gateway requests.

**Live acceptance.** Not performed because agent client testing is disabled. The screenshot records the original behavior.

**Optional maintainer checks.** After updating the server, open Opportunities and refresh Freelance Jobs. Verify the enlistment dialog stays closed and the panel displays its empty state.

## Corporation window voting-capability startup correction

The maintainer reported **Invalid Packet Destination — Unsupported local call voteManager.CanViewVotes** when opening the corporation screen after the player-corporation milestones. The pinned client's `corporationWindow.construct_corporation_panels` asks `corpvotes.CanViewVotes(session.corpid)` while building the window for both NPC and player corporations. `corporation/voting/voting.CanViewVotes` forwards a single corporation ID to the unbound `voteManager.CanViewVotes` service and caches the boolean reply for the current corporation. Eden had no owner for that service/method, so its router sent the unsupported-call error before window construction could finish.

A selected-pilot governance handler now validates the single positive Int32 corporation ID and returns native **False** while Eden's voting/share-governance functionality remains unimplemented. The native window then skips `construct_corporation_voting_panels`; the existing corporation details and membership features remain available. The explicit no-access path in `corp_ui_votes.ShowOpenVotes` also returns before querying vote cases. This correction does not advertise voting access or implement vote reads/writes. Malformed queries reject; unsupported voting operations remain unsupported. No world schema change is required.

Sources: inert inspection of build **3662168** `code.ccp`, SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**, specifically `corporationWindow.construct_corporation_panels`, `corporation/voting/voting.CanViewVotes` and `corp_ui_votes.ShowOpenVotes`. Existing scalar boolean serialization was checked against [Carbon Blue Marshal.cpp revision fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp), `ReadObjectFalse` and `WriteObject`'s `TY_FALSE` path. No game launch or client-script execution occurred. The screenshot establishes the original failure, not acceptance of the corrected corporation screen.

**Automated validation:** the four new cases exercise real simulated sessions with repeated native requests for NPC members and player CEOs, assert actual decoded boolean False replies without ErrorResponse, and cover selection gating, malformed arguments, metadata filtering, unchanged corporation state and disabled voting mutations. Tool/solution restore and build passed with zero warnings/errors. Filter `FullyQualifiedName~CorporationScreenTests|FullyQualifiedName~PlayerCorporationTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed **66 tests in 11 seconds**. Both Python policy suites passed (3 catalog and 9 formatting cases), formatting verified 707 files, and diff checks passed. The complete validation sequence took **26 seconds**. No full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled. Native window construction/rendering after the correction remains unverified.

**Optional maintainer checks:** Rebuild/restart and open the corporation screen; close and reopen it for an NPC member and a player-corporation CEO.

## Player corporation applications and membership

World schema **73** adds persistent applications and per-pilot ordered corporation change records. `corpRegistry.InsertApplication`, `GetMyApplications`/`GetApplications`, `GetMyOldApplications`/`GetOldApplications`, and the seven-argument `UpdateApplicationOffer` implement application submission, CEO offers/rejections, applicant withdrawal/rejection/acceptance and renegotiation. Active application dictionaries group by corporation for applicants and character for CEOs; historical applications remain readable after they close. The saved applicant, corporation and application IDs are validated together; a supplied application date must match the original. Recruiters cannot overwrite the applicant's statement. Active duplicates, closed recruitment, invalid text and unauthorized status changes reject without modifying membership or wallets.

The native flow requires **both** a CEO offer (status 6) and applicant acceptance (status 2). A CEO cannot force an applicant into the corporation. Acceptance checks current membership, current CEO status and corporation capacity again in the same transaction, changes the pilot's corporation, appends employment, closes the application and withdraws other open applications. Forming a corporation also withdraws its founder's open applications. Nominal roles remain zero for ordinary members; the CEO session exposes director (1) and personnel-manager (128) bits because the pinned applications UI checks the personnel bit directly. Delegated roles, cooldowns for relinquishing such roles and CEO transfers remain deferred.

`CanLeaveCurrentCorporation` returns the native three-field permission/error reply; `CanBeKickedOut` and `KickOutMember` support leaving and CEO removal of ordinary members. A member returns to the first recorded NPC corporation without moving ships, assets, station or home station, and without transferring ISK. NPC-corporation pilots cannot use this operation to choose another NPC corporation. The CEO cannot be removed or leave while CEO transfer/dissolution remain unavailable. Direct invitations and bulk removals are deferred.

Application events retain their full typed snapshot and target only the applicant and owning CEO. Membership events target connected old/new corporation members through persisted per-pilot records. Session polling drains events in order with a bounded cursor, publishes `OnCorporationApplicationChanged`, `OnCorporationMemberChanged` and member-count `OnCorporationChanged`, and does not repeat them on the next tick. Login establishes a current cursor and reads authoritative state rather than replaying historical changes. Both RPC-driven and idle changes update corporation ID/roles before later notifications. Chat access follows the selected identity, station guest tuples are refreshed on corporation changes, and existing space observers receive `OnSlimItemChange` without a park or movement reset.

Member reads include `GetMembersByIds` and `GetMembersPaged`. The latter returns `eve.common.script.util.pagedCollection.PagedResultSet` with collection/count/page/page-size fields; requests are one-based while response offsets are zero-based, following the native collection's insertion offset. `corpmgr.GetAggressionSettings` and the selected registry return the native `crimewatch.corp_aggression.settings.AggressionSettings` state for the stored creation preference, allowing the application dialog to render it. This read does not add friendly-fire combat rules. Faction enlistment returns no eligible factions while faction warfare remains deferred.

Sources: inert build-3662168 inspection of `bco_applications`, `corp_ui_applications`, `base_corporation.CanLeaveCurrentCorporation`/`KickOut`, `bco_members` member/notification paths, `pagedCollection.PagedResultSet`/`PagedCollection.Add`, `crimewatch/corp_aggression/settings`, and `appConst.crpApplication*`/`corpRolePersonnelManager`. The pinned `code.ccp` hash and consulted Carbon Blue **fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae** sources from the foundation remain unchanged; no new codec or Destiny binary shape is introduced. No game launch, client-script execution or normal runtime database modification occurred.

Automated validation covers consent, withdrawal/rejection/reapplication, malformed and forged requests, private reads, full-corporation rejection, unchanged money/assets, disk/resident reopen, version-72 migration, repeated ordered membership cycles, online session/chat/station updates and existing space-observer slims. Tool/solution restore and build passed with zero warnings/errors. The filter `FullyQualifiedName~Corporation|FullyQualifiedName~HomeStationServiceTests|FullyQualifiedName~CharacterSheetServiceTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~ChatWorldTests|FullyQualifiedName~XmppChatTests|FullyQualifiedName~Wallet` passed **178 tests in 44 seconds**, including all 16 membership cases. Both Python policy suites passed (3 catalog and 9 formatting tests); formatting checked 705 files. The complete validation sequence took **61 seconds**. No full .NET suite was requested or run. Live acceptance: **Not performed; agent client testing is disabled.** Optional maintainer checks: apply from another account and accept a CEO offer; leave/rejoin while both pilots are connected and inspect member lists/chat; inspect corporation identity from another pilot in space. Native rendering and input remain unverified.

## Player corporations: creation foundation

The first player-corporation milestone implements `corpRegistry.AddCorporation` with the pinned 15 positional arguments, `GetSuggestedTickerNames`, corporation-scoped `GetEveOwners`/`GetMember`, `corpRegistry.GetCorporation`, `corpmgr.GetCorporations`, and dynamic owner/ticker priming. The original corporation replies used the same 25-column native DBRow descriptor for NPC and player corporations (expanded to 38 columns by the CEO-refresh correction above): the client's `LoadCorporation` retains its first descriptor when caching later rows. Ticker suggestions are rows with `tickerName`, as required by the native creation dialog.

Formation requires an existing docked pilot with Corporation Management I and 1,599,800 ISK, and rejects an existing CEO. A transaction checks unique names/tickers, saves the corporation and founder/CEO, moves membership, records the original and new employment entries, pays the fee and saves its wallet journal entry (native reference type 39). The creation station becomes headquarters; capacity is twenty members per trained Corporation Management level at formation. Names/tickers, website, description, finite fractional tax settings, logo values, application availability and friendly-fire preference are saved. Shared wallets/assets, tax collection, friendly-fire combat policy, logo editing, delegated roles, shares/votes, alliances and dissolution are deferred. The stored nominal share count does not implement share ownership.

World schema **72** adds corporations, employment and formation payments. Startup accepts saved player corporation memberships and checks their CEOs. The existing pilot corporation ID remains the membership authority. The CEO receives the native director role bit; session changes include corporation ID and corporation role fields before the successful formation reply. Old corporation registry references are invalidated; new binds require the pilot's current corporation. Chat uses the resulting selected-pilot identity on the existing update path. Public employment history includes the real starter corporation instead of attributing a character's birth to the newly formed corporation.

Sources: inert inspection of build **3662168**, `code.ccp` SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**, especially `bco_corporations.AddCorporation`/`LoadCorporation`, `bco_members.MemberCanCreateCorporation`/`__PrimeEveOwners`, `corp_dlg_editcorpdetails.Submit`/`PickTicker`, `corp_ui_home.GetSubContentDetails` and `appConst.corporationStartupCost`, `corpRoleDirector`, `refCorporationRegistrationFee`. Consulted the public Carbon repository inventory: no public corporation gameplay service owns these rules. Existing DBRow and tuple/list adapters reuse [Carbon Blue Marshal.cpp at fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp), including `ReadObjectDBRow` and the DBRow writer. No game launch or client-code execution occurred.

Automated validation: focused creation tests cover native argument/reply shapes, owner priming, CEO role/session ordering, old registry rejection, fee/journal state, malformed and rejected requests, duplicate identities, version-71 migration and disk/resident-world reopen. Tool/solution restore and build passed with zero warnings/errors. The filter `FullyQualifiedName~PlayerCorporationTests|FullyQualifiedName~HomeStationServiceTests|FullyQualifiedName~CharacterSheetServiceTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~ChatWorldTests|FullyQualifiedName~ChatServiceTests|FullyQualifiedName~Wallet` passed **141 tests in 31 seconds**. Both Python policy suites passed (3 catalog and 9 formatting tests), and formatting verification checked 701 files. The complete validation sequence took **49 seconds**; no full .NET suite was requested or run. Live acceptance: **Not performed; agent client testing is disabled.** Optional maintainer checks: form a corporation and inspect its details; reconnect and inspect the CEO, ticker and employment history.


## Resident corporation services

Corporation creation, membership and employment, role and title changes, contact labels,
bills, office rentals and storage, shares, wallets, and loyalty point transfers use resident
state. A new corporation's inventory owner, shares, and bookmark access group publish with
its founding action. Corporation wallet and loyalty store purchases commit the debit, journal,
and inventory changes together. Corporation change cursors and financial reads use the
same committed state.

Gameplay references are [managing corporation members](https://wiki.eveuniversity.org/Managing_corporation_members),
[corporation logistics](https://wiki.eveuniversity.org/Corporation_logistics), and
[loyalty points](https://wiki.eveuniversity.org/Loyalty_Points). The local EveJS 261108.1
comparison used `officeRentalSettlementState.js` and `walletState.js` (SHA-256 prefixes
`98B9BFFB17EF87C0` and `83965CB640B33208`).

**Automated validation.** The focused corporation, loyalty, wallet history, character deletion,
shared bookmark, and access-group filter passed 262 tests in 1 minute 57 seconds. The run
covered file and resident operation, projection and reopen, permission failures, and ordered
notifications. Solution build, both Python policy suites, and formatting verification passed.

**Live acceptance.** Not performed. Agent client testing is disabled.

**Optional maintainer checks.** None.
