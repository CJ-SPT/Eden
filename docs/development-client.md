# Windows client setup

Use a shared EVE installation containing the sibling directories `tq` and `ResFiles`. Choose a separate, empty destination with room for both directories. The example paths below can be changed.

```powershell
.\tools\DevelopmentClient.ps1 -PrepareFrom 'E:\Eve' -DevelopmentRoot 'E:\EdenDev\3662168'
```

Preparation copies the client files and resources. It checks SHA-256 hashes for the executable, Blue library, script archive, and startup configuration. Repeat the command to resume an interrupted copy or refresh the staged files. The script marks the client ready after both copies and the final hash checks succeed.

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

The pilot card shows **Eden Pilot**, **Ibis**, and **Jita**. Click it to enter the Caldari Navy Assembly Plant. Open Inventory from the left sidebar and check the active ship. The server reports the selected pilot, station and ship item ID. For a restart check, close the client, stop and restart Eden, and log in again. The active ship and the logged item ID should match the first run. Eden stores the world in `%LOCALAPPDATA%\Eden\world.sqlite`. A single authenticated pilot can be connected at once.

After the source installation finishes downloading, run the preparation command again with the staged client closed. The ready marker checks the pinned binaries and successful copy. The main resource index can still have missing assets if the source was mid-download.

## Fly the Ibis

1. Select **Eden Pilot** and click **Undock** in the station panel. Wait for the ship HUD and the **Jita Trade Hub** entry in Overview.
2. Double-click open space. Watch the speed readout and the distance to the station change.
3. Right-click the Ibis and choose **Stop My Ship**. Check that the HUD reads **0.0 m/s** and the station range holds steady.
4. To check space persistence, note the range, close the client, restart Eden, and launch the client with `-Login`. Select the pilot and compare the range.
5. Select the station in Overview and click **Dock** in Selected Item. From outside docking range, allow time for the approach. Once inside, open Inventory and check **Ibis (Ibis)**.
6. Restart Eden and the client again. Select **Eden Pilot** and verify the station label, ship and active inventory entry match the previous session.
