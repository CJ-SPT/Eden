# Alliances

## Scope and references

Alliances are corporation associations with resident authority and checkpoint persistence. Wars and sovereignty are
explicitly outside this implementation. The six milestones are foundation, formation/public identity, membership/governance,
billing/dissolution, diplomacy/communication, and shared tools/affiliation consumers.

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
at `E:\EdenDev\3662168`; its `start.ini` identifies V24.01/build 3662168. This prepared archive differs from the original
signed archive hash recorded by the development preparation script. The updated `E:\Eve` installation is build 3693310
and is not the contract authority for this work.
Static Python bytecode inspection covers `ui/services/alliances/all_cso*.pyj`,
`ui/services/corporation/bco_alliance.pyj`, and the non-sovereignty `ui/shared/neocom/Alliances/all_ui*.pyj` panels.
It confirms `corpRegistry.CreateAlliance(name, shortName, description, url)`, application management,
`allianceRegistry` public and bound member/application reads, executor support, bills, contacts and bulletins.
Membership cache handlers require native DBRow descriptors even for initially empty rowsets, and clear on `allianceid` changes.
The evejs `allianceViewState.js` snapshot SHA-256 is
`ebaee4bb2f6dcfe0b4cf6a864cb05b9a38d3963e06f268cf3ecb5a83c441909b`.
All new wire models use the existing codec. Its reference remains
[Carbon Blue Marshal.cpp, af0d65b29f951260ea09140ca11adf5d40566997](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp).

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
