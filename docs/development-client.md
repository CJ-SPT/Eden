# Windows client setup

Use a shared EVE installation containing the sibling directories `tq` and `ResFiles`. Choose a separate, empty destination with room for both directories. The example paths below can be changed.

```powershell
.\tools\DevelopmentClient.ps1 -PrepareFrom 'E:\Eve' -DevelopmentRoot 'E:\EdenDev\3662168'
```

Preparation copies the client files and resources. It checks SHA-256 hashes for the executable, Blue library, script archive, and startup configuration. Repeat the command to resume an interrupted copy or refresh the staged files. The script marks the client ready after both copies and the final hash checks succeed.

## Creating and recreating the databases

From the repository root, run `.\tools\Setup-Catalog.ps1 -DevelopmentRoot 'E:\EdenDev\3662168'` after staging the client. Use 64-bit Windows Python 3.11 or later with the standard library, PowerShell 7, the pinned .NET SDK, and at least 8 GB free for the catalog, temporary records, and an existing installation. The first run downloads the [CCP build 3662168 export](https://developers.eveonline.com/static-data/tranquility/eve-online-static-data-3662168-jsonl.zip). To rebuild offline, reuse the cached export under `%LOCALAPPDATA%\Eden\catalog-input` or pass `-Archive` with a local copy.

Stop the server before rebuilding. Setup prints source counts and verifies typed reads before printing `Activated` and the installed path. An interrupted import or checksum error leaves the previous catalog available. Restore the matching input files and rerun the same command.

Start the server with `dotnet run --no-build --project src/Eden.Server`. The first startup creates `%LOCALAPPDATA%\Eden\world.sqlite` with Eden Pilot and Eden Wing. An existing save at versions 1 through 5 migrates in a transaction. To back up progress, stop the host and copy the save. To start fresh, move the stopped host's save to a backup location and start the host again. To restore progress, restore the backed-up save while the host is stopped.

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

## Fly the Ibis

1. Select **Eden Pilot** and click **Undock** in the station panel. Wait for the ship HUD and the **Jita Trade Hub** entry in Overview.
2. Double-click open space. Watch the speed readout and the distance to the station change.
3. Right-click the Ibis and choose **Stop My Ship**. Check that the HUD reads **0.0 m/s** and the station range holds steady.
4. To check space persistence, note the range, close the client, restart Eden, and launch the client with `-Login`. Select the pilot and compare the range.
5. Select the station in Overview and click **Dock** in Selected Item. From outside docking range, allow time for the approach. Once inside, open Inventory and check **Ibis (Ibis)**.
6. Restart Eden and the client again. Select **Eden Pilot** and verify the station label, ship and active inventory entry match the previous session.

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

## Warp within Jita and verify braking

Start both clients in space. The **General** Overview shows stations and stargates. Use **All** to include planets and moons.

1. Select a distant object such as the Perimeter stargate or Jita IV. Use **Warp to Within** in Selected Item. Watch the Ibis turn and gain speed, then watch the tunnel and the range to the destination during **Warp Drive Active**.
2. Keep the second pilot at the departure point until the first ship disappears from Overview. Warp the second pilot to the same object at the same range. Check for the first Ibis nearby on arrival.
3. Fly into open space away from a station or planet. Once the Ibis has accelerated, use **Stop My Ship** (Ctrl+Space). Watch the speed fall and the range continue changing until the ship settles at **0.0 m/s**.
4. To return home, warp to **Jita IV - Moon 4 - Caldari Navy Assembly Plant** and select **Dock** inside docking range.

For the celestial landing check, warp to zero at **Jita V**, then at **Jita V – Moon 16**. Selected Item should settle near 30,342 km and 5,164 km respectively. Allow a few kilometres for the landing lane and braking. Have the second pilot arrive from another direction and check that both ships share the same area.

For tunnel entry, use a long leg such as the Jita Trade Hub to the Perimeter gate. Watch for **Establishing Warp Vector** while the Ibis turns, then **Warp Drive Active** and the tunnel as it accelerates. Leave the second pilot at the destination and watch the arriving Ibis in Overview.
