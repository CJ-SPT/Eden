# Windows client setup

Combat and standing history use world schema **71** after rebuilding and restarting the host. The migration recovers combat records supported by existing ship-loss manifests and NPC bounty awards. New turret/missile events save detailed reports; eligible NPC faction losses now save both the standing value and dated transaction. Old standing changes were not recorded and are not invented. Optional maintainer checks are to open Show Kills and Show Losses plus a report, inspect a faction history after an eligible NPC kill, and reconnect to verify persistence. Native presentation is unverified; see the [history implementation and validation](protocol.md#character-interaction-history-november-5-2026). Agents must not perform these interactive checks without an explicit request.

After rebuilding and restarting the server, an optional visibility check is to use a capsule and **TR me here!** between two occupied belts in the same system. In the inspected save, Vellaine belts **40088018** and **40088019** each held two pirates. Check that the destination pirates appear in the overview and in space. If a contact is still absent, capture the matching client exception and server `EDEN_TRACE_MOTION=1` output. The `MotionNpc` lines identify the NPCs sent to that viewer. See [NPC visibility after same-system GM translocation](protocol.md#npc-visibility-after-same-system-gm-translocation).

Current NPC baseline: ordinary known-space belts now use persistent regional pirate groups, and the four main empires have locally defensive gate/station guards. Safest (displayed 0.9-1.0) systems no longer contain the old Perimeter Rookies. Historical Rookie procedures below describe prior acceptance fixtures; they are not current population guarantees. See [regional populations](protocol.md#regional-belt-pirate-populations) and [empire guards](protocol.md#local-empire-guard-populations) for policy and automated evidence. No new native acceptance was performed. Optional maintainer checks are to inspect representative belts, observe local guard retaliation, and inspect missile/patrol presentation plus restart continuity. Agents must not launch the client unless explicitly requested.

## Agent testing policy

Agents perform **automated testing only**, following the [contributor testing policy](../AGENTS.md#testing) and [README build order](../README.md#build-and-run). Use focused tests during development and one full automated validation after a code milestone is stable. Documentation-only changes require relevant documentation checks rather than the gameplay suite. Do not repeat a successful full suite for unchanged code or publishing; after small corrections rerun affected tests and required build/format checks, expanding to the full suite only when broad coverage is invalidated.

Do not launch the game, use Computer Use, or conduct interactive gameplay acceptance unless the user explicitly requests it. This applies to hidden or unattended client launches as well. Do not substitute an extended manual server-testing session. The setup, launch, restart, and gameplay procedures below are optional maintainer checks or instructions for explicitly requested live testing; agents must not run them as routine milestone acceptance.

Report automated checks, results, elapsed time, and limitations separately from **Live acceptance: "Not performed; agent client testing is disabled."** Supply at most three short optional maintainer actions for behavior automated tests cannot establish. If live testing is explicitly requested, record only the actions exercised and visible results, and identify any blocker and remaining unverified behavior.

Automated validation and confirmation that the milestone commit is on the remote branch establish milestone completion. Native rendering, input, and gameplay feel remain unverified until tested. Existing client observations in this guide and the [protocol notes](protocol.md) remain historical evidence, not acceptance of subsequent changes.

## Stage the client

Use a shared EVE installation containing the sibling directories `tq` and `ResFiles`. Choose a separate, empty destination with room for both directories. The example paths below can be changed.

```powershell
.\tools\DevelopmentClient.ps1 -PrepareFrom 'E:\Eve' -DevelopmentRoot 'E:\EdenDev\3662168'
```

Preparation copies the client files and resources. It checks SHA-256 hashes for the executable, Blue library, script archive, and startup configuration. Repeat the command to resume an interrupted copy or refresh the staged files. The script marks the client ready after both copies and the final hash checks succeed.

## Developer travel

On a developer account, right-click a solar system in the map or a known NPC station/static celestial, then choose **GM / WM Extras → TR me here!**. Solar-system and celestial targets move your active ship into space with collision clearance; NPC station targets dock it directly. Cross-system space targets use an approximately three-second native jump transition for the camera/input handoff. A docked departure automatically undocks in the source system and waits for that ballpark to load before starting the jump. Wait for that transfer to finish before requesting another move. Same-system moves and station destinations are immediate. The active hull, fittings, cargo, wallet and saved medical home are retained.

The native menu submits `/tr me <destination ID>` to the server. Other subjects, coordinate syntax, flags, player targets, player structures and other developer commands remain unsupported. Ordinary player accounts cannot use this route. No account-role changes are made by TR. See the [protocol notes](protocol.md#developer-translocation) for supported targets, automated coverage and the maintainer-reported camera/menu issue. Native recovery of those controls remains unverified after the revised handoff.

## Creating and recreating the databases

From the repository root, run `.\tools\Setup-Catalog.ps1 -DevelopmentRoot 'E:\EdenDev\3662168'` after staging the client. Use 64-bit Windows Python 3.11 or later with the standard library, PowerShell 7, the pinned .NET SDK, and at least 8 GB free for the catalog, temporary records, and an existing installation. The first run downloads the [CCP build 3662168 export](https://developers.eveonline.com/static-data/tranquility/eve-online-static-data-3662168-jsonl.zip). To rebuild offline, reuse the cached export under `%LOCALAPPDATA%\Eden\catalog-input` or pass `-Archive` with a local copy.

Stop the server before rebuilding. Setup prints source counts and verifies typed reads before printing `Activated` and the installed path. An interrupted import or checksum error leaves the previous catalog available. Restore the matching input files and rerun the same command.

Start the server with `dotnet run --no-build --project src/Eden.Server`. The first startup creates `%LOCALAPPDATA%\Eden\world.sqlite` with Eden Pilot and Eden Wing. An existing save at versions 1 through 37 migrates to version 38 in a transaction, preserving existing ships and cargo while adding assembled-ship storage. To back up progress, stop the host and copy the save. To start fresh, move the stopped host's save to a backup location and start the host again. To restore progress, restore the backed-up save while the host is stopped.

To recheck an installed catalog independently, run the following command.

```powershell
dotnet run --no-build --project src/Eden.Server -- --verify-catalog "$env:LOCALAPPDATA\Eden\catalog.sqlite"
```

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

Supported rewards are catalog types delivered as inventory, including minerals, ships, modules, charges and skillbooks. The command also supports the pinned client's 59 fixed SP rewards. For example, `TypeId: 54652` with `Quantity: 1` credits **1,000 unallocated SP**, and type **43680** credits **250,000 SP** per token. Use the same manifest/command with a fresh reference. SP rewards have no fixed `StationId`, can be redeemed in space, and create no inventory or reverse-redeemable stack. Currency rewards follow the same delivery rules: `TypeId: 52996` credits **one ISK per quantity unit**, and `TypeId: 73277` credits **one EverMark per unit**. For example, quantity 1000 grants 1,000 ISK or 1,000 EverMarks respectively. ISK credits appear in the existing wallet journal under **Redeemed ISK token**; EverMarks appear through the personal LP-wallet API for Paragon. The command uses the same account/reference replay protection for every reward kind. Native queue/skill-sheet operations can spend the saved unallocated SP. Blueprint rewards, other special-edition/subscription rewards, SKIN licenses and personalization rewards still require separate mechanics. The command does not activate ordinary market skill injectors or boosters, alter subscriptions or open reward crates. Existing gameplay restrictions still govern use or assembly of delivered items. Native rendering and input remain unverified; see the [redemption milestone](protocol.md#persistent-account-redemption), [unallocated SP notes](protocol.md#persistent-unallocated-skill-points) and [currency/LP-wallet notes](protocol.md#currency-redemption-and-loyalty-wallets). LP-store offers, Paragon missions and corporation donation/spending are still pending.

## Run the startup test

Start the host in one terminal. Launch the staged game from another.

```powershell
dotnet run --project src/Eden.Server
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

Close the game and launch it again to repeat. Ctrl+C stops the server. For unattended checks the script accepts `-Hidden` and returns the launched process, which the caller can stop after reading the server output.

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

The pilot card shows **Eden Pilot**, **Ibis**, and **Jita**. Click it to enter the Caldari Navy Assembly Plant. Open Inventory from the left sidebar and check the active ship. The server reports the selected pilot, station and ship item ID. For a restart check, close the client, stop and restart Eden, and log in again. The active ship and the logged item ID should match the first run. Eden stores the world in `%LOCALAPPDATA%\Eden\world.sqlite`. Each local account can have one authenticated session at a time.

After the source installation finishes downloading, run the preparation command again with the staged client closed. The ready marker checks the pinned binaries and successful copy. The main resource index can still have missing assets if the source was mid-download.

## Create a pilot and check the first spawn

Launch with `-Login`, choose **Add Character**, and complete the origin, portrait and name steps. Enter the game and skip the tutorial if offered. Check the location panel in the upper left, then compare the active ship in **Inventory** and the home station in **Character Sheet → Character → Home Station**. A Science & Trade Institute pilot starts in Amsen with an Ibis. A Federal Navy Academy pilot starts in Duripant with a Velator. After restarting the client and server, select the same character card and compare the location and ship.

To test an existing save separately, stop the host, copy `world.sqlite` into a test directory beside a matching `catalog.sqlite`, and launch the host with `dotnet run --no-build --project src/Eden.Server -- --world "C:\path\to\test\world.sqlite"`. The usual client launch command connects to that host.

## Fly the Ibis

To check cargo before departure, select **Eden Pilot**, open **Inventory** and verify **Ibis (Ibis)** is active. Open **Item Hangar** and drag the **Civilian Miner** onto **Ibis (Ibis)** in the inventory tree. The item should disappear from the hangar and appear in cargo with **5.0/125.0 m³** used. Drag it back onto Item Hangar to unload, then load it again. Restart the host and client and verify the same single miner remains in cargo. The one-time starter grant is made for existing pilots when upgrading to world version 13 and for newly created pilots at their starting station. Carrying a miner does not fit or activate it. When moving a stack larger than the free cargo space, the client opens Set Quantity with the maximum that fits; accepting it leaves the remainder in the hangar.

1. Select **Eden Pilot** and click **Undock** in the station panel. Wait for the ship HUD and the **Jita Trade Hub** entry in Overview.
2. Double-click open space. Watch the speed readout and the distance to the station change.
3. Right-click the Ibis and choose **Stop My Ship**. Check that the HUD reads **0.0 m/s** and the station range holds steady.
4. To check space persistence, note the range, close the client, restart Eden, and launch the client with `-Login`. Select the pilot and compare the range.
5. Select the station in Overview and click **Dock** in Selected Item. From outside docking range, allow time for the approach. Once inside, open Inventory and check **Ibis (Ibis)**.
6. Restart Eden and the client again. Select **Eden Pilot** and verify the station label, ship and active inventory entry match the previous session.

## Fit a mining laser

While docked, select **Eden Pilot**, open **Inventory**, and verify **Ibis (Ibis)** is active. Open **Fitting** using the circled ship icon at the far right of the station panel's top service row, or press Alt+F.

1. Drag a **Civilian Miner** from Item Hangar or cargo onto an empty high slot in Fitting. One unit leaves its stack and appears in the slot. The client brings it online automatically when resources allow.
2. Right-click the module and choose **Put Offline**. The module dims and its CPU and power grid return to the ship. Choose **Put Online** and compare the resource totals. Eden Pilot's starting Ibis has 162 CPU and 27.6 power grid after its fitting skills; one online Civilian Miner leaves 97 CPU and 25.6 power grid.
3. Choose **Unfit**. The slot clears and the assembled miner appears in Item Hangar. Drag it back onto the empty slot.
4. Restart the host and client. Open Fitting and check that the same module and its online state return.

World version 14 adds assembled and online state to owned items without repeating the starter grant. Supported modules can be fitted, unfitted or rearranged while docked; saved fitting templates and in-space online changes remain separate work. Civilian Miner activation on corvettes is described below.

To rearrange a fitting, drag the fitted module onto an empty compatible slot, or onto another fitted module in the same rack to exchange their slots. Equipment stays in its catalog rack. Slot positions and turret hardpoints are checked independently. A loaded gun's icon drags its charges in this client; drag the other unloaded module onto that gun to swap them while retaining its ammunition. Loaded charges and paid cycle/reload deadlines follow the module identity, without creating ammunition or restarting a reload. Check both module tooltips after a swap and bring either module online through its menu if the client took it offline during the drag. Undock and verify that the new HUD order activates the intended module, then dock and restart to check the saved layout.

## Check hull passive modules

Use a copied acceptance save and the ordinary build 3662168 catalog. Supply the test modules explicitly; this fitting milestone does not add market offers or blueprints. Select Eden Pilot and verify the active Ibis in Inventory before opening Fitting.

1. Fit Expanded Cargohold I in a low slot. With the existing starter skills, cargo rises from 125 to 146.875 m³, maximum speed becomes about 288.4 m/s and structure falls to about 161 HP. Put it offline: cargo returns to 125 m³ while the fitted penalties remain. Unfit it to remove those penalties. Offlining or removing it with more than the resulting capacity aboard must fail without losing cargo.
2. Fit Co-Processor I and compare CPU output: the seeded pilot's Ibis changes from 162 to 174.96. Fit Small Shield Extender I: Shield Management II gives 660 HP total shield and Shield Upgrades II reduces its power need to 1.8. Check the navigation signature readout as well; the extender adds 2 m.
3. Fit 200mm Steel Plates I in the other low slot. Hull Upgrades II gives 632.5 HP armor, and the hull mass becomes 1,303,000 kg before any Armor Layering reduction. Native Fitting truncates some HP readouts. Check skills before fitting other sizes or modules.
4. Undock, fly and dock with the passive equipment. Restart both the server and client against the same copied save, then compare module identities, slots, online state, cargo, defenses and navigation. Output reductions may automatically offline the largest consumer of the most overloaded resource; an unaffordable explicit online request is rejected instead.

Rigs, drone equipment, in-space fitting/online changes and activated variants such as Assault Damage Controls are outside this milestone. Some supported stat families have no active combat counterpart yet; this does not enable additional weapons.

## Check passive equipment bonuses

The higher-tier expansion now covers 856 unrestricted static profiles across the existing 31 passive families. The earlier Tech I observations do not establish native acceptance of every new tier. Higher-tier native checks are pending while the maintainer's instruction to leave game windows alone is in effect. When testing resumes, select Eden Pilot and verify the active ship in Inventory, then use a copied save and the following comparisons within its CPU/power/slot budget:

- Compare Magnetic Field Stabilizer II, Federation Navy and Cormack's Modified variants on a supported hybrid turret. Their damage multipliers are 1.10, 1.12 and 1.145, and cycle multipliers are 0.895, 0.89 and 0.89. Mixed-tier duplicates share strongest-first diminishing returns regardless of fit order. Heat Sink II, Imperial Navy and Chelm's Modified Heat Sink must leave that hybrid gun unchanged.
- Compare Small Shield Extender II and Republic Fleet Small Shield Extender. They add 520 and 550 shield HP before hull skills, with +2 m and +1 m signature. Online/offline changes preserve the shield percentage and existing armor/hull damage.
- Compare Expanded Cargohold II to its offline state. Its online capacity multiplier is 1.275; its fitted speed and structure multipliers are 0.82 and 0.77 even while offline. Do not remove capacity from an overfilled hold. Check a deadspace resistance module with compensation skills, and an ordinary higher-tier Damage Control whose separate resistance multiplication stays exempt.

The complete cohort has automated eligibility coverage. Per-item Abyssal mutations, ship-restricted profiles, six variants with additional drone effects and the active Breach Control still require their separate implementation; buying them does not bypass fitting rejection.

Use the same copied acceptance save and explicitly supplied test modules. Verify Eden Pilot's active Ibis in Inventory. Keep enough CPU/power available and check each module's required skills; these tests do not grant skills or ISK.

1. Fit 75mm Gatling Rail I and note its damage multiplier and cycle in Attributes. Heat Sink I must leave both unchanged: it targets energy weapons. Replace one low-slot module with Magnetic Field Stabilizer I. Online increases the hybrid turret's damage by 7% and multiplies cycle time by 0.92; offline removes both bonuses. The pinned catalog unusually puts all civilian turrets, including Civilian Gatling Railgun, in Energy Weapon group 53; use the standard hybrid turret for this family-isolation check.
2. Fit Tracking Enhancer I and compare the turret's optimal, falloff and tracking. It does not increase a mining laser's range. Repeated penalized bonuses use the catalog's stacking rules: the second equal modifier contributes about 86.9% of its listed bonus, the third about 57.1%. Two Tracking Enhancer I modules multiply optimal range by 1.14007951, rather than 1.15025625 without the penalty. Reopen the gun's Attributes window after fitting changes; the stock information panel retains its displayed rows while open.
3. With the required skills trained, fit Mining Laser Upgrade I. Its CPU penalty applies while fitted, including offline; its 5% yield bonus requires online state. Mining Upgrades reduces the penalty. Check actual ore gains as well as tooltips when testing mining.
4. Fit Small Shield Booster I and Shield Boost Amplifier I. The amplifier's online 30% boost bonus affects restored shields. Capacitor Power Relay I improves capacitor recharge but applies its online 10% shield-boost penalty to that same booster. Offline state removes their online bonuses and penalties.
5. Restart host/client and compare the modules and bonuses again. Missile guidance and ballistic-control modifiers can be fitted and evaluated; missile firing remains outside the implemented weapon families.

## Check repeating HUD cycle timers

Fit an afterburner, undock and activate it. Observe the module's cycle circle across at least three complete cycles. An automatic repeat should continue the active state without an idle flash or extra partial-cycle reset. Capacitor is still charged for each server cycle. Click once to request shutdown; the paid cycle finishes and the circle stops. Reconnect must retain the paid deadline without automatically activating another cycle. Weapons, miners and shield boosters share the same continuous-HUD rule, with their existing manual-stop policies.

## Fly two pilots in Jita

Start one server. From the repository root, launch both clients against it.

```powershell
.\tools\DevelopmentClient.ps1 -DevelopmentRoot 'E:\EdenDev\3662168' -Login
.\tools\DevelopmentClient.ps1 -DevelopmentRoot 'E:\EdenDev\3662168' -Login -Pilot Wing
```

Select **Eden Pilot** in the first client and **Eden Wing** in the second. Each account has its own ship and settings profile. Windowed mode makes it easier to switch between them. Undock either ship that is still in the station.

1. In each client's **General** Overview, find the other pilot by name and confirm the type is **Ibis**.
2. Leave Wing stopped. Fly Pilot into open space, switch to Wing, and watch Pilot's velocity and range. Stop Pilot and check that Wing sees zero velocity.
3. Reverse the roles. Keep Pilot stationary while Wing flies, then stop Wing and compare the final range.
4. Close one client or dock its ship. The other pilot should remain in space and the departed ship should disappear from Overview.

## Lock and unlock another pilot

Start the two clients as above and undock both Ibises at the same station. On **Eden Pilot**, open Inventory and verify **Ibis (Ibis)** is active.

1. Right-click **Eden Wing** in Overview and choose **Lock Target**. A targeting countdown should run, then Wing's target portrait should appear with its name, ship and distance.
2. Switch to Wing. Pilot's Overview entry should have the yellow targeted indicator.
3. On Pilot, choose **Unlock Target** from Wing's menu. The target portrait should disappear and Wing's indicator should clear.
4. Lock Wing again, wait for completion, then dock Wing. Pilot should lose both Wing's Overview entry and target portrait while remaining in space.

Eden checks range and target slots on the server, including pending locks. Long Range Targeting and Signature Analysis modify the hull's range and scan resolution. Target Management and Advanced Target Management increase the pilot's limit, capped by the hull. Locks are temporary connection state and do not survive logout. Weapon activation remains separate work.

## Warp within Jita and verify braking

Start both clients in space. The **General** Overview shows stations and stargates. Use **All** to include planets and moons.

1. Select a distant object such as the Perimeter stargate or Jita IV. Use **Warp to Within** in Selected Item. Watch the Ibis turn and gain speed, then watch the tunnel and the range to the destination during **Warp Drive Active**.
2. Keep the second pilot at the departure point until the first ship disappears from Overview. Warp the second pilot to the same object at the same range. Check for the first Ibis nearby on arrival.
3. Fly into open space away from a station or planet. Once the Ibis has accelerated, use **Stop My Ship** (Ctrl+Space). Watch the speed fall and the range continue changing until the ship settles at **0.0 m/s**.
4. To return home, warp to **Jita IV - Moon 4 - Caldari Navy Assembly Plant** and select **Dock** inside docking range.

For the celestial landing check, warp to zero at **Jita V**, then at **Jita V – Moon 16**. Selected Item should settle near 30,342 km and 5,164 km respectively. Allow a few kilometres for the landing lane and braking. Have the second pilot arrive from another direction and check that both ships share the same area.

For tunnel entry, use a long leg such as the Jita Trade Hub to the Perimeter gate. Watch for **Establishing Warp Vector** while the Ibis turns, then **Warp Drive Active** and the tunnel as it accelerates. Leave the second pilot at the destination and watch the arriving Ibis in Overview.

## Jump between Jita and Perimeter

1. Select the **Perimeter** stargate in Jita's Overview and warp to zero. Click **Jump** in Selected Item once the Ibis is within 2,500 m of the gate.
2. Watch the **Jumping** message, gate camera and tunnel. On exit, check the **Perimeter** system label, the starfield, and the **Jita** gate about 12 km away in Overview.
3. Select the Jita gate and click **Jump**. Allow the Ibis to approach before it activates the gate. Verify that the rendered Jita scene and flight controls return after the tunnel.
4. To check persistence, restart the client and host while stopped in the destination system. The character card and Overview should show that system.

## Visit an asteroid field

Jita has no asteroid belts in the pinned catalog. Jump to Perimeter as above and select **Eden Pilot**. Open Inventory and verify **Ibis (Ibis)** is active.

1. Use the **Mining** overview tab to find an asteroid belt. Right-click its marker and choose **Warp to Within → 0 m**.
2. After arrival, check that rocks render around the Ibis and the Mining overview lists nearby Veldspar, Scordite and their grade variants. Right-click an asteroid within targeting range and choose **Lock Target**. Its portrait, ore name and distance should appear after the countdown.
3. Warp to a different belt. The previous target should clear, and the new field should appear without disconnecting the client. Use the Mining tab again to inspect its rocks.
4. Stop near the second belt, restart the host and client, then compare the field, ore types and distances. The rocks retain their saved IDs, positions and quantities.

World version 15 adds saved asteroid fields. The catalog supplies belt anchors and per-type counts; Eden generates the individual rocks on first use and saves them. Rocks within 250 km load into the local ballpark. Depleted rows remain saved so reopening a belt cannot replenish it.

## Mine and unload ore

Fit an online **Civilian Miner** to the Ibis while docked, then visit a belt as above. Select Eden Pilot and check **Ibis (Ibis)** in Inventory. Leave cargo room for the ore.

1. Lock a Veldspar or Scordite asteroid within the miner's **10 km** range. Activate the module on the ship HUD, or press **F1** when it occupies the first high slot. Watch the beam and active-module indicator.
2. After its **10-second** cycle, look for the native mining message and an ore stack appearing in cargo. Further cycles add to that stack. Mining and Astrogeology increase yield; critical successes add bonus ore without consuming the bonus from the deposit.
3. Press the module button again to stop manually. The elapsed part of the cycle returns whole ore units. Auto-repeat also stops when no unit fits in cargo, the deposit empties, capacitor is insufficient or the target is lost. Warp and logout cancel active mining; mining does not continue offline.
4. Warp to a station, dock, and drag the ore from Ibis cargo into **Item Hangar**. Compare the quantity and used cargo space before and after unloading. During automated input checks, select the stack and wait for its selection to appear before dragging.
5. Restart the host and client. Check that the ore remains in its saved cargo or hangar location. A depleted asteroid should remain absent when returning to its belt.

Mining results appear after collection, including a full cycle or the earned portion on manual stop. To inspect them, open **Neocom → Utilities → Log and Messages**. Its gear menu under **Combat Messages to Display** includes **Mining Messages**. Keep that enabled to see native mining feedback. The green ring belongs to the active module: in the verified Venture layout **F1** is Miner I and **F2** is the civilian railgun. A miner can continue collecting from its locked asteroid after a separate NPC target dies.

World version 16 saves capacitor charge and its recharge timestamp. Charge regenerates while connected or offline, using the pinned client's curve and the pilot's capacitor skills. Activation supports regular mining lasers on corvettes and the Venture, including Civilian Miner, Miner I and Miner II. Strip/deep-core miners, crystals, other hulls, boosts and survey scans remain separate work.

To upgrade, sell mined ore and buy **Miner I** through Regional Market. Search for `miner`, expand **Ship Equipment**, select Miner I and double-click the local station offer. Open Fitting with the **circled ship** button, unfit a civilian laser if both high slots are occupied, then use **Fit to Active Ship** on the purchase. Miner I requires Mining I and has a 10 km range, 15-second cycle, 10 m³ base yield and 5 GJ activation cost. Miner II requires Mining IV and has a 12 km range, 15-second cycle, 15 m³ base yield and 18 GJ cost. With Mining III and no other yield bonuses, a noncritical full Miner I cycle produces 115 Veldspar. Fitting tooltips currently show base yield; compare actual cargo gains to check the server's skill bonuses.

## Reprocess mined ore

1. Dock at a station with reprocessing, such as **Perimeter II – Moon 1 – Caldari Navy Assembly Plant**. Select **Eden Pilot**, open **Inventory**, and check that **Ibis (Ibis)** is the active ship.
2. Right-click an ore stack in Item Hangar or the active ship's cargo and choose **Reprocess**. Review the native window's predicted mineral quantities. Veldspar and Scordite use batches of 100 units; smaller stacks cannot be processed.
3. Choose Item Hangar or cargo as the output destination and press **Reprocess**. Complete batches become minerals; remaining ore stays in its original stack. Output must fit if sent to cargo. Reopen Inventory and check the mineral and leftover quantities.
4. Restart the host and client and check that the same stacks remain. A repeated request cannot process the incomplete remainder again.

The catalog supplies station efficiency, ore recipes, batch sizes and skill bonuses. This milestone supports fixed ore outputs and personal cargo/hangars. It does not recycle equipment or process randomized recipes. The fee display currently reads zero because Eden has no adjusted market-price feed; this follows the pinned client's missing-price behavior. Mineral losses reflect station efficiency and trained skills, rather than withholding minerals as a station tax.

## Sell minerals and buy equipment

1. Select **Eden Pilot**, open **Inventory**, and verify **Ibis (Ibis)**. At a market station, unload ore or minerals to Item Hangar, then right-click them and choose **Sell This Item**.
2. Select **Immediate**, review the quantity, price and tax, then complete the native sale confirmation. The sold quantity leaves Inventory and the personal ISK balance increases by the net proceeds.
3. Right-click a Civilian Miner and choose **Buy This Type**. Buy one at the station. It arrives in Item Hangar; the price is deducted from the same saved wallet. Fit it through the station's circled ship button.
4. Open Wallet to compare the balance and transaction records. Restart the host and client, then compare wallet balance, owned items and remaining NPC order quantities.

Eden's default testing market stocks every pinned client type with `published=1` and a positive `marketGroupID`: **19,727 types**, each sold for **1 ISK per unit**. Docked market access populates the current station. Each sell order displays **1,000,000 units**; supply is restored on access and after purchases, and expired sell orders renew. Larger requests retain partial fills up to that displayed quantity. Old sell-order identities and trade history are retained when a station upgrades to this policy.

Every existing pilot receives an additive **1,000,000 ISK** grant when upgrading to world version 28. New pilots receive it in their creation transaction. Wallet Journal shows **GM Cash Transfer**; reconnecting or restarting does not repeat it. This is explicit testing policy. Availability does not add gameplay support for otherwise unsupported items, hulls or blueprints. Unpublished types and types without a market group remain unavailable through this browser.

NPC buyback orders remain finite, with initial demand of one million units per type. They do not replenish or renew:

| Item | Pilot sells for | Pilot buys for |
| --- | ---: | ---: |
| Veldspar | 3.00 ISK | 1.00 ISK |
| Scordite | 5.00 ISK | 1.00 ISK |
| Tritanium | 2.00 ISK | 1.00 ISK |
| Pyerite | 8.00 ISK | 1.00 ISK |

These buyback prices intentionally preserve existing sale tests; this testing market does not model a balanced economy.

Sales tax is currently pinned to the staged client's observed **7.5%** runtime flag, reduced by **11% per Accounting level**. Its packaged fallback is 8%; external flag changes are not yet hosted by Eden. Immediate trades have no broker fee. A half-cent sales tax is rounded to cents before crediting proceeds; the native preview can display a net amount one cent higher because it rounds the unrounded net independently. Regional history comes from actual saved executions, grouped by UTC day with volume-weighted average prices. Open **Price History**, then **Show Table**, to compare quantities and prices. Before an item's first local trade, the client's fallback average can trigger large price warnings even for these documented prices. This history does not supply the separate adjusted prices used for reprocessing fees. Reopen market details or transaction panels to refresh their displayed rows. Trading supports personal immediate orders at the docked station. Player listings and corporation/remote trading remain separate work.

## Verify the full testing market

1. Select **Eden Pilot**, open **Inventory**, and verify the expected active ship. Open Wallet and confirm the one-time GM Cash Transfer and current balance.
2. Open Regional Market with station range selected. Browse or search for a ship, skillbook, blueprint and ammunition type. Confirm the current-station offer reads **1.00 ISK** and **1,000,000** units.
3. Buy representative items. Verify the exact quantities in Item Hangar and one ISK per unit deducted from Wallet. Reopen the quote and check that supply remains available. PLEX quotes use the same local testing stock; redemption/account services remain unsupported.
4. Restart the host and client. Confirm purchased stacks and balances persist and the grant appears once. Repeat a purchase with a pilot docked at a school station that provides market service.

## Upgrade to a Venture

1. Dock at a market station with sufficient ISK and Mining Frigate I trained. Select **Eden Pilot** and check the current ship in Inventory. Search Regional Market for `venture`, expand **Ships**, select Venture, and buy one from the current station's offer.
2. Open **Ship Hangar**, right-click the packaged Venture and choose **Assemble Ship**. Confirm a name or use **Later** for the default. Double-click the assembled ship to board it. Check that Inventory names the Venture as active and the previous ship remains in Ship Hangar.
3. Compare the Venture's **50 m³** cargo and **5,000 m³ Mining Hold**. Only asteroid ore can enter the mining hold. Open the previous ship's cargo to check its saved contents, then board it to check its saved fitting. Return to the Venture and fit an online mining laser from Item Hangar using **Fit to Active Ship**.
4. Undock, warp to a Perimeter belt, lock a nearby asteroid and activate the laser. Ore should accumulate in Mining Hold while ordinary cargo remains separate. With Mining III and Mining Frigate I, a noncritical full Miner I cycle yields 241 Veldspar; a civilian laser yields 144 per full cycle. The calculation floors whole ore units after applying yield bonuses.
5. Dock and drag ore from Mining Hold into Item Hangar to sell it, or reprocess directly from the hold with **Item Hangar** selected for output. The pinned client does not offer Sell This Item from Mining Hold. Minerals belong in ordinary cargo or Item Hangar. Restart the host and client and compare ship names, active hull, fitting, hold contents and wallet.

Ship switching preserves each hull's items and capacitor. The server requires ownership, an assembled hull in the same station, a docked pilot and the catalog's required skills. Inventory bindings retain the ship ID they were opened for; activate a ship before changing its contents. Purchased hull assembly supports the Venture, Merlin, Punisher, Incursus and Rifter. Repackaging, assembled-ship sales and boarding in space remain unavailable.

## Board a recovery corvette

1. Dock while aboard a supported ship other than a corvette, such as the Venture. Select **Eden Pilot**, open **Inventory**, and note the active hull and its cargo or Mining Hold contents.
2. Press **Board my Corvette** below Undock in the station panel. Confirm the native prompt to leave the active ship. Inventory should show the race's corvette as active and retain the previous ship in **Ship Hangar**. The hangar model should also change.
3. Open Fitting with the **circled ship** button. Check that a **Civilian Miner** and **Civilian Gatling Railgun** are fitted and online. The recovery kit costs no ISK and grants no skills. It uses the pilot's race hull, but currently supplies the same civilian railgun to every race.
4. Board the previous ship again and compare its cargo, mining hold and fitting. Repeat **Board my Corvette**: Eden should reuse the ready recovery hull without adding another kit. Restart both host and client and verify the active hull, fitted modules, previous ship and wallet remain.

Eden reuses a corvette at the current station only when its armor and structure are undamaged and both recovery modules occupy their expected online high slots. It does not repair, refill or strip another ship. Otherwise it creates a new equipped hull in one world transaction. Requests require the current ship, station, ownership and catalog skills. Repeated or competing requests for the same previous hull cannot create duplicate kits. The button is disabled while already aboard a corvette. This is an explicit local recovery policy. Recovery itself needs no schema change beyond version 20; version 21 adds NPC retaliation and saved ship losses.

## Check belt NPCs

Select **Eden Pilot**, check the active ship in Inventory, undock and warp to **Perimeter II – Asteroid Belt 1**. The local encounter policy places one Guristas Rookie 3,500 m from each Perimeter belt anchor. It should appear as a red NPC in an Overview preset that includes **Minor Threat**. The existing Mining preset also showed it during acceptance. Right-click it and choose **Lock Target**; after the normal scan delay, its portrait and health rings should appear. **Look at** should render its native ship model. **Approach** should move toward it. Restart the host and client at the belt and verify the same NPC appears without a duplicate.

Idle Rookies automatically detect the closest connected, available ship within 20 km surface distance. After the catalog's minimum 7.5-second reaction delay they pursue it at 220 m/s cruise speed, entering Orbit inside 1,875 m and aiming for a 1,250 m surface circle. They fire within 3,125 m and resume pursuit beyond that range. An earlier player shot can claim retaliation; a closer arrival does not steal an existing target or reset its deadline. Capsules, warping/alignment, gate transitions and disconnected ships cannot be selected. Leaving weapon range does not end pursuit. Warp, dock, disconnect or leave the local 50 km belt leash to disengage. The same damaged NPC then returns toward its belt anchor and stops within 25 m at low speed before detecting another target. Movement retains its identity and health, and restarting does not simulate a missed journey or shots. Detection range, closest-ship selection and leash are Eden policy. The following checks cover firing, pursuit, bounty payments and capsule recovery.

## Fire a civilian railgun and earn a bounty

1. While docked, select **Eden Pilot** and check the active ship in Inventory. World version 20 grants each existing pilot one **Civilian Gatling Railgun** in the active ship's station Item Hangar; new pilots receive one at their starting station. This is a one-time Eden starter grant, including for other races. It does not grant skills or ISK. The pinned type is unpublished, so use the owned item rather than searching Regional Market for it.
2. Right-click the gun and choose **Fit to Active Ship**. It requires Gunnery I, an available high slot and turret hardpoint, 4 CPU and 6 power grid. Open Fitting with the **circled ship** button and check that it is online. A Venture can carry one mining laser and this gun together.
3. Undock and warp to **Perimeter II – Asteroid Belt 1**. Lock the Guristas Rookie. Stop near its range of 4,800 m plus trained Sharpshooter bonuses, then activate the railgun's HUD button. Use **F2** if it occupies the second high slot. It carries its own civilian charge and consumes capacitor per shot. It fires at cycle start, repeats while the target remains locked, and stops after the current cycle when pressed again.
4. Watch for native combat messages showing the damage amount, target, weapon and hit quality, while the target's shield, armor and structure rings decline. Misses are possible outside optimal range or with angular motion. When the Rookie is destroyed, it disappears from Overview, the gun stops, and the personal wallet increases by **3,000 ISK**. Under **My Wallet → Transactions**, check **Bounty Prizes**, the resulting balance and the Perimeter description.
5. Restart both client and host using the same save. Verify the active hull, fitted gun, bounty balance and journal entry remain. The defeated NPC must not reappear early or pay again on reconnect. After ten minutes a replacement may appear at the same belt with a new identity. Dead rows and the paid bounty remain saved.

Player turret accuracy and shot-quality randomness are Eden's initial model; the client does not expose Tranquility's server RNG. Immediate individual bounty payments and the ten-minute respawn delay are local encounter policy. Fleet splits and bounty tax remain unavailable. Loot, NPC retaliation and player loss are covered below. Small Tech I hybrids and their ammunition are covered under [Upgrade to an ammunition-fed railgun](#upgrade-to-an-ammunition-fed-railgun); other civilian guns and weapon families remain unavailable. Reopen transaction panels to refresh their rows.

## Check NPC pursuit and return

1. Select **Eden Pilot**, verify the active fitted ship in Inventory, and warp to a Perimeter belt. Leave the gun inactive and do not lock or shoot the Rookie. Within 20 km it should select the ship and show an incoming targeted indicator.
2. After 7.5 seconds, watch its distance and velocity in Overview. It should approach at about 220 m/s, begin incoming fire inside weapon range, and circle near 1,250 m after closing within 1,875 m. Watch its nonzero angular movement and continuing velocity between shots. Lock it if desired; the lock remains while it moves and player Approach uses its current position. Movement affects both turrets' relative angular velocity. Verify that your own magazine has not spent any rounds. Choose **Keep at Range → 5,000 m** to pull beyond 3,125 m: the Rookie should stop firing and resume direct pursuit. Stop your ship and verify that it closes the gap, starts firing again and re-enters Orbit.
3. Warp to a station and dock. This must stop its aggression and incoming effects. Return to the belt and check that the same NPC is moving home or waiting near its original anchor with retained damage. Returning does not grant a bounty or replace its identity. It must finish returning before acquiring again; an eligible ship still inside detection range then starts a new reaction delay.
4. Restart the host and client on the same save and compare the NPC identity, position, damage, inventory and wallet. Movement advances at most one shared tick after a gap, without an offline chase or return journey. Reconnecting clears an old fight but allows fresh detection after return, with a full reaction delay and no overdue burst. Automatic acquisition uses schema 27's existing aggression records and grants no items, ISK or health. NPC collision avoidance remains separate work.

## Loot a defeated Rookie

1. Select **Eden Pilot** and verify the active equipped ship in Inventory. Undock, warp to a Perimeter belt, lock the Rookie and defeat it. Expect it to approach after the reaction delay; keep shields and capacitor available for retaliation.
2. Look for the new **Guristas Rookie Frigate Wreck**. Use the **General** or **All** Overview tab if the Mining preset hides wrecks. The wreck type comes from the NPC's pinned catalog definition.
3. Choose **Open Cargo**. The native controls approach automatically when needed. Transfer requires the active ship to be in the same system, outside warp and within **2,500 m of the wreck's surface**. Only the pilot credited with the kill has loot rights in this first implementation.
4. The wreck contains **100 Tritanium / 1 m³**, an explicit Eden starter loot rule. Drag some or all into the active ship's ordinary cargo, or press **Loot All**. Check the cargo quantity and remaining contents. Capacity failures must leave the untransferred items in the wreck; a partial move can use the quantity picker.
5. The empty wreck should use the native empty-wreck marker. It remains until its two-hour lifetime ends. Restart the host and client on the same save: collected cargo stays collected, and reopening the wreck must not grant another stack. Uncollected contents expire with the wreck. Take the minerals to a station to unload or sell through the existing market.

Migration does not create wrecks or loot for kills from earlier versions. Wreck pickup, bounty and NPC respawn have separate durable identities. Player-loss drops are covered below. Theft, fleet rights, wreck combat, salvaging and randomized NPC loot tables remain separate work.

## Take NPC fire and recover from a loss

Use an isolated copy of the save when testing destruction. The active hull is lost; supported modules and cargo stacks independently have a 50% survival chance. Station hangars, other hulls, wallet and skills are retained.

1. Select **Eden Pilot**, open Inventory and verify the active equipped ship. Undock and warp to a Perimeter belt. Select its Rookie and use **Keep at Range → 1,000 m**. An idle Rookie detects the ship automatically; a player target lock is optional.
2. Leave the gun inactive to keep the NPC alive. After the reaction delay, look for the NPC's incoming beams, red damage amounts naming **Guristas Rookie**, the targeted indicator and declining health rings. The server uses the catalog's damage, tracking, range and resistances.
3. Warp away or dock to end the fight. Reconnecting clears the old fight without missed offline shots; a returned guard can detect the ship again with a fresh reaction delay. For destruction, leave Inventory open and remain in range until all structure is lost. The old ship should disappear, Inventory should change to **Capsule (Capsule)** with no cargo or modules, and both the NPC portrait lock and its targeted indicator should clear. The capsule must not display the old ship's cargo.
4. Use the capsule's normal flight controls to warp and dock at a station. **Board my Corvette** should provide a flyable recovery hull with an online miner and civilian gun. Check the previous surviving ships and their cargo in Inventory, and compare the wallet.
5. Return to the loss location in the recovery hull. In **General** or **All** Overview, find the wreck named after the lost ship. Choose **Open Cargo**, approach within 2,500 m and recover the surviving contents with **Loot All** or a drag into ordinary cargo. Modules arrive assembled and offline, with their original item IDs. A capsule can inspect a nearby wreck but has no cargo capacity. Some losses can leave an empty wreck.
6. Restart the host and client on the same save. The destroyed hull stays absent; the replacement hull, recovered cargo, remaining wreck contents and saved drop result must persist without another roll or duplicate items. Wrecks expire after two hours, taking uncollected contents with them. Capsules are not NPC targets in this initial policy. Restore **Set Auto-Repeat On** for normal repeated firing when appropriate.

World version 24 preserves existing NPC wrecks and adds player wreck sources and saved drop identities. Earlier loss manifests stay historical; migration does not restore their destroyed contents. The normal 50% baseline applies independently to each supported fitted module or whole cargo/mining-hold stack. Seasonal modifiers, rigs, nested containers, theft/fleet sharing, insurance and native killmails remain unavailable. Only the lost ship's pilot has loot rights initially.

For a faster destructive acceptance fixture, use a separate physical copy of the lab catalog and increase only Guristas Rookie's damage multiplier there. Never edit a hardlink to the normal catalog: that would change the original file. The recorded accelerated run used **100× NPC damage**, leaving player damage unchanged. Stop the host before changing the test catalog and restore normal catalog data afterward. This is a test override, not the normal encounter balance.

## Repair a surviving ship

Use a station with **Repair Facilities**, such as Perimeter II - Moon 1 - Caldari Navy Assembly Plant. Repairs require an owned assembled hull in that station's hangar and a docked active ship. The active hull does not have to be the hull being repaired.

1. Select **Eden Pilot**, open Inventory and verify the active ship. Open **Repair Facilities** from the station panel. Select the damaged ship and request its quote.
2. Check the damage and price. Eden combines saved armor and hull damage, rounds each selected ship's damage up to a whole point, and charges **1 ISK per point**. Shields and capacitor are not part of the bill. This rate is local emulator policy.
3. Choose **Repair All** and enter part of the full amount to test a partial repair. The same fraction of armor and hull damage is removed from every selected ship. The refreshed quote must show the remaining damage. Each new quote rounds up again, so splitting a repair can slightly change the final total.
4. Pay the remaining full quote. The repair list should become empty. Open Fitting and check restored armor/hull; check the wallet and its **Repair Bill** journal entries. Repeating an already completed repair must not charge again.
5. Restart the host and client on the same save. Compare health, wallet, journal, cargo and fitting. No money, repairs or shield refill are granted by migration or restart.

The server recomputes damage at payment time and caps the charge at the current bill. An insufficient balance rejects the payment without healing. Packaged items, other pilots' property, ships elsewhere, ships in space and stations without repair facilities are rejected. Owned assembled modules currently quote zero damage because Eden does not yet simulate module heat/damage; station structures are outside this service.

## Train and use a shield booster

The tier backfill validates 89 static normal-booster profiles; the five Abyssal types still need per-item mutation support. Higher-tier native checks remain pending under the instruction to leave game windows alone. When testing resumes, compare Small Shield Booster II (35 HP / 20 GJ / 2 seconds, Shield Operation III), Domination Small (28 HP / 16 GJ / 1.6 seconds), Pithi A-Type Small (76 HP / 20 GJ / 2 seconds) and Ramaku's Modified Small (66 HP / 14 GJ / 2 seconds). Shield Compensation III reduces the ordinary capacitor amounts by 6% once; check the tooltip and actual expenditure. Shield Boost Amplifier II multiplies supported ordinary boost amount by 1.36. Near full shields, the gain clips to missing HP while capacitor still pays the full adjusted cost. Capital boosters use Capital Shield Operation instead; adequate hull fitting/capacitor resources are required. Use a copied save and record restart/cooldown preservation when authorized.

1. Dock at a station with a market and fitting service. Select **Eden Pilot**, open Inventory and verify the active ship. The testing market sells a **Shield Operation** skillbook for **1 ISK** and **Small Shield Booster I** for **1 ISK**. These prices and replenishing stocks are Eden testing policy.
2. Buy the skillbook and find it in Item Hangar. Right-click **Inject Skill**. One book should disappear and Shield Operation should appear in the Skill Catalogue at level zero. A stack retains its unused books. Injection grants no SP, and an already injected skill cannot consume another book. Current-ship ordinary cargo also supports injection; remote hangars and specialized holds do not.
3. Add Shield Operation I to the training queue and start it. Training uses the saved Alpha rate and requires Power Grid Management I. Wait for level I; restarting retains the queue and offline progress. Direct skill purchases from the Skill Catalogue are separate work: use the physical book path.
4. Buy the booster. Use **Fit to Active Ship** or the station's circled ship **Fitting** button, then verify it is online in a medium slot. The existing high-slot miner and civilian gun must remain fitted. CPU and power grid include all online supported modules; the booster uses no turret hardpoint.
   In Fitting's expanded **Defense** panel, click the repair-rate selector and choose **Shield boost rate** to see **13.0 HP/s** for one Small Shield Booster I. The default **Passive shield recharge** option reports a different calculation and rounds down to whole HP/s; an Ibis can therefore show **0 HP/s** there while the booster works normally.
5. Undock and take shield damage from a Rookie using the encounter steps above. Activate the booster on the HUD. It needs no target lock. Watch the shield ring, capacitor, cycling indicator and native shield effect. The unmodified small booster restores up to 26 HP immediately for 20 capacitor, with a 2-second cycle. At full shields it still spends capacitor. Manual deactivation finishes the paid cycle; insufficient capacitor rejects the next cycle without healing.
6. Warp or dock to end NPC aggression. The shield booster is warp-safe and stops on docking or hull loss. Restart and compare fitting, health and capacitor; no offline boosts are granted. Shield Compensation modifies capacitor cost when trained. Module overheating, ancillary charges and other repair families are separate work.

## Upgrade to an ammunition-fed railgun

1. Select **Eden Pilot**, dock at a market/fitting station and verify the active ship in Inventory. Buy a **75mm Gatling Rail I** for **1 ISK** and small hybrid charges for **1 ISK per round**. Antimatter Charge S and Iron Charge S each have replenishing testing stock. These prices are Eden policy.
2. Free a high slot if needed, then use **Fit to Active Ship** on the new gun. Keep the previous gun in the hangar. Fitting requires Small Hybrid Turret I, Gunnery I, a turret hardpoint, CPU and power grid.
3. Drag a charge stack onto the gun in Fitting, or carry it in ordinary cargo and use the gun's HUD ammunition menu. The 75mm railgun holds **200 rounds**. A load or ammo change has a saved **5-second** reload deadline; firing during it is rejected. Changing ammo returns the old rounds to the source container.
4. Carry reserve charges in cargo, undock and lock a Rookie. Activate the new gun. Check its ammo icon/count, cycle ring, capacitor, native turret effect, damage messages and the target's health. Every paid shot consumes the catalog's charge rate, including misses. A gun with no loaded ammunition cannot deal damage.
5. Stop the gun and wait for the paid cycle to finish. Use **Unload to Cargo**, reload, or select a different charge. Compare Antimatter's damage/shorter optimal range with Iron's lower damage/longer optimal range and lower capacitor cost. Compare the docked gun tooltip with its in-space HUD tooltip and Fitting offense panel: trained gunnery bonuses should agree. Cargo limits still apply when unloading. Unfitting while docked returns remaining loaded rounds into the same destination as the module.
6. Restart the host/client on the same save and compare magazine count, reserve stacks, reload deadline and wallet. Reconnecting grants no rounds and cannot finish a reload early. Loaded rounds are included separately in the original hull-loss manifest and its normal survival roll. Weapon banks, other turret families and Tech II charge groups remain unsupported.

## Manufacture ammunition from mined minerals

1. Select **Eden Pilot**, dock at a station with manufacturing and verify the active ship in Inventory. Buy an **Iron Charge S Blueprint** original for **1 ISK** from the testing market. This price and replenishing supply are Eden testing policy. Keep the original in the current station's **Item Hangar**. Industry I is required; use the pilot's actual trained skills.
2. Mine Scordite in a Perimeter belt, unload it into Item Hangar and use **Reprocess** to convert complete batches into Tritanium and Pyerite. One Iron run needs **27 Tritanium and 21 Pyerite**. Mining and reprocessing retain their normal catalog cycle times and yields.
3. Open the station's **Industry** factory button. In **Blueprints**, double-click the owned Iron original. Choose the current station and personal **Item Hangar** for both input and output. One run should preview **100 Iron Charge S**, using 27 Tritanium and 21 Pyerite, with a base five-minute build reduced to **4 minutes 48 seconds at Industry I**. Materials missing from the hangar should be marked unavailable.
4. Choose **Start**. Verify the materials leave the hangar, the original becomes unavailable for another job, and **Jobs** shows the countdown. The installed original must remain reserved when a move is attempted. A ready job still occupies the original and slot until delivered or cancelled in this initial Eden policy.
5. Restart the host and client on the same save before completion. The original, consumed materials and finish time must remain saved; restarting must not start the job again or create its output early. Industry time continues while disconnected.
6. Once the job is ready, choose **Deliver** from **Jobs**. Exactly **100 Iron Charge S** should arrive in Item Hangar, and the original should become available again. Load the manufactured charges into the supported railgun, wait for its reload, and fire: the magazine must spend ordinary rounds through the existing ammunition system. Restart and compare job history, charges, original and wallet.

Schema **27** adds saved manufacturing jobs without granting blueprints, materials, skills or ISK. Small Tech I hybrid ammunition and turret originals, Tech I mining lasers, shield boosters and afterburners support current-station personal jobs. Installation fees currently show zero because adjusted industry prices are unavailable, matching the existing missing-price fallback. Cancellation releases the blueprint and slot but consumes the paid materials without output. Research, copies, invention, corporate/remote jobs and other product families remain separate work.

## Manufacture starter equipment

1. Select **Eden Pilot** and verify the active ship in Inventory. At a manufacturing station, buy a **75mm Gatling Rail I Blueprint** from Regional Market for **1 ISK**. Other starter originals are **Miner I**, **Small Shield Booster I** and **1MN Afterburner I**, each at the same Eden price.
2. Double-click the owned original in Item Hangar. Its Industry preview should show the catalog recipe, one packaged module output and **14 minutes 24 seconds** with Industry I. A railgun requires **231 Tritanium and 220 Pyerite**. Missing materials disable Start. Right-click a missing material and use **Buy This Type** to purchase the shortfall, or supply minerals from reprocessing. The testing market sells **Mexallon** and **Nocxium** for **1 ISK/unit**.
3. Start one run. Check the material stacks, reserved original, occupied slot and countdown. Restart the host/client before completion and verify the same deadline and consumed materials; the new module must not exist early. Industry continues while disconnected.
4. At the original deadline, choose **Deliver** in Jobs. Exactly one packaged module should appear in Item Hangar and the slot/original should become available. Fit that module to the active ship using an available compatible slot, bring it online, and check the native fitting readout. For the railgun, load supported small hybrid charges and test a paid shot in space.
5. Dock and restart again. Compare the delivered job, reusable original, new module identity/fitting, consumed materials, ammunition and wallet. Retrying delivery must not produce another module. Existing ships, cargo and other jobs must remain intact.

Tech I manufacturing follows the same owned-original, exact-recipe, trained-skill and current-station rules as ammunition. Small hybrid turrets, regular mining lasers, shield boosters and afterburners are supported; Tech II products, other weapon families, ships, research and copies remain separate work. Existing offers are never replenished when the new blueprint and mineral offers appear. Installation still uses the missing-adjusted-price fallback of zero ISK fees. These four starter module recipes use the pinned catalog's 900-second base time; manufacturing skills reduce it.

## Orbit a target

1. Select **Eden Pilot**, verify the active ship in Inventory and undock. Warp to a Perimeter belt. Rookies automatically engage nearby ships, so keep shields available or test peaceful movement around a station outside a belt instead.
2. Select the Rookie or an asteroid in Overview. Right-click **Orbit** and choose **5,000 m**. The native indication should say Orbiting, the ship should turn and move around the target, and range should settle near the requested surface distance. Orbit does not require a target lock.
3. Change the orbit range and watch it adjust under inertia. An afterburner increases speed and adds mass, so the actual orbit can widen. Deactivate it and verify that orbiting continues using the ordinary flight properties.
4. Choose **Keep at Range** on the same object, then choose Orbit again. The client should change the movement mode each time. Press Stop or click the HUD stop control: orbiting must end and the ship should coast to rest. A new heading also replaces the orbit.
5. Warp or dock while orbiting. The course must end without resuming after arrival. If the target disappears, an orbiter continues along its velocity heading; a Keep at Range follower stops. Restart host and client and compare the saved position, cargo, fitting, wallet and ammunition. No orbit order or offline movement is restored.

Both controls use visible, local targets within **8,000 km** and accept surface ranges up to **1,000 km**. Self, missing and departing targets and invalid ranges are rejected. Nearby pilots receive native Orbit records and streamed course changes. Player orbiting uses the same steering calculation as the Rookie's close-range NPC orbit. NPC pursuit/orbit thresholds follow the encounter policy described above.

## Fit and fly with an afterburner

The tier validation covers 64 unrestricted static variants. Higher-tier native acceptance remains pending while game windows are left alone. When testing resumes, compare 1MN Afterburner II (135% base speed factor / 22 GJ, Afterburner IV), Domination 1MN (145% / 18 GJ), Gistii A-Type 1MN (160% / 18 GJ), Analog Booster 1MN (135% / 15 GJ), Asine's Modified 1MN (165% / 18 GJ) and Usaras' Modified 1MN (170% / 24 GJ). Their base cycle is 10 seconds. With Afterburner IV, Fuel Conservation III and Acceleration Control II, that becomes 8 seconds, 42% of the base capacitor cost and 110% of the base speed factor. Actual maximum velocity still accounts for hull mass, thrust and added mass. High Speed Maneuvering must not add another afterburner cost reduction. Check continuous repeats, paid shutdown and saved cooldowns on a copied save. Six ship-restricted capital variants and four Abyssal types remain unavailable.

1. Select **Eden Pilot**, dock at a market/fitting station and verify the active ship in Inventory. Search the Regional Market for **1MN Afterburner I**, buy one for **1 ISK**, and use **Fit to Active Ship** in Item Hangar. This finite starter offer is Eden policy. The module requires Afterburner I, a medium slot, CPU and power grid.
2. Open Fitting using the station's **circled ship** button. Check Navigation and hover over the afterburner. An Ibis with Navigation III shows **339.25 m/s** without the module and **691.15 m/s** with it. Afterburner III reduces its base 10-second, 20-GJ cycle to **8.5 seconds / 14 GJ**. Fuel Conservation and Acceleration Control modify cost and speed when trained.
3. Undock, set a direction in open space and choose full speed. Activate the afterburner on the HUD. Its ring should cycle, capacitor should be spent, and the ship should accelerate above its normal limit. Repeated cycles must retain one bonus and a steady maximum speed, rather than multiplying the speed again.
4. Click the active module to stop it. It finishes the paid cycle, clears its ring and restores normal speed and mass; actual velocity coasts toward the new limit. Warp prevents starting another afterburner cycle. Docking, hull loss and disconnecting remove the effect.
5. Restart both host and client on the same save. Confirm the module remains fitted and online, the wallet retains the purchase, and cargo and ammunition are unchanged. The speed bonus must be absent until a new paid activation. An unexpired saved cooldown still prevents immediate reactivation; disconnected time grants no propulsion cycles.

Afterburners originally used world version **27** and the existing saved capacitor and module cooldown records; no equipment, skills or ISK were granted. Nearby pilots receive the changed speed, mass and native propulsion effect; peer behavior has automated coverage. Microwarpdrives now use the additional live-module markers described below. Overheating, implants and other navigation bonuses remain separate work.

## Fit and fly with a microwarpdrive

Native acceptance is pending: the maintainer asked that game windows be left alone. The following is a future acceptance procedure, not a recorded live result. It covers unrestricted static drives; capital drives with ship-group restrictions and Abyssal instance mutations are deferred.

1. Select **Eden Pilot**, verify the active ship in Inventory and dock at a fitting station. Obtain a **5MN Microwarpdrive I**, train **High Speed Maneuvering I** and fit it in a medium slot with enough CPU and power grid. Keep other equipment within the hull's fitting budget.
2. Online the inactive drive. Its 25% capacitor-capacity penalty should apply, preserving the existing capacitor percentage. Signature radius and speed must remain normal until activation. Taking it offline restores capacity. Other tiers use their own catalog penalty, speed factor, signature bonus, cost and skills.
3. Undock and activate it on the HUD. With Navigation III, High Speed Maneuvering III and no other bonuses, the Ibis should have a **10-second / 38.25 GJ** cycle, approximately **1,869 m/s** maximum speed, **1,663,000 kg** mass and **312 m** signature radius. The ordinary signature is 52 m. Repeats retain one bonus and a continuous HUD cycle.
4. Click the drive to stop it. Its paid cycle finishes before normal speed, mass and signature return. Attempting to run an afterburner at the same time must fail. Warp blocks a new activation and the next repeat; docking, loss and disconnect remove the live bonus.
5. Restart only when authorized, using a copied save. Version **31** renames the hardener activation table to shared `WorldActiveModule`. It clears live markers while retaining equipment, online capacity penalties, spent capacitor and paid cooldowns. Reconnecting grants no offline propulsion and cannot bypass an unexpired cooldown. Compare a Tech II or faction drive to confirm its distinct attributes and skill requirement.

## Fit and use an armor repairer

The tier backfill covers 101 static ordinary repairers, including civilian, Tech II, storyline, faction, deadspace and officer variants. Higher-tier native acceptance remains pending under the maintainer's instruction to leave game windows alone. Once testing resumes, use a copied save and compare Small Armor Repairer II (92 HP / 40 GJ), Domination Small (83 HP / 36 GJ), Coreli A-Type Small (120 HP / 40 GJ) and Asine's Modified Small (130 HP / 40 GJ). With Repair Systems III these four use 5.1-second paid cycles. Harmony Small restores 104 HP / 40 GJ over 5.7375 seconds with that skill. Capacitor is paid at the start and armor is restored once at the end, capped by missing HP. Small Armor Repairer II needs Repair Systems III and Mechanics III. Capital modules need adequate fitting/capacitor resources and use Capital Repair Systems for duration; the ordinary skill does not add another reduction. Per-item Abyssal mutations remain pending.

1. Select **Eden Pilot**, dock and verify the active ship in Inventory. Train **Repair Systems I** and **Mechanics I**, obtain a **Small Armor Repairer I**, and use **Fit to Active Ship**. It needs a low slot, 5 CPU and 5 power grid. Ordinary named Tech I repairers also use their pinned catalog values.
2. Open Fitting or the module's Attributes. The basic small repairer shows **69 armor HP**, **40 GJ** and **5.7 seconds** with Repair Systems I (6 seconds before skills). Armor repair is applied at the end of a cycle. Existing armor damage is needed to observe healing; use a separate copied save for artificial damage fixtures.
3. Undock and activate the repairer on the HUD. Capacitor should fall immediately, while armor stays unchanged until the paid cycle ends. Then armor rises by up to 69 HP, capped at the hull's maximum. Shields and structure must not be repaired. It may run during warp.
4. Click the active module again to stop it. The current paid repair finishes, the ring clears and no further capacitor is spent. Let it repeat through several cycles to check a steady HUD ring and one payment per new cycle. Insufficient capacitor stops the next cycle without losing an already paid repair.
5. Disconnect or dock during a paid cycle, then restart on the same save. The single paid completion remains due at its original deadline, even if that deadline passed while disconnected. Reconnecting before it ends restores the original cycle without another payment or automatic repeats. Inspect armor again to confirm there is no duplicate completion. World version **29** adds the pending-repair table while retaining inventory, skills, health and saved cooldowns.

## Fit and activate resistance hardeners

The expanded cohort contains 103 shield and 148 armor profiles. Higher-tier native checks remain pending while game windows are left alone. When authorized testing resumes, compare EM Shield Hardener II (55%, 20 GJ / 10 seconds), Dread Guristas EM Shield Hardener (56.24%, 20 GJ / 15 seconds), Pith X-Type EM Shield Hardener (63.5%, 20 GJ / 12 seconds) and Estamel's Modified EM Shield Hardener (64%, 20 GJ / 12 seconds). Compare EM Armor Hardener II (55%, 30 GJ / 20 seconds) and Core X-Type EM Armor Hardener (62.88%, 46 GJ / 20 seconds). Verify the actual fit, tooltip values, inactive baseline, active resistance, mixed-tier diminishing returns, paid shutdown and restart cleanup on a copied save. Shield II requires Tactical Shield Manipulation IV; armor II requires Hull Upgrades V. Compensation skills must not amplify active hardeners, and Damage Control retains its separate multiplication.

1. Select **Eden Pilot**, dock and verify the active ship in Inventory. Obtain an **EM Shield Hardener I** and **EM Armor Hardener I**. The shield module requires Tactical Shield Manipulation I, a medium slot, 40 CPU and 1 power grid. The armor module requires Hull Upgrades III, a low slot, 33 CPU and 1 power grid. Make room or take other modules offline if the Ibis lacks CPU.
2. Open Fitting and expand Defense. Online but inactive hardeners must leave the hull's resistances unchanged. Undock and activate the shield hardener on the HUD. It spends 20 GJ for each 10-second cycle. On the existing Ibis fixture, shield EM resistance changes from **8% to 54%** while active. A second identical hardener should raise this to about **74%**, showing diminishing returns.
3. Activate the armor hardener. It spends 30 GJ for each 20-second cycle and changes the fixture's armor EM resistance from **50% to 75%**. Hardeners do not restore missing HP. Applicable passive coatings/amplifiers share the stacking order; Damage Control's separate multiplication remains exempt.
4. Click the active module to stop it. Its resistance remains through the paid cycle, then returns to the value supplied by the remaining fit. Let it repeat and check that the bonus does not compound each cycle and the HUD ring remains active. An unpaid next cycle removes the bonus. Both families are warp-safe.
5. Dock, disconnect or restart while a hardener runs. Its active bonus must be absent on the next session, and starting it again before its saved cooldown expires must be rejected without a second payment. World version **30** adds connection-owned activation markers. Explicit world initialization clears those markers while retaining paid capacitor and cooldowns; it grants no equipment or skills.

## Load and use a capacitor booster

All 43 published static variants now use their own catalog values. The existing Small Capacitor Booster I result is maintainer-confirmed; higher-tier native checks remain pending under the instruction to leave game windows alone. When testing resumes, compare Small Capacitor Booster II (Capacitor Systems Operation III, 15 m³ magazine, 12-second cycle), Small Siesta (12 m³, 13.5 seconds), Dark Blood Small (12 m³, 11.25 seconds), Saddle Small (15 m³, 13.75 seconds) and Usaras' Modified Small (12 m³, 9 seconds). A Cap Booster 25 occupies 1 m³, making magazine-volume differences visible. Each still consumes one charge for its real charge amount, observes the 10-second reload and retains paid deadlines on restart. Test only when authorized, using a copied save and fitting budgets the hull can afford.

1. Select **Eden Pilot**, dock and verify the active ship in Inventory. Obtain a **Small Capacitor Booster I** and **Cap Booster 100** charges. Capacitor Systems Operation I, a medium slot, 15 CPU and 5 power grid are required. Fit the booster and bring it online.
2. Load charges from current-station Item Hangar or the ship's ordinary cargo. The small booster holds three 100-GJ charges in its 12 m³ magazine. Wait for the **10-second reload**. Larger cap charges are limited by volume: a Cap Booster 200 occupies 8 m³ and fits once, while an ordinary Cap Booster 800 at 32 m³ does not fit. A Navy Cap Booster 400 at 12 m³ fits once.
3. Undock and spend capacitor with another module, then activate the booster on the HUD. The loaded count should fall by one immediately, and capacitor should increase by up to 100 GJ. Its cycle lasts **15 seconds**. Activating at or near full capacitor wastes the excess while still consuming the charge. It is warp-safe.
4. Click the active booster to stop it. The current interval completes without another injection. Repeated cycles consume one real charge each and retain the HUD ring; an empty magazine stops further injections. Replenish it from cargo or reload while docked, then check the reload delay again.
5. Restart on the same save during a reload or paid interval. Magazine contents, capacitor and the original deadline must remain saved, with no disconnected repeat consumption or free injection. Unload into cargo/hangar or unfit at a station and verify all remaining charges are returned. This feature reuses existing magazine/cooldown tables and adds no world schema migration.

## Compare projectile turret tiers and ammunition

Native acceptance remains pending while game windows are left alone. When testing resumes, use a copied save, select **Eden Pilot**, open Inventory and verify the active ship. Fit a 125mm Gatling AutoCannon I or an affordable named/faction version in a turret hardpoint. The Tech II version requires Small Projectile Turret V, Gunnery II and Small Autocannon Specialization I. Use a suitable hull and its real resources for larger guns; the automated capital tests use artificial budgets.

Load EMP S or Carbonized Lead S from the docked hangar or ordinary cargo and wait for the **ten-second reload**. A 125mm Gatling AutoCannon I holds 200 rounds; Dual 180mm AutoCannon I with medium charges holds 200; 1200mm Artillery Cannon II with large charges holds 40; Hexa 2500mm Repeating Cannon II with XL charges holds 80. Check conserved quantities through partial unloading, a restart during reload, and unfitting. No projectile shot should spend capacitor, including a miss or a shot made with empty capacitor.

Compare named, Tech II, storyline, Domination and officer guns. Hail/Barrage belong to advanced autocannon groups, while Quake/Tremor belong to advanced artillery groups; both group and size must match. Eligible faction/storyline guns can also accept their listed advanced group, while specialization damage only applies to a gun requiring that skill. Check loaded-ammo and skill-adjusted damage, cycle time, range, tracking, falloff and reload deadlines in module readouts. Wrong groups, wrong sizes and missing skills must leave source stacks and magazines intact.

Where the hull has room, compare repeated Gyrostabilizer I modules. Their 7% damage and 8% cycle-time bonuses should diminish on later modules; heat sinks and magnetic field stabilizers should add no projectile bonus. Verify continuous HUD repeats and paid shutdown without resets or extra rounds. Fit a polarized autocannon and check all shield/armor/hull resistances, including while offline and with Damage Control; unfitting should restore the remaining fit's defenses without healing. Record these native observations separately from automated results.

## Compare hybrid turret tiers and ammunition sizes

Native acceptance for the new hybrid tiers remains pending under the instruction to leave game windows alone. When testing resumes, use a copied save, select **Eden Pilot**, open Inventory and verify the active ship. Fit a weapon whose real CPU, grid, hardpoint and skill requirements the hull meets. Larger weapons must use an appropriate fit; the capital automated fixtures use artificial budgets and do not establish a real corvette fit.

Compare a named railgun, 125mm Railgun II, a Shadow Serpentis gun, a storyline Musket, and an officer blaster. Load ammunition of the exact required size and a permitted catalog charge group. Tech II railguns accept Spike/Javelin, and eligible blasters accept Void/Null. Some faction and storyline guns also explicitly accept advanced charges. Train the ammunition's specialization as well as the weapon's requirements. Check range, tracking, falloff and capacitor changes when swapping ammunition; a specialization should only add weapon damage when that gun requires it. A wrong size or group must leave the ammunition stack and current magazine intact.

Check real magazine capacities and the five-second reload: 75mm Gatling Rail I with Antimatter S holds 200 rounds; 250mm Railgun II with Antimatter M holds 40; 425mm Railgun II with Antimatter L holds 40; Ion Siege Blaster II with Antimatter XL holds 80. Test paid shots, misses, continuous repeat rings, manual shutdown, partial unloading and restart without extra rounds or a reset deadline. For a polarized blaster, compare Defense before fitting, while fitted offline, while online, and after unfitting. All twelve resistances should be zero while fitted, even with Damage Control or a corvette role bonus. Unfitting should restore the remaining fit's defenses without repairing damage. Native observations must be recorded separately from automated checks.

## Compare ordinary mining-laser tiers

Native checks for the new tiers, residue and stochastic rounding remain pending while game windows are left alone. When testing resumes, use a copied save, select **Eden Pilot**, open Inventory and verify the active ship. Fit a laser the hull can afford, train its real requirements, undock and lock an ordinary ore asteroid within range. Compare Miner II (Mining IV, 12 km, 15 seconds, base 15 m³ / 18 GJ), EP-S Gaussian Scoped (16 km, 15 seconds, 13 m³ / 5 GJ), Gallente (10 km, 15 seconds, 12 m³ / 2 GJ), Habitat (12 km, 15 seconds, 14 m³ / 5 GJ) and ORE Miner (16 km, 15 seconds, 21 m³ / 18 GJ). Trained yield and critical skills should affect the tooltip and actual cargo consistently.

Repeat cycles and check the continuous HUD ring, paid capacitor, ore stacks and deposit depletion. Miner II has 34% residue probability and Gallente 20%; other ordinary profiles have none. Residue should appear separately in mining feedback and reduce the asteroid without entering cargo. Check that the native scanner subtracts residue once. Compare nearly depleted rocks, nearly full holds and a partial manual stop; only completed cycles can crit. Fractional ore units sometimes round up, so use repeated samples instead of expecting the same truncated integer every cycle. Restart on the copied save and compare cargo and remaining deposit. Deep-core lasers remain unavailable until cloud hazards are implemented; ice variants remain deferred by ship restrictions, and Abyssal variants need instance mutation support. No native success is claimed from automated tests alone.

## Check a rejected gameplay action

Select **Eden Pilot**, open Inventory and verify the active equipped ship. Undock safely outside a station. Right-click an online module on the HUD, choose **Put Offline** and accept the local module prompt. Eden currently requires docking to change online state, so the native **Information** dialog should explain: **Change module online state on your docked active ship.** Dismiss it and check that the module remains online and the ship still responds. This check must preserve the module, cargo and wallet. Capacity errors retain the client's existing quantity-selection flow.

## Check saved ship health

Combat encounters are still being integrated. For an isolated health acceptance fixture, start with a copy of the verified Venture save and let the server migrate it to world version 18 or later. With the client stopped, add a `WorldShipHealth` row for the active Venture with `ShieldCharge=75`, `ArmorDamage=100`, `StructureDamage=100` and `UpdatedAt` set to the current Windows FILETIME. This is test damage, not damage earned through a working encounter. Keep the normal gameplay save separate.

Select **Eden Pilot**, verify **prospector (Venture)** and its saved ore in Inventory, and open Fitting with the circled ship button. Expand Defense to check the trained maximum health values. Undock and hover over the three health rings. On the existing fixture (Mechanics III, Hull Upgrades II and Shield Management II), the client rounds the maximum values to 248 shield, 193 armor and 230 structure; the saved damage leaves 93 armor (48%) and 130 structure (57%). Shield charge should rise over time. Restart both host and client and compare the health rings again. Switching to another hull must retain damage on the original hull; that path also has automated coverage. There is no public damage RPC or repair service in this milestone.

## Check the character sheet

Start a fresh client with `-Login` and select **Eden Pilot**. Open the sheet from the portrait on the left, or use the window restored at login. Under **Character → Home Station**, check the pilot name, birth date, Jita home station, and total SP. Click **Skills → Skill Catalogue**, filter to **My skills**, and select **Spaceship Command**. On a fresh save the Caldari pilot has Spaceship Command III and 384,047 total SP. Reopen the client after restarting the server to compare against the saved pilot.


## Compare energy-turret tiers and crystal wear

Native acceptance is pending while game windows remain untouched. When testing resumes, use a copied save, select **Eden Pilot**, open Inventory and verify the active ship. Fit a laser whose actual fitting budget, hardpoints and skill requirements the hull meets. Larger and capital automated cases use artificial budgets and do not establish a real Ibis fit. Compare a Tech I/named laser, Dual Light Beam Laser II, a Dark Blood laser, a storyline/officer variant and a polarized pulse laser. The starter civilian railgun must still fire its built-in charge.

Load a crystal of the exact size and a permitted catalog group. Beam and pulse advanced crystals require their matching specialization; eligible faction guns may accept them but only receive specialization weapon damage if they require that skill. Compare Radio, faction Radio, Gleam and Scorch where compatible, including damage, range, tracking, falloff and capacitor. Swapping crystals retains the catalog's near-instant 0.01 ms reload, with no five/ten-second substitution. Wrong group/size/skills must leave owned stacks and the loaded crystal intact.

Ordinary Tech I crystals stay at zero wear. Faction crystals gain 0.025% damage each paid shot; Tech II crystals have a 10% chance to gain 1% damage. Check the crystal damage tooltip during firing, including misses. Unload a worn crystal, move it to cargo/hangar, load a fresh crystal of the same type, and reload the worn one: damage belongs to that individual. Restart the copied save and compare condition. A nearly broken test crystal should resolve its last paid shot, disappear and stop repeat firing until reloaded. Compare heat-sink stacking, polarized defenses offline/online/unfitted, continuous repeat rings and manual shutdown. Record each observed result separately from automated tests.

## Compare local hull-repair tiers

Native checks remain pending while game windows are left alone. When testing resumes, use a copied save, select **Eden Pilot**, open Inventory and verify the active ship. Leave room in a medium slot and meet the module's real CPU, grid and skill requirements. Compare Small Hull Repairer I, Small Hull Repairer II, Small Inefficient and Small Integrative. The cohort has Tech I/named, Tech II and storyline variants, including capital profiles. Large/capital automated cases use artificial budgets and do not establish a normal Ibis fit.

With nonlethal hull damage on the copied save, undock and activate on your own ship. Capacitor should decrease at cycle start and hull should increase at cycle end, while armor stays unchanged. Repair Systems III makes Small Hull Repairer I cycle 25.5 seconds for 25 HP / 30 GJ, Tech II 20.4 seconds for 30 HP / 30 GJ, and Integrative 17 seconds for 45 HP / 30 GJ. Capital hull profiles receive both Repair Systems and Capital Repair Systems; V/IV makes their base 60-second cycle 36 seconds. Capital armor repairers keep their separate single-skill ownership.

Check hull animation, observer replay, continuous repeat rings and manual stop. Two repairers should complete independently; repair clips to missing hull without refunding payment. Dock/reconnect or restart during a paid cycle and verify one completion without offline repeats or a new payment. Moving the module to another ship must retain its cooldown and leave the old paid repair on the old ship. A destroyed hull must never be revived. Record each action and visible result separately from automated checks.
## Compare missile fitting and ammunition inputs

Native acceptance is pending while game windows are left alone. This milestone implements fitting/loading and calculations; missile firing and flight are still pending. When testing resumes, select **Eden Pilot** on a copied save, open Inventory and verify the active ship. Use a hull with the real launcher hardpoints, fitting budget and trained requirements. Larger/capital automated cases use artificial budgets and do not establish a normal Ibis fit. Civilian Light Missile Launcher uses a high slot without requiring a launcher hardpoint.

Compare ordinary, named, Tech II, faction and officer profiles where present. Light Missile Launcher I holds 40 light missiles with a ten-second reload; Rocket I holds 37 rockets; Rapid Light I holds 19 light missiles with a 35-second reload. Torpedo I and Republic Fleet Cruise must recognize their third/sixth charge-group selectors, including permitted advanced ammunition. Incorrect groups or untrained advanced-ammo requirements must preserve stacks and leave the current magazine intact. Unload partially, reconnect/restart the copied save and compare remaining rounds and deadlines.

Compare earned damage/flight/application values before and after missile skills, a guidance enhancer and ballistic controls. A second ballistic control must provide a diminished damage and cycle bonus; skills stay separate. Heat sinks and hybrid/projectile upgrades must not change missiles. Ibis's kinetic role affects kinetic damage only. Take a guidance enhancer offline and check that its bonus disappears from the loaded charge. Compare polarized defenses while fitted offline, online and after unfitting. Record visible results separately from automated checks.

## Verify missile firing and flight

Launcher activation, saved projectiles, shared NPC impacts, automatic acquisition and native ball updates now have automated coverage. Deployment and native acceptance remain pending. Leave the current game windows alone until the maintainer resumes native testing; perform the following checks only then, against a separately staged server and copied save.

On a copied save, select **Eden Pilot** and verify the active ship in Inventory. Fit an unrestricted launcher with its actual skills and compatible ammunition, complete its reload and lock a nearby NPC. Check that one real round leaves the magazine at activation, a missile visibly leaves the source ship/module, and damage appears on contact rather than at launch. Compare stationary, approaching, crossing and escaping targets. Contact may occur between simulation endpoints; a target that escapes the lifetime must take no damage. A lost target must not be silently replaced with an innocent nearby ship. Missile launchers must still fire with empty capacitor. Automatic ammunition should work without a lock only when an NPC is already attacking the source ship, and must reject an idle closer NPC.

Use another observer and a joining client to compare projectile source/target identities, moving positions, trail/warhead effects and removal on impact versus expiry. Check visible impact effects explicitly: automated mode-5 and RemoveBall payload checks do not establish native animation or explosion acceptance. Two missiles completing a kill together must produce one bounty and one wreck. Keep a launcher repeating through several payments and check that its HUD circle remains continuous; manual stop should finish the current paid interval without buying another shot. Exhaust the magazine, then reload and resume, checking both stack conservation and deadlines.

Repeat with ordinary, named, Tech II, faction and officer equipment where present, plus ballistic controls and guidance equipment. Compare actual contact damage with effective charge attributes and the NPC's signature/speed. Changing skills after launch should not retroactively alter that missile's saved motor or warhead. Dock, disconnect, warp, jump, lose the source hull and restart the copied server with shots in flight: pending flights must disappear, paid ammunition and deadlines must remain, and reconnecting must not produce offline impacts. Record each action and visible result separately from automated checks.

## Verify scripted tracking, guidance and sensor modules

Deployment/native acceptance for these modules is pending. Keep current game windows untouched until the maintainer resumes testing. Then use a separately staged server and copied save, select **Eden Pilot**, and verify the active ship in Inventory before fitting. Use a legal hull and actual skill/resource budgets; automated combinations with expanded Ibis budgets do not establish a legal native fit.

1. Fit a tracking computer beside a supported turret. Compare its inactive and active optimal/falloff/tracking values, actual targeting/fire behavior and ten-second capacitor payments. Tracking Computer I gives 5%/10%/10% for 7 GJ; Tech II gives 7.5%/15%/15% for 10 GJ. Compare named, storyline, faction and officer equipment where present. A miner and missile launcher must not receive tracking bonuses.
2. Stop the computer and finish its paid interval, then load one Optimal Range Script. Optimal/falloff source bonuses should double and tracking should disappear; Tracking Speed Script reverses that choice. Script loading uses the catalog's 0.01 ms deadline. Check that the script remains after many cycles and that a running module rejects swap/unload requests without moving either stack. Stop, swap and unload, then verify total script quantities.
3. Fit a missile guidance computer and load compatible missiles into a launcher. Compare unscripted, Missile Range and Missile Precision values in the module/charge readouts and in actual missile flight/application. Range doubles the computer's speed/lifetime bonuses while suppressing its application bonuses; Precision does the reverse. Tech II, Caldari Navy and Republic Fleet profiles have distinct catalog strengths. Existing missiles retain their launch snapshot when a computer later stops or changes.
4. Fit a sensor booster. Verify longer lock range, faster new locks and sensor-strength readouts. Test Targeting Range, Scan Resolution and ECCM scripts separately. ECCM changes the sensor-strength attributes; the separate ECM/jamming combat family remains unavailable. Lock a target beyond the unboosted range, stop and finish the paid cycle, then confirm that the now-invalid lock disappears. Add active/passive equipment and verify diminishing returns independently for each affected attribute.
5. Repeat several paid cycles without HUD-ring resets, stop manually during an interval and check that bonuses last through that payment. Repeat with insufficient capacitor: failure to buy another interval must remove the bonus. Check warp-safe operation, docking, gate/ship-loss transitions and reconnecting. On restart, scripts/capacitor expenditure/deadlines remain, while active bonuses clear. Compare visuals with an existing observer and a fresh snapshot; native OnSpecialFX visibility still requires this direct check.

Record each action, fitted type/skill/script, displayed values and visible result separately from automated checks. Overheating and remote tracking/sensor/guidance families require their own implementations and acceptance.


## Check tackle and warp navigation

Use a copied acceptance save beside the matching catalog. Legal Ibis fits can exercise Webifier II/Scrambler I on Eden Pilot and a 5MN MWD/Disruptor I/Aura stabilizer on Eden Wing, with Prototype Hyperspatial Accelerators in available low slots and the required skills. Select Eden Pilot and confirm the active Ibis in Inventory first. The accelerator should show 3.30 AU/s rather than the Ibis's base 3.00 AU/s. Native acceptance of other tiers remains separate from automated catalog coverage.

Undock both ships, move clear of the station's collision volume, and keep them within tackle range. For high security player-versus-player acceptance, use Omega accounts in the copied test save; Alpha is restricted from disabling safety by the pinned client. Normal accounts remain Alpha by default. Use the native safety selector to choose yellow, green and red; reconnect to confirm the saved choice. Red is needed for offensive player-versus-player tackle in Jita. Lock Wing, run the web and compare Wing's maximum speed and EWAR indicator; a Tech II web reduces speed by 60%. Check several repeats and a paid manual stop. Start Wing's MWD, then apply Pilot's scrambler: the MWD must stop, refuse reactivation, and return when the scrambler ends. A disruptor alone must leave the MWD available.

With Scrambler I's two points active, attempt warp and verify rejection. Activate Aura's -2 core strength and retry warp during its 18-second cycle: the tie permits warp while MWD blocking remains in effect. A further point must block it again. The stabilizer must run one cycle and refuse reactivation until its saved delay ends. Verify target/source lock loss, docking, gate travel, disconnect and restart cleanup without refunds. Web an NPC to check actual motion as well as the graphic. Record only the actions visibly exercised; automated cases do not establish native acceptance of every tier.

## Verify ship collisions with two clients

Use a copied save beside the matching catalog and launch the host with EDEN_TRACE_MOTION=1 in its process environment. The server prints the committed Destiny tick and surface range for each nearby player/player or player/NPC pair.

Launch the normal development client once with -Login -Pilot Pilot and once with -Login -Pilot Wing. Select Eden Pilot and Eden Wing. Place the two ships in clear space, select the other ship in both overviews and use Approach on one pilot. Watch the moving ship contact the stopped ship, then stop both ships. Compare both overview ranges with the logged server surface range, allowing the labels to refresh. Watch for at least twenty seconds after settling to catch delayed corrections. Repeat with the other pilot approaching, then approach a Perimeter belt NPC while the second pilot observes. Record actual visible movement, any snap, settled ranges and the matching server ticks separately from automated test results.

## Fly the four racial Tech I frigates

Use a copied save and the normal pinned catalog. Buy Merlin, Punisher, Incursus and Rifter hulls from the station testing market, assemble them in Ship Hangar, name them and board each. The catalog required racial Frigate skills remain mandatory. Select Eden Pilot and check the active hull in Inventory after every boarding.

With racial Frigate III, Merlin receives 15% small-hybrid damage and 12% shield resistance bonuses. Punisher receives 30% small-energy capacitor reduction and 12% armor resistance bonuses. Incursus receives 15% small-hybrid damage and 22.5% local armor repair amount bonuses. Rifter receives 30% small-projectile falloff and 22.5% cycle-time reduction. Fit and use a compatible weapon with its ammunition, preserving the real CPU, power and hardpoint budgets. Check Fitting and the weapon or repairer attributes, then defeat a Perimeter belt NPC in each hull.

Switch back to the starting ship and verify its original cargo and fit. Restart the host and client against the same copied save, reselect Eden Pilot, and check the last active hull, name, fit, loaded ammunition and damage. The Incursus drone bay awaits drone control. These hulls also retain the existing wider gaps for rigs, heat and unsupported module families.

Reproduce the full 275-ID Tech I inventory and first-cohort fixtures with the following command.

```powershell
python tools/Export-HullCatalog.py --catalog "$env:LOCALAPPDATA/Eden/catalog.sqlite" --output tests/Eden.Protocol.Tests/Fixtures
```

The first-cohort acceptance used a copied Perimeter save, racial Frigate III and trained test-equipment prerequisites. All four ships were bought at the existing 1 ISK offers and assembled, named, boarded and fitted through native controls. Each destroyed a belt Rookie and redocked. The native weapon context menus loaded cargo ammunition. The armor repairer displayed 113 HP per 4.5 seconds and visibly consumed capacitor. Returning to the original Ibis showed its empty 125 m³ hold. Restarting the host/client retained the named Rifter, three loaded guns, 20.1 DPS in Fitting and 400 spare rounds in Inventory. The [protocol acceptance record](protocol.md#tech-i-hull-expansion-milestone-1) separates exact automated checks from these observations and describes the copied-save ammunition preparation and the station-alignment limitation.


## Small Tech I hull acceptance

Use the copied-world procedure above. This cohort includes all 99 qualifying frigates, destroyers, shuttles, corvettes and small specialist-group entries. Read [the per-hull capability record](tech-one-hulls.json) before selecting a role: flight support does not enable its listed drone, probe, analyzer or other deferred systems.

Reproduce the small-hull definitions and the coverage record from the pinned catalog:

```powershell
python tools/Export-HullCatalog.py --catalog "$env:LOCALAPPDATA/Eden/catalog.sqlite" --output tests/Eden.Protocol.Tests/Fixtures --cohort small
dotnet run --no-build --project src/Eden.Server -- --export-hull-coverage "$env:LOCALAPPDATA/Eden/catalog.sqlite" docs/tech-one-hulls.json
```

For native acceptance, select Eden Pilot and verify the active hull in Inventory after each boarding. Exercise a no-rack shuttle, Zephyr's one-high/no-hardpoint layout, Echelon's one-medium layout, a destroyer and representative supported hull bonuses. Use legal CPU, power, slots and hardpoints. Compare fitted attributes with the operation that uses them: Leopard warp speed, Succubus afterburner speed, Daredevil web strength, projectile reload, missile flight/damage, Venture Consortium Issue's mining hold/yield/shield boost, Fleet Thrasher's MWD and Navy Catalyst's armor-plate mass. Keep each previously flown hull's inventory separate and reconnect to check persistence. Use the station's Board my Corvette recovery control as a regression.

The verified run flew eleven additional small hull types, recovered the racial Ibis and restarted the copied host/client with Consortium active. Inventory retained 2,573 Veldspar in its 6,250 m³ Mining Hold and Fitting retained its miner and shield booster. The [protocol record](protocol.md#tech-i-hull-expansion-milestone-2) separates the native observations from automated-only hulls and role gaps. Full repository checks run separately from the interactive game session.

## Cruiser, battlecruiser and battleship acceptance

Use the same copied-world procedure and the 101-type combat cohort. Reproduce the definitions with:

```powershell
python tools/Export-HullCatalog.py --catalog "$env:LOCALAPPDATA/Eden/catalog.sqlite" --output tests/Eden.Protocol.Tests/Fixtures --cohort combat
dotnet run --no-build --project src/Eden.Server -- --export-hull-coverage "$env:LOCALAPPDATA/Eden/catalog.sqlite" docs/tech-one-hulls.json
```

Select Eden Pilot. Board representative cruiser, battlecruiser and battleship hulls and verify each active name in Inventory. Use actual fitting resources: medium guns on a cruiser, reduced-cost large guns on an attack battlecruiser and appropriately sized launchers. Check the fitted values against paid firing/reloads and actual flight. On a battleship, inspect the extender, plate and bulkhead role values and resulting defenses. Dock and change ships, then restart to verify cargo, modules, magazines and names.

For incoming web resistance, use two local Omega test accounts in the same copied world; the stock client blocks Alpha aggression between these pilots. Fit a web on Eden Wing's ship, undock both pilots and lock Eden Pilot's Cenotaph. Compare its displayed maximum and actual movement before, during and after the paid web. Keep both accounts and inventories separate. The [milestone 3 record](protocol.md#tech-i-hull-expansion-milestone-3) records verification status and identifies deferred role systems.

The verified run flew Moa, Caracal, Oracle, Raven, Anhinga and Cenotaph, docked each, switched back to the original Ibis and restarted with Raven active. Inventory retained 480 Scourge Cruise, and Fitting retained all four modules, its defenses and the 17-round loaded magazine. All 24 copied-world ships and 89 inventory items matched the pre-restart snapshot; the normal world remained unchanged. The other 95 combat hulls have automated coverage only. Full repository checks passed separately from the interactive run.

## Industrial, utility and personal-hold acceptance

Milestone 4 delivers 35 industrial/utility hulls, bringing assembly admission to 235. Its [acceptance record](protocol.md#tech-i-hull-expansion-milestone-4) separates the completed full baseline suite, focused final corrections, native observations and automated-only behavior. The duplicate final full rerun was stopped at the user's request to end extended testing; it is not reported as a completed run. Capital admission remains deferred. Reproduce the catalog and independent native inventory vectors with:

```powershell
python tools/Export-HullCatalog.py --catalog "$env:LOCALAPPDATA/Eden/catalog.sqlite" --output tests/Eden.Protocol.Tests/Fixtures --cohort utility
python tools/Export-ShipInventory.py --catalog "$env:LOCALAPPDATA/Eden/catalog.sqlite" --client E:/EdenDev/3662168 --output tests/Eden.Protocol.Tests/Fixtures/NativeShipInventory.json
```

Use a copied save and select **Eden Pilot**. Exercise each distinct hold filter through native Inventory: Hoarder gas/ammunition, Kryos minerals/ice, Epithal command centers/planetary goods, a Quafe Miasmos, Squall infrastructure and Odysseus expedition cargo. On Orca, inspect personal Drone Bay, Fuel Bay, Mining Hold, Fleet Hangar and Ship Maintenance Bay. Fleet sharing, refitting and drone operations remain unavailable. Check both permitted and incompatible cargo, an over-capacity request, unloading at another station and reconnect persistence.

Use a barge with normal fitting budgets for both ordinary strip miners. Compare Covetor's cycle/cost/range bonuses and Retriever's hold/yield against actual collected ore. Fit two miners without turret hardpoints, and reject a third or fitting one to a corvette. Ice harvesters, modulated crystals and deep-core profiles remain unavailable. Fly a freighter and yacht, including a hull with no weapon fittings.

For assembled storage, test Nestor's 5,000 m³ maintenance bay with a Leopard, Bowhead's skill-adjusted bay and a battleship's single-frigate escape bay. Also carry an assembled frigate in Orca's personal fleet hangar. Keep fitted equipment, charges, permitted consumables/subsystems and drones; unload other cargo and nested ships first. Permitted ammunition may remain in a specialized ammunition hold. Verify the carried hull's name and read-only contents, reject boarding while held, haul and unload it, then board and inspect the preserved fit, ammunition and damage. The frigate escape bay counts one qualified ship, rather than cubic metres; automatic escape on loss is a deferred role. The native access buttons initialize closed, and enabling shared access is rejected. Keep native observations separate from automated restart, loss, recovery and expiry cases in the protocol record.

The native run flew Hoarder, Orca, Covetor, Retriever, Charon, Squall and Victorieux Luxury Yacht. Inventory verified the specialized cargo examples above, plus Metamorphosis's Mobile Depot Hold. Covetor and Retriever collected catalog-adjusted ore; Squall fired its reduced-cost launcher and retained its magazine after docking. Orca hauled Quafe and an assembled fitted Rifter to another station, unloaded both, and recovered an assembled shuttle from an explicitly seeded owned NPC wreck. Host/client restarts retained the Orca's stored ship and holds and, separately, Covetor's miners and ore. Actual player carrier loss and expiry cleanup have automated coverage only. The normal world was unchanged.

Undock clearance includes the station and hull radii. A course pointing through a station can still be blocked: stop and manually orbit clear before warping. The milestone does not add automatic obstacle avoidance. Fleet/corporation access, combat drones/fighters, automatic frigate escape, industrial cores and cloaking remain traced as deferred roles in the per-hull audit. Stop after milestone 4; do not begin capital acceptance.
