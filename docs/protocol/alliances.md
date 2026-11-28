# Alliances

## Scope and references

Alliances are corporation associations with resident authority and checkpoint persistence. The original six milestones
cover identity, governance, billing, diplomacy and shared services. Later records cover
[sovereignty](sovereignty.md) and [Faction Warfare enlistment](faction-warfare.md#organization-enlistment-and-militia-communications--november-20-2026).
[Corporation and alliance wars](corporations.md#corporation-and-alliance-wars) cover executor declarations, billing and alliance membership.

Gameplay references: [UniWiki Alliances](https://wiki.eveuniversity.org/Alliances),
[corporation diplomacy](https://wiki.eveuniversity.org/Corporation_diplomacy), and
[CCP closure guidance](https://support.eveonline.com/hc/en-us/articles/203209062-Corporation-Alliance-Closure).

The local evejs source snapshot at `F:\Git Repos\evejs` has no Git metadata. Its package declares AGPL-3.0-only.
Reference its behavior, without copying implementation into Eden. Relevant files under `server/src/services/corporation/`
are `allianceFormationEligibility.js`, `allianceViewState.js`, `allianceApplicationState.js`,
`allianceGovernanceState.js`, `allianceDepartureState.js`, `allianceMaintenanceBilling.js`, and
`allianceAffiliationSettlement.js`. Source hashes are recorded with each implemented contract below.

The owning wire contract is Windows client build **3662168**, `code.ccp` SHA-256
`238ef5811fcecd77db8ecdb8f143f0e74bbd337d225a2582ed25132def900d8c` for the prepared installation
at `E:\EdenDev\3662168` at the time of the original alliance implementation. Its `start.ini` identifies V24.01/build 3662168.
The prepared archive differs from the signed original recorded by the development preparation script.
Static Python bytecode inspection covers `ui/services/alliances/all_cso*.pyj`,
`ui/services/corporation/bco_alliance.pyj`, and the non-sovereignty `ui/shared/neocom/Alliances/all_ui*.pyj` panels.
It confirms `corpRegistry.CreateAlliance(name, shortName, description, url)`, application management,
`allianceRegistry` public and bound member/application reads, executor support, bills, contacts and bulletins.
Membership cache handlers require native DBRow descriptors even for initially empty rowsets, and clear on `allianceid` changes.
The evejs `allianceViewState.js` snapshot SHA-256 is
`ebaee4bb2f6dcfe0b4cf6a864cb05b9a38d3963e06f268cf3ecb5a83c441909b`.
All new wire models use the existing codec. Its reference remains
[Carbon Blue Marshal.cpp, af0d65b29f951260ea09140ca11adf5d40566997](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp).

## Alliance registry binding after formation

The Create Alliance dialog runs `corpRegistry.CreateAlliance` inside `PerformSessionChange` and closes after the call
completes. When the incoming session delta sets `allianceid`, the client alliance service immediately binds its registry
moniker. Build 3662168's `eveMoniker.GetAlliance` calls `GetAllianceEx(session.allianceid, 1)`, which constructs the
`allianceRegistry` moniker with `(allianceID, isMaster)`. Eden's scalar-only validation rejected that tuple in both
`MachoResolveObject` and `MachoBindObject`, leaving the client without a working registry after formation.

Both calls accept the native master tuple and check the ID against resident corporation affiliation before dispatching
an embedded request. The existing scalar form remains accepted for the current alliance. The founding session delta
precedes the creation reply, as it does in the EVE.js founding flow.

Static client evidence comes from the signed build-3662168 archive with SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`. The relevant functions are
`all_ui_home.CreateAllianceWnd.Confirm_thread`, `all_cso.Alliances.OnSessionChanged` and `GetMoniker`, and
`eveMoniker.GetAlliance` and `GetAllianceEx`. The installed build 3749835 has the same binding and window sequence
in archive SHA-256 `870a30c0e01909c1427d3996ad9aca5110ae798d4b6d837b053211f72a830e9f`.
EVE.js `allianceRegistryRuntime.js` (`073f759c9ea8`)
also accepts tuple and scalar parameters. Its `allianceFoundingState.js` (`f5781a5c0fc2`) records the founding
flow, with affiliation synchronization in `corporationSessionAffiliationSync.js`.

**Automated validation.** The exact test
`Eden.Protocol.Tests.Alliances.AllianceMembershipTests.NativeCreationPublishesAllianceSessionChangeBeforeReply`
failed on unchanged code with five RPC errors following creation. It passed in 7.77 seconds after the fix.
The replay verifies the session delta order, native resolve and bind replies, the first bound alliance read,
embedded and bound member reads, and rejection of a foreign alliance tuple. The solution build reported zero
warnings or errors. The build and focused run took 40.83 seconds, with 135 passing cases using
`FullyQualifiedName~Eden.Protocol.Tests.Alliances|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~CorporationScreenTests|FullyQualifiedName~PlayerCorporationTests|FullyQualifiedName~CorporationWarServiceTests`.
Eight catalog and nine formatting policy tests passed.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** On a server running this revision, create an alliance and verify that the dialog
closes and the Alliance Home and Members tabs load.

## Alliance feature compatibility audit

The pinned Windows client is build **3662168**, signed archive SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`. The comparison uses EVE.js **261108.1**.

- `FormAlliancesRankings.ShowRankings` prompts to show every alliance when `maxLen` is zero. Eden now returns the
  full active ranking for zero and honors positive limits. `SetShortNameWnd.ConstructLayout` populates its radio
  buttons from `shortName` rows. Suggestions exclude reserved corporation and alliance tickers and offer alternatives
  when the first candidate is unavailable. EVE.js `allianceRegistryRuntime.js` (`073f759c9ea8`) also treats zero
  as unlimited, and `corpRegistryRuntime.js` (`41d691387ff4`) supplies multiple ticker candidates.
- `bco_corporations.CorporationsO.GetCorporation` caches rows for both registry and public reads. `OnCorporationChanged`
  updates those rows. Eden now refreshes the observed affiliations when the committed alliance change head advances.
  An unchanged `corporationID` pair accompanies the nullable `allianceID` pair, so the client's
  `BaseCorpObject.GetAddRemoveFromChange` treats joining and leaving as updates to existing rows. The reference sends
  corporation changes during alliance settlement in `corporationNotifications.js` (`101462c93041`).
- The native application menu offers deletion for New and Rejected applications. Accepted applications retain their
  due times. The applicant must be the CEO. Departure removes completed applications from live lists and caches,
  while saved membership and application history support rejoining and autonomous recruitment. The applicable client
  functions are `all_ui_applications.GetApplicationMenu` and `base_corporation.CheckCanApplyToJoinAlliance`.
  `all_ui_members.__GetLabel` displays Secret for an undisclosed executor vote. Replies and notifications now apply
  the same corporation-level visibility as EVE.js `allianceViewState.js` (`ebaee4bb2f6d`). Nullable changes
  retain an identity field when needed by the client alliance cache's add/remove classifier.
- Alliance bill balance reads accept the selected bill ID. Member accountants can view invoices, and payment requires
  the current executor's wallet authority. The automatic settings maps use the bill's debtor alliance ID and share
  the corporation division selected by the client. Legacy preferences still load. Maintenance wallet entries
  send the alliance owner ID for the journal's name lookup while retaining the bill ID in storage. Restart
  catch-up settles admission and maintenance in due-time order, with billing first at a tied deadline.
  Evidence includes `all_cso.GetBillBalance`, `CorpBillsPayablePanel`, `CorpBillsAutoPaidPanel`,
  `CorpBillsAutoPaySettings`, and `walletEntryFormat._FmtWalletEntryManual`. The EVE.js counterparts are
  `billMgrService.js` (`d025d8987907`) and `billRuntimeState.js` (`21716142f6a8`).
- Executor Diplomats can manage alliance contacts and labels. Allied Directors can create and sort bulletins,
  Communications Officers can edit and delete them, and the native bulletin cap is ten. Deletion keeps order contiguous.
  Associated controls are `AddressBookSvc.ShowLabelMenuAndManageBtn`, `FormAlliancesBulletins` and
  `base_corporation.GetBulletinEntries`. The native calendar and mail use Communications Officer authority for
  alliance events and broadcasts. See `CalendarSvc.GetEventMenu`, `CalendarNewEventWnd.SetupReadOnlyElements`,
  and `NewNewMessage.ValidateCorpOrAlliance`. Compared EVE.js `allianceRegistryRuntime.js` (`073f759c9ea8`),
  `calendarAccess.js` (`5303c18b2ba5`), and `mailMgrService.js` (`cf0491958c10`).
- Alliance chat returns only the requested owner, admin or member affiliation. Online pilots with current MOTD authority
  appear as admins. The pinned `XmppChatService.IsOwnerOrOperatorOfChannel` and `chatutil.GetUsersFromResponse` treat
  returned JIDs as matches for the requested affiliation, so returning all occupants exposed the edit control to ordinary
  members. The filtered list follows EVE.js `xmppStubServer.js` (`9058203c0dc4`).

Shared fitting authority, current-membership fleet adverts, loyalty age eligibility, and the Alliance Systems,
capital and prime-time reads were cross-checked alongside the existing access, chat and war integrations.
Alliance dissolution now clears territory and its dependent hub systems at downtime.
[UniWiki alliance mechanics](https://wiki.eveuniversity.org/Alliances) ·
[CCP closure guidance](https://support.eveonline.com/hc/en-us/articles/203209062-Corporation-Alliance-Closure) ·
[Sovereignty cleanup on alliance dissolution](sovereignty.md#sovereignty-cleanup-on-alliance-dissolution)

**Automated validation.** Three new regressions reproduced the original ranking, ticker and corporation affiliation bugs.
The final exact-method run passed 17 cases in 13.58 seconds after a 12.38-second build. It covered the native alliance
bind, vote privacy during a stale session, rejoining, billing and restart ordering, social authority, and TLS/XMPP affiliation
queries. The affiliation notification test asserted zero SQL commands and connections. The bulletin test inspects the native
outbound model because Eden's inbound decoder deliberately rejects that row constructor.

The initial broader run passed 276 of 277. The expedition replacement case failed identically on unchanged `f444f916`:
the two replacement purchases totaled 20 ISK, and the fixture inherited the funded-refit default while expecting a
corporation debit. The fixture now explicitly selects corporation funding and retains the exact treasury assertion.
All three cases of `FundedAllianceStagesOneHubAcrossRestartForAnUnobservedClaimableSystem` passed in 19.85 seconds
after a 6.17-second build.

The final solution build had zero warnings and errors, and all 277 focused tests passed. Build and tests took 104.18
seconds. The `FullyQualifiedName~` selectors were `Eden.Protocol.Tests.Alliances`, `PilotServicesTests`,
`PlayerCorporationTests`, `CorporationScreenTests`, `CorporationFinancialPanelTests`, `CorporationWalletTests`,
`CorporationWalletStartupTests`, `CorporationCalendarTests`, `CalendarServiceTests`, `MailTests`, `ChatMotdTests`,
`XmppChatTests`, `CapsuleerAllianceTests`, `SovereigntyProtocolTests`, `SovereigntyDevelopmentProtocolTests`,
`SovereigntyWarfareProtocolTests`, `SavedFittingsTests` and `MilitiaOrganizationTests`. All eight catalog checks
and nine formatting policy checks passed, along with CSharpier, line width and whitespace verification.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Use Show All in the alliance directory and check the returned rankings. Verify automatic
payment settings under the alliance bill owner after reconnecting. Check the Alliance chat MOTD menu as a regular member
and then as a current executor Director.

## Alliance maintenance ownership and public cache updates

The Alliance Wallet stores the automatic-maintenance checkbox on the debtor alliance. A new executor inherits the
alliance's choice and pays from its own selected automatic-payment division. A former executor can found or join another
alliance without carrying the checkbox to that debtor. Schema **241** moves a legacy corporation flag to the active
alliance it executes at migration time. Earlier settings did not retain the debtor ID. The older corporation-only request
shape still applies its maintenance entry to the current executor alliance. Standalone corporation replies preserve
the disabled catalogue entry for older wallet clients. Autonomous founding keeps its opt-in and respects an explicit disable.

The pinned client tracks each checkbox by owner and bill type. In `CorpBillsAutoPaySettings.GetBillCheckedSettings`
(offsets 9–87), it derives the owner from the settings map. `SubmitAutomaticPaymentSettings` (0–21) sends that map to
`billMgr.SendAutomaticPaySettings`. EVE.js `billRuntimeState.js` similarly reads settings by the invoice's debtor and
gets the division from the active executor at settlement.

The pinned `AllianceO.GetAlliance` (51–91) returns a cached public row, and `OnAllianceChanged` updates it. Eden now
tracks public details read by each connection, including pilots in NPC corporations. It refreshes a watched row when
the committed change feed contains new public state for that alliance. The applicant, member and calendar feeds
retain their existing authorization.

An alliance with a suspended election shows foreign viewers its last seated executor as an application recipient
while that corporation remains a member. The earliest remaining member supplies the address when the last executor
has departed. Members see the vacant office in alliance controls and the EverMark panels. When an observer joins or
leaves the alliance, the client receives a correction for its cached row. The pinned client chooses local bound
`GetAlliance()` for its own alliance and remote `GetAlliance(allianceID)` for others. `ApplyToAllianceWnd.ConstructLayout`
(301–400) passes the display ID to `CorporationsO.GetCorporation`, which converts a null ID to the applicant's own
corporation (0–18). EVE.js also separates executor display data from active authority during a suspended election.

REA extracted the native alliance services and panels from signed build **3662168**, archive SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`, evidence
`ev_f04545bf75163bc7d95ccf3d57f194c5bdbf64beeafc544eb5be764581dd553e`. Python 2.7 disassembly established the
client-side cache, wallet and application behavior. [Alliance management](https://wiki.eveuniversity.org/Alliances)

**Automated validation.** All five alliance cases passed in the combined 22-case exact-method run.
`AutomaticPaymentSurvivesExecutorHandoverWithoutFollowingTheFormerExecutor` checks payer division, balances
and a successor alliance's fresh setting. `Version240MigratesLegacyAutomaticPaymentToTheCurrentAlliance` checks
the old save upgrade. `PublicAllianceObserversReceiveSummaryChangesWithoutPrivateEvents` covers NPC and neutral
corporation observers. `VacantExecutorKeepsAnApplicationRecipientWithoutGovernanceAuthority` covers own and foreign
reads, loss of authority, cache updates on affiliation change, and election recovery. The build took 21.52 seconds
and the combined tests took 22.90 seconds.

## Alliance membership age and EverMark donations

The personal and corporation EverMark panels in build **3662168** ask `allianceRegistry.GetDaysInAlliance(allianceID, corporationID)`
for whole elapsed days in the corporation's current membership. A missing member or vacant executor returns zero.
The executor's corporation panel also calls `GetAllianceMembersOlderThan(allianceID, minimumDays)`. It expects a Python
list of corporation IDs and includes members at the exact threshold. Leaving removes the corporation from that list.
Rejoining starts the clock at the new admission time.

| Source wallet | Eligible EverMark recipients |
| --- | --- |
| Personal | The pilot's own player corporation after 30 days of personal membership. The alliance executor also qualifies when both corporations have 30 days of current alliance membership. |
| Member corporation | Its alliance executor once both corporations qualify. Accountant, director or CEO authority is required. |
| Executor corporation | A qualifying member corporation in its own alliance. The same financial role and membership requirements apply. |

The two existing `LPSvc` transfer calls recheck the route, current membership and authority in the same resident
transaction that debits the source, credits the recipient and records the transfer. Balances are checkpointed by issuer.
The original personal and corporation wallet bytecode (`personalDonateEMPanel` offsets 81 and 180,
`corpDonateEMPanel` offsets 66 and 192) was inspected from archive SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`. Eden uses the pinned 30-day default.
The local EVE.js **261108.1** snapshot uses whole days and the inclusive comparison, with its donation threshold
configured to zero. Compared `allianceRegistryRuntime.js` (`073f759c9ea8`), `allianceViewState.js` (`ebaee4bb2f6d`)
and `lpWalletState.js` (`4da2d5a25706`) under `server/src/services/corporation`.
[CCP's EverMark donation rules](https://support.eveonline.com/hc/en-us/articles/6807979979036-Heraldry-and-Emblems) ·
[Corporation loyalty wallets and pinned constants](corporations.md#corporate-lp-and-evermark-wallets)

**Automated validation.** The exact method `MembershipAgeUnlocksEverMarkDonationsAtTheDeadlineAndDepartureRevokesThem`
passed in 10.45 seconds (build 12.30 seconds). Its first run crossed an unpaid maintenance deadline and correctly
dissolved the test alliance. The fixture now pays its invoice before advancing the clock. Coverage includes the last
tick before eligibility, the exact deadline, departure and readmission, stale-session requests, and balances after reopening.
Warmed membership lookups issued zero SQL commands and opened zero connections.
The solution built with zero warnings and errors. The filter
`FullyQualifiedName~Eden.Protocol.Tests.Alliances|FullyQualifiedName~CorporationLoyaltyTests|FullyQualifiedName~LoyaltyWalletServiceTests|FullyQualifiedName~CorporationCalendarTests`
passed 62 tests in 25.21 seconds. Eight catalog and nine formatting policy tests passed. Formatting and line width
passed across 2,939 files. Total milestone validation took 71.36 seconds.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None.

## Autonomous bot alliances

An established bot-led corporation and a willing independent neighbor can form an ordinary alliance. Each corporation needs
six unsuspended simulated members and an active bot CEO with director authority. Headquarters can share a system or connect
through one reciprocal gate. The founder pays the native one-billion-ISK registration fee. If its CEO's wallet needs a top-up,
the shortfall comes from its existing master wallet through a recorded corporation withdrawal. The designated bill division
must be able to cover a month at the proposed membership after paying the registration shortfall. Eden qualifies the selected
founder with Empire Control V and its catalog prerequisites once the funding and a second corporation are in place.

The founder becomes the executor. Its partner submits a standard application and waits the native 24 hours after acceptance
to become a member. The executor can recruit up to a target of three corporations, counting accepted applications as reserved
places. An existing open application, previous application to that alliance, or earlier membership takes precedence over
automated recruitment. Bot planning selects only bot-created alliances whose current executor is led by an active bot CEO.
Player corporations and directors continue to use the existing alliance services.

The bot founder defaults its maintenance bill to automatic payment in the existing corporation bill preferences. An explicit
setting takes precedence. Native bill settlement, membership history, approval deadlines, owner caches and affiliation updates
carry forward across checkpoints. Alliance membership feeds the existing gang, escort and patrol ally checks. The feature
uses the existing save schema.

The server's corporation maintenance timer reviews up to 32 station systems per 15 seconds, with at most one formation or
accepted application per review. It rotates past unobserved headquarters while any player is in the world. The native scheduler
continues to settle already accepted applications and bills on their saved wall-clock deadlines.

The fee, skill and admission delay follow [EVE University](https://wiki.eveuniversity.org/Alliances) and Eden's existing
[formation](#formation-and-public-identity--november-16-2026) and
[membership](#membership-governance-and-session-affiliation--november-16-2026) contracts. The inspected local EVE.js
`allianceApplicationEligibility.js` snapshot SHA-256 is `e0504a59e4648b6c8b6ebf29cb30e5a6f4ae00e9b927cca854b0c516037b241c`.

### Automated validation

The resident integration fixture begins with independent corporations on an existing save. It exercises the funding threshold,
the empty-world pause, exact fee settlement, the accepted application across a restart, effective gang affiliation, manual
application priority, the reserved third slot and recurring maintenance.

- The exact filter
  `FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerAllianceTests.FundedNeighborsFormAndGrowAnAllianceThroughDueApplicationsAcrossRestart`
  passed in 10.80 seconds. The solution restored and built in 9.02 seconds with zero warnings and errors.
- All 126 focused tests passed in 57.03 seconds. The combined `FullyQualifiedName~` filter covered
  `Eden.Protocol.Tests.Alliances`, `CapsuleerAllianceTests`, `CapsuleerCorporationTests`, `CapsuleerMaintenanceTests`,
  `CapsuleerAmmunitionSupplyTests`, `RoamingGangTests`, `CorporationWalletTests`, `CorporationFinancialPanelTests`,
  `CorporationMembershipTests`, `CorporationRoleTests` and `CorporationPublicInfoTests`.
- The two Python policy suites passed all 17 cases. Formatting and diff whitespace checks passed.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer check

Open a formed alliance in Show Info after admission and confirm both corporations appear in its member list.

## First-claim hub procurement

The alliance review can fund a single Sovereignty Hub order at the executor corporation's NPC headquarters market.
The alliance must be bot-founded, have an active bot-led executor, have two admitted corporations and hold no claims.
The planner checks the existing reciprocal, traversable gate graph out to three jumps and shares the sovereignty
owner's claimability and occupancy rules. The candidate system also needs a catalog star. An open corporation hub bid
or an owned packaged hub covers procurement. The selected system becomes a durable route assignment when an expedition
can take custody of that hub.

A docked, active bot submits one seven-day order at the catalog reference sell price. Normal corporate escrow and
broker fees apply. The order may spend only the balance above a month's alliance bill reserve. The existing market
settles any seller and places the package in the ordering pilot's station hangar. The review runs while anyone is online,
including when headquarters and the potential claim are unobserved. The buy order and owned inventory checkpoint
through the resident economy. They require no new save fields.

Build 3662168 type 32458 is published and trades under market group 1275. Its packaged volume is 60,000 cubic meters.
EVE.js package 261108.1 `server/src/space/modules/entosisLinkRuntime.js` distinguishes neutral hub capture from
owned hubs and command nodes (SHA-256 `AFE03A1E9E0FA1525AA56A4D3782EF4E91BA06E509A49CE15803E3D1732E1D1D`).
The procurement radius, threshold, price cap and duration are Eden policy. The next [development milestone](../bot-development-plan.md)
handles ship capacity, travel, deployment and paid Entosis. [CCP sovereignty deployment and capture](https://www.eveonline.com/news/view/sovereignty-structures-and-transition)

### Automated validation

The exact `CapsuleerAllianceTests.FundedAllianceStagesOneHubAcrossRestartForAnUnobservedClaimableSystem` case passed in
8.74 seconds including runner startup. It tests zero human activity, an occupied reachable system, an insufficient budget,
and a funded order with only a neighboring system observed. A player fills the order through the resident market. Two
reopens verify the open escrow and the delivered hub with enough money available for a duplicate bid. Earlier development
runs caught fixture configuration and funding errors, and reproduced a duplicate bid before the planner recognized the
market's existing delivery into the placing pilot's hangar.

Tool and solution restore and the solution build passed with zero warnings and errors. The affected .NET selection
passed 256 cases in 1 minute 33 seconds. The `FullyQualifiedName~` filter combined `Eden.Protocol.Tests.Alliances`,
`Eden.Protocol.Tests.Sovereignty`, `CapsuleerAllianceTests`, `CapsuleerCorporationTests`, `CapsuleerAmmunitionSupplyTests`,
`PersonalMarketTests` and `Eden.Protocol.Tests.Market.ResidentEconomyTests`. Both Python policy suites passed (8 and 9 cases).
Formatting and diff checks passed. The full sequence took 129.10 seconds and used isolated resident worlds.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

None.

## Foundation — November 16, 2026

Schema 174 adds saved alliance identity and one membership row per corporation. Membership uses the corporation ID as its
primary key, including a resident primary-key lookup for affiliation. Saved records have immutable resident snapshots,
private transaction copies, indexed member lookup and detached checkpoint projection/hydration. No alliance gameplay
operation has an offline SQL fallback. Creation and other mutations arrive in later milestones.

**Automated validation.** The exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Alliances.AllianceFoundationTests.ResidentMembershipIsAtomicAndSurvivesCheckpointReopen`
passed after correcting a diagnostics API assertion and replacing an array-Contains expression unsupported by the resident
expression interpreter. Stable checks passed in **37.86 s**: tool/solution restore, zero-warning solution build,
**12 focused tests** selected by
`FullyQualifiedName~Eden.Protocol.Tests.Alliances.AllianceFoundationTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.DetachedCheckpointTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.IncrementalCheckpointTests`,
both Python policy suites (**8 + 9 tests**), and formatting/width verification (**2,381 files**).
Coverage includes rollback, reopened checkpoints, identity validation and warmed zero-SQL/connection/fitting affiliation reads.
The full .NET suite was not requested or run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None; this milestone exposes no new client actions.

## Formation and public identity — November 16, 2026

`corpRegistry.CreateAlliance` validates a player-corporation CEO, no current alliance, trained Empire Control V and
one billion ISK in the actor's personal wallet. The wallet debit, alliance identity, founding membership/executor and
`refAllianceRegistrationFee` service journal record commit together. Duplicate names/tickers are case-insensitive;
closed alliance identities remain reserved. Description and URL edits require executor-corporation director authority.

Public reads include directory rankings, alliance details, public member rows, alliance search, owner-cache priming,
short-name priming and corporation public affiliation. Private member reads use a native indexed rowset. The router
only binds the caller's current alliance with pilot lifetime, including validation before an embedded initial call.
Session affiliation propagation and historical membership records are implemented in the following milestone.

Pinned bytecode confirms `GetRankedAlliances` returns a list; the client puts it into its own ranked KeyVal cache.
`carbon/common/script/sys/crowset.pyj` defines `CIndexedRowset(header, columnName)` as a dict subclass. The explicit
outbound adapter uses Carbon REDUCE's dictionary iterator to populate it, retaining a native descriptor when empty.
No additional incoming Python class construction is allowed. Both public and indexed member shapes share the same columns.
Pinned `appConst.pyj` supplies registration reference **48**, maintenance reference **50**, maintenance bill type **5**,
and application states **1/2/3/4** for new/accepted/effective/rejected. No sovereignty columns are added to gameplay state.
The evejs `allianceFormationEligibility.js` reference snapshot SHA-256 is
`cf5f1552b202f8e971a32ca8368b52ef9b2eb70a6f1f1c0525522f60fcfe1a26`.

**Automated validation.** Exact creation/reopen filter passed (1 test; build 8.64 s, tests 6.11 s).
Stable checks passed in **48.65 s**: tool/solution restore, zero-warning solution build, **94 focused tests** selected by
`FullyQualifiedName~Eden.Protocol.Tests.Alliances|FullyQualifiedName~Eden.Protocol.Tests.Serialization.BlueMarshalTests|FullyQualifiedName~CorporationPublicInfoTests|FullyQualifiedName~CorporationShowInfoTests|FullyQualifiedName~UniversalSearchTests|FullyQualifiedName~CorporationOwnerNamesTests`,
both Python policy suites (**8 + 9**) and formatting/width verification (**2,386 files**). Coverage includes concurrent
formation with one debit, duplicate/authority/funds/skill rejection, native request/reply models, public owner/search data,
registration journaling and checkpoint/reopen. Full .NET regression was not requested or run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Open alliance creation, public Show Info and the member list; check native labels and layout.

## Membership, governance and session affiliation — November 16, 2026

Schema 175 adds typed resident applications, membership history and committed change records. Corporation directors can
apply or withdraw, executor-corporation directors can accept/reject, and accepted applications become effective after
24 hours. New applications replace another outstanding application; rejected history can be reapplied to. Repeated
acceptance preserves the original deadline. Due callbacks revalidate the corporation and alliance and cannot replay
an effective admission. Membership history retains the saved effective time even when restart catch-up runs late.

One member-corporation CEO declares one executor-support choice. At least half of corporations must support a candidate.
Eden resolves equal support by retaining an eligible incumbent; an unresolved tie or no qualifying candidate suspends
executor authority until support resolves. New members initially support the current executor. Departure clears votes
for the departing corporation. Self-departure requires corporation director authority; other expulsions require current
executor authority. With wars excluded, departures are immediate. Corporation closure removes alliance membership in
the same resident transaction. Final alliance dissolution is handled by the billing milestone's downtime boundary.

Native member, alliance and application cache notifications are derived from committed before/after records. Private
application contents are restricted to the applicant corporation's directors and current executor directors. These cache
deltas do not generate a player-facing application notification. An unchanged global change head skips event reads.
Pilot identity derives alliance affiliation from corporation membership. Session deltas include `allianceid`, membership
changes invalidate the resident corporation-identity cache, and connected clients release old alliance bindings.
The pinned ticker-suggestion UI also requires rows exposing `shortName`; its reply now supplies that native rowset.

The evejs `allianceGovernanceState.js` reference snapshot SHA-256 is
`c0bec07063c32b06e63c759b4335c5786744946f26e09c2bb710259840ebed09`.

**Automated validation.** The exact admission/reopen filter passed (1 test; build 8.25 s, tests 6.13 s) after the new
saved history/application rows were connected to explicit resident ID allocation. An intermediate focused run passed
41 tests covering membership, corporation membership/selection and closure. The first broader run passed 103 and failed
5 offline session cases when their tick reached resident-only Abyssal state; subsequent policy/format checks did not run.
Isolated unchanged-main comparisons passed (4 selection cases and 2 session cases), so that failure is timing-sensitive,
not a verified deterministic baseline failure. The affected session fixtures now use the resident runtime, including reopen.
The native creation session test verifies that alliance session changes precede the RPC reply. Final review also made
the feed cursor acknowledge records committed between its head read and coherent event read, preventing duplicate replay.

Final stable checks passed in **50.51 s**: tool/solution restore, zero-warning build, **109 focused tests** selected by
`FullyQualifiedName~Eden.Protocol.Tests.Alliances|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~CorporationClosureTests|FullyQualifiedName~CorporationSelectionTests|FullyQualifiedName~CorporationPublicInfoTests|FullyQualifiedName~CorporationRoleTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~DetachedCheckpointTests`,
both Python policy suites (**8 + 9**) and formatting/width verification (**2,398 files**). Coverage includes exact admission
deadlines and restart, withdrawn/replaced applications, executor handover/ties, rejected old-executor edits, expulsion,
corporation closure, session propagation/binding invalidation, repeated polling, and zero-SQL/connection/fitting idle reads.
No full .NET suite was run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Observe admission and departure refreshing the native alliance/member panels without relogging.

## Maintenance billing and dissolution — November 16, 2026

Schema 176 adds resident invoices and an explicit saved empty-alliance closure deadline, plus an optional alliance ID
on saved inbox notifications. Formation issues an invoice due 30 days later. Each invoice snapshots two million ISK per
member corporation at issue time. Paid invoices renew from their preceding due date, so late callbacks do not drift the
schedule. Fixed 30-day months, the first-bill anchor and no proration are explicit Eden policies; public references do not
establish those exact server details. No separate alliance wallet is created.

Native alliance bill reads and balance reads require current executor-corporation financial authority. Payment also
requires Accountant authority and withdrawal access to the chosen corporation division. The bill, wallet debit and
maintenance journal commit together; repeated payment is idempotent. Existing corporation automatic-payment settings
for bill type 5 select the executor division. Executor changes move payment authority, not wallet money or invoice identity.
Catch-up can pay and renew successive saved periods using the current executor settings, exactly once per invoice.

An unpaid due invoice closes the alliance at the next 11:00 UTC downtime boundary. The final member's departure saves
its next downtime deadline immediately; restart cannot postpone it. Closure removes memberships and rejects pending
applications atomically, retaining alliance identity, bills and history. It does not transfer corporation assets.
An indexed resident deadline snapshot skips unchanged future billing work. Unpaid invoices retry automatic payment
until closure; an unsuccessful attempt does not invalidate access-group observers.

Invoice issue produces the pinned maintenance notification type 4 with `allianceID` data for executor financial staff.
Inbox history, live delivery, Bills grouping and checkpoint hydration preserve that typed payload. The maintenance
reference is 50 and the creditor is catalog CONCORD corporation 1000125.
The evejs `allianceMaintenanceBilling.js` reference snapshot SHA-256 is
`3c5eac04685a4c356126c7dc87a129ae159766f96929935b336c35e7e65ce0ab`.

**Automated validation.** Exact native payment/reopen test passed (build 5.46 s, tests 5.97 s), followed by 36 affected
membership, billing, inbox and financial-panel tests (build 3.27 s, tests 14.44 s). Stable checks passed in **59.72 s**:
tool/solution restore, zero-warning build, **75 focused tests** selected by
`FullyQualifiedName~Eden.Protocol.Tests.Alliances|FullyQualifiedName~InboxTests|FullyQualifiedName~InboxServiceTests|FullyQualifiedName~CorporationFinancialPanelTests|FullyQualifiedName~CorporationWalletTests|FullyQualifiedName~CorporationClosureTests|FullyQualifiedName~DetachedCheckpointTests`,
both Python policy suites (**8 + 9**) and formatting/width verification (**2,407 files**). Coverage includes native/manual
and automatic payments, division journals, duplicate payment, insufficient funds/unauthorized unchanged state,
executor handover, corporation-count snapshots, multi-period restart catch-up, exact saved downtime closure, retained
identity, persisted notification data and SQL/connection/fitting-free warmed maintenance. No full .NET suite was run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Inspect native bills and the maintenance notification; pay a bill from a non-master division.

## Diplomacy and communication — November 16, 2026

Schema 177 adds a resident alliance social aggregate and an organization recipient on saved mail headers. The aggregate
contains typed contacts, labels, bulletins, chat MOTD and alliance calendar entries. It is checkpointed as a saved JSON
payload and published through immutable, per-alliance cached views. Unchanged readers share records and poll only the
saved revision. Contacts are directional; targets can be known pilots, corporations or alliances. Executor directors
edit contacts, labels, bulletins and MOTD. Bulletin edits check their saved edit time and ordering must include each
current bulletin exactly once. The fifteen-bulletin storage limit is an Eden policy, not verified Tranquility behavior.

Native contact and legacy relationship reads retain descriptors, label masks and before/after cache notifications.
The core alliance handler now claims only its own methods before validating arguments, allowing other owners' native
keyword arguments such as bulletin edit revisions and payment divisions to reach the correct handler.

Alliance chat uses `alliance_<ID>`, advertises alliance user data and delivers saved MOTD on joins. A lock-free published
owner-directory affiliation map updates from committed pilot and corporation-alliance changes. Sender, recipient and
room checks use that current map under the chat gate, without taking the resident owner in the reverse lock order.
Departed users cannot join/send or receive subsequent alliance messages/subjects through a stale XMPP game-session identity.

[UniWiki EVE Mail](https://wiki.eveuniversity.org/EVE_Mail) describes corporation/alliance recipients and dedicated labels.
Organization mail snapshots all current local members at send time, retains the organization header and native Corp/Alliance
label, and uses the existing rate limits and committed per-recipient delivery feed. Ordinary members may send to their own
organization; NPC corporation broadcasts remain unavailable. Membership changes affect future delivery, while already
received mail remains personal mailbox history. Mailing-list and direct recipients retain their existing behavior.

Alliance calendar events belong to the alliance aggregate, independent of the creator's corporation or character lifetime.
They share the existing calendar ID allocator, date/title/duration rules and native replies/notifications. Current members
can read and RSVP; executor directors create, edit and cancel. Rescheduling invalidates prior attendance according to
the shared rules. Native month/response reads combine personal and current alliance data. Owner routing checks actual
alliance identity rather than assuming every sufficiently large pilot ID is an alliance. Former members lose event
details, RSVP and future event-feed access immediately. Corporation-owned calendar events remain outside this milestone.

Reference evejs snapshots: `allianceRegistryRuntime.js` SHA-256
`073f759c9ea8d219d6d72f89829a6c30737aba63dd1be31b1cf87d5b6ee7a5e1`,
`calendar/calendarMgrService.js` SHA-256
`8a1f814f2efd9cbda7a259056bc78162bd3c1e92a5bab1a2c58b491fff01c807`.

**Automated validation.** The exact social/reopen test passed (build 3.42 s, tests 5.76 s) after a new regression exposed
and corrected the missing membership publication hook in the owner directory. An intermediate run passed **97 tests**
covering social behavior and immediate chat/mail/calendar dependencies (build 1.10 s, tests 26.87 s).
Stable checks passed in **117.99 s**: tool/solution restore, zero-warning build, **195 focused tests** selected by
`FullyQualifiedName~Eden.Protocol.Tests.Alliances|FullyQualifiedName~Eden.Protocol.Tests.Chat|FullyQualifiedName~MailTests|FullyQualifiedName~MailServiceTests|FullyQualifiedName~MailRulesTests|FullyQualifiedName~MailLifecycleTests|FullyQualifiedName~MailingListServiceTests|FullyQualifiedName~CalendarTests|FullyQualifiedName~CalendarServiceTests|FullyQualifiedName~CorporationContactsTests|FullyQualifiedName~CorporationOwnerNamesTests|FullyQualifiedName~UniversalSearchTests|FullyQualifiedName~DetachedCheckpointTests`,
both Python policy suites (**8 + 9**) and formatting/width verification (**2,417 files**). Coverage includes shared contacts,
label masks/removal, stale bulletin edits, unauthorized atomic rejections, saved MOTD and monotonic bulletin IDs after
reopen, chat sender/recipient revocation through stale sessions, mail recipient snapshots and labels, calendar membership,
native event notifications and shared ID allocation, plus SQL/connection/fitting-free warmed social reads. No full suite ran.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Inspect alliance contact/bulletin panels; verify alliance chat/MOTD presentation;
compose alliance mail and open an alliance calendar event.

## Shared tools and affiliation consumers — November 16, 2026

Schema **178** registers alliance inventory-owner identities for fitting/contact foreign keys and upgrades existing
alliances when opening a checkpoint. This identity does not grant a wallet, hangar, corporation assets, structure
ownership or administration. Allocation skips existing owner identifiers. Personal contact targets now reference
inventory-owner identities; migration preserves existing character contacts and their deletion cleanup.

`allianceFittingMgr` implements native reads, single/bulk saves and deletes, overwrites and renames. All current alliance
members can read; the executor corporation's CEO, Directors and Fitting Managers can edit. Capacity is **600** per
alliance, confirmed by pinned `appConst.maxAllianceFittings`. Membership and executor changes are checked at each command,
including stale selected sessions. Shared fitting monitoring retains revision-based polling and checkpoint persistence.

Fleet adverts accept the pinned Alliance invite-scope bit. Visibility, explicit allow/block entities and advert-invitation
acceptance use committed current affiliations, including both the boss and applicant. The fleet adapter refreshes changed
public identities from immutable resident directory snapshots without acquiring the world owner while holding its fleet
lock. Unchanged directory snapshots avoid refresh work. Existing fleets remain transient and militia scope is unsupported.

Access-list precedence is character, corporation, alliance, Everyone. Corporation/alliance identities can grant Member
or Excluded access; only characters can administer a list. Shared bookmark access and existing structure access entries
resolve membership when used, immediately losing an alliance grant on departure. More specific entries retain precedence; archived alliance entries can still be removed after dissolution.
Structure ownership and management remain corporation-role checks. No sovereignty behavior is introduced.

Personal standings/labels and corporation contact books accept known local alliance identities. Personal corporation
contacts also use saved owner identities. Watchlists, blocking and add/edit notification messages are character-only.
Recruitment rows include current alliance identity, and the standalone-corporation search filter excludes actual members.
Character selection, character corporation show-info, corporation DBRows, station/structure guest replies and events,
fleet applicants/members, ship slim items and Destiny mass-sector identity now carry affiliation. In-space affiliation
changes publish slim-item changes. Guest reads and fleet access checks do not rely on a departed session's stale alliance value.
Combat-party JSON captures affiliation when damage/loss records are produced; subsequent departure or dissolution does
not rewrite reports. Old records without this field retain unknown affiliation. The pinned kill-blob parser uses `a` for
an additional attacker's alliance and the native report fields for victim/final-blow alliance.

References: [UniWiki Access Lists](https://wiki.eveuniversity.org/Access_Lists),
[structure permissions](https://wiki.eveuniversity.org/Structure_permissions), and
[CCP Access Lists](https://support.eveonline.com/hc/en-us/articles/208289645-Access-Lists).
Static inspection additionally covered pinned `environment/fittingSvc.pyj`, `common/lib/appConst.pyj`, and
`common/script/util/eveCommonUtils.pyj`. Local evejs reference snapshots:
`services/fitting/allianceFittingMgrService.js` SHA-256
`7702df902c287efb6b9db4d706f0612857aa02cc885347f7ee82d5679f2c3a91`;
`_secondary/fitting/fittingStore.js` SHA-256
`be4e3ebd0b4cb389c74576e91542d636b1c59d3203192149d0e79247bd4602c9`.
Behavior was independently implemented; no reference source was copied.

**Automated validation.** Stable checks passed in **134.75 s**: tool/solution restore, zero-warning build,
**300 focused tests**, both Python policy suites (**8 + 9**) and formatting/width verification (**2,419 files**).
The final filter was
`FullyQualifiedName~Eden.Protocol.Tests.Alliances|FullyQualifiedName~SavedFittingsTests|FullyQualifiedName~FleetRulesTests|FullyQualifiedName~CooperativeFleetTests|FullyQualifiedName~SharedBookmarksTests|FullyQualifiedName~SharedBookmarkServiceTests|FullyQualifiedName~ContactsServiceTests|FullyQualifiedName~PilotContactsTests|FullyQualifiedName~CorporationContactsTests|FullyQualifiedName~CorporationRecruitmentTests|FullyQualifiedName~StructureTests|FullyQualifiedName~CharacterInteractionHistoryTests|FullyQualifiedName~InteractionHistoryPersistenceTests|FullyQualifiedName~CharacterSheetServiceTests|FullyQualifiedName~CorporationSelectionTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~HomeStationServiceTests|FullyQualifiedName~DetachedCheckpointTests`.
Coverage includes executor transfer, fitting bulk atomicity/reopen, immediate bookmark/structure revocation, ACL precedence
and archived entries, stale fleet sessions/invitations, SQL/connection/fitting-free warm adverts, recruitment filtering,
personal/corporation alliance contacts, version-177 owner migration and event-time combat affiliation after reopen.

Development validation found a historical version-40 fixture that retained contact-cleanup triggers while reconstructing
its old table; the owner migration now drops and recreates them around replacement. Existing fleet-warp fixtures required
resident worlds, the catalog capacitor profile and coordinator boundaries; the offline warp failure also reproduced on
unchanged main `85878d9a`. The isolated saved-fitting session test passed on that main revision, so its earlier offline
Abyssal exception was timing-sensitive. Its session fixture now uses a resident world. A subsequent dependency run passed
299/300 and exposed a real-time fleet-tag fixture failure; both variants passed after moving it to `TestClock`, followed
by the successful stable run above. No production warp or Abyssal behavior was changed. The full .NET suite was not
requested and was not run. Documentation links, UTF-8 and Git whitespace checks passed.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Inspect alliance fittings and Fleet Finder; check ACL/structure access after corporation
departure; inspect alliance identity in station guests, space and saved combat reports.

## Main merge — November 16, 2026

The merge combines alliance branch tip `b6a7006b` with main `85878d9a`, preserving the saved-Omega license check
and planetary scan/preview records. It required no conflict resolution. Wars and sovereignty remain excluded.

**Automated validation.** Merged-state checks passed in **77.72 s**: tool/solution restore, zero-warning solution build,
**88 focused tests**, both Python policy suites (**8 + 9**), and formatting/width verification (**2,420 files**).
The filter was
`FullyQualifiedName~Eden.Protocol.Tests.Alliances|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~CorporationSelectionTests|FullyQualifiedName~CloneLicenseClientTests`.
Git whitespace checks passed. The full .NET suite was not requested and was not run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None specific to this merge; the feature checks above remain optional.
