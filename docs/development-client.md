# Windows client setup

Use a shared EVE installation containing the sibling directories `tq` and `ResFiles`. Choose a separate, empty destination with room for both directories. The example paths below can be changed.

```powershell
.\tools\DevelopmentClient.ps1 -PrepareFrom 'E:\Eve' -DevelopmentRoot 'E:\EdenDev\3662168'
```

Preparation copies the client files and resources. It checks SHA-256 hashes for the executable, Blue library, script archive, and startup configuration. Repeat the command to resume an interrupted copy or refresh the staged files. The script marks the client ready after both copies and the final hash checks succeed.

## Creating and recreating the databases

From the repository root, run `.\tools\Setup-Catalog.ps1 -DevelopmentRoot 'E:\EdenDev\3662168'` after staging the client. Use 64-bit Windows Python 3.11 or later with the standard library, PowerShell 7, the pinned .NET SDK, and at least 8 GB free for the catalog, temporary records, and an existing installation. The first run downloads the [CCP build 3662168 export](https://developers.eveonline.com/static-data/tranquility/eve-online-static-data-3662168-jsonl.zip). To rebuild offline, reuse the cached export under `%LOCALAPPDATA%\Eden\catalog-input` or pass `-Archive` with a local copy.

Stop the server before rebuilding. Setup prints source counts and verifies typed reads before printing `Activated` and the installed path. An interrupted import or checksum error leaves the previous catalog available. Restore the matching input files and rerun the same command.

Start the server with `dotnet run --no-build --project src/Eden.Server`. The first startup creates `%LOCALAPPDATA%\Eden\world.sqlite` with Eden Pilot and Eden Wing. An existing save at versions 1 through 23 migrates to version 24 in a transaction. To back up progress, stop the host and copy the save. To start fresh, move the stopped host's save to a backup location and start the host again. To restore progress, restore the backed-up save while the host is stopped.

To recheck an installed catalog independently, run the following command.

```powershell
dotnet run --no-build --project src/Eden.Server -- --verify-catalog "$env:LOCALAPPDATA\Eden\catalog.sqlite"
```

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

World version 14 adds assembled and online state to owned items without repeating the starter grant. Mining lasers can be fitted or unfitted while docked; other module families, saved fitting templates and in-space online changes remain separate work. Civilian Miner activation on corvettes is described below.

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

Eden seeds finite starter offers at visited market stations. These local prices are emulator policy:

| Item | Pilot sells for | Pilot buys for |
| --- | ---: | ---: |
| Veldspar | 3.00 ISK | — |
| Scordite | 5.00 ISK | — |
| Tritanium | 2.00 ISK | 2.40 ISK |
| Pyerite | 8.00 ISK | 9.60 ISK |
| Civilian Miner | — | 100.00 ISK |
| Miner I | — | 1,000.00 ISK |
| Miner II | — | 5,000.00 ISK |
| Venture | — | 10,000.00 ISK |

Each buyback order starts with demand for one million units. Mineral offers start with 100,000 units; each laser offer starts with 100 and Venture offers start with 10. Orders last one year and depleted stock does not regenerate after a restart. A later policy can add missing offers to a visited station while retaining its existing quantities, prices and expiry dates. No initial ISK grant is added to existing or new pilots.

Sales tax is currently pinned to the staged client's observed **7.5%** runtime flag, reduced by **11% per Accounting level**. Its packaged fallback is 8%; external flag changes are not yet hosted by Eden. Immediate trades have no broker fee. A half-cent sales tax is rounded to cents before crediting proceeds; the native preview can display a net amount one cent higher because it rounds the unrounded net independently. Regional history comes from actual saved executions, grouped by UTC day with volume-weighted average prices. Open **Price History**, then **Show Table**, to compare quantities and prices. Before an item's first local trade, the client's fallback average can trigger large price warnings even for these documented prices. This history does not supply the separate adjusted prices used for reprocessing fees. Reopen market details or transaction panels to refresh their displayed rows. Trading supports personal immediate orders at the docked station. Player listings and corporation/remote trading remain separate work.

## Upgrade to a Venture

1. Dock at a market station with sufficient ISK and Mining Frigate I trained. Select **Eden Pilot** and check the current ship in Inventory. Search Regional Market for `venture`, expand **Ships**, select Venture, and buy one from the current station's offer.
2. Open **Ship Hangar**, right-click the packaged Venture and choose **Assemble Ship**. Confirm a name or use **Later** for the default. Double-click the assembled ship to board it. Check that Inventory names the Venture as active and the previous ship remains in Ship Hangar.
3. Compare the Venture's **50 m³** cargo and **5,000 m³ Mining Hold**. Only asteroid ore can enter the mining hold. Open the previous ship's cargo to check its saved contents, then board it to check its saved fitting. Return to the Venture and fit an online mining laser from Item Hangar using **Fit to Active Ship**.
4. Undock, warp to a Perimeter belt, lock a nearby asteroid and activate the laser. Ore should accumulate in Mining Hold while ordinary cargo remains separate. With Mining III and Mining Frigate I, a noncritical full Miner I cycle yields 241 Veldspar; a civilian laser yields 144 per full cycle. The calculation floors whole ore units after applying yield bonuses.
5. Dock and drag ore from Mining Hold into Item Hangar to sell it, or reprocess directly from the hold with **Item Hangar** selected for output. The pinned client does not offer Sell This Item from Mining Hold. Minerals belong in ordinary cargo or Item Hangar. Restart the host and client and compare ship names, active hull, fitting, hold contents and wallet.

Ship switching preserves each hull's items and capacitor. The server requires ownership, an assembled hull in the same station, a docked pilot and the catalog's required skills. Inventory bindings retain the ship ID they were opened for; activate a ship before changing its contents. Venture assembly is the first supported purchased hull. Repackaging, assembled-ship sales and boarding in space remain unavailable.

## Board a recovery corvette

1. Dock while aboard a supported ship other than a corvette, such as the Venture. Select **Eden Pilot**, open **Inventory**, and note the active hull and its cargo or Mining Hold contents.
2. Press **Board my Corvette** below Undock in the station panel. Confirm the native prompt to leave the active ship. Inventory should show the race's corvette as active and retain the previous ship in **Ship Hangar**. The hangar model should also change.
3. Open Fitting with the **circled ship** button. Check that a **Civilian Miner** and **Civilian Gatling Railgun** are fitted and online. The recovery kit costs no ISK and grants no skills. It uses the pilot's race hull, but currently supplies the same civilian railgun to every race.
4. Board the previous ship again and compare its cargo, mining hold and fitting. Repeat **Board my Corvette**: Eden should reuse the ready recovery hull without adding another kit. Restart both host and client and verify the active hull, fitted modules, previous ship and wallet remain.

Eden reuses a corvette at the current station only when its armor and structure are undamaged and both recovery modules occupy their expected online high slots. It does not repair, refill or strip another ship. Otherwise it creates a new equipped hull in one world transaction. Requests require the current ship, station, ownership and catalog skills. Repeated or competing requests for the same previous hull cannot create duplicate kits. The button is disabled while already aboard a corvette. This is an explicit local recovery policy. Recovery itself needs no schema change beyond version 20; version 21 adds NPC retaliation and saved ship losses.

## Check belt NPCs

Select **Eden Pilot**, check the active ship in Inventory, undock and warp to **Perimeter II – Asteroid Belt 1**. The local encounter policy places one Guristas Rookie 3,500 m from each Perimeter belt anchor. It should appear as a red NPC in an Overview preset that includes **Minor Threat**. The existing Mining preset also showed it during acceptance. Right-click it and choose **Lock Target**; after the normal scan delay, its portrait and health rings should appear. **Look at** should render its native ship model. **Approach** should move toward it. Restart the host and client at the belt and verify the same NPC appears without a duplicate.

NPCs remain stationary and do not aggress arriving miners. Attacking one within its 3,125 m attack range starts retaliation after 7.5 seconds. Positions and health are saved. The following checks cover firing, bounty payments and capsule recovery.

## Fire a civilian railgun and earn a bounty

1. While docked, select **Eden Pilot** and check the active ship in Inventory. World version 20 grants each existing pilot one **Civilian Gatling Railgun** in the active ship's station Item Hangar; new pilots receive one at their starting station. This is a one-time Eden starter grant, including for other races. It does not grant skills or ISK. The pinned type is unpublished, so use the owned item rather than searching Regional Market for it.
2. Right-click the gun and choose **Fit to Active Ship**. It requires Gunnery I, an available high slot and turret hardpoint, 4 CPU and 6 power grid. Open Fitting with the **circled ship** button and check that it is online. A Venture can carry one mining laser and this gun together.
3. Undock and warp to **Perimeter II – Asteroid Belt 1**. Lock the Guristas Rookie. Stop near its range of 4,800 m plus trained Sharpshooter bonuses, then activate the railgun's HUD button. Use **F2** if it occupies the second high slot. It carries its own civilian charge and consumes capacitor per shot. It fires at cycle start, repeats while the target remains locked, and stops after the current cycle when pressed again.
4. Watch for native combat messages showing the damage amount, target, weapon and hit quality, while the target's shield, armor and structure rings decline. Misses are possible outside optimal range or with angular motion. When the Rookie is destroyed, it disappears from Overview, the gun stops, and the personal wallet increases by **3,000 ISK**. Under **My Wallet → Transactions**, check **Bounty Prizes**, the resulting balance and the Perimeter description.
5. Restart both client and host using the same save. Verify the active hull, fitted gun, bounty balance and journal entry remain. The defeated NPC must not reappear early or pay again on reconnect. After ten minutes a replacement may appear at the same belt with a new identity. Dead rows and the paid bounty remain saved.

Player turret accuracy and shot-quality randomness are Eden's initial model; the client does not expose Tranquility's server RNG. Immediate individual bounty payments and the ten-minute respawn delay are local encounter policy. Fleet splits and bounty tax remain unavailable. Loot, NPC retaliation and player loss are covered below. Other civilian guns, ordinary ammunition and other weapon families remain unavailable. Reopen transaction panels to refresh their rows.

## Loot a defeated Rookie

1. Select **Eden Pilot** and verify the active equipped ship in Inventory. Undock, warp to a Perimeter belt, lock the Rookie and defeat it. Staying outside its 3,125 m retaliation range avoids taking fire.
2. Look for the new **Guristas Rookie Frigate Wreck**. Use the **General** or **All** Overview tab if the Mining preset hides wrecks. The wreck type comes from the NPC's pinned catalog definition.
3. Choose **Open Cargo**. The native controls approach automatically when needed. Transfer requires the active ship to be in the same system, outside warp and within **2,500 m of the wreck's surface**. Only the pilot credited with the kill has loot rights in this first implementation.
4. The wreck contains **100 Tritanium / 1 m³**, an explicit Eden starter loot rule. Drag some or all into the active ship's ordinary cargo, or press **Loot All**. Check the cargo quantity and remaining contents. Capacity failures must leave the untransferred items in the wreck; a partial move can use the quantity picker.
5. The empty wreck should use the native empty-wreck marker. It remains until its two-hour lifetime ends. Restart the host and client on the same save: collected cargo stays collected, and reopening the wreck must not grant another stack. Uncollected contents expire with the wreck. Take the minerals to a station to unload or sell through the existing market.

Migration does not create wrecks or loot for kills from earlier versions. Wreck pickup, bounty and NPC respawn have separate durable identities. Player-loss drops are covered below. Theft, fleet rights, wreck combat, salvaging and randomized NPC loot tables remain separate work.

## Take NPC fire and recover from a loss

Use an isolated copy of the save when testing destruction. The active hull is lost; supported modules and cargo stacks independently have a 50% survival chance. Station hangars, other hulls, wallet and skills are retained.

1. Select **Eden Pilot**, open Inventory and verify the active equipped ship. Undock and warp to a Perimeter belt. Lock its Rookie and use **Keep at Range → 1,000 m**. A nearby Rookie does not attack until fired upon.
2. To leave the NPC alive during the test, right-click the civilian railgun on the HUD and choose **Set Auto-Repeat Off**. Press its shortcut once. After the reaction delay, look for the NPC's incoming beams, red damage amounts naming **Guristas Rookie**, the targeted indicator and declining health rings. The server uses the catalog's damage, tracking, range and resistances.
3. Warp away or dock to end the fight. Reconnecting also clears aggression; it does not simulate missed offline shots. For destruction, leave Inventory open and remain in range until all structure is lost. The old ship should disappear, Inventory should change to **Capsule (Capsule)** with no cargo or modules, and both the NPC portrait lock and its targeted indicator should clear. The capsule must not display the old ship's cargo.
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

1. Dock at a station with a market and fitting service. Select **Eden Pilot**, open Inventory and verify the active ship. The starter market sells a **Shield Operation** skillbook for **5,000 ISK** and **Small Shield Booster I** for **1,000 ISK**. These prices and finite stocks are Eden policy.
2. Buy the skillbook and find it in Item Hangar. Right-click **Inject Skill**. One book should disappear and Shield Operation should appear in the Skill Catalogue at level zero. A stack retains its unused books. Injection grants no SP, and an already injected skill cannot consume another book. Current-ship ordinary cargo also supports injection; remote hangars and specialized holds do not.
3. Add Shield Operation I to the training queue and start it. Training uses the saved Alpha rate and requires Power Grid Management I. Wait for level I; restarting retains the queue and offline progress. Direct skill purchases from the Skill Catalogue are separate work: use the physical book path.
4. Buy the booster. Use **Fit to Active Ship** or the station's circled ship **Fitting** button, then verify it is online in a medium slot. The existing high-slot miner and civilian gun must remain fitted. CPU and power grid include all online supported modules; the booster uses no turret hardpoint.
5. Undock and take shield damage from a Rookie using the encounter steps above. Activate the booster on the HUD. It needs no target lock. Watch the shield ring, capacitor, cycling indicator and native shield effect. The unmodified small booster restores up to 26 HP immediately for 20 capacitor, with a 2-second cycle. At full shields it still spends capacitor. Manual deactivation finishes the paid cycle; insufficient capacitor rejects the next cycle without healing.
6. Warp or dock to end NPC aggression. The shield booster is warp-safe and stops on docking or hull loss. Restart and compare fitting, health and capacitor; no offline boosts are granted. Shield Compensation modifies capacitor cost when trained. Module overheating, ancillary charges and other repair families are separate work.

## Check a rejected gameplay action

Select **Eden Pilot**, open Inventory and verify the active equipped ship. Undock safely outside a station. Right-click an online module on the HUD, choose **Put Offline** and accept the local module prompt. Eden currently requires docking to change online state, so the native **Information** dialog should explain: **Change module online state on your docked active ship.** Dismiss it and check that the module remains online and the ship still responds. This check must preserve the module, cargo and wallet. Capacity errors retain the client's existing quantity-selection flow.

## Check saved ship health

Combat encounters are still being integrated. For an isolated health acceptance fixture, start with a copy of the verified Venture save and let the server migrate it to world version 18 or later. With the client stopped, add a `WorldShipHealth` row for the active Venture with `ShieldCharge=75`, `ArmorDamage=100`, `StructureDamage=100` and `UpdatedAt` set to the current Windows FILETIME. This is test damage, not damage earned through a working encounter. Keep the normal gameplay save separate.

Select **Eden Pilot**, verify **prospector (Venture)** and its saved ore in Inventory, and open Fitting with the circled ship button. Expand Defense to check the trained maximum health values. Undock and hover over the three health rings. On the existing fixture (Mechanics III, Hull Upgrades II and Shield Management II), the client rounds the maximum values to 248 shield, 193 armor and 230 structure; the saved damage leaves 93 armor (48%) and 130 structure (57%). Shield charge should rise over time. Restart both host and client and compare the health rings again. Switching to another hull must retain damage on the original hull; that path also has automated coverage. There is no public damage RPC or repair service in this milestone.

## Check the character sheet

Start a fresh client with `-Login` and select **Eden Pilot**. Open the sheet from the portrait on the left, or use the window restored at login. Under **Character → Home Station**, check the pilot name, birth date, Jita home station, and total SP. Click **Skills → Skill Catalogue**, filter to **My skills**, and select **Spaceship Command**. On a fresh save the Caldari pilot has Spaceship Command III and 384,047 total SP. Reopen the client after restarting the server to compare against the saved pilot.
