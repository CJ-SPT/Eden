# Chat, mail and contacts

[Documentation](../README.md) / [Protocol index](../protocol.md)

Native chat, certificate trust, contacts, guests, calendars and notifications.

These records preserve milestone evidence and historical observations. Dates, schema versions, test results and live acceptance apply to the recorded change; they do not establish acceptance of later changes.

## Contents

- [Corporation owner cache width, November 14, 2026](#corporation-owner-cache-width-november-14-2026)
- [Presence failure diagnostics, November 14, 2026](#presence-failure-diagnostics-november-14-2026)
- [Chat channel handoffs and queued station signals, November 14, 2026](#chat-channel-handoffs-and-queued-station-signals-november-14-2026)
- [Local presence and station guest snapshots, November 12, 2026](#local-presence-and-station-guest-snapshots-november-12-2026)
- [Translocation destination Local handoff (November 6, 2026)](#translocation-destination-local-handoff-november-6-2026)
- [Translocation chat continuity (November 6, 2026)](#translocation-chat-continuity-november-6-2026)
- [Native chat ready-handshake correction (November 5, 2026)](#native-chat-ready-handshake-correction-november-5-2026)
- [Chat startup, first send and initial roster correction (November 5, 2026)](#chat-startup-first-send-and-initial-roster-correction-november-5-2026)
- [Chat certificate manifest correction (November 5, 2026)](#chat-certificate-manifest-correction-november-5-2026)
- [Native chat and multi-pilot Local (November 5, 2026)](#native-chat-and-multi-pilot-local-november-5-2026)
- [Personal contacts and online status](#personal-contacts-and-online-status)
- [Persistent personal calendar](#persistent-personal-calendar)
- [Persistent notification inbox and contact messages](#persistent-notification-inbox-and-contact-messages)
- [Persistent mailing lists](#persistent-mailing-lists)
- [Mail arrival notification arity](#mail-arrival-notification-arity)
- [Mail compose optional source defaults](#mail-compose-optional-source-defaults)
- [Persistent personal EVE Mail](#persistent-personal-eve-mail)
- [Station guests, November 4, 2026](#station-guests-november-4-2026)

## Corporation owner cache width, November 14, 2026

After diagnostic revision `5922473b` was deployed and the user reopened three clients, native logs captured
`IndexError` in `XmppChatWindow._GetUserEntryNode` and `InitUsers`, through `EveOwners.__getattr__` line 1505
and Carbon `Row.__getattr__` line 32. Membership included the missing pilots; reading their owner names
aborted row construction. The user also reported ships rendered in space but absent from the overview.

`corpRegistry.GetEveOwners` returned three columns: ownerID, ownerName and typeID. Build 3662168's
`CorporationMembersO.__PrimeEveOwners` copies each row and appends `None` for ownerNameID, expecting gender
to already be present. The short reply therefore polluted the five-column owner cache with four values.
Every native owner name lookup reads ownerNameID, producing the captured exception. XMPP's owner metadata
does not replace an already cached owner. This explains why corporation mates could be missing while a
pilot from a different corporation remained visible. Native overview `PrimeDisplayName` calls
`uix.GetSlimItemName`, which reads the same owner name for piloted ships.

The reply now includes saved gender as its fourth field. The resident corporation-name projection carries
gender without materializing full member records, opening connections or issuing warmed SQL queries.
Fresh client processes are required to discard the malformed in-memory owner records.

Reference comparison: the local evejs `corpRegistryRuntime.js` SHA-256 is
`41d691387ff45dce3cf4680da40291557b868f0b3f589234d0eb244b77c4e35c` (no Git metadata). Its owner reply
includes gender and ownerNameID. Eden follows the newer pinned client's four-field input contract because
that client appends ownerNameID itself. No reference implementation was copied. Carbon row access and
the native corporation, chat, guest and overview methods were inspected as inert code from the verified
build-3662168 archive identified below. Expected Local membership includes docked pilots;
see [EVE University](https://wiki.eveuniversity.org/Identifying_war_targets_in_Local).

**Automated validation.** Tool and solution restore passed; the solution built with zero warnings/errors
in 1.70 seconds. The exact native regression passed with the actual handler reply exported into the pinned
Python 2 harness. It executes native corporation priming, cached owner access, chat/guest row construction,
and overview naming/sort keys. Three-field input reproduces the captured IndexError; four-field input
passes. The first harness attempt failed because isolated Python 2 lacks `copy_reg`; direct inert literals
replace the unnecessary pickle dependency. This was a test harness failure, not a production failure.

The final focused run passed 77 tests in 28 seconds (runner reported 29.80 seconds including setup), with
`EDEN_TEST_CLIENT_ROOT=E:\EdenDev\3662168`. Filter:
`FullyQualifiedName~CorporationOwnerNamesTests|FullyQualifiedName~PlayerCorporationTests|FullyQualifiedName~CorporationAdministrationTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~PresenceListTests|FullyQualifiedName~ConcordOverviewTests|FullyQualifiedName~Eden.Protocol.Tests.Chat`.
The persistence tests cover membership, rename, rollback and reopen; owner reply checks include saved
gender and zero warmed SQL/connection/fitting work. Both Python policy suites passed (8 cases in 1.211
seconds and 9 cases in 0.021 seconds). Formatting application, verification and diff checks passed.
No full .NET suite was run.

**Live acceptance.** Not performed; agent client testing is disabled. The user's diagnostic-session
failure is observed evidence, not acceptance of this correction. Server logs also recorded three chat
connections ending after approximately 366–368 seconds and reconnecting. This cache correction does not
establish the cause or resolution of those connection closures.

**Optional maintainer checks.** After updating the server, fully reopen the clients and compare Local,
station Guests and nearby piloted ships in the overview. Observe chat beyond the previous disconnect
interval and retain the diagnostic logs if it reconnects.

## Presence failure diagnostics, November 14, 2026

The user's fresh-session screenshots still show Local with five members and four rows, a chat reconnect,
and Guests with three members but no rows. Read-only SSH verification found the Docker host running
`0218f7dbc837d1657aff030ae3835dc497ecf286`, including the earlier presence adapters. Its saved world
contains the three selected pilots with valid corporations and ordinary Player roles; none has the
chat-invisibility flag. These observations do not establish which native operation failed.

The local evejs station snapshot and XMPP metadata paths were compared with build 3662168's native
guest panel, owner records, row construction and chat parser. The reference directory has no Git metadata.
Inspected SHA-256 values are `34c675bfafdfc62474a9afa29a673a40dc3ed072c3f2c3806ddd8fccef246f6e`
for `stationService.js` and `9058203c0dc4253b2edde87df5f45a977ec51d44e08425e58ba18af7308afb65`
for `xmppStubServer.js`. No reference implementation was copied. The differences checked did not
reproduce the reported failure. Expected Local membership includes docked pilots;
see [EVE University](https://wiki.eveuniversity.org/Identifying_war_targets_in_Local).

The login adapter now writes bounded diagnostics to `%TEMP%/eden-presence-<process-id>.log`: installation,
member/guest IDs, scroll IDs, and exception types with code locations. It preserves native return values
and exceptions, excludes exception text, frame locals, chat messages and credentials, and tolerates
unwritable output. Each process records at most 128 successful operations and 128 failures, with up to
64 IDs per list and 24 stack frames. A fresh process overwrites a reused PID's file. Reinstalling the
adapter neither wraps methods twice nor resets the diagnostic. Chat lifecycle logs now identify the
pilot, connection, duration, closure category and any exhausted delivery queue in normal host logging.

This is diagnostic coverage, not a claimed fix. A fresh client login against the diagnostic server is
required before the native failure can be collected. Existing processes retain their old adapters.

**Automated validation.** Tool/solution restore and solution build passed (5.27 seconds, zero warnings
or errors). The exact native presence test passed, followed by 69 focused tests in 19.80 seconds with
`EDEN_TEST_CLIENT_ROOT=E:\EdenDev\3662168`. The final filter combined `FullyQualifiedName~` selections
for `Eden.Protocol.Tests.Chat`, `StationGuestTests`, `PresenceListTests`, `ConcordOverviewTests`,
`PilotLoginTests`, `StreamDeadlineTests` and `ServerLogTests`. The pinned native harness checks that diagnostics
retain the original row exception, omit private exception text, tolerate write failures and bound output.
Both Python policy suites passed (8 catalog cases in 1.34 seconds, 9 formatting cases in 0.02 seconds),
and formatting application, verification and diff checks passed. No full regression suite was run.
The first exact `Test-Feature.ps1` invocation was blocked by another active testhost locking its shared
output DLL; subsequent validation uses an isolated artifacts directory without stopping the other run.

**Live acceptance.** Not performed; agent client testing is disabled. Read-only deployed-revision,
server-log and saved-world inspection was performed. No interactive client was launched or controlled.

**Optional maintainer checks.** Reopen the clients after the diagnostic server update, reproduce the
blank list, and retain the per-process presence logs and matching server chat lifecycle lines.

## Chat channel handoffs and queued station signals, November 14, 2026

The user reported continuing chat and station-list differences between pilots after the earlier corrections.
Headless checks found two further gaps in the signed login adapter. A chat roster could yield before entering
`CharacterJoinedChannel`, let `SetChannel` replace the reused Local window, and then insert a source member into
the destination. A station callback queued behind a rebuild could apply an old arrival or departure to the new
controller's list. Serializing only the individual row methods did not establish which channel or station owned
the pending work.

Roster application now holds the window lock across owner priming and every member addition. Ordinary chat
arrivals also hold that lock across their native priming helper; queued admin departures recheck the channel
after acquiring it. Guest callbacks reconcile their character against the current controller instead of trusting
the old signal direction. They recheck the controller and guest identity after a yielding owner lookup, preserve
the search filter, and avoid duplicate rows. These changes use the existing login adapter; restart the updated
server and fully close/relaunch both clients to install it in fresh client processes.

Sources: inert inspection of build **3662168** `XmppChatService`, `XmppChatWindow`, `GuestsPanelController`,
`GuestList`, `GuestsPanel` and `StationSvc` methods, with the executed methods identified below. The verified original archive is
`E:\EdenDev\3662168\.eden-original-code.ccp`, SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`, with the pinned Python runtime
`1266e119d5db70281831f06049faa2694e658a68d504e0cc5680ec5726ba3aa2`.
[EVE University Local membership](https://wiki.eveuniversity.org/Identifying_war_targets_in_Local) establishes
the expected known-space membership behavior. The local evejs reference's `stationService.js` confirms the
snapshot/delta ledger boundary; `localChannelMove.js` delegates server room handoffs. That reference checkout
has no Git metadata; the inspected station service's SHA-256 is
`34c675bfafdfc62474a9afa29a673a40dc3ed072c3f2c3806ddd8fccef246f6e`. No reference implementation was copied.

**Automated validation.** The exact development filter was
`FullyQualifiedName=Eden.Protocol.Tests.PilotSessions.PresenceListTests.NativePresenceRebuildsPreserveConcurrentArrivalsDeparturesAndRowIndexes`.
The added checks failed with source members leaking into destination Local and with stale station signals
overwriting current membership before the respective corrections. The harness uses native roster/window,
station-cache, guest-list and panel methods, with cooperative tasklets and stubbed rendering/services.
It also covers queued departures across a channel switch, ordinary room arrivals, controller replacement
during owner lookup, unresolved owners, and the previous snapshot/rebuild regressions. An initial invocation
against `E:\Eve` stopped at the pinned-runtime hash guard; subsequent tests use the verified staged original.
Tool/solution restore and solution build passed in **1.85 seconds**, with zero warnings or errors. Filter
`FullyQualifiedName~Eden.Protocol.Tests.Chat|FullyQualifiedName~StationGuestTests|FullyQualifiedName~PresenceListTests|FullyQualifiedName~TranslocationTests|FullyQualifiedName~CharacterLogoffTests|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~ConcordOverviewTests`
passed **116 tests**, zero failed/skipped, in **107.79 seconds**, using four workers and
`EDEN_TEST_CLIENT_ROOT=E:\EdenDev\3662168`. This includes loopback TLS, repeated transitions and multi-pilot guest
notifications. After strengthening the queued-signal harness to execute native `GuestList` mutations and real
callback delivery, the exact presence filter passed again (**1 case**, **1.53 seconds** build plus **2.60 seconds** test command).
Both Python policy suites passed (**8 catalog**, **9 formatting**, **1.58 seconds** combined). Formatting application
and verification (**26.27 seconds**), the new documentation links/anchors and `git diff --check` passed. A broader
index audit encountered existing legacy encodings in `fleets.md`/`industry.md` and the existing bulk-industry
delivery anchor; those unrelated records were left intact. No full .NET suite was requested or run.

**Live acceptance.** Not performed; agent client testing is disabled. Native rendering and actual multi-client
presentation remain unverified.

**Optional maintainer checks.** After restarting the server and both clients, compare Local and Guests while
one pilot docks, undocks, changes systems and reconnects; repeat with Guests open on the observing pilot.

## Local presence and station guest snapshots, November 12, 2026

Build 3662168's `xmppchatclient/xmppchatsvc.pyj` handles `/admin` joins as direct window updates. Its
`_UnpackVikingPresence` also primes owner data and records the channel participant role. Human arrivals now use
one-record `/viking` presence for existing observers. Initial joins and simulated-pilot batches continue to send
complete rosters. A `/viking` roster adds entries, so departures still use explicit `/admin` messages.
The signed client adapter serializes roster application with admin updates for the same channel and clears the
participant role on departure. A yielding roster application could previously reinsert a departed pilot.

In `eve/client/script/ui/station/base.pyj`, `StationSvc.GetGuests` clears its cache and yields on the snapshot RPC.
An intervening departure removes nothing from the cleared cache, allowing a row from the pending snapshot to
restore that pilot. The signed adapter replays intervening deltas in order after the snapshot and suppresses the
result if the session moved to a different station during the call. Concurrent readers share the native cache.

The original archive SHA-256 is `37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.
[EVE University Local membership](https://wiki.eveuniversity.org/Identifying_war_targets_in_Local)

**Automated validation.** The original native methods reproduced both races in the pinned Python runtime.
The exact regressions passed in 3.38 seconds using in-process xUnit after VSTest timed out connecting to its host.
The focused selection passed 82 cases in 24.43 seconds. Loopback socket permissions prevented 17 TLS and
connection cases from reaching their assertions. The passing coverage includes human Local participant
wire data, multi-pilot chat state, station guest transitions and the signed adapter in the pinned runtime.
The exact development selection was `PeerLocalArrivalCarriesNativeParticipantDataAndDepartureUsesExplicitLeave`,
`NativePresenceRebuildsPreserveConcurrentArrivalsDeparturesAndRowIndexes` and
`PortraitBatchAndPublicProfileWorkBeforeSelectionAndAcrossAccountsAfterReopen`.
Tool restore, cached solution restore, build (10.36 seconds, zero warnings or errors), both Python policy
suites (8 and 9 cases), CSharpier and line-width checks passed. NuGet vulnerability data was unavailable
and the formatter's separate .NET style check could not connect to its build-host pipe.

**Live acceptance.** Agent client testing was not performed.

**Optional maintainer checks.** Restart the server and reopen two clients. Join the same system and station on
different accounts, then undock, redock and log off one pilot while checking the other pilot's Local and Guests.

### Missing rows follow-up

The follow-up screenshots show Local with four members and three visible rows, and Guests with two members
and no visible rows. Local's native `SetChannel` can clear the member and row dictionaries while an arrival is
yielding in owner lookup. The adapter now includes the channel switch in the view's lock. After a `/viking`
update it checks actual scroll rows against visible members and rebuilds only on a mismatch.

Station rows require a cached owner profile. The native `Recordset._Prime` remembers missing owners and skips
them on future prime calls. A missing profile can then abort the entire guest rebuild when it reads the name.
The signed adapter retries those IDs through the existing owner RPC and builds rows for the profiles it can
resolve. Incremental arrivals respect the search filter and skip a row already included in a concurrent rebuild.

**Automated validation.** The pinned presence test reproduced the missing-owner guest rebuild failure and the
channel-switch race. It checks cache recovery, duplicate arrivals and Local row reconciliation. In-process xUnit
passed 79 affected cases in 21.22 seconds. After consolidating owner priming into one request, the solution
built in 24.10 seconds with zero warnings or errors and 36 focused cases passed in 8.69 seconds. Both Python
policy suites, CSharpier and the line-width check passed. Socket tests and the formatter's .NET style host remain
blocked by the sandbox.

**Live acceptance.** Agent client testing was not performed.

## Translocation destination Local handoff (November 6, 2026)

After the connection-continuity correction, the user reported that **TR me here** still did not update Local correctly. `ChatWorld.Update` revoked the source Local membership but never joined the replacement. The prior transport and repeated-translocation tests explicitly joined destination Local themselves, masking the missing server operation.

In pinned build **3662168**, `XmppChatService.OnSessionChanged` schedules leaving the old session channels; ordinary Local is not joined there. `_JoinSystemChannels` is part of the login callback, which no longer runs during TR because chat correctly stays connected. The server now transfers an existing Local membership after updating the pilot's authoritative system/category, sending the destination self-presence and `/viking` roster and notifying destination observers. Old-system sends/joins remain rejected. Same-system TR retains its membership, while corporation, private and player channels retain their existing connections and memberships. No save schema, token, certificate or transport format changes are required.

Wormhole and Triglavian joins now receive the same roster envelope, filtered by their existing delayed-presence rules. An empty roster switches the native Local window without disclosing silent pilots. This is necessary because `_HandleXmppPresence` ignores self-presence in delayed Local. `_UnpackVikingPresence` calls `GetOrCreateGroupChatWindow`; native `XmppChatWindow.SetChannel` adopts the destination and clears the old members. A late source-room leave is harmless after the transfer, and a repeated destination join acknowledges/resends the roster without duplicating arrival events to observers. The existing room slot is reused even at the joined-room limit.

Sources: inert inspection of the staged archive at `E:\EdenDev\3662168\tq\code.ccp`, whose SHA-256 was reverified as **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**. An automated audit executed the actual pinned `OnSessionChanged`, `_HandleLocalSuppressedSessionChange`, `GetOrCreateGroupChatWindow`, `_UnpackVikingPresence` and `XmppChatWindow.SetChannel` code objects with UI/service dependencies stubbed. Ordinary Local replaced a stale member with the two supplied destination pilots; empty wormhole and Triglavian rosters switched the window and cleared the stale member without revealing another pilot. The first inspection used the separate `E:\Eve` archive; the final contract inspection and audit use the verified staged archive. Consulted [Carbon Blue Marshal.cpp at fcbf0c2](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp); the existing Blue/notification and Destiny adapters remain unchanged.

**Automated validation:** On top of `4d3561a`, tool/solution restore and build passed with zero warnings/errors (**5.38 seconds** combined). Filter `FullyQualifiedName~Eden.Protocol.Tests.Chat|FullyQualifiedName~TranslocationTests|FullyQualifiedName~CharacterLogoffTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting`, run with `dotnet test --no-build` and `xUnit.MaxParallelThreads=4`, passed **111 tests**, zero failed/skipped, in **24.27 seconds** command time (**22 seconds** reported test duration). Both Python policy suites passed (**3 catalog**, **9 formatting**, **0.55 seconds** combined), and `Format.ps1 -Check` passed across **783 files** in **12.14 seconds**. Required restore/build/test/policy/format checks took **42.35 seconds** combined. Documentation link/anchor/whitespace checks and `git diff --check` passed. Four new gameplay cases cover ordinary/delayed Local, return travel, room-capacity reuse, unchanged-system updates, stale access rejection and retry idempotence. Existing TLS tests now cover ordinary and both delayed categories without a destination join request, and repeated native TR coverage retains docked/space origins and real logoff cleanup. The three initial handoff regressions failed before the gameplay correction. A subsequent development run was interrupted after both TR cases timed out because its corporation-message helper had already consumed the newly automatic roster; moving that helper before immediate travel corrected the harness. The corrected seven-case development selection passed before the final expanded validation. No full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Rebuild/restart the server and reconnect both pilots, then TR between systems; confirm the Local destination/member list and both observers' arrival/departure lists update without a chat reconnect.

## Translocation chat continuity (November 6, 2026)

The user reported repeated **Connection to chat server lost / Reconnected to chat server** messages and identified **TR me here!** as the trigger. Saved client chat logs corroborated three disconnect/reconnect pairs at **05:14:40**, **05:20:08** and **05:24:51 UTC** on November 6; the host process had remained running. These are user/client-log observations, not agent gameplay acceptance.

`TranslocationHandler` called `PilotUpdateCoordinator.Leave` to cancel old activities before moving the ship. That shared logoff path also disposed `ChatHandler`, removed the selected pilot from `ChatWorld` and queued `ChatEvent.Closed`, deliberately terminating the existing TLS stream. Re-entering the destination then required the client's automatic reconnect. Even a same-system move used this cleanup. The defect was in Eden's pilot lifecycle, rather than the native ready handshake or an idle timeout.

Travel now uses `PrepareTranslocation`, which preserves the selected pilot's chat lease while retaining the existing activity cancellation, checkpoint and monitor-reset behavior. Actual character logoff still disposes chat before performing that cleanup. Existing `ChatHandler.Advance`/`ChatWorld.Update` changes authoritative Local access when the system changes; corporation, private and player memberships remain attached to the same connection. Same-system movement and docking retain Local membership too. No XMPP keepalive, TLS, token, certificate, client-file, database or wire-format change is included.

The regression routes native `/tr me` commands through the real coordinator while a TLS client remains connected. Both docked and in-space origins make six repeated moves covering same-system celestials, docking, cross-system station targets and staged space transfers. Corporation messages are delivered before and after each transfer without reconnecting or rejoining; Local messages work at arrival, and sends to the old system are rejected. Actual logoff emits departures, closes that same connection and removes online chat presence. Existing tests retain multi-pilot roster, travel persistence, activity and logoff coverage. The pinned build-3662168 `XmppChatService.HandleXmppConnectionLost` displays the reported notices; its transport/native readiness contract remains as recorded below.

**Automated validation:** On top of `0aa3cee`, tool/solution restore and build passed with zero warnings/errors. Filter `FullyQualifiedName~Eden.Protocol.Tests.Chat|FullyQualifiedName~TranslocationTests|FullyQualifiedName~CharacterLogoffTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~TackleTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting`, run with `dotnet test --no-build`, passed **145 tests** with zero failed/skipped in **36 seconds**. Both Python policy suites (**3 catalog**, **9 formatting**), formatting verification (**757 files**), documentation anchor/link/whitespace checks and `git diff --check` passed. Required restore/build/test/policy/format checks took **59.5 seconds** combined. Before the correction, both new docked/in-space cases failed on the first TR with the chat transport closed (**4 seconds** combined). The passing focused correction run completed both cases in **5 seconds**. Intermediate test-harness corrections spaced messages with `TestClock` to respect the existing chat rate limit and drained the normal unavailable-presence notifications before asserting logoff closure. No full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** After rebuilding/restarting the server and reconnecting once, use TR repeatedly within and between systems; confirm chat shows no reconnect notices, corporation chat continues and Local follows the destination. Native UI behavior remains unverified on this revision.

## Native chat ready-handshake correction (November 5, 2026)

The user still reported empty Local/corporation windows and the first-message cannot-speak dialog after **c29d878**. The running server's protocol DLL was checked and contained the new endpoint/safety handlers; this was not an older build. The previous tests authenticated their socket and initiated channel joins directly, skipping the native client's final readiness condition. The earlier roster/first-send corrections did not close that handshake gap.

In build 3662168, `XmppHandler.process_single_queue_item` sends IQ responses exclusively through `_handle_iq_result`. The session callback `handle_session` sends `<presence><show/></presence>` and sets `state = 'ready'`, but it does not invoke `handle_logged_in`. `handle_response_state_not_ready` switches to the ready response handler and calls that hook only when the **next non-IQ stanza** arrives. `XmppConnection._connectToXmppServer` installs `XmppChatService.HandleXmppLoggedIn` as the callback; that schedules `_JoinSystemChannels` for Local and corporation access. A completed TLS/SASL/bind/session exchange therefore did not establish channel membership by itself.

Eden previously ignored the initial undirected availability presence and sent no further stanza. The native connection could remain authenticated while waiting indefinitely to schedule channel joins. A first outgoing message to an unjoined/placeholder room elicited an error stanza, which finally triggered the login callback and joins while also displaying the misleading moderation/gag dialog. Waiting for an early message to join cannot resolve that startup deadlock.

The protocol session now responds to undirected availability with self-presence whose `from` and `to` identify the authenticated bound resource. That normal presence response advances the native callback before any user message. Supplied sender identity, join IDs and user-data fields are not reflected into the response. Existing token gating, room eligibility, queued-message limits and roster rules remain unchanged. This implements the self-presence delivery described in [RFC 6121 section 4.2.2](https://www.rfc-editor.org/rfc/rfc6121.html#section-4.2.2); it does not add full contact subscriptions or server federation.

The shared TLS test client now waits for this presence before declaring chat ready or submitting channel joins. A new regression connects two silent pilots, observes their Local membership/rosters before any chat body is sent, and then verifies the first message reaches both once with no error. Another case checks that availability replies use the bound pilot/resource despite forged incoming identity or role fields. All chat transport tests now include this readiness requirement.

Sources: inert inspection/execution of the pinned **3662168** `xmpp/xmpphandler`, `xmpp/xmppConnection` and `xmppchatclient/xmppchatsvc`; `code.ccp` SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**. The audit executed the actual IQ dispatcher, session callback and not-ready/ready handlers with the native service's login and system-channel routines, with external socket/UI/service dependencies mocked. It reproduced zero joins after the session result, then scheduled Local/corporation joins once after consuming XML emitted by Eden's new availability adapter. Repeated availability did not repeat the login hook. Previously consulted Carbon Blue **fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae** references remain unchanged; no Blue, Destiny or game-session packet change is introduced.

**Automated validation:** tool/solution restore and build passed with zero warnings/errors (1.8 seconds). Filter `FullyQualifiedName~Eden.Protocol.Tests.Chat|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~CharacterLogoffTests|FullyQualifiedName~StationGuest|FullyQualifiedName~Eden.Protocol.Tests.Launcher|FullyQualifiedName~LiveWorldTests`, with `xUnit.MaxParallelThreads=4`, passed **80 cases in 9 seconds**. Both Python policy suites (3 catalog, 9 formatting), formatting verification (676 files) and diff checks passed. No full Eden .NET suite was requested or run. Before the production correction, filter `FullyQualifiedName~SilentPilotsCompleteTheNativeReadyHandshake` failed after **17 seconds** waiting for the missing initial presence on the c29d878 behavior. After adding the response, the initial focused Chat run passed **29 cases in 4 seconds**. The final 80-case run includes the added identity case. An initial audit fixture still used the handler's default no-op login callback; it was corrected to match the callbacks installed by native `_connectToXmppServer` before the passing native audit. The native audit was inert execution, not a launched game or a live-client acceptance run.

**Live acceptance:** Not performed; agent client testing is disabled. The user's screenshots remain evidence for the pre-correction build; updated native UI timing remains unverified.

**Optional maintainer checks:** Rebuild/restart the server and relog both pilots; wait for both Local members without typing; send the first message once from each pilot.

## Chat startup, first send and initial roster correction (November 5, 2026)

User observations after the manifest correction: the first send raised `browserLockdownSvc.GetFlaggedSitesHash`, then a native cannot-speak/moderation dialog. Later sends worked and both pilots eventually appeared. Local initially looked empty, leading to the reported impression that chat connected only after typing. These are user observations, not agent gameplay acceptance.

Endpoint discovery (`XmppChatMgr.Hostname` and `GetDeprecatedPrefsFallback`) is now owned by an account-phase handler, so initialization can discover chat before pilot selection. Credential issuance and room access remain selected-pilot operations. The pinned client's `OnSessionChanged` invokes `HandleLoggingIn` for a non-null character delta, and that schedules `Login` independently of sending. Automated routing checks cover early discovery without creating a chat lease or exposing a token; the normal simulated two-pilot startup joins and receives its roster before sending any message.

The URL-safety helper now implements `browserLockdownSvc.GetFlaggedSitesHash` and `GetFlaggedSitesList`, with strict empty arguments and consistent Python-list/cache semantics. Eden has no configured flagged-site rows, so the list is empty and its version is MD5 of Python `str([])`, **d751713988987e9331980363e24189ce**. The client can initialize its chat URL checks without an unsupported-call popup; this does not introduce an externally maintained URL block list.

Native `SendGroupChat` can finish login and submit a message while `_JoinSystemChannels` is still joining in another tasklet. Previously the missing membership yielded error 403, which `_HandleMessageError` displays as a moderation/gag notice. Gameplay now retains at most **one** validated early message per connection for **five seconds**, only for the pilot's authoritative Local/delayed-Local/corporation room. Joining that room publishes it once, after the acknowledgement and roster. It reserves the normal send-rate budget at queue time. Expiry, explicit leave, disconnect, pilot change or loss of access cancels it. Private/player rooms still require an explicit authorized join; wrong-system sends remain rejected. No database query or message archive is added.

Local join replies now send the correlated self-presence acknowledgement first, followed by a `/viking` presence snapshot containing native owner hints and authoritative user data for every visible member, including silent peers. `_UnpackVikingPresence` populates both the member list and participant roles from these direct child records. Retrying a join resends the snapshot to repair a cleared roster. Subsequent Local peer changes retain the existing `/admin` join/leave format. Other channel types receive their ordinary presence roster after the acknowledgement, with delayed-presence rules preserved.

Blue text in the supplied screenshots matches the pinned client's role coloring. `XmppChatWindow.GetColorKeyForSender` uses the session role for self messages and authoritative member roles for peers. `chat.common.util.get_classification_from_role` classifies the developer account's **QA** flag as Developer before testing GM flags; that classification uses the native blue color. Ordinary Player roles use the normal white fallback. Message bodies are not wrapped in a server-injected color tag, and this fix does not revoke developer/GM privileges to change their presentation.

Sources: inert inspection of build **3662168**, `code.ccp` SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**: `carbonui/control/browser/sites.__PopulateSystemLists`, `xmppchatclient/xmppchatsvc` startup/join/message handlers, `xmppchatwindow.GetColorKeyForSender`, `chat/common/util` and `chat/common/const`. The prior Carbon Blue **fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae** wire references remain unchanged; this milestone changes no Blue codec or Destiny packet format. Native session dispatch in `SessionChangeGPCS`/`basesession.ApplyRemoteAttributeChanges` was also checked; session packet layout is unchanged.

**Automated validation:** tool/solution restore and build passed with zero warnings/errors (1.8 seconds). Filter `FullyQualifiedName~Eden.Protocol.Tests.Chat|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~CharacterLogoffTests|FullyQualifiedName~StationGuest|FullyQualifiedName~Eden.Protocol.Tests.Launcher|FullyQualifiedName~LiveWorldTests`, with four workers, passed **78 cases in 10 seconds**. Both Python policy suites (3 catalog, 9 formatting), formatting verification (676 files) and diff checks passed. Coverage includes discovery/URL-cache replies before selection, authentication gating, Local/corporation first-send races over verified TLS, roster ordering with no prior send, expiry at the exact deadline, one-time delivery/rate accounting and cancellation on leave/disconnect/access changes. Intermediate checks caught two assertions tied to the old roster order and a nullable test-helper return; both were corrected before the passing final run. No full Eden .NET suite was requested or run. An inert audit executed the actual pinned `_UnpackVikingPresence` code against XML emitted by Eden's new roster adapter, confirming both pilot IDs (including a silent pilot), corporation metadata and participant roles. The actual `OnSessionChanged`/`HandleLoggingIn` code also scheduled Login for a character-selection delta without any send. Only these inert functions ran with mocked UI/service dependencies; the game executable was not launched.

**Live acceptance:** Not performed; agent client testing is disabled. Native timing and rendering after this correction remain unverified.

**Optional maintainer checks:** Rebuild/restart the server in Rider and relaunch both pilots; inspect Local membership before either types; send the first message once, then change systems or log off one pilot and inspect both rosters.

## Chat certificate manifest correction (November 5, 2026)

The initial chat milestone appended the server certificate to `root:/bin64/packages/certifi\cacert.pem` without updating the file's manifest entry. The reported native failure was **VerifyManifestFile: Couldn't open manifest file bin:/manifest.dat**. Carbon Blue first verifies `root:/manifest.dat`; if any signature or file hash fails, it retries `bin:/manifest.dat` and displays that second error. A read-only audit of the prepared build found exactly one mismatched file: the modified certifi bundle. Creating a second manifest file would conceal the actual failure rather than repair the signed file set.

Launcher API v1 now accepts optional `LaunchRequest.ChatBundleSha256`. The signer still requires the exact original build-3662168 prefix (length 17,769, SHA-256 `04EB4A00671E7289BF14AAFBEE6849D490678A291DAD0DF333E519913921C631`) and permits only the 32-byte certifi hash at offset **1479** to vary in the signed reply. Its original SHA-256 is `54ABC073292AB1B3EA95361D7E9FBC0FDF3C2B74777F0A4E2B6EC26670F0A453`. The separate `root:/bin64\cacert.pem` entry at offset 3176 and every other entry, directive, timestamp and key flag stay pinned. The final manifest retains the existing server key and RSA signature format.

Individual Play and group launches prepare the prospective bundle in memory, send its exact hash with the canonical prefix, and validate the returned signed manifest against both before file writes or process launch. Repeated account launches reuse identical bundle/manifest bytes. A server that returns the original hash despite advertising chat trust is rejected before changing files or starting a client. Switching signing keys or chat trust while that copy has running clients remains blocked. Reading a previously updated manifest normalizes only the certifi slot before validating the full pinned prefix, allowing repeated launches and migration from the broken chat build. The development ticket helper uses the same signer after prepared-copy/prefix validation and continues without opening the live world.

Sources: Carbon Blue **fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae**, [`manifest.cpp`](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/manifest.cpp) (`UnpackManifest`, `VerifyManifestEntry`, `VerifyManifestFile`) and [`BlueOS.cpp`](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/BlueOS.cpp) (`VerifyManifestAndGatherDirectives`), checked against the pinned client manifest and binary error strings. The original tests covered trust installation and network chat but missed the native manifest check; the correction adds this boundary explicitly.

**Automated validation:** tool/solution restore and build passed with zero warnings/errors (2.7 seconds). Filter `FullyQualifiedName~Eden.Protocol.Tests.Chat|FullyQualifiedName~Eden.Protocol.Tests.Launcher|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~LiveWorldTests`, with four workers, passed **64 cases in 10 seconds**. Both Python policy suites (3 catalog, 9 formatting), formatting verification (673 files) and diff checks passed. Companion launcher build passed with zero warnings/errors (1.5 seconds), with **35 tests passing in 1 second** and formatting verification passing. An intermediate formatter run caught a misplaced import in the certificate test; it was corrected before these final checks. No full Eden .NET suite was requested or run. A headless file audit provisioned an isolated scratch copy through the real CLI helper, verified **219 native file hashes**, the actual RSA signature, unchanged prefix bytes outside the one allowed hash, identical repeated preparation and no world database acquisition. It used the real prepared client's files as read-only inputs and did not launch the game.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Restart the rebuilt server and launcher, then launch two accounts and exchange Local messages. The next corrected launch repairs the prepared copy's manifest automatically; reinstalling the source EVE client is unnecessary for this failure.

## Native chat and multi-pilot Local (November 5, 2026)

Build 3662168's default chat path uses XMPP; the newer public-gateway Local feature defaults off. Eden now hosts the pinned client's **direct TLS on TCP 5222**, SASL PLAIN with single-use credentials, stream restart, resource binding, legacy session establishment, MUC join/leave, group messages, room discovery/information, EVE user-data reads and private invitations. No plaintext listener, shared development password or server federation is enabled.

Gameplay owns typed pilot/room/message records, membership eligibility, message limits, delayed-presence rules and recipient selection. Server owns encrypted sockets, bounded delivery queues and credential issuance. Protocol owns native Blue service replies and explicit XML/JSON adapters. The shared chat state stays in memory; unchanged session advancement performs no chat database query. `ChatHandler` synchronizes selected pilot identity/system/corporation from the authoritative game context on entry and updates, and releases the lease on character logoff or connection-scope disposal.

`chatAuthenticationService.GetAuthenticationToken()` requires a selected pilot. Credentials contain 32 random bytes, expire after one minute, are stored by hash, are consumed once and are bound to both the game lease and selected character. Logoff/reselection invalidates pending credentials. Authenticating a replacement chat connection removes the old connection's memberships. Sender identity, nickname, corporation and role always come from the game lease; supplied stanza `from` values and user-data writes cannot forge them.

`XmppChatMgr.Hostname()` returns the logical realm `eden.chat`; `GetDeprecatedPrefsFallback()` returns the advertised game's host as the network destination. `ResyncSystemChannelAccess()` returns `local_<system>` and `corp_<corporation>` channels. Wormhole/Zarzakh and Pochven use their pinned `wormhole`/`triglavian` categories with delayed presence: silent peers are hidden until they speak. These category mappings use `chatutil.channelnames`, system ID ranges/30100000 and Pochven region 10000070. Normal null-security space retains ordinary Local; blackout and fully suppressed Local are not introduced by this milestone.

Local's native roster has a build-specific quirk: `_HandleXmppPresence` ignores ordinary **other-pilot** Local departure presence. Eden therefore sends Local peer changes as `groupchat` messages from `<room>/admin`, with the `cmd`, `charid` and `userdata` JSON consumed by `_HandleMessageFromAdmin`. Self join acknowledgements retain the original request ID and native MUC presence/status 110. Other room types use ordinary presence. User-data appears at the stanza level, where `_GetPresenceUserData` reads it, with `corpid`, `allianceid`, `warfactionid`, `typeid`, `role` and a JSON owner-row hint. Messages preserve their ID and Unicode/body markup as escaped XML text, echo once to the sender and reach only current authorized room members.

Changing systems removes old Local membership before further sends are accepted; a stale/forged join or send to another system fails. Docked and undocked pilots share the same solar-system room. Corporation rooms continue across system changes. Disconnect, reconnect, logoff, leave and host shutdown release memberships and notify remaining viewers. Recipient queues hold at most 256 deliveries; a stalled receiver disconnects instead of blocking other pilots. Messages are limited to 1,024 characters and three per second per connection, with 20 joined and 20 created rooms per pilot, plus bounded XML size/depth and a 128-connection listener cap. XML DTDs/external entities are rejected.

Private conversations are created through `CreatePrivateChannel`, access is limited to the named participants, and the native mediated invitation carries the verified inviter's JID. Runtime player-created rooms support creation, joining, discovery and display names. Dynamic room IDs use a random host namespace so stale saved IDs do not alias newly created rooms after restart. Chat messages, private/public room definitions and delivery state are transient; server-side archives, offline messaging, persistent channel settings, topic/moderation controls and federation remain unimplemented. Existing client-side chat logs remain client-owned.

The host keeps its stable chat certificate/private key in `chat-tls.pfx` beside the world database, outside version control. Windows imports its private key with UserKeySet because Schannel rejects ephemeral keys for TLS; the key container is released with the certificate. The launcher API adds optional `ChatCertificatePem` metadata. The companion launcher change appends only the public certificate to the **prepared copy's** `tq/bin64/packages/certifi/cacert.pem` before launch, idempotently preserving the original roots. It neither disables TLS nor installs a system trust root. Older servers without this field retain their existing launch behavior. `--prepare-login` provisions this trust for the isolated development client as well. Rebuild/restart the server and use the rebuilt launcher for this feature; remote hosts must expose TCP 5222 in addition to their existing ports.

References: staged client **3662168**, `code.ccp` SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**, inert inspection of `xmpp/xmppConnection`, `xmpphandler`, `xmppchatsvc`, `chatutil`, `chat/common/const`, `globalConfig` and `certifi/core`. The key contracts are `GetAuthenticationToken`, `ResyncSystemChannelAccess`, `_JoinChannelHelper`, `_HandleGroupchat`, `_HandleXmppPresence`, `_HandleMessageFromAdmin`, `_GetPresenceUserData` and the room-info readers. Protocol references are [RFC 6120](https://www.rfc-editor.org/rfc/rfc6120.html), [XEP-0045](https://xmpp.org/extensions/xep-0045.html) and Microsoft's [Schannel ephemeral-key limitation](https://learn.microsoft.com/en-us/dotnet/core/extensions/sslstream-troubleshooting#handshake-failed-with-ephemeral-keys). Existing Blue adapters retain the previously consulted Carbon Blue fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae implementation; no Blue or Destiny format changes.

**Automated validation:** tool/solution restore and build passed with zero warnings/errors (build approximately **2.8 seconds**). Filter `FullyQualifiedName~Eden.Protocol.Tests.Chat|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~CharacterLogoffTests|FullyQualifiedName~StationGuest|FullyQualifiedName~Eden.Protocol.Tests.Launcher`, with `xUnit.MaxParallelThreads=4`: **55 passed** in approximately **9 seconds**. Both Python policy suites (**3 catalog**, **9 formatting**) and formatting verification passed. Companion launcher validation passed **33 tests** in approximately **0.7 seconds**, with build and formatting checks passing. Tests use actual loopback TLS with verified custom-root chains and two/three simulated pilots, not game-window input. They also cover concurrent ordered broadcasts, native Local admin envelopes, private invitations/occupant counts, system/corporation isolation, delayed Local, token expiry/replay/reselection, forged metadata, certificate persistence/trust and bounded XML input. Initial transport checks exposed Schannel's ephemeral-key rejection and a test reply-conversion error; both were corrected before these successful runs. An initial launcher formatting check found one long JSON fixture literal, split before successful validation. No full Eden .NET regression suite was requested or run.

After rebasing onto resident-world commit `4ab8541`, the three-pilot system-transition check runs against both direct-file and resident-world hosting. The combined filter above plus `FullyQualifiedName~LiveWorldTests` passed **67 cases in 10 seconds**, with four workers; build passed with zero warnings/errors in **3.0 seconds**. Both Python policy suites and formatting verification (**671 files**) passed again. The development ticket path retains its original prepared-copy validation before adding chat trust and continues to avoid acquiring the running world.

**Live acceptance:** Not performed; agent client testing is disabled. Native UI rendering, input and chat-window behavior remain unverified.

**Optional maintainer checks:** With two pilots in one system, exchange Local messages and inspect the member count; jump/dock/log off one pilot and inspect both rosters; accept a private invitation and reply across systems.

## Personal contacts and online status

Build **3662168** inert inspection of `eve/client/script/ui/shared/neocom/addressBook/addressbookService.pyj` confirms `GetContactList()` returns a KeyVal with `addresses` and `blocked` lists. Contact entries expose `contactID`, `relationshipID`, `inWatchlist` and `labelMask`; blocked entries expose `senderID`. `AddContact` and `EditContact` receive `(contactID, standing, watchlist, sendNotification, message)`. Bulk standing changes use `EditContactsRelationshipID(ids, standing)`. Deletion, blocking and unblocking take a list of IDs. `GetLabels()` returns a dictionary keyed by each label's bit value, with `labelID`, `name` and `color`. Label operations are `CreateLabel(name, color)`, `EditLabel(id, name, color)`, `DeleteLabel(id)`, `AssignLabels(ids, mask)` and `RemoveLabels(ids, mask)`. The client updates its own contact/label caches after successful edits, so these operations do not send duplicate cache notifications.

Eden now persists these operations for known local pilots. Offline pilots resolve through `config.GetMultiOwnersEx`, rather than disappearing when outside the caller's visible scene. All mutations derive the owner from the selected session. Standings must be finite and between -10 and 10. Eden bounds batches to 1024 entries and label names to 40 nonblank characters without NUL. Up to 63 labels use positive bits in the signed 64-bit mask. Assignment rejects unknown bits, label deletion clears the bit from all of that pilot's contacts, and reused bits never inherit stale assignments. Duplicate contacts/names, unknown pilots, self-targets, malformed arguments and unsupported notification requests fail with unchanged state. Blocks are independent of contact membership. They persist and suppress watchlist presence; chat/mail enforcement awaits those systems.

World version **41** adds `WorldContact`, `WorldBlockedContact` and `WorldContactLabel`, with pilot foreign keys, composite keys and unique per-pilot label names. Every edit/batch and label-bit cleanup commits in one transaction. Migration from version 40 preserves character settings and initializes empty contact data. Reopening does not reset saved contacts, blocks or labels.

`eve/client/script/ui/shared/comtool/onlineStatus.pyj` primes `GetInitialState().Index('contactID')`, fetches missing entries through `GetOnlineStatus(characterID)`, and consumes `OnContactLoggedOn/Off(characterID)`. Eden's online policy exposes selected-session presence for the caller's own local contacts. Initial state includes watchlisted contacts; blocked contacts report offline. The existing connection presence registry now retains in-space pilots as well as docked guests, while station lists still filter by station. A primed watchlist receives each observed online/offline transition once on session ticks, with no docking, undocking or ship-switch flicker. Logoff resets the connection's subscription and disconnect removes its presence. Changes shorter than a session tick can be coalesced. At that milestone, sending contact notifications/CSPA, NPC-agent contacts and corporation/alliance contacts remained unavailable; selecting Send Notification failed explicitly and required clearing it before saving. NPC-agent address-book support was added by the later directory milestone below. The later notification-inbox milestone adds personal contact messages; paid CSPA and organization contacts remain pending.

References: unchanged pinned `code.ccp` hash and Carbon Blue revision **fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae** recorded above; the existing rowset, KeyVal and list adapters remain in use. No gameplay Python was executed and no client was launched.

**Automated validation.** Development filter `FullyQualifiedName~Contacts|FullyQualifiedName~OnlineStatusTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~TypedCallResultTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~CharacterSettings|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed **64 tests** in approximately **6 seconds**. Coverage includes typed state, actual Blue replies, rejection/unchanged-state checks, Unicode labels, label-bit limits and reuse, version-40 migration, restart persistence, offline names, blocks, repeated docking/reconnect cycles and logoff. Early development found a test KeyVal-unwrapping issue and a concrete dictionary adapter mismatch, both corrected before the passing run. Final filter `FullyQualifiedName~Eden.Protocol.Tests.PilotSessions|FullyQualifiedName~PilotContactsTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~CharacterSettings|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed **81 tests** in approximately **7 seconds**. Tool/solution restores, solution build (zero warnings/errors), both Python policy suites (3 catalog and 9 formatting tests), formatting and diff checks passed; final build/test/policy/format checks took approximately **21 seconds** combined. No full .NET suite is authorized or run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Add another local pilot with Send Notification cleared, assign a label and reconnect; verify the contact and label remain. With both pilots connected, check watchlist indicators through logoff/reconnect. Native rendering and interaction remain unverified.

## Persistent personal calendar

Replaced the always-empty `calendarProxy.GetEventList(month, year)` with saved personal events visible only to their creator and invited local pilots. The native result is a **list of result groups**, each containing native `blue.DBRow` records; a flat event list is incorrect. Event rows preserve `eventID`, `ownerID`, `eventDateTime`, nullable `eventDuration`, Unicode `eventTitle`, `importance`, `isDeleted` and `dateModified`. `GetEventDetails(eventID, ownerID)` returns `creatorID` and Unicode `eventText`. The owner argument is checked against saved ownership and never grants access. Cancelled events remain marked as cancelled in history; they do not become upcoming live events.

Pinned build **3662168** inert inspection covered `eve/client/script/ui/services/eveCalendarsvc.pyj`, `eve/client/script/ui/shared/eveCalendar.pyj`, `eve/client/script/util/eveMisc.pyj` and `appConst`. The creator uses `calendarMgr.CreatePersonalEvent(dateTime, duration, title, description, important, invitees)` and receives a new event ID. `EditPersonalEvent(eventID, oldDateTime, dateTime, duration, title, description, important)`, `UpdateEventParticipants(eventID, add, remove)`, `SendEventResponse(eventID, ownerID, response)` and `DeleteEvent(eventID, ownerID)` return None. Only the creator can edit, change invitations or cancel. A stale old date prevents a competing reschedule from overwriting the first change. Repeated cancellation and unchanged invitation edits are idempotent.

`GetResponsesForCharacter()` returns `eventID`/`status` rows, including the creator's implicit accepted status. `GetResponsesToEvent(eventID, ownerID)` returns invitee `characterID`/`status` rows after the same visibility check. Pinned statuses are **Uninvited 0**, **Deleted 1**, **Declined 2**, **Undecided 3**, **Accepted 4**, **Maybe 5**. New invitees start undecided. Only invitees can submit accepted, declined or tentative replies. Moving an event resets accepted and tentative replies to undecided while preserving declined replies, matching the native `OnEditCalendarEvent` branch; changing text alone preserves replies. Removing a guest revokes event, details, attendee and reply access; re-invitation starts with a new undecided reply. Replies are saved immediately. Native response calls update their own cache; reopening the uncached attendee query reads current replies rather than forcibly closing other event windows.

The server enforces the pinned **40-character title**, **500-character description**, **50 other invitees**, and **12 calendar months ahead** limits. Duration is None or the native selector's **1–24 whole hours**, stored as minutes. Month queries use UTC month boundaries and preserve history from the client's calendar start year **2003**. New/edited events cannot be moved into the past; the current minute is allowed because the native editor uses minute precision. Unknown, duplicate, self and blocked invitations, unauthorized changes, malformed fields, stale dates and invalid response statuses fail without partial writes. Personal invitations currently have **no configured CSPA charge**. The native CSPA wrapper first tries the ordinary create/update call; it does not require a separate fee query. Only an optional zero `approvedCost` is accepted. Paid communication settings, corporation/alliance event permissions, official CCP events and automated structure events are separate future work.

Create, edit, cancellation and invitation membership changes commit together with a private, ordered change feed. Connected recipients receive native `OnNewCalendarEvent`, `OnEditCalendarEvent` and `OnRemoveCalendarEvent` argument shapes, including the prior date for a move and the cancelled-versus-uninvited removal flag. A connection starts at its current feed cursor; reconnects fetch saved event state without replaying old notifications. Tick delivery drains at most **256 changes** at a time and continues when a batch remains even if no new write occurs. A process-local revision signal avoids SQLite polling on unchanged idle ticks, and publishes only after commit. Pilot logoff resets the observer. No email, chat message or external calendar is sent.

World version **48** adds `WorldCalendarEvent`, `WorldCalendarParticipant` and `WorldCalendarChange`, with pilot/event foreign keys and event-time, membership and per-recipient feed indexes. Version-47 migration is additive. Calendar rules and immutable commands/results live in Gameplay; Server owns transactional persistence and Protocol owns Blue adapters and notifications. Consulted Carbon Blue **fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae** `Marshal.cpp` tuple/list reductions and `PyRowSet.cpp` `DBRow::_New`/`InitSeq` for the explicit descriptor/value-sequence constructor. Eden's inbound decoder intentionally refuses executable DBRow reductions; tests inspect the outgoing constructor and encode it, then round-trip each inert data cell. No codec allowlist was expanded. No game launch or gameplay Python execution occurred.

**Automated validation.** The 19 focused development cases passed in approximately **4 seconds**. Coverage includes selected-pilot routing, native grouped rows/details/response fields, Unicode and None, private/offline access, calendar bounds and leap-month ranges, invitation limits and blocks, creator authority, reply persistence and reschedule resets, cancellation history, revoked/reinvited access, no-op updates, concurrent stale-date rejection, forced create/edit/invite/cancel/reply rollback, version-47 migration/restart, connected peer notification order, reconnect initialization and delivery beyond one tick batch. Development corrected an enum member name, assertion analyzer requirements and an outbound-constructor test that incorrectly used the inbound reduction decoder. Final filter `FullyQualifiedName~Calendar|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~PilotContactsTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting`, run with `dotnet test --no-build` and `-- xUnit.MaxParallelThreads=4`: **118 passed**, approximately **15 seconds**. Tool/solution restores, build (zero warnings/errors), both Python suites (3 catalog and 9 formatting tests), formatting and diff checks passed in **34.64 seconds** combined. The pinned archive SHA-256 was rechecked and matches the recorded build. No full .NET suite was requested or run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Create and open a personal event, invite another pilot, and inspect the participant response after reopening. Observe rescheduling, cancellation and removal in a second client, then reconnect. Native calendar rendering, editor interaction and notification presentation remain unverified.

## Persistent notification inbox and contact messages

Replaced the always-empty `notificationMgr.GetAllNotifications` route with a private saved inbox. Native history calls use **`GetAllNotifications(fromID=...)`**: pinned `NotificationUIService._NotificationProvider` supplies `lastClearedNotificationId`, so this is an **exclusive newer-than cutoff**, not a request for older pages. None and zero fetch all visible history. Other reads are `GetUnprocessed()` and `GetByGroupID(groupID)`. Replies are lists of explicit KeyVal DTOs with `notificationID`, `typeID`, `senderID`, `receiverID`, Boolean `processed`, FILETIME `created` and typed `data`.

The native write calls `MarkAsProcessed([ids])`, `MarkAllAsProcessed()`, `MarkGroupAsProcessed(groupID)`, `DeleteNotifications([ids])`, `DeleteAllNotifications()` and `DeleteGroupNotifications(groupID)` return None. The pinned selected-ID limit is **100**, from `appConst.notificationsMaxUpdated`; group/all operations are not restricted to that batch size. Each selected batch validates all IDs against the recipient before changing anything. Duplicate owned IDs and repeated read/deletion operations are idempotent. Deletion retains a tombstone, so IDs and delivery cursors do not reset after clearing an inbox. Reads and writes never accept a caller-supplied recipient. Group **0** means unread; contact notifications belong to group **9**. Other valid pinned groups return empty until their gameplay has a producer; unassigned group **13** and malformed IDs are rejected.

The first server producers are the contact form's optional **Send Notification** add/edit actions. `notificationTypeContactAdd` is **89** and `notificationTypeContactEdit` is **90**. Inert inspection of `contactManagementWindow.pyj` establishes the **120-character** single-line message limit. `contactFormatters.pyj` consumes the known `level` and `message` payload. Both history DTOs and live events carry an explicit dictionary with the standing snapshot and Unicode message. Pinned `TryUnYamlNotificationData` only parses string payloads and accepts an already-decoded mapping, so no YAML parser or executable serializer is introduced. Empty messages are valid. Silent add/edit calls still create no notification. NPC agent bookmarks do not emit personal-player notifications.

Contact changes, the contact-added task event and the recipient's notification commit together. A blocked sender/recipient pair, invalid text, failed inbox write, unknown pilot or rejected contact operation leaves all three unchanged. Both existing standing and watchlist flags are retained on a failed edit. The contact remains private to its owner; sending a notification does not add a reciprocal contact. The message is a saved snapshot, so subsequent rating changes do not rewrite earlier notifications. Paid CSPA settings are not configured or implemented, and these sends do not charge ISK.

Connected recipients receive `OnNotificationReceived(notificationID, typeID, senderID, created, data)`. A per-connection observer starts at the current durable ID on pilot selection, resets at logoff and drains at most **256 rows** per tick. Tombstones and already-read rows advance that cursor without being reannounced as unread. A history/unread/group fetch that wins the race with delivery records only its returned IDs, preventing a duplicate push without dropping notifications outside the fetched group. Remaining batches drain without requiring another publication. A process-local revision signal publishes only after a successful contact/notification commit and avoids SQLite polling on unchanged idle ticks. The native read/delete methods update their own client caches, so the server does not send duplicate deletion events. Offline recipients fetch saved history after reconnecting.

Calendar reminders are client-generated type **1050**, as shown by the pinned calendar service and formatter. This milestone does not fabricate server invitation notifications or duplicate those reminders. Other server notification families, paid communication rules and back-office undelete behavior remain separate work. The inbox has no client-authorized create/send endpoint.

World version **49** adds `WorldInboxNotification`, with a recipient foreign key and recipient/ID index. Sender identities and payload snapshots remain audit data; version-48 upgrade is additive. Gameplay owns the immutable notification/query/content models and validation, Server owns persistence inside the existing contact transaction, and Protocol owns the selected handler, native adapters and observer. Inert inspection covered `notificationSvc.pyj`, `notifications/common/notification.pyj`, `notificationconst.pyj`, contact UI/formatters and notification UI history loading. The pinned archive hash was rechecked and matches the value above. Consulted Carbon Blue **fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae** dictionary/Unicode handling in `Marshal.cpp`; existing adapters suffice. No game launch, gameplay Python execution or external message delivery occurred.

**Automated validation.** The focused persistence run passed **11 cases** in approximately **3 seconds**, and the native inbox/contact run passed **6 cases** in approximately **3 seconds**. Coverage includes native DTO/event fields, Unicode and empty messages, selection gating, cutoff direction, processed/unread/group state, private and mixed-ID rejection, repeat operations, message and batch bounds, blocked pairs, silent writes, contact/task/inbox rollback, failed batch/group mutation, competing contact adds, restart/tombstone retention, version-48 migration, fetch-versus-push races and delivery beyond a tick batch. Final filter `FullyQualifiedName~Inbox|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~PilotContactsTests|FullyQualifiedName~AgentDirectory|FullyQualifiedName~TaskTracking|FullyQualifiedName~Calendar|FullyQualifiedName~StationGuestTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting`, run with `dotnet test --no-build` and `-- xUnit.MaxParallelThreads=4`: **167 passed**, approximately **17 seconds**. Tool/solution restores, build (zero warnings/errors), both Python suites (3 catalog and 9 formatting tests), formatting and diff checks passed in **39.06 seconds** combined. No full .NET suite was requested or run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Send a contact add/edit message to another pilot and inspect its inbox entry and live indicator. Mark/delete notifications and reconnect to inspect saved state. Native mail-window rendering, message formatting and interaction remain unverified.

## Persistent mailing lists

Replaced the permanent empty `mailingListsMgr.GetJoinedLists` compatibility response with persistent lists. Added `GetInfo`, `Create`, `Join`, `Leave`, `Delete`, `GetMembers`, `KickMembers`, `SetMembersMuted`, `SetMembersOperator`, `SetMembersClear`, `GetSettings`, `SetDefaultAccess`, `SetEntitiesAccess`, `ClearEntityAccess`, `GetWelcomeMail`, `SaveWelcomeMail`, `SendWelcomeMail` and `ClearWelcomeMail`. `mailMgr.SendMail` now accepts one list alongside up to 50 direct recipients, including list-only sends, and `MarkAsReadByList`, `MarkAsUnreadByList` and `MoveToTrashByList` operate on the caller's private copies. Organization mail, alliance access and paid CSPA remain pending. Native list controls supply zero mail cost; nonzero legacy cost arguments reject explicitly, without charging or silently approving a fee.

Inert inspection of build **3662168** `mailingListsSvc.pyj`, `evemailingListConfig.pyj`, `mailSvc.pyj`, `appConst.pyj` and Carbon `format.pyj` establishes fields and calls. Create returns a list ID; Join returns a KeyVal with `id`, lookup `name`, `displayName`, `isMuted`, `isOperator` and `isOwner`. Joined lists are keyed by list ID. Members are a dictionary of pilot IDs to role integers: muted/member/operator/owner **0/1/2/3**. Settings contain `defaultAccess`, `defaultMemberAccess`, zero `mailCost` and an entity/access dictionary; blocked/allowed are **0/1**. Welcome reads use an indexable empty/one-row list with Unicode `title` and `body`, matching the fields the editor consumes; the choice of a list of KeyVals is an adapter inference, not a captured retail server row class. The access editor's legacy `lookupSvc.LookupPCOwners(query, exact)` now returns local character rows with `ownerID`, `ownerName`, `typeID` and `groupID`, enabling name selection in its allow/block controls. General NPC and organization owner lookup remains separate pending work.

Names have the pinned **60-character** limit. `GetKeyAndNormalize` removes whitespace from lookup keys, selects the suffix after the last backslash, and repeatedly applies uppercase/lowercase folding; display names only collapse repeated identical whitespace. The embedded, readable `MailingListCaseFold.txt` preserves the pinned Python 2.7 **Unicode 5.2** UTF-16 mapping (1,003 changed units), with its historical whitespace set also retained. It was derived using only standard runtime string methods, not by executing client code. This avoids modern Unicode rules changing native lookup keys after reconnect. Tests include case/space/prefix aliases, Greek final sigma, preserved sharp-s/ligature behavior, historical U+180E whitespace and surrogate-pair text. Active keys are unique; deletion retires the original ID/name for old messages while allowing a new active list to claim the same key.

Lists hold at most **3,000 subscribers**, including their owner; member/access updates accept at most **1,000 distinct entries** atomically. The creator becomes the sole immutable owner. Owners and operators can manage members, defaults, welcome text and access; only the owner can delete the list. Ordinary/muted members cannot obtain administrative membership/settings data or promote themselves. Owner removal, muting and demotion reject. Operators may demote themselves, after which further management fails. Kick removes the membership and adds an explicit character block so it cannot immediately be rejoined. Invalid members in a batch, unauthorized targets and invalid access entities roll back the whole operation.

Joining evaluates explicit local-character access before catalog-corporation access, then the list default. Alliance identities have no saved membership model yet and reject instead of fabricating a rule. A personal allow can override a corporation block. Explicit blocking removes affected current subscribers, retaining the owner; closing public enrollment and changing the default role affect subsequent joins, without evicting or muting existing subscribers. Muted members continue receiving but cannot post. Ordinary posts retain the existing five-per-minute sender throttle. Direct recipients blocking the sender reject the request; blocked list recipients are skipped without preventing delivery to other subscribers.

Each post validates current membership, send permission and all direct targets in its write transaction, snapshots the eligible subscriber IDs, and writes the immutable mail plus each private mailbox copy and change records together. A direct/list overlap receives one copy. The header and event expose the direct-recipient list and list ID, never the expanded subscriber roster. Native header `toListID` is text, while `OnMailSent` receives the numeric list ID and still has exactly **eight arguments**, preserving the verified arrival fix. List-only received copies have no Inbox bit, and the sender's copy has Sent; both remain addressable through the native list filter. Historical mail survives leaving, kicking and list deletion.

Welcome templates persist per list. First join sends one automatic welcome in the same transaction; idempotent/concurrent duplicate joins cannot send it twice. Save-and-send delivers the template to current members; clear disables future automatic welcomes without erasing existing mail. Automatic messages use the list identity as sender and status bit **32**, which the pinned client resolves through list display-name lookup. Existing body reads still return zlib-compressed UTF-8. Queueing, subscription insertion, welcome/broadcast delivery and change records roll back together on failure.

Initial read/label rules now live in Gameplay and match pinned `OnMailSent`: only a non-received outgoing personal copy starts read. Self-mail and the sender's subscribed list copy start unread, with their native Inbox/Sent labels. The earlier self-mail test assumed every sender copy started read; that expectation was corrected from the inspected callback. Existing saved read flags are preserved.

World version **63** adds `WorldMailingList`, `WorldMailingListMember`, `WorldMailingListAccess`, indexed `WorldMailingListChange` and nullable indexed `WorldMail.ListId`. Version-62 migration preserves personal messages and private state with a null list ID. Scoped event delivery uses `OnMailingListSetOperator`, `OnMailingListSetMuted`, `OnMailingListSetClear`, `OnMailingListLeave` and `OnMailingListDeleted`. Self-demotion receives its role event because the RPC wrapper does not change the native role cache itself. Lists joined in another simultaneous session require a fresh `GetJoinedLists` or reconnect: the pinned service has no external join callback. Role events for unknown cached memberships are withheld to avoid a native missing-key failure. The database and subsequent reads remain authoritative.

Reference: unchanged `code.ccp` SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**. Consulted [Carbon Blue at fcbf0c2](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp). Existing KeyVal, list, dictionary, bytes and notification adapters suffice; no codec allowlist or transport change. Client code objects were inspected inertly, never executed. Only standard string operations were used to derive the embedded compatibility data, which needs no EVE installation at server runtime.

**Automated validation.** Tool restore (0.22 s), solution restore (0.65 s), and build (1.46 s) passed with zero warnings/errors. The focused filter `FullyQualifiedName~Mail|FullyQualifiedName~CharacterSearch|FullyQualifiedName~Inbox|FullyQualifiedName~Contacts|FullyQualifiedName~Calendar|FullyQualifiedName~CharacterSettings|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~SkillExtractionLifecycleTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~TypedSessionTests|FullyQualifiedName~BlueMarshalTests|FullyQualifiedName~PilotCallRouterTests` passed **210/210 cases** in 18.96 s wall time using `-- xUnit.MaxParallelThreads=4`. This includes **39 new mailing-list cases**, the existing compose-default and eight-argument arrival regressions, persisted permissions/reopen coverage, concurrent joins/name claims, transactional failure rollback, actual 3,000-member/1,000-update boundaries, private audience snapshots and native role/welcome/broadcast events. Both Python policy suites passed (3 catalog and 9 formatting cases, 0.51 s combined). Formatting verification passed (591 files, 13.76 s wall time), as did `git diff --check`. The full .NET suite was not requested or run; no live rendering, input or gameplay acceptance was attempted.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Create/join a list and exchange a broadcast between disposable pilots; use roles and allow/block/welcome controls; reconnect and inspect historic list mail, labels and alerts. Native rendering, input and list-cache presentation remain unverified.

## Mail arrival notification arity

After the compose-default correction, the maintainer reported that the recipient received no mail notification. Inert inspection of pinned build **3662168** `eve/client/script/ui/services/mail/mailSvc.pyj` shows `OnMailSent.co_argcount == 9`: **self plus eight arguments**: `messageID`, `senderID`, `sentDate`, `toCharacterIDs`, `toListID`, `toCorpOrAllianceID`, `title` and `statusMask`. `labelMask` and `read` occur later in `co_varnames` because they are locals. Eden incorrectly sent a ninth label-mask argument, which the native callback cannot accept.

Removed that extra wire argument. The client computes Inbox/Sent/group labels itself and, for an unread recipient, calls `OnNewMailReceived`, which applies the user's existing blink/popup settings. No separate invented popup or `OnNewMailReceived` server event is sent. The corrected event retains Unicode title, recipient list, None optional group IDs and the numeric status mask. Private mailbox labels remain persisted and continue to travel through status/synchronization and external label-update events.

Four encoded-byte regressions assert the exact eight-field tuple for unread, read and automated status combinations. The actual two-session delivery regression now also checks callback arity and unread status, alongside recipient/header/body persistence and writer-echo suppression. Before the fix all five selected regressions failed, including the real-delivery assertion of nine arguments instead of eight. These checks validate protocol shape rather than just the existence of a named notification.

References: unchanged pinned archive SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368** and previously consulted [Carbon Blue revision fcbf0c2](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp). Client code objects were inspected inertly, never executed. No codec, schema, runtime-world or mail-preference changes were made.

**Automated validation.** Filter `FullyQualifiedName~Mail|FullyQualifiedName~CharacterSearch|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed **77 cases**, including **four new encoded arrival-contract regressions** and the tightened real-delivery case, in approximately **11 seconds**, with `xUnit.MaxParallelThreads=4`. Tool restore, solution restore, solution build (zero warnings/errors), both Python policy suites (3 catalog and 9 formatting tests), formatting verification (582 files, 140-character maximum), documentation checks and `git diff --check` passed. Before the fix the five selected reproductions all failed, including the service-delivery check expecting eight arguments and observing nine. After the fix every ordinary/read/automated payload has the pinned shape, while existing mail defaults, replies, private state, name lookup, body encoding, delivery and echo checks remain green. No full .NET suite or interactive client acceptance was performed. Mailing-list work remains isolated and uncommitted in a separate checkout.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Load the rebuilt server, send a message between the two selected pilots, and inspect the recipient's unread indicator/popup with its normal mail notification settings. The reported missing alert is historical failure evidence; native presentation of this fix remains unverified.

## Mail compose optional source defaults

The maintainer's November 4 screenshot showed **Expected a mail identifier or None** when sending new mail to pilot 2. Inert inspection of build **3662168** `eve/client/script/ui/shared/neocom/evemail.pyj`, class `NewNewMessage`, confirms `default_isForwardedFrom = False` and `default_isReplyTo = False`. These values reach `mailMgr.SendMail` through `mailSvc.SendMail`. Eden's optional-ID adapter accepted None and integer zero but rejected Python False, so its automated requests with None defaults had missed the native failure.

`MailHandler.OptionalId` now maps exactly **None, False and integer zero** to absence. Positive integer source IDs retain ownership checks. True remains invalid and cannot become message ID 1. No protocol reply, persistence, CSPA policy or schema changes are needed. The original native mail fixture now uses the actual False defaults; existing reply/forward cases therefore also exercise a positive source ID with the other source set to False. Nine new cases cover False/None/zero combinations, one ordinary private delivery, no spurious source flags, and True rejection in every optional-ID position without delivery.

Reference: pinned archive SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368** and the [Carbon Blue revision](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp) recorded for the mail milestone remain unchanged. Client code objects were inspected inertly, never executed. No codec change or runtime-world edit was made.

**Automated validation.** Filter `FullyQualifiedName~Mail|FullyQualifiedName~CharacterSearch|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed **73 cases**, including **nine new composer-default regressions**, in approximately **nine seconds**, with `xUnit.MaxParallelThreads=4`. Tool restore, solution restore, solution build (zero warnings/errors), both Python policy suites (3 catalog and 9 formatting tests), formatting verification (579 files, 140-character maximum), documentation checks and `git diff --check` passed. The pre-fix regression failed the three False-bearing combinations and passed the six control/rejection cases, reproducing the reported condition. Existing encoded-wire mail tests now use stock False defaults and retain real positive reply/forward sources. No full .NET suite, game launch or interactive client acceptance was performed. The ongoing mailing-list implementation remains isolated in its own checkout and is not part of this fix.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Load the rebuilt server and retry a new message from pilot 1 to pilot 2; inspect delivery, then reply. The screenshot establishes the original live failure, not acceptance of the corrected build.

## Persistent personal EVE Mail

Implemented `mailMgr.SendMail`, `SyncMail`, `GetMailHeaders`, `GetBody`, `GetLabels`, `CreateLabel`, `EditLabel`, `DeleteLabel`, `AssignLabels`, `RemoveLabels`, `ReplaceLabels`, `MarkAsRead`, `MarkAsUnread`, `MarkAllAsRead`, `MarkAllAsUnread`, `MarkAsReadByLabel`, `MarkAsUnreadByLabel`, `MoveToTrash`, `MoveFromTrash`, `MoveAllToTrash`, `MoveAllFromTrash`, `MoveToTrashByLabel`, `DeleteMail` and `EmptyTrash`. Personal mail works for local Eden pilots in station or space. At this milestone, the initializing `mailingListsMgr.GetJoinedLists` returned empty membership and group sends rejected. Mailing-list management and delivery are implemented in the later mailing-list section above. Organization delivery and paid CSPA negotiation remain pending. Current personal mail is free; only absent/zero `approvedCost` is accepted, with no wallet debit or fabricated fee approval.

Inert inspection of pinned build **3662168** `eve/client/script/ui/services/mail/mailSvc.pyj`, `mailingListsSvc.pyj`, `eve/client/script/util/eveMisc.pyj` and `appConst.pyj` establishes the wire contract. `SendMail` carries a recipient list, optional list/organization IDs, Unicode subject/body and optional reply/forward source IDs; it returns a scalar message ID. `SyncMail(firstID, lastID)` returns a KeyVal with mutable-list `newMail`, `oldMail` and `mailStatus` members. New and older headers bracket the client's cached range; current private statuses let the client remove permanently deleted cached headers and fetch missing ones through `GetMailHeaders`. Headers contain `messageID`, `senderID`, `sentDate`, comma-separated `toCharacterIDs`, None list/organization IDs and Unicode `title`. The body response is **zlib-compressed UTF-8 bytes**, matching native `zlib.decompress(...).decode('utf-8')`. The search path was checked against `searchOld.pyj`, `searchUtil.pyj` and `eve/common/script/search/const.pyj`. Character-owned mail settings reuse the already implemented `characterSettings` persistence through `mailSettings`; no separate opaque settings parser was added.

Pinned limits are **50 distinct recipients**, **150 subject characters**, **8,000 plain body characters**, **10,000 tagged body characters**, **40 label-name characters**, **25 custom labels** and **five sends per rolling minute**. Rich text is retained; tag-stripped text and the complete body are checked separately. NUL text, malformed identifiers/masks, unknown recipients, foreign/deleted reply/forward references and recipients blocking the sender reject before any commit. Duplicate recipients reject rather than creating duplicate copies. The exact minute boundary is allowed, and throttle accounting uses durable message rows, so restarts and concurrent sends cannot bypass it. Every recipient is validated together. Replies/forwards mark only the sender's owned source copy; they cannot reveal or modify another pilot's private source message.

The compose recipient field also required the previously missing `search.QuickQuery` route. Added local character-category **2** lookup through `QuickQuery` (flat ID list) and `Query` (category-keyed ID lists), with pinned match modes **0/1/2/3** for partial terms, exact terms, ordered exact phrase and exact name. Matching is case-insensitive, follows token boundaries, and local names have no alternate localized name. Queries are bounded to **100 characters** and **500 results**. Short wildcard terms fall back to exact-term matching; language-specific concise-text heuristics remain outside this local lookup. These routes expose only public local pilot IDs; unknown categories, malformed options and caller-supplied owner filters reject. NPC/corporation/alliance/location search remains pending. The normal typed-recipient mail test resolves a saved pilot name through this native path before sending.

An immutable message stores sender identity, time, subject, body and a typed JSON recipient list. Each participant owns an independent mailbox entry with status flags and label mask. Ordinary sender copies start read with Sent label **2**; recipient copies start unread with Inbox **1**. Self-mail creates one read copy with both labels. Native status bits preserve read/replied/forwarded/trashed **1/2/4/8**. Headers and body reads require the caller's visible copy. Reading, labelling or deleting one participant's copy never changes another participant's state. Permanent deletion requires trash and hides that pilot's copy; it does not erase the shared body from other participants. Bulk operations validate ownership before changing any selected row; all-message and label filters exclude trash, while restoration/empty-trash select only trash.

Custom label IDs are individual bits **16 through 2^28**, independently owned per mailbox. Names are checked case-insensitively for duplicates. System labels cannot be renamed or deleted; organization label bits cannot be assigned to personal messages. Deleting a custom label clears its bit from all of that pilot's entries before the bit can be reused. Label assignment/removal/replacement, message flags, sends and their change records share the same transaction. Forced event insert failures roll back message creation, source flags and all deliveries, not just the final notification.

World version **62** adds `WorldMail`, `WorldMailEntry`, `WorldMailLabel` and indexed `WorldMailChange`. Version-61 upgrade preserves prior skills, assets, queues and wallet state. Historical headers can outlive a sender; participant rows retain pilot ownership. A scoped monitor drains durable changes in bounded **128-row** batches, using a server-created per-connection origin ID to avoid echoing changes that the initiating native client already applies itself. Other connected participants receive `OnMailSent`, `OnMailUpdatedByExternal`, `OnMailTrashed`, `OnMailRestored`, `OnMailDeleted` and `OnLabelsCreatedByExternal` with named wire adapters. A delivery permanently deleted before its next tick cannot revive the header. Offline clients recover through normal synchronization. Native external events support read/label-mask updates and label creation/editing; reply/forward badges and deletion of another session's cached label name may require a mail-cache refresh or reconnect. Saved state and subsequent reads remain authoritative.

Reference: installed `code.ccp` SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**, reverified for this milestone. Consulted [Carbon Blue Marshal.cpp at fcbf0c2](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp), including inert native object construction. Existing dictionary, list, Unicode, KeyVal and bytes adapters suffice; no codec allowlist or transport change. Client code objects were inspected, never executed.

**Automated validation.** Expanded filter `FullyQualifiedName~Mail|FullyQualifiedName~CharacterSearch|FullyQualifiedName~Inbox|FullyQualifiedName~Contacts|FullyQualifiedName~Calendar|FullyQualifiedName~CharacterSettings|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~SkillExtractionLifecycleTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~TypedSessionTests|FullyQualifiedName~BlueMarshalTests|FullyQualifiedName~PilotCallRouterTests` passed **158 cases**, including **58 new mail/search cases**, in approximately **11 seconds** on parent `0bc489f`. After integrating the launcher Omega-registration change, final filter `FullyQualifiedName~Mail|FullyQualifiedName~CharacterSearch|FullyQualifiedName~Eden.Protocol.Tests.Launcher|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed **86 cases** in approximately **ten seconds** on parent `64aad60`. Both runs used `xUnit.MaxParallelThreads=4`; TRX files remain under ignored development artifacts. Tool restore, solution restore, solution build (zero warnings/errors), both Python policy suites (3 catalog and 9 formatting tests), formatting verification (578 files, 140-character maximum), documentation checks and `git diff --check` passed. Tests cover all personal bulk actions, sender/recipient/self copies, native name lookup followed by sending, all four matching modes, Unicode and compressed body round trips, private/foreign/deleted reads and source references, reply/forward flags, the 50-recipient boundary, blocked/unknown recipients, malformed/group/owner/cost requests, exact text and minute limits, concurrency, durable throttling, event-failure rollback, 25 label bits and reuse, native events and writer-echo suppression, feed batching/deletion coalescing, reopen and version-61 migration. The extraction migration regression now checks `CurrentVersion` instead of a hardcoded historical version, and remains included in the expanded run. Initial development checks passed 24 cases and exposed a test assumption about outbound Unicode wrappers; the subsequent encoded-wire assertion distinguished native bytes from Unicode correctly, and all 25 then passed. Bulk/boundary cases passed 42 before the final reply/forward cases brought mail coverage to 44; the initial expanded run passed 144 before compose-path inspection identified and resolved missing character lookup. An over-width migration-test SQL string was split before final verification. No full .NET suite, game launch, interactive acceptance or runtime-world modification occurred.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Exchange a rich-text message between disposable local pilots and inspect unread/body rendering; reply or forward and reconnect; exercise labels and trash/restoration through native menus. Native rendering, input, cache presentation and gameplay feel remain unverified.

## Local and station list rebuild races, November 11, 2026

The user reported ordinary-pilot Local counts exceeding visible rows and station Guests showing four members but only
one name, without recovery on reopening. The screenshots establish the reported symptoms; no live client acceptance
was performed. Inert inspection and a deterministic cooperative-tasklet reproduction identify two native list defects
that can produce missing or stale rows independently of the server's membership snapshots.

In build 3662168 `xmppchatclient/xmppchatwindow.pyj`, `InitUsers` (lines 1762–1775) clears the scroll but retains
`userlistNodesByName`. It also iterates `members` while owner lookup can yield. An arrival or departure during that
lookup can change the dictionary during iteration. Retained row indexes can then suppress a later visible insertion.
In `eve/client/script/ui/shared/dockedUI/guests.pyj`, `_update_scroll_content` (lines 323–351) captures guest rows,
allows yielding name/entry work, and finally replaces the scroll. Incremental `_add_guest_to_scroll` and
`_remove_guest_from_scroll` do not invalidate that rebuild: an intervening arrival can be overwritten and a departed
guest can be reinserted. The guest controller maintains its membership/count independently of those rendered rows.

The signed login adapter now uses the client's existing per-view `locks.TempLock` (native default: `RLock`) to serialize
each full rebuild with its incremental arrivals and departures. Local clears its row index before rebuilding it.
Other chat windows and guest panels retain independent locks. No extra membership polling, database work, server
population change or per-arrival full redraw is introduced. Installation is idempotent and reaches regular accounts
through the existing signed login challenge; rebuild/restart the server and fully reopen the client to install it.

Source archive SHA-256: `37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.
The tested original archive is the staging backup `.eden-original-code.ccp`; the staged mod archive is intentionally
different. The relevant chat, guest and lock modules match the original byte for byte. Native Python runtime SHA-256:
`1266e119d5db70281831f06049faa2694e658a68d504e0cc5680ec5726ba3aa2`.
Gameplay reference: [EVE University Local membership](https://wiki.eveuniversity.org/Identifying_war_targets_in_Local).
The reference evejs release `261108.1`, `server/src/services/station/stationService.js` and
`server/src/services/chat/xmppStubServer.js`, was checked for its guest snapshot and chat-presence boundaries;
it targets an older client and supplies no code for this native UI correction. No Blue, transport or Destiny change
is involved; the existing documented Carbon serialization references remain historical evidence for those unchanged formats.

**Automated validation.** Tool restore, solution restore/build (zero warnings/errors), 120 focused .NET cases with no
skips, both Python policy suites (8 catalog and 9 formatting cases), formatting/width verification across 1,665 files
and diff verification passed in 49.19 seconds. The focused tests reported 23 seconds. The affected filter was
`FullyQualifiedName~PresenceListTests|FullyQualifiedName~ConcordOverviewTests|FullyQualifiedName~CharacterPortraitTests|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~Eden.Protocol.Tests.Chat|FullyQualifiedName~Eden.Protocol.Tests.Hosting`.
The exact first filter was
`FullyQualifiedName=Eden.Protocol.Tests.PilotSessions.PresenceListTests.NativePresenceRebuildsPreserveConcurrentArrivalsDeparturesAndRowIndexes`.
The headless check first reproduces overwritten arrivals, resurrected departures, retained Local row indexes and a
dictionary mutation error in the unmodified native methods. It then evaluates the exact signed adapter in the pinned
Python interpreter and checks three repeated arrival/departure cycles, count/row/index agreement and idempotent
installation. Only extracted method code executes with test UI, owner lookup and cooperative lock doubles; native
rendering and the game's complete scheduler are outside this test. Initial fixture setup rejected modified archives
and exposed missing test-double fields/builtins and Python 2 method-identity semantics; these were corrected before
the passing exact test. No full .NET regression suite was requested or run.

After integrating main's coarse-simulation update at `41c569ba`, the same required restore/build/test/policy/format
sequence passed in 57.42 seconds, with zero build warnings/errors. The filter above plus
`|FullyQualifiedName~CapsuleerMaintenanceTests` passed **125 cases with no skips** (25 seconds reported test duration),
including the high-density real-world Local/station membership check. Both Python suites and formatting passed again.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** After updating the server and reopening the client, compare Local names/counts and
station Guests through arrivals/departures; reopen each list once during busy traffic. Native timing and rendering
remain unverified.

## Station guests, November 4, 2026

The user's screenshot showed two pilots at the same station, each seeing only themselves under Guests. The compatibility handler returned a hard-coded singleton containing the requesting pilot, and no station presence notifications existed.

Build 3662168's `eve/client/script/ui/station/base.pyj`, lines 122–141, caches `station.GetGuests()` until the station changes. Its response is a Python list of `(characterID, corporationID, allianceID, warFactionID)` tuples. Both `OnCharNowInStation(rec)` and `OnCharNoLongerInStation(rec)` accept one such tuple. `eve/client/script/ui/shared/dockedUI/guests.pyj`, lines 222–229, consumes the same arrival/departure events for the displayed list. Alliance and faction remain None for Eden's current pilots. The inspected `code.ccp` SHA-256 is `37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`. These service contracts come from the pinned client; [Carbon Blue Marshal.cpp at af0d65b29f951260ea09140ca11adf5d40566997](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp) remains the consulted list/tuple serialization reference. No codec or transport change is required.

`SharedStationGuests` now holds immutable, connected-pilot contacts under a lock, scoped by exact station ID. `StationGuestHandler` publishes selected docked pilots, returns a deterministic list including self, and compares snapshots on the existing one-second session updates. Idle docked sessions keep that update loop active even without training or flight. Arrivals and departures use named notifications with the native tuple shape. A GetGuests refresh also resets its notification baseline, preventing duplicate arrival events. Station changes reset the observer baseline; same-station ship changes retain membership. Other-station, in-space, offline and character-selection pilots are excluded. Guest names and corporation tickers resolve through the existing account compatibility calls. Presence is transient, with removal on character logoff, dependency-scope disposal, and session termination including read/write failures; reconnect rebuilds it without a world schema change.

**Automated validation.** Tool/solution restore and Debug build passed with zero warnings/errors (2.30 seconds). All 97 focused .NET tests passed (13.62 seconds command time, 12 seconds reported test duration), including six new regression cases. Both Python policy suites passed (3 catalog setup and 9 formatting cases), and `Format.ps1 -Check` passed across 405 files with the 140-character policy (9.26 seconds combined policy/format command time). Regressions cover the initial two-pilot list, offline/other-station/in-space exclusion, owner/ticker replies, exact list/tuple bytes, idle observer updates across three docking cycles, native notification envelopes, no duplicate events, ship refresh, logoff/reselection, disconnect/reconnect, malformed calls, selection gating and read/write failure cleanup. No full .NET regression run was requested or performed. The focused filter was:

```powershell
dotnet test --no-build --filter 'FullyQualifiedName~Eden.Protocol.Tests.Docking|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions|FullyQualifiedName~Eden.Protocol.Tests.Flight.FlightSessionTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~Eden.Protocol.Tests.Combat.ShipSafetyTests'
```

**Live acceptance.** Not performed; agent client testing is disabled. The screenshot establishes the original defect; native rendering and list refresh remain unverified.

**Optional maintainer checks.** Restart the updated server and reconnect both pilots at the same station; confirm both guest names/counts, then undock/redock or log off one pilot while watching the other's Guests panel.

### Native request metadata correction, November 4, 2026

The user subsequently reported **Guests [0]** in both clients after the first fix. The original 97-test validation omitted native MachoNet metadata, so it did not establish compatibility of the real request. Build 3662168's `carbon/common/script/net/ServiceCallGPCS.pyj`, `RemoteServiceCallWithoutTheStars`, lines 282–307, adds `machoVersion=1` to every uncached remote call; cached calls can instead carry their existing version value. The no-argument station handler rejected that transport field as an unexpected service keyword. The separate flatbuffer 501 error visible in the screenshot has not been linked to guest loading and is not treated as evidence of this cause.

The router now removes the reserved `machoVersion` field before account-level or selected-pilot dispatch, including embedded `MachoBindObject` calls. Business keywords remain available and the incoming dictionary is not mutated. Unknown station arguments remain rejected. This also lets the existing strict character-logoff and requested-disconnect handlers receive their intended empty service keyword set. The guest presence registry and notification shapes are unchanged. The source is the pinned archive and SHA-256 recorded above; Blue/transport serialization is unchanged.

A new simulated-session regression using the native metadata failed against `2aeb40a` with a station GetGuests ErrorResponse, then passed with the router correction. It checks both docked pilots and the other pilot's name through encoded replies. All `PilotServicesTests` requests now include the native field by default, and direct/bound router regressions verify that a cached version value is removed while `qty` and the original dictionary remain intact.

**Automated validation.** Tool/solution restore and Release build passed with zero warnings/errors (2.21 seconds). All 100 focused .NET tests passed (15.20 seconds command time, 13 seconds reported test duration), including the new native guest-session regression and two direct/bound metadata cases. Both Python policy suites passed (3 catalog setup and 9 formatting cases), and `Format.ps1 -Check` passed across 405 files (8.94 seconds combined policy/format command time). The intentionally failing one-test reproduction preceded the correction; the later focused pass covers the corrected router and native request fixture. No full .NET regression run was requested or performed. The final filter was:

```powershell
dotnet test --no-build -c Release --filter 'FullyQualifiedName~Eden.Protocol.Tests.Docking|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions|FullyQualifiedName~Eden.Protocol.Tests.Flight.FlightSessionTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~Eden.Protocol.Tests.Combat.ShipSafetyTests'
```

**Live acceptance.** Not performed; agent client testing is disabled. The user's second screenshot demonstrates the previous implementation's failure, not acceptance of this correction.

**Optional maintainer checks.** Rebuild/restart the server and reconnect both pilots; confirm both guest lists populate and still update on undocking/redocking.

## Simulated-pilot Local roster batches — November 9, 2026

The user reported 19 station guests but only four Local members after selecting 10× station traffic and six gate visitors.
A read-only saved-world check confirmed those population settings were active. This is user-provided evidence for the
pre-correction build, not agent client acceptance.

Inert build-3662168 inspection of `xmppchatclient/xmppchatsvc.pyj` confirms that `_HandleMessageFromAdmin` updates individual
window arrivals/departures, while `_UnpackVikingPresence` also supplies user metadata and the per-channel participant registry.
The pinned `code.ccp` archive has SHA-256 `37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.
No game module bodies, game executable, renderer or interactive session were launched.

Previously maintenance published simulated pilots individually and never refreshed an already joined Local's complete roster.
Those cached individual updates did not establish a later complete snapshot. Maintenance now publishes its changed pilots in
one chat operation. Gameplay applies the whole membership batch before constructing one shared ordinary-Local roster per
affected room. Each observer receives the existing `/viking` wire format with all visible humans and simulated pilots.
Arrivals use this full snapshot, avoiding a burst of per-pilot join messages; departures retain explicit `/admin` leaves because
the native roster handler adds members rather than clearing absent entries. Unchanged batches produce no chat delivery.
Joining or retrying Local still sends the ordinary initial full roster. Wormhole/Pochven delayed Local remains unchanged.

This uses the existing TLS/XMPP adapter; no Blue or Destiny format changed. Gameplay reference:
[EVE University Local membership](https://wiki.eveuniversity.org/Identifying_war_targets_in_Local), including docked capsuleers
in the same system. Traffic multipliers determine roster provisioning, not how many travelers must remain in one system.

**Automated validation.** The initial exact regression
`FullyQualifiedName=Eden.Protocol.Tests.Chat.ChatWorldTests.SimulatedLocalArrivalIncludesAnAuthoritativeRosterForNativeParticipantState`
failed on the old implementation because no roster was emitted, then passed after the correction. Further cases cover a
1,000-pilot/two-observer batch sharing one roster, unchanged reads, explicit departure, delayed-Local privacy, a sixty-pilot
burst over TLS, reconnect/retry, and high-density real-world station guests matching Local membership.

Tool/solution restore and the solution build passed with zero warnings/errors (5.71 seconds). The final affected run passed
**150 tests**, with no skips, in **60.89 seconds** including runner startup (feature build 2.32 seconds). Both Python policy
suites passed: eight catalog checks in 1.692 seconds and nine formatting checks in 0.028 seconds. Formatting, width and diff
verification passed. An earlier run passed 148 cases and failed two new delayed-Local fixture expectations: those incorrectly
excluded the querying pilot itself. The corrected two-case exact filter and the complete affected filter then passed.
No full .NET regression suite was requested or run.

Final filter:
`FullyQualifiedName~Eden.Protocol.Tests.Chat|FullyQualifiedName~CapsuleerMaintenanceTests|FullyQualifiedName~CapsuleerHaulerTests|FullyQualifiedName~CapsuleerInboundRuntimeTests|FullyQualifiedName~CapsuleerEncounterTests|FullyQualifiedName~CapsuleerPopulationTests|FullyQualifiedName~CapsuleerPopulationSettingsTests|FullyQualifiedName~CapsuleerDeadlineTests|FullyQualifiedName~CapsuleerDefenseFrameTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.AdministrationTests|FullyQualifiedName~AdministrationComponentTests|FullyQualifiedName~WorldSettingsPageTests|FullyQualifiedName~WorldConfigurationTests|FullyQualifiedName~ServerPerformanceTests|FullyQualifiedName~StationGuest`.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** After rebuilding/restarting the server, reconnect chat and compare Local against docked guests;
observe one arrival/departure while Local remains open. Native timing and rendering remain unverified.

## Resident mail, inbox and calendar

Mail, mailing lists, contact notifications and personal calendars now use the resident transaction owner.
A send publishes its message, per-pilot mailbox copies and ordered delivery entries together. Joining a
mailing list publishes its membership, feed and optional welcome mail in the same action. Per-pilot mail
and mailing-list cursors are maintained from committed rows so an acknowledged poll skips feed history.
Contact blocks are visible to the next send or invitation. Calendar invitations, edits and cancellations
publish their private change records with the event. The process-local revision advances after commit.

The older local EveJS distribution identifies itself as `261108.1`. Reference checks covered audience
selection and delivery in `mailState.js` (SHA-256 prefix `82B61A1F917EC16B`) and event creation and
responses in `calendarRuntimeState.js` (`0F6CDE6AD3340639`). EVE University's calendar reference
documents the use of UTC. [EVE Server Time](https://wiki.eveuniversity.org/EVE_Server_Time)

### Automated validation

The warmed resident fixture verifies same-tick mail and list delivery, acknowledged cursors, rejected
invitations, attendee replies, and zero SQL commands, connection opens and fitting calculations.
A forced calendar-feed failure during checkpoint projection rolls the entire saved image back.
Retrying the same checkpoint preserves the original mail IDs, origins, and calendar responses.
Contact-label renames that exchange two names across several actions also survive one checkpoint.
The file-backed inbox rollback test now checks the tracked adapter's exception type and verifies
that every affected entry keeps its original read and deletion state.

The combined run through `tools/Test-Feature.ps1` passed 236 cases in 91.82 seconds. Tool and solution
restore passed, the solution built in 6.85 seconds with zero warnings or errors, and the Python catalog
and formatting policy suites passed 8 and 9 cases. `tools/Format.ps1 -Check` passed. The run selected
these `FullyQualifiedName~` filters.

```text
WreckTests, CalendarTests, CharacterDeletionTests, InboxTests, MailingListLifecycleTests, MailingListTests,
MailLifecycleTests, MailTests, PilotBookmarksTests, PilotContactsTests, ResidentSocialTests, SharedBookmarksTests,
BookmarkServiceTests, CalendarServiceTests, ContactsServiceTests, InboxServiceTests, MailArrivalContractTests,
MailComposerDefaultsTests, MailingListServiceTests, MailServiceTests, SharedBookmarkServiceTests,
PlanetaryIndustryTests, PlanetaryProtocolTests, PlanetaryRulesTests, PlanetarySkillExtractionTests
```

After incorporating the concurrent AutoMining change, the solution built in 7.93 seconds and 47
affected cases passed in 23.88 seconds. The rerun selected `ResidentSocialTests`,
`SharedBookmarkServiceTests`, `CalendarServiceTests`, `InboxTests`, `PlanetaryIndustryTests`, and `WreckTests`.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

None.
## Notepad and shared settings — November 14, 2026

A bounded comparison with evejs found nine missing native calls, now implemented:

| Feature | Calls | Behavior |
| --- | --- | --- |
| Personal notepad | `charMgr.GetOwnerNoteLabels`, `GetOwnerNote`, `AddOwnerNote`, `EditOwnerNote`, `RemoveOwnerNote` | Private entries, titles, rich text, rename without replacing the body, deletion and folder metadata. |
| Overview sharing | `overviewPresetMgr.StoreLinkAndGetID`, `GetStoredPreset` | Immutable profile links readable by other selected pilots. |
| Inventory filters and fleet broadcasts | `sharedSettingsMgr.StoreSettingLinkAndGetID`, `GetStoredSharedSetting` | Immutable shared links with the native setting-type discriminator. |

Existing `charMgr.GetNote`/`SetNote` describe Show Info character notes and remain separate from the notepad.
Notepad ownership comes only from the selected pilot. Reading another pilot's note ID produces an empty native
note result; editing it fails and removing it does nothing. Missing note IDs behave identically. Labels retain
the native `N:` and `S:` prefixes. The client stores folder metadata in ordinary hidden note rows, split into
6,000-character chunks; the server preserves these without interpreting the client folder encoding. Titles
allow the native 80 characters plus their two-character prefix. A two-argument rename or explicit None body
preserves the previous text; an empty string clears it.

Shared settings arrive as inert dictionaries. The wire adapter writes a YAML-compatible list of key/value
pairs for the native `safe_load`/`GetDictFromList` path. Nested numeric dictionary keys, sequences, Unicode,
booleans, None and finite numbers retain their meanings. Class instances and unsupported values are rejected.
Eden bounds profiles to 256 KiB UTF-8, 20,000 nodes and 32 nesting levels. The server never loads YAML or executes
settings. Keys contain the saved ID and SHA-256 payload digest, plus kind 2 for inventory filters or 3 for fleet
broadcast settings. Exact repeated payloads of the same kind reuse a key. Editing a profile creates a new link;
old links remain valid. Reads require the complete matching key and return None when it does not match.

World schema 156 adds separate notepad and shared-setting tables. Notes follow pilot deletion through the
existing cascade; shared profiles are public link targets independent of the creator's lifetime. Both tables
participate in resident transactions and checkpoint capture, including sequence allocation, immutable captured
generations and failed-projection retry. No Blue codec, transport or Destiny implementation was changed.

### Reference evidence

The local evejs distribution identifies itself as **261108.1**, targeting client **3396210**, and contains no
Git metadata. Its package declares AGPL-3.0-only. It was used to identify service gaps; Eden's C# implementation
was authored against the pinned client's observable contracts and existing Eden abstractions. File fingerprints:

| Local evejs source | SHA-256 |
| --- | --- |
| `server/src/services/character/charMgrService.js` | `82731FCEBB67A53A6723F95464A7FFA838BC98C57344FA6C64B0E3A7A90858F1` |
| `server/src/services/character/characterNoteState.js` | `E39C0669C4E66B8F1B2E994660C300FC03D87DA934F09947B6E15F60C40CA39B` |
| `server/src/services/overview/overviewPresetMgrService.js` | `9C90E387CA9FFE5FB6794E0FFD60974231A6EE728AA2ED6F748AB9350E0CAC75` |
| `server/src/services/settings/sharedSettingsMgrService.js` | `DD32CC2036B17D95365314D76232AAEC7C530EB2910288C8B4D63EE2FB4153A7` |

Build **3662168** `code.ccp` was inspected as inert Python bytecode with xdis 6.3.0. Relevant modules are
`eve/client/script/ui/shared/neocom/notepad.py`, `eve/client/script/parklife/overview/presetservice.py`,
`eve/client/script/ui/control/eveEditPlainText.py`, `eve/client/script/ui/shared/inventory/filterSvc.py`,
`sharedSettings/util.py`, `sharedSettings/constants.py`, and the overview/shared-setting link parsers.
The untouched `.eden-original-code.ccp` archive SHA-256 is `37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.
All eight inspected modules match the staged build-3662168 archive byte-for-byte. No game modules were executed. [EVE University's overview-sharing guide](https://wiki.eveuniversity.org/Overview_settings#Saving_and_sharing_your_Overview_Profile)
describes dragging shared profile links into text fields and retaining them in the notepad.

This closes the listed calls, not the entire evejs comparison backlog. The older generated RPC inventory is
historical evidence and is not a current count of missing handlers.

### Automated validation

Tool and solution restore passed. The final solution build completed in **7.60 seconds**, with zero
warnings/errors. The combined affected run passed **69 tests**, no skips, in **22.82 seconds** including runner
startup (incremental feature build **1.36 seconds**). Both Python policy suites passed: **8 catalog checks** in
**0.964 seconds** and **9 formatting checks** in **0.014 seconds**. Formatting and diff verification passed.
No full .NET regression suite was requested or run.

The initial exact filter was
`FullyQualifiedName=Eden.Protocol.Tests.Persistence.PersonalSettingsTests.NotepadRenameFoldersPrivacyAndDeletionSurviveReopen`
(1 pass; build 32.02 seconds, tests 8.10 seconds). The first shared-link protocol run failed three test
assertions that incorrectly expected decoded Blue byte strings to be CLR strings; using the existing text
adapter corrected the assertions. The subsequent new-feature run passed all 14 cases in 9.28 seconds.
An initial formatter run also reported two over-width SQL lines; both were split before final validation.

Final filter:

```text
FullyQualifiedName~PersonalSettingsTests|FullyQualifiedName~PersonalSettingsServiceTests|FullyQualifiedName~PilotNotesTests|FullyQualifiedName~PilotNotesServiceTests|FullyQualifiedName~CharacterDeletionTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~WorldInitializationTests|FullyQualifiedName~ResidentSocialTests|FullyQualifiedName~DockedServicesTests
```

Coverage includes native rowset/list/KeyVal shapes, selection gating, malformed calls, private notes for
characters sharing one account, partial rename and explicit clearing, rich text and Unicode, folder chunk
limits, shared-link key matching, deduplication, immutable earlier links, reopen and schema upgrade. Resident
coverage verifies immediate reads, zero warmed SQL commands/connections/fitting calculations, immutable
checkpoint capture, injected projection failure with complete rollback, successful retry, deletion and IDs
that are not reused after deletion. Native UI rendering and application are not established by these tests.

### Live acceptance

Not performed; agent client testing is disabled. Native rendering, drag/drop and client-side profile application
remain unverified.

### Optional maintainer checks

- Create, rename and reopen a note inside a notepad folder after a server restart.
- Drag an overview profile into a note and open the link from another pilot.
- Share an inventory filter and fleet broadcast settings, then load them from another pilot.
