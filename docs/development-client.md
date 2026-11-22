# Windows client setup

[Documentation index](README.md) / [Launcher hosting](launcher.md)

Prepare the pinned client, create local data and connect to Eden. Commands run from the repository root.

## Setup and operations

- [Agent testing policy](#agent-testing-policy)
- [Stage the client](#stage-the-client)
- [Creating and recreating the databases](#creating-and-recreating-the-databases)
- [Moving an existing server](#moving-an-existing-server)
- [Run the startup test](#run-the-startup-test)
- [Local account login](#local-account-login)
- [Developer travel](#developer-travel)
- [Developer Type Browser](#developer-type-browser)
- [Developer Skill Tool](#developer-skill-tool)
- [Insider QA tab](#insider-qa-tab)
- [Issuing account redeem rewards](#issuing-account-redeem-rewards)
- [Compatibility and historical fixtures](#compatibility-and-historical-fixtures)
- [Creating an economy world without testing supply](#creating-an-economy-world-without-testing-supply)
- [Optional maintainer client checks](#optional-gameplay-checks)

## Agent testing policy

Follow the [contributor testing policy](../AGENTS.md#testing) and [automated validation guide](testing.md).

## Stage the client

Use a shared EVE installation containing the sibling directories `tq` and `ResFiles`. Choose a separate, empty destination with room for both directories. The example paths below can be changed.

```powershell
.\tools\DevelopmentClient.ps1 -PrepareFrom 'E:\Eve' -DevelopmentRoot 'E:\EdenDev\3662168'
```

Preparation copies the client files and resources. It checks SHA-256 hashes for the executable, Blue library, script archive, and startup configuration. Repeat the command to resume an interrupted copy or refresh the staged files. The script marks the client ready after both copies and the final hash checks succeed.

## Creating and recreating the databases

The server administration **Optional data** page can import [live market orders](world-configuration.md#market-presets)
for the Standard preset and [zKill-backed traffic](capsuleer-population.md#zkill-backed-travelers) for simulated pilots.
Other market presets and generated traffic work without these downloads. Eden adopts new snapshots on restart.

From the repository root, run `.\tools\Setup-Catalog.ps1 -DevelopmentRoot 'E:\EdenDev\3662168'` after staging the client. Use 64-bit Windows Python 3.11 or later with the standard library, PowerShell 7, a stable .NET 10 SDK, and at least 8 GB free for the catalog, temporary records, and an existing installation. The first run downloads the [CCP build 3662168 export](https://developers.eveonline.com/static-data/tranquility/eve-online-static-data-3662168-jsonl.zip). To rebuild offline, reuse the cached export under `%LOCALAPPDATA%\Eden\catalog-input` or pass `-Archive` with a local copy.

Stop the server before rebuilding. Setup prints source counts and verifies typed reads before printing `Activated` and the installed path. By default, setup writes to `src/Eden.Server/data/catalog.sqlite`. Use `-Output` to write a catalog for another server. An interrupted import or checksum error leaves the previous catalog available. Restore the matching input files and rerun the same command.

### Exporting station and structure geometry

The server includes baked build-3662168 collision profiles; ordinary setup needs no mesh converter or client at runtime.
To regenerate all station, deployable, starbase, sovereignty, orbital and Upwell structure profiles from a prepared client:

```powershell
.\tools\Export-StaticCollisionGeometry.ps1 -DevelopmentRoot 'E:\EdenDev\3662168' -Preflight
.\tools\Export-StaticCollisionGeometry.ps1 -DevelopmentRoot 'E:\EdenDev\3662168'
```

Use `-Catalog` for a catalog outside the default server data directory and `-Resources` for a separate shared `ResFiles`
cache. The command opens the catalog read-only and verifies the pinned resource index and each input's checksum.
It bootstraps the pinned external [Knit converter](https://github.com/neptuwunium/Knit) into ignored `artifacts/tools`,
builds it against the pinned SDK, and converts and bakes unique meshes with two workers. `-Workers` changes that limit;
`-Converter` reuses an existing external `Knit.Convert.dll`. First-time bootstrap needs Git and network access.
It never launches the client or changes a saved world. Allow several minutes and additional disk space for mesh inputs
and intermediate glTF files. Unchanged resource hashes reuse completed profiles; `-Refresh` forces a new bake.

The generated payload is `src/Eden.Data/Catalog/Flight/static-collision-3662168.json.gz`; its coverage, missing types,
source hashes and primitive counts are in [the provenance manifest](protocol/static-collision-3662168.json).
Build the server after regeneration to embed the new payload. To rebuild the catalog and geometry together, add
`-CollisionGeometry` to `Setup-Catalog.ps1`; its `-GeometryResources`, `-GeometryConverter` and `-GeometryWorkers`
parameters forward the corresponding exporter options. Catalog rebuilding retains the server-stop procedure above;
geometry export alone reads existing files and does not require stopping a server.

The default export covers 384 model-backed types using 117 meshes (798 KiB compressed, 7.14 MiB raw JSON).
Fifteen catalog entries have no graphic, including office containers, two obsolete stations and unfinished structure
definitions; their exclusions are explicit in the manifest. Missing or modified graphics retain the sphere fallback.
These conservative mesh-derived shapes approximate the visible hull, rather than reproducing private CCP collision data.
The [protocol record](protocol/structures.md#station-and-structure-hull-geometry--november-16-2026) describes runtime use
and remaining limitations. The developer-only `-Scope all-static` also exports fixed collidable scenery and gates;
those extra types are outside this station/structure runtime integration. Moving ships keep native spherical collisions.

### Starting with saved data

Start the server with `dotnet run --no-build --project src/Eden.Server`. The server uses `src/Eden.Server/data/world.sqlite` from source or `data/world.sqlite` beside the published executable. Set `EDEN_DATA_DIR` to choose a different data directory for the server and catalog setup. Relative values resolve from the server folder. An explicit `--world` path takes precedence and uses the catalog beside that save.

If startup reports a missing `catalog.sqlite`, copy your matching catalog to the path in the error or run `Setup-Catalog.ps1`
with `-Output` set to that path. If you double-click `Eden.Server.exe` on Windows, its console displays the error until you
press a key. Set `EDEN_NO_PAUSE_ON_ERROR=1` for automation that starts Eden in a dedicated Windows console.

New worlds seed Eden Pilot and Eden Wing.
Existing saves migrate on startup. Eden loads the world into memory and checkpoints committed changes every 15 seconds
by default. Graceful shutdown flushes remaining changes. Account credential and session changes can request a durable save.
Set `EDEN_VERBOSE_LOGGING=1` for routine checkpoint diagnostics.

Combat, flight and navigation run in the resident world. Checkpoints include committed gameplay and motion, and
monitoring and scene views rebuild when the save reopens. [Resident world and background saves](architecture.md#resident-world-and-background-saves)

The file is the last completed checkpoint, and `world.sqlite.previous` holds its predecessor. A forced process exit can lose progress since the last completed checkpoint; slow or failing saves can extend that rollback window. Save failures retain resident changes and retry on later intervals. A held `.owner` file lock rejects another resident host for the same path; the file itself can remain after shutdown. Stop the host and copy the save to back up progress. To start fresh, move the stopped host's save to a backup location and start again. To restore progress, copy a backed-up checkpoint to the configured save path while the host is stopped. Do not edit the saved file while the resident host is running. See [resident-world behavior and automated evidence](protocol/performance.md#resident-world-checkpoints-and-ten-brutix-replay-november-5-2026).

World-opening maintenance commands, including `--grant-redeem` and `--set-account-password`, also require a stopped host. The development script's `--prepare-login` helper only signs a local ticket and prepares the staged manifest; it opens no world and can still run while the host is active.

To recheck an installed catalog independently, run the following command.

```powershell
$catalog = Join-Path $PWD 'src/Eden.Server/data/catalog.sqlite'
dotnet run --no-build --project src/Eden.Server -- --verify-catalog $catalog
```

## Moving an existing server

Stop the old server and copy its data directory to the new host. Keep the world filename and the files beside it together:

- `world.sqlite` and `catalog.sqlite`. Keep `world.sqlite.previous` if available. Include any SQLite `-wal` or `-journal` files alongside their matching databases.
- `chat-tls.pfx` and `traffic.sqlite`, if present.
- `market-orders/`, including its manifest and referenced generation, when using the [Standard market preset](world-configuration.md#market-presets).
- Any `world.sqlite.mods*`, `world.sqlite.skill-packages`, `world.sqlite.skills.sqlite`, and `world.sqlite.skills-active` files or directories. Include any SQLite journals for these databases too.
- `world.sqlite.skills-previous`, if present, to retain the legacy skill package's rollback generation.
- `world.sqlite.administrators.json` for an existing administrator. Windows administration cookies are scoped to the save path and their encryption keys are tied to Windows. Sign in again at the new path.

Preserve the old directory as a backup. Copy any separate hosting configuration and certificate to the paths used by
the new host. Startup logs the full path of the save it opens.

## Run the startup test

For this optional native startup check, enable verbose logging and start the built host in one terminal. Launch the staged game from another. See the [README build order](../README.md#build-and-run) if it has not been built.

```powershell
$env:EDEN_VERBOSE_LOGGING = '1'
dotnet run --no-build --project src/Eden.Server
```

```powershell
.\tools\DevelopmentClient.ps1 -DevelopmentRoot 'E:\EdenDev\3662168'
```

The script selects the loopback game server, staged resource cache, and a client settings profile named Eden. The startup check runs automatically without entering credentials. Look for these lines in the server terminal.

```text
Sent server greeting.
Client confirmed build 3662168 and MachoNet 496.
Answered queue check with position zero.
```

Close the game and launch it again to repeat. Ctrl+C stops the server. Remove `Env:EDEN_VERBOSE_LOGGING` to return to quiet logging. For unattended checks the script accepts `-Hidden` and returns the launched process, which the caller can stop after reading the server output.

The version confirmation shows the real client parsed the greeting. The final line shows it requested a queue position. The loopback setting applies to the game endpoint. The client also opened external connections on ports 80 and 443 during testing.

If hashes differ, identify the client build before changing the profile. If Eden cannot bind port 26000, check which process owns the listener.

## Local account login

The legacy script login requires starting the server with explicit `--development-login`. The script passes the same flag to provisioning. Normal launcher accounts do not require this development option.

On Windows with PowerShell 7, build the solution, start Eden, then add `-Login` to the client launch command. The launcher signs a short-lived local account ticket and switches the staged manifest to the current Windows user's Eden key. This preserves the stock file entries, executable, script archive, encrypted transport and challenge verification. The client process receives a loopback address for its separate public gateway. The private key stays in the Windows key store.

To restore the stock manifest, copy `tq/manifest.dat` from the pinned installation into the staged copy while the client is closed. Successful authentication prints `Authenticated local pilot account; session established.` in the server terminal.

To enter character selection from the repository root, keep the server running in the first terminal and use the following command in the second.

```powershell
.\tools\DevelopmentClient.ps1 -DevelopmentRoot 'E:\EdenDev\3662168' -Login
```

The pilot card shows **Eden Pilot**, **Ibis**, and **Jita**. Click it to enter the Caldari Navy Assembly Plant. Open Inventory from the left sidebar and check the active ship. The server reports the selected pilot, station and ship item ID. For a restart check, close the client, stop and restart Eden, and log in again. The active ship and the logged item ID should match the first run. Each local account can have one authenticated session at a time.

After the source installation finishes downloading, run the preparation command again with the staged client closed. The ready marker checks the pinned binaries and successful copy. The main resource index can still have missing assets if the source was mid-download.

## Developer travel

On a developer account, right-click a solar system in the map or a known NPC station/static celestial, then choose **GM / WM Extras → TR me here!**. Solar-system and celestial targets move your active ship into space with collision clearance; NPC station targets dock it directly. Cross-system space targets use an approximately three-second native jump transition for the camera/input handoff. A docked departure automatically undocks in the source system and waits for that ballpark to load before starting the jump. Wait for that transfer to finish before requesting another move. Same-system moves and station destinations are immediate. The active hull, fittings, cargo, wallet and saved medical home are retained.

The native menu submits `/tr me <destination ID>` to the server. Other subjects, coordinate syntax, flags, player targets, player structures and unimplemented developer commands remain unsupported. Type Browser inventory creation is described below. Ordinary player accounts cannot use this route. No account-role changes are made by TR. See the [protocol notes](protocol/navigation.md#developer-translocation) for supported targets, automated coverage and the maintainer-reported camera/menu issue. Native recovery of those controls remains unverified after the revised handoff.

## Developer Type Browser

Skill injectors, containers and additional physical categories are supported. Fixed **Skill Points** rewards, ISK and EverMarks
tokens go to **Redeem Items** for your account. Server restart automatically recovers earlier misplaced personal reward stacks in
station hangars and owned-ship cargo into that queue. Ordinary skill injectors remain inventory items with the normal injection action.
Native batch redemption now accepts the client's token pairs and avoids the earlier duplicate-queue-entry connection failure.

With a pilot selected on a developer account, open Insider's **Type Browser**, find a type and use its context menu. **WM: /create this type** creates a packaged stack in your current station Item Hangar while docked. **GM: /load me this type** creates it in your active ship's cargo, including in space, subject to normal cargo capacity. Quantity defaults to one for direct slash commands. The category/group actions create or load each listed type through separate calls.

The accepted commands are `/createitem <typeID> [quantity]`, `/create <typeID> [quantity]` and `/load me <typeID> [quantity]`. Creation requires WORLDMOD; cargo loading requires GML or GMH. The normal developer role includes all three. Ordinary player accounts cannot create items. Unknown or noninventory types and invalid quantities reject without changing inventory. Wait for any TR handoff to finish first.

Ship types arrive packaged and retain Eden's existing assembly and boarding restrictions. Blueprint types create originals. A browsable or creatable type does not establish gameplay support. Separate in-space `/spawn`, `/spawnN`, `/entity deploy`, fitting and unload actions remain unsupported. Successful creations survive saves and restarts. See [implementation and automated coverage](protocol/navigation.md#native-type-browser-item-creation-november-8-2026); native rendering/input acceptance has not been performed.

## Developer Skill Tool

Open **Insider → Tools → Skill Tool**, build or load a profile, then choose **Give skills in the current profile to current character**.
The native tool also accepts another saved character. Developer/GMH accounts can grant published skills from level zero through five;
zero learns a missing skill. Existing progress is never reduced and unallocated SP is retained. Satisfied queued levels are removed,
remaining work is replanned and the normal skills, brain and queue displays are notified. Slash Console syntax is
`/giveskill me <skillTypeID> <level>` or `/giveskill <pilotID> <skillTypeID> <level>`. Command discovery now supplies the native
role-filtered list, correcting the **Unable to acquire server slash command list** popup. Restart the server and fully reopen the
client after updating so its cached command list is refreshed. See the
[implementation record](protocol/navigation.md#insider-tools-and-reward-destinations-november-9-2026). Native presentation remains unverified.

## Insider QA tab

Select a pilot on a developer account and open **Insider → QA**. The client builds the menu using two status reads from the server.
**Asset Holding** starts with both switches off. The switches record entitlement QA settings for that connection.
Eden's inventory validation and item creation continue to use their normal rules.

**Jump Clones → Reset last clone jump time** clears the selected pilot's cooldown immediately and refreshes the client's clone cache.
The reset survives a world checkpoint. Other native menu entries may require additional services, including entitlement processing,
HyperNet and test-token generation. See the [Insider QA protocol record](protocol/navigation.md#insider-qa-tab-november-21-2026).

## Issuing account redeem rewards

Create a local JSON grant file for an existing account. For example, save the following as `reward.json` outside the repository:

```json
{
  "AccountId": 1000001,
  "Reference": "testing-tritanium-2026-11-04",
  "TypeId": 34,
  "Quantity": 100,
  "Label": "Testing supplies",
  "Description": "Tritanium for your station hangar."
}
```

Issue it against the intended world database:

```powershell
dotnet run --no-build --project src/Eden.Server -- --grant-redeem 'C:\Eden\reward.json'
```

Pass `--world 'C:\Eden\world.sqlite'` to target a nondefault save. This local administrator command writes the token and exits; it does not start server listeners. Connected clients that have fetched the redeem queue receive a refresh on the next one-second poll, including before pilot selection. A fresh client reads the persisted queue. There is no client RPC for issuing grants.

Keep each account/reference pair unique for a distinct reward. Repeating exactly the same grant returns the original token, even after it was claimed, trashed or expired; changing its payload under the same reference rejects. References are case sensitive, nonblank, at most 128 characters, and may not begin with `#` (reserved for reversal provenance). Labels allow 255 characters and descriptions 4096; no field accepts NUL. Quantities are positive 32-bit integers. Optional `StationId` pins delivery to an existing NPC station. Optional `ExpiresAt` is a future UTC FILETIME integer; omit it for no expiry. These field names match the JSON example's casing.

The native redeem window can deliver ordinary inventory to a docked owned pilot or its saved home station, including before selecting the pilot. Fixed-station tokens retain their station. Claims return individual unavailable/expired/type-mismatch errors while eligible tokens succeed in the same committed transaction. Trash requires a valid owned batch. Batches permit one to 100 distinct tokens. A claimed stack may be reverse redeemed only while intact, packaged and in the original pilot's current station hangar, before any token expiry; splitting, consuming, assembling or transferring it removes eligibility. Reversal creates a new pending token and removes the old item atomically. Claim/trash/reversal records remain in the grant ledger, so retries cannot duplicate inventory.

Supported rewards are catalog types delivered as inventory, including minerals, ships, modules, charges and skillbooks. The command also supports the pinned client's 59 fixed SP rewards. For example, `TypeId: 54652` with `Quantity: 1` credits **1,000 unallocated SP**, and type **43680** credits **250,000 SP** per token. Use the same manifest/command with a fresh reference. SP rewards have no fixed `StationId`, can be redeemed in space, and create no inventory or reverse-redeemable stack. Currency rewards follow the same delivery rules: `TypeId: 52996` credits **one ISK per quantity unit**, and `TypeId: 73277` credits **one EverMark per unit**. For example, quantity 1000 grants 1,000 ISK or 1,000 EverMarks respectively. ISK credits appear in the existing wallet journal under **Redeemed ISK token**; EverMarks appear through the personal LP-wallet API for Paragon. The command uses the same account/reference replay protection for every reward kind. Native queue/skill-sheet operations can spend the saved unallocated SP. Blueprint rewards, other special-edition/subscription rewards, SKIN licenses and personalization rewards still require separate mechanics. The command does not activate ordinary market skill injectors or boosters, alter subscriptions or open reward crates. Existing gameplay restrictions still govern use or assembly of delivered items. Native rendering and input remain unverified; see the [redemption milestone](protocol/characters.md#persistent-account-redemption), [unallocated SP notes](protocol/characters.md#persistent-unallocated-skill-points) and [currency/LP-wallet notes](protocol/industry.md#currency-redemption-and-loyalty-wallets). LP-store offers, Paragon missions and corporation donation/spending are still pending.

## Compatibility and historical fixtures

Native chat requires a rebuilt server listening with TLS on TCP **5222** and the companion rebuilt Eden Launcher. The launcher adds the server’s public chat certificate to the prepared client copy’s certifi bundle; the normal EVE installation and operating-system trust store are untouched. The local `--prepare-login` development path installs that trust too. No client re-copy is needed. For remote hosts, allow TCP 5222 alongside the existing game/API/gateway ports. Chat rooms/messages are transient across host restart. See [chat implementation and automated multi-pilot checks](protocol/social.md#native-chat-and-multi-pilot-local-november-5-2026).

## Creating an economy world without testing supply

Put the verified build-3662168 `catalog.sqlite` beside the new world path and start Eden normally with that path.

```powershell
dotnet run --no-build --project src/Eden.Server -- --world 'E:\EdenWorlds\Economy\world.sqlite'
```

Follow the console to [World setup](world-configuration.md#first-start). The default Progressive market seeds regional NPC
and starter stock. Turn off `settings.developmentSupply.testingGrants` in the advanced draft to disable the testing wallet grant.
You can adjust `settings.developmentSupply.institutionalStock`
(default 1,000 replenishing units per order), service costs, prices, manufacturing indices and belt renewal in the same draft.

Institutional NPC offers stock catalog blueprints and skillbooks at pinned base prices. Ships, modules and minerals
also depend on the market policy selected in the draft. See [market presets](world-configuration.md#market-presets)
and the [starter production audit](starter-production.json).


## Optional gameplay checks

See the [maintainer checklists](live-checks.md) for recorded client procedures and fixtures.

- <a id="check-character-portraits"></a>[Character portraits](live-checks.md#check-character-portraits)
- <a id="optional-player-electronic-warfare-checks"></a>[Electronic warfare](live-checks.md#optional-player-electronic-warfare-checks)
- <a id="optional-remote-shield-and-armor-repair-checks"></a>[Remote shield and armor repairs](live-checks.md#optional-remote-shield-and-armor-repair-checks)
