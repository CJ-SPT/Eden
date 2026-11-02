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
