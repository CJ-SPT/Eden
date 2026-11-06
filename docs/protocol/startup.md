# Startup and authentication

[Documentation](../README.md) / [Protocol index](../protocol.md)

Handshake, encryption, launcher accounts, character selection and initial sessions.

These records preserve milestone evidence and historical observations. Dates, schema versions, test results and live acceptance apply to the recorded change; they do not establish acceptance of later changes.

## Contents

- [Launcher Omega account default (November 4, 2026)](#launcher-omega-account-default-november-4-2026)
- [Launcher accounts and developer roles](#launcher-accounts-and-developer-roles)
- [Startup exchange](#startup-exchange)
- [Acceptance record](#acceptance-record)
- [Encrypted local authentication](#encrypted-local-authentication)
- [Character selection](#character-selection)
- [Docked session and inventory](#docked-session-and-inventory)
- [First flight](#first-flight)
- [Two pilots in Jita, November 2, 2026](#two-pilots-in-jita-november-2-2026)
- [Character logoff to selection, November 4, 2026](#character-logoff-to-selection-november-4-2026)
- [Client-requested exit logging, November 4, 2026](#client-requested-exit-logging-november-4-2026)

## Launcher Omega account default (November 4, 2026)

Launcher API registration now explicitly saves `AccountCloneGrade.Omega` in the same transaction as the new account and credentials. Both standard and invitation-authorized developer registrations receive Omega; role assignment is independent. Existing launcher builds use the updated server behavior. The general database Alpha default and the existing native clone-grade adapters remain unchanged, and registration does not upgrade unrelated accounts. This is Eden's account-registration policy; no Carbon serialization, transport or simulation behavior changes.

The active local development world was backed up before a one-time conversion of all four existing accounts to Omega. Reopening the database and restarting the development server confirmed all four saved grades remained Omega. This conversion is local runtime data, not a migration that upgrades other worlds.

**Automated validation.** Tool and solution restores, solution build (zero warnings/errors), focused .NET filter `FullyQualifiedName~Eden.Protocol.Tests.Launcher|FullyQualifiedName~CloneGradeTests|FullyQualifiedName~PilotLoginTests` with `xUnit.MaxParallelThreads=4` (**29 passed**, **9 seconds**), catalog setup policy (**3 passed**), formatting policy (**9 passed**), formatting verification (**567 files**) and diff checks passed. Coverage includes actual HTTP registration, standard/developer roles, persisted Omega after world reopen, existing Alpha isolation, and native clone-grade/login/training behavior. No full .NET suite was requested or run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Reconnect an existing pilot and inspect the native Omega status; its rendering remains unverified.

## Launcher accounts and developer roles

API v1 adds registration, launcher sessions, rotating remembered sessions, logout and single-use 180-second game tickets. Credentials and token hashes use additive Launcher tables; account/pilot identities and gameplay schema versions are preserved. The RSA/AES/challenge exchange remains unchanged. Manifest signing accepts only the exact fingerprinted 17,769-byte build 3662168 prefix. Private keys never leave the host.

Consulted [Carbon Blue crypto.cpp at af0d65b](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/crypto.cpp) for manifest-backed RSA and SHA-256 behavior. Pinned publicGateway/grpc/connectionConfig.pyj confirms that EVE_PUBLIC_GATEWAY_ADDRESS selects cleartext. The launcher uses a loopback relay with certificate-validated HTTP/2 TLS upstream for remote gateways.

Pinned carbon/common/script/sys/serviceConst.pyj gives WORLDMOD at bit 12, PROGRAMMER at bit 51, QA at bit 52, GMH at bit 53, GML at bit 54, and PLAYER at bit 61. Developer accounts receive those six roles; QA satisfies the Insider elevated-player mask and GML enables GM/WM Extras. The stored mask is sent in both login session_init and SessionInitialStateNotification. Standard accounts receive PLAYER. Developer registration requires a server-controlled invitation. Missing slash commands and privileged services remain unsupported.

Historical automated validation on the launcher branch: all 501 tests passed after integration with the committed hardener release, including launcher accounts, developer roles, HTTP throttling and the QA starter-system compatibility response.

After Computer Use was stopped, subsequent maintainer input produced server records confirming successful CreateCharacterWithDoll and selection of pilot test (90000003), at station 60015154 in ship 2000000000005. The final station scene and Insider/GM menus were not visually observed by the agent.

Historical live observations on the launcher branch: the Avalonia launcher created an invitation-protected developer account over the same PC's LAN HTTPS endpoint. Its prepared client connected on TCP 26200, verified build 3662168 and completed encrypted login/challenge verification. The client displayed character creation. The maintainer advanced to naming, where QA-only GetQAStarterSystemIDs was exposed. Pinned ccSvc._QAPickStarterSystem returns None for an empty system list, preserving normal school allocation; QA callers now receive that empty list, while standard callers remain unsupported. The underlying CreateCharacterWithDoll call retains ten arguments with a None override. Computer Use was stopped by the user with Escape after the fix was rebuilt. Subsequent maintainer input confirmed creation and selection server-side as recorded above; the final station scene, Insider/GM menus and the remaining native acceptance checks were not visually verified. The test topology used a non-loopback address and TLS on one machine, not a second-machine deployment.

### Main integration and test performance (November 4, 2026)

Integrated launcher branch `73da857` into main while retaining the hull audit export and the host's injectable clock. A production-service integration test checks single-use launcher tickets, account roles and expiry at the exact clock boundary. Fixture changes cache immutable base catalog bytes and parsed definitions while preserving independent writable databases and real world initialization. Identity helpers read only the required fields. The test runner now permits twelve conservative workers for the development machine's 14 cores / 20 hardware threads. Scratch databases use `artifacts/test-data` on the repository drive, with `EDEN_TEST_TEMP_DIRECTORY` available as an override, rather than the Windows temporary folder on C:.

**Automated validation:** tool and package restore passed; the final build passed with zero warnings/errors. The final focused run passed 34 tests in four seconds using `FullyQualifiedName~Eden.Protocol.Tests.Fixtures.TestCatalogTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldMigrationTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldInitializationTests|FullyQualifiedName~Eden.Protocol.Tests.Launcher|FullyQualifiedName~Eden.Protocol.Tests.Hosting`. This covers fixture isolation, initialization/migrations, accounts, developer roles, API throttling and host composition. Both Python policy suites passed (three catalog setup cases and nine formatting cases), and formatting verification passed across 394 files at the 140-character limit. A representative pair of Caracal cases, selected with `FullyQualifiedName~Eden.Protocol.Tests.Persistence.CombatHullTests&DisplayName~type: 621`, passed before and after the fixture optimizations: aggregate case duration fell from 14.69 to 6.17 seconds (about 2.4 times faster). That comparison preceded the scratch-directory move and is not a measurement of full-suite runtime. Two earlier full runs were interrupted at the maintainer's request, first to improve performance and then after C: disk saturation; neither is a pass. The maintainer then changed policy: full .NET runs require an explicit regression-check request. Repository instructions and GitHub validation now honor that opt-in; no full run covers this final integration.

**Live acceptance:** Not performed; agent client testing is disabled. Native rendering, input and gameplay feel remain unverified for this integration.

**Optional maintainer checks:** Sign in and launch through the desktop launcher; check remembered sign-in and tray/relay behavior with a running client.

### Six-character launcher passwords (November 4, 2026)

Launcher registration and the account-password command now accept 6–128 characters. The launcher password hint and hosting guides use the same range. Boundary coverage checks empty, five-, six-, 128- and 129-character passwords for both registration and password changes. Accepted credentials survive reopening; rejected changes preserve the prior password and active session and create no account. The HTTP registration/sign-in flow also uses a six-character password.

**Automated validation:** restore/build, 21 focused server tests (`FullyQualifiedName~Eden.Protocol.Tests.Launcher`, six seconds), both Python policy suites (three catalog and nine formatting cases), and formatting verification passed; the server validation sequence took approximately 32 seconds. Four launcher view-model tests (`FullyQualifiedName~Eden.Launcher.Tests.ViewModelTests`) passed in under one second using a clean Release build. The initial launcher Debug build was blocked by the open desktop executable; Release validation avoided that lock. No full regression suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Restart the server to apply the new password policy and use six characters in launcher account creation.

## Startup exchange

The startup configuration identifies release 24.01, codename `V24.01`, region `ccp`, and port 26000. The client's packaged MachoNet version module specifies **496**. The installed GPS transport and the build-matched Blue marshaller supplied the message order, field types, and serialization rules. [Blue marshal implementation](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp)

Each startup frame begins with a four-byte little-endian payload length. Blue marshal version 0 starts with `7e` and a four-byte shared-object count. The verified startup frames use zero shared objects. Eden accepts unflagged frames up to 16 KiB during this phase and reads each frame to completion before processing it. [Carbon transport flags](https://github.com/carbonengine/io/blob/8d8f339fd19f2e8095fcd90ad5415cb4ca00c331/src/protocol.h)

| Order | Sender | Payload |
| --- | --- | --- |
| 1 | Eden | Seven-field tuple containing marker 170472, protocol 496, pilot count 0, version 24.01 as a double, build 3662168, byte string `V24.01@ccp`, and update status `Unknown`. |
| 2 | Client | Six-field compatibility tuple with the same first six values. |
| 3 | Client | The tuple `(None, b'QC')` requests queue status. |
| 4 | Eden | Integer zero reports an empty login queue. |

The implementation compares the observed client messages byte for byte. A status request ends the probe. A login request continues into the encrypted account session. The host logs progress without recording arbitrary payloads.

## Acceptance record

On November 1, 2026, two fresh Windows client processes sent the compatibility reply and queue check to the C# listener after receiving its greeting. Each used the isolated installation with the stock executable and script archive. The tests preserve the matching bytes and check a different build, an unsupported command, short reads, and invalid frame headers.

## Encrypted local authentication

The development account uses the client's SSO launch path. After its version reply the client sends a VIP-key request and a pair of RSA-encrypted AES parameters. Eden acknowledges key negotiation, decrypts the local ticket with the session cipher, and validates its signature and expiry. Each side proves receipt of the other's random challenge before the server issues the account session. Two fresh client processes reported `LLV Succeeded` and sent the authenticated `machoNet.GetServiceInfo` call.

The client uses AES-256-CBC with per-packet padding. Challenge hashes cover a Blue-marshaled one-element tuple. The signed client expression is fixed to an empty dictionary. The outer MachoNet envelope may be zlib-compressed and contains another Blue stream for service arguments. The built-in string table comes from MIT-licensed Carbon Blue. [Carbon crypto](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/crypto.cpp) · [Carbon license](../carbon-license.md)

## Character selection

After authentication Eden returns service information and an initial player session. The client requests `charUnboundMgr.GetCharacterSelectionData`, primes its owner cache, then builds the pilot card. The seeded roster contains one player account and one pilot with ID 90000001, an Ibis, and the Caldari Navy Assembly Plant at Jita IV – Moon 4. The portrait uses the client silhouette.

Selecting the card checks the single-login policy, resolves the population service and its load-slot request, checks character lock status, and sends `SelectCharacterID`. Eden loads the account’s docked pilot from SQLite and validates the selected ID before sending the world-session transition. Unsupported service calls receive a MachoNet error.

### Live verification

On November 1, 2026, the staged build 3662168 client displayed the account's pilot name, ship, and Jita station. A click on the card sent the selection request, and the C# host accepted character 90000001. A second login to the same running host fetched the same pilot in a fresh session. The character-selection tests cover the roster, valid and invalid selections, and unsupported methods.

## Docked session and inventory

Build 3662168 accepts a command-16 `SessionChangeNotification` before the successful `SelectCharacterID` reply. Its body is `(sessionID, (0, changedFields), nodesOfInterest)`. Each changed field contains its previous and new values. A docked session sets `stationid`, `stationid2`, `locationid` and `solarsystemid2`. The in-space `solarsystemid` stays unset. An invalid selection returns only the call error.

The client binds `dogmaIM.GetAllInfo` to prime pilot and ship state. It uses `stationSvc.GetStationItemBits` for the station model and `map.GetStationInfo` for the station row. The station inventory broker binds to `(60003760, 15)`. Its first call can be nested in the bind request. `GetInventory(10004, None)` returns the hangar handle, while `GetInventoryFromId(shipID, 1)` returns the ship handle. The hangar lists the assembled ship with hangar flag 4. The ship's own hold starts empty. Inventory rows use native `blue.DBRow` and `blue.DBRowDescriptor`. `config.GetMultiLocationsEx` supplies the owned ship's name. All handles are scoped to the authenticated connection.

### Static data provenance

The SQL seed contains extracted values from the staged Windows client build 3662168. Resource names refer to entries in its `resfileindex.txt`. The following SHA-256 digests cover the uncompressed source files.

| Source | Extracted values | SHA-256 |
| --- | --- | --- |
| `bin64/staticdata/mapObjects.db` | `npcStations` row 60003760, station type, owner, orbit, operation, system and coordinates | `2E04B4162CB4E80097A6A304DDD471FA1E8ED1E78B4E62F9310810877277467D` |
| `staticdata/starmapcache.pickle` | Jita 30000142 belongs to constellation 20000020 and region 10000002 | `726B2735F60BC9098CB76D8FA5147A42A146E87188F7896B39B905B33171B4A4` |
| `staticdata/types.fsdbinary` | Ibis 601, group 237, cargo capacity 125 m³, station type 52678, group 15 | `16D778CBD9ABCF9FEE988DF84DB7B21B00E7D7C578371849351AB2F41B2CBF0A` |
| `staticdata/groups.fsdbinary` | Ship category 6 and station category 3 | `9EFD21D6219802B4D980E475A186B0B3C2A3DC56BF6BBBA1EC3314F3295113CA` |
| `staticdata/typedogma.fsdbinary` | Ibis attribute 102, two turret slots | `39D13BB393A7A28B7C2365319A5CE359EB8B5C61D556B17F321E9ABCE4DAFD93` |

### Docked acceptance record

The staged client rendered the Ibis inside Jita IV – Moon 4 and opened its native Inventory window. After restarting the server and client, the active inventory entry read **Ibis (Ibis)**. The host reported ship `1000000000001` at station `60003760`. SQLite contained one ship owned by pilot `90000001` and passed its integrity and foreign-key checks. Ancillary services including bookmarks, contracts, and the separate public gateway are incomplete.

## First flight

Undocking the owned Ibis changes `locationid` from the station to Jita, clears both station fields, and sets `solarsystemid`. Docking reverses those changes. The pilot and ship IDs stay the same. In space the client binds its inventory broker to the system with group 5 and binds `beyonce` to the system ID.

Eden sends `DoDestinyUpdate` after the ballpark bind. Its `SetState` carries a full-state stream with the simulation tick, a rigid station and a free ship, plus the corresponding slim items. Jita's station radius is 100,000 m. The Ibis has a radius of 47 m, mass of 1,163,000 kg, agility of 4.5, and base speed of 295 m/s. These values come from the pinned types and typedogma resources listed above. [Destiny stream implementation](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Thunkers.cpp) · [Destiny motion integration](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ballpark.cpp)

A direction or speed command changes the local course. Stop commits the current position and sends the ship's position and zero velocity back to the client. When Dock is selected outside interaction range, the client first follows the station and requests docking on arrival. Eden checks the ship, station and surface distance before accepting the request.

SQLite world version 4 adds a flight row keyed by ship ID. The station on the ship identifies its home port and system. A flight row supplies the active space position. Eden checkpoints moving ships about once a second and removes the row on docking. Login starts the ship at rest at the last committed position.

### First-flight acceptance, November 2, 2026

Using staged Windows build 3662168, Eden Pilot undocked the Ibis and double-clicked open space. In one run the station range increased from 453 m to 1,699 m. After **Stop My Ship** and a restart of both processes, Overview still showed 1,699 m and the HUD showed 0.0 m/s. A separate Dock request from 7,394 m completed the approach and opened the hangar.

On the final build the HUD showed 224 m/s during flight and 0.0 m/s after **Stop My Ship**, with the station 2,254 m away. Selecting **Dock** returned the Ibis to the Caldari Navy Assembly Plant. After another server and client restart, the character card showed the Ibis in Jita. Selecting Eden Pilot opened the station with **Ibis (Ibis)** active in Inventory. The host loaded ship `1000000000001` at station `60003760`. SQLite passed integrity and foreign-key checks.

Automated checks passed separately. Restore and build succeeded without warnings, all 29 tests passed, and formatting verification passed. The tests cover movement with a controlled clock, restart in space and in station, approach, docking range, ship identity and the snapshot layout.

## Two pilots in Jita, November 2, 2026

World version 5 adds a second local account, Eden Wing, with its own pilot and Ibis. The signed ticket identifies the account. Both accounts can connect concurrently, and a second session for the same account is rejected.

Each client receives its own ship as the ballpark ego. The initial snapshot includes other connected ships in the same system. Once the observer has bound the ballpark, Eden sends `AddBalls` when another ship appears and `RemoveBall` when it docks or disconnects. Movement and stop commands use the observing client's simulation tick.

**Live observations on earlier revisions.** Both staged clients selected their own pilot and undocked in Jita. With Wing stopped, its Overview showed Eden Pilot moving from 1,687 m to 2,144 m. With Pilot stopped, its Overview showed Wing moving from 11 km to 12 km. That run exposed a speed discrepancy near the station hull. After correcting station contact and course updates, Wing observed Pilot at 294 m/s and later at rest 23 km away. Disconnecting Wing removed it from Pilot's Overview, but the displayed station range jumped. That prompted the switch to individual ship arrival and departure messages.

**Automated checks on the committed revision.** Restore and build passed with zero warnings or errors, all 34 tests passed, and formatting verification passed. The flight tests cover peer arrival, course changes in both directions, departure, and stopping at the station surface.

**Maintainer live verification on `64360ce`.** The maintainer confirmed that each pilot saw the other Ibis move and stop in Overview. When one pilot disconnected, only that ship disappeared. The stationary observer's range to Jita Trade Hub stayed steady.

## Character logoff to selection, November 4, 2026

The user reported an **Invalid Packet Destination** dialog for `userSvc.UserLogOffCharacter` when choosing Log Off. In build 3662168's `eve/client/script/parklife/gameui.py`, `GameUI.LogOffCharacter` passes the no-argument remote method to `sessionMgr.PerformSessionChange('logoff', ...)`. A truthy result invokes `DoCharacterLogoff`, which chains `ProcessSessionReset`, closes character windows, broadcasts `OnSessionReset` and activates `charsel` on the existing connection. Python None would deny the transition. The inspected `code.ccp` SHA-256 is `37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.

`CharacterLogoffHandler` now owns this account-phase call. For a selected pilot it checkpoints motion, cancels connection-owned module repeats and NPC combat, removes shared target locks, resets industry monitoring and deferred inventory primes, and disposes the flight using the existing disconnect save/removal behavior. It clears pilot/location bindings, queued old-pilot notifications, the active pilot and the session's next skill deadline. Account bindings, authentication and the monotonically allocated binding IDs remain. The roster is reloaded from persistence so reselection sees the saved ship/location. Saved training is neither paused nor deleted and continues by UTC deadlines.

Before replying True, the session sends the existing Macho session-change envelope with the previous character/ship/location fields changing to None. Account ID and roles remain intact. Selected-pilot calls are gated until a new owned pilot is selected; old bound references remain invalid even after reselection. A logoff request without a selected pilot returns False, and malformed arguments are rejected without deselecting the pilot. This uses the existing Blue/Macho delta encoding, rather than changing the transport or serializer.

**Automated validation.** Tool restore, solution restore, Release build (zero warnings/errors), 175 focused .NET tests (29 seconds), both Python policy suites (3 catalog and 9 formatting cases), and `Format.ps1 -Check` passed in 41.65 seconds overall. Focused simulated-session tests cover docked/in-space logoff, session-field clearing before the True reply, repeated logoff/reselection and switching to another owned pilot on the same connection, malformed requests, stale binding rejection, observer ship removal, target-lock cleanup, pending notification cleanup, saved positions, continued training and stopping an active afterburner's repeats across reselection. Development checks initially caught incomplete test setup (targeting attributes, a dock-only inventory request and a baseline-versus-active-speed assertion); the corrected cases pass. Release output avoids the running Debug server's locked files; that process remains on its existing binaries. No full .NET regression run was requested or performed. The final filter was:

```powershell
dotnet test --no-build -c Release --filter 'FullyQualifiedName~Eden.Protocol.Tests.PilotSessions|FullyQualifiedName~TargetingTests|FullyQualifiedName~PropulsionTests|FullyQualifiedName~TackleTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~IndustryTests|FullyQualifiedName~PilotTrainingTests|FullyQualifiedName~FlightSessionTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting' -- xUnit.MaxParallelThreads=4
```

**Live acceptance.** Not performed; agent client testing is disabled. The user's screenshot establishes the original error, not acceptance of this implementation. Native session reset, rendering and return to character selection remain unverified.

**Optional maintainer checks.** Rebuild/restart the server; choose Log Off while docked and select another owned character; repeat from space and confirm the old ship disappears for an observer and the next selection has no old module/target state.

## Client-requested exit logging, November 4, 2026

The user reported an **Invalid Packet Destination** dialog while exiting the client: `Unsupported local call eventLog.LogPlayerRequestedDisconnect`. Build 3662168's `eve/client/script/sys/clientStatsSvc.py` confirms that `ClientStatsSvc.OnProcessExit` calls `sm.ProxySvc('eventLog').LogPlayerRequestedDisconnect()` with no arguments whenever MachoNet is connected, regardless of whether a character has been selected. The preceding `LogClientStats(session.sid, session.locationid, [finalPackage], isShutdown=True)` is only sent with a selected character and was already acknowledged. The inspected script archive has SHA-256 `37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`. This is a client service compatibility contract; no Carbon transport or serialization change is needed.

The account-phase compatibility handler now acknowledges the exact no-argument disconnect-log call with Python None, before and after selection. Retries also succeed. This acknowledges exit intent without prematurely closing the connection; ordinary transport teardown continues to own session cleanup. No gameplay or world state is changed. Malformed calls and other event-log methods retain their unsupported-call response.

**Automated validation.** Tool restore, solution restore, Release build (zero warnings/errors), 32 focused .NET tests (4 seconds), both Python policy suites (3 catalog and 9 formatting cases), and `Format.ps1 -Check` passed in 17.22 seconds overall. Simulated-session regression tests assert successful None replies before/after selection, repeated exit reports, a successful subsequent time query, unchanged saved pilot state, and rejection of extra arguments/unknown methods. The filter was `FullyQualifiedName~PilotServicesTests|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~TypedCallResultTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting`, run with `dotnet test --no-build -c Release` and `-- xUnit.MaxParallelThreads=4`. Release output avoided the running Debug server's locked files; that process remains on its existing binaries. No full .NET regression run was requested or performed.

**Live acceptance.** Not performed; agent client testing is disabled. The user's screenshot establishes the original failure, not acceptance of the fix.

**Optional maintainer checks.** Rebuild/restart the server, then exit the client from character selection and from a selected pilot to check that the reported dialog no longer appears.
