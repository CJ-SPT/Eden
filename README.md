# Eden

Eden is a C# server emulator for EVE Online 24.01, Windows build **3662168**. Two local pilots can fly together using separate Windows clients.

## Current status

On the local server at `127.0.0.1:26000`, **Eden Pilot** can undock the Ibis from Jita IV – Moon 4, fly on a heading, stop, and dock at the Caldari Navy Assembly Plant. **Eden Wing** has a separate account and Ibis at the same station. Both pilots can warp to catalog objects within Jita, see each other leave and arrive in Overview, and coast to a stop under hull inertia. Planets and large moons have repeatable warp-in points. Ships align on their actual velocity, coast out of warp, and stop at solid catalog objects during sublight flight. Jita contains 18 stations, seven stargates, eight planets, 33 moons, and one star. Pilots can jump through paired stargates. The Windows client plays the gate transition and loads the destination system. A restart restores each ship in its hangar or at its last saved system and position in space.

Each account has three character slots. Choose **Add Character** to create a pilot in the stock client. New pilots start docked at their school's AIR station with their race's corvette and starting skills. The location panel shows the system, security status, constellation, region, and station.

Pilots can lock and unlock nearby ships using the stock target controls. Locks take time based on scan resolution and the target's signature, obey the hull and pilot's target limit, and clear when a ship leaves range, warps, docks, jumps or disconnects. The other pilot sees the targeted indicator in Overview.

Perimeter's three asteroid belts contain saved Veldspar and Scordite fields, including their ore variants. Warp to a belt, lock an asteroid within the fitted mining laser's range, and collect ore in the corvette's cargo. Civilian Miner, Miner I and Miner II use the pinned catalog's cycle, yield, capacitor and skill requirements. The laser stops when its target disappears, the deposit empties or cargo has no room. Ore stacks and remaining deposits survive restarts. Belt locations, ore types and counts come from the pinned catalog; Eden generates and saves individual rock positions and quantities. Fields load and unload as the ship travels.

Pilots receive one Civilian Miner and one Civilian Gatling Railgun in their station's Item Hangar. Drag equipment into the active ship's cargo and back while docked. Owned item stacks and cargo locations survive restarts; the server checks ownership, quantity and the hull's cargo capacity. A stack too large for the hold opens the client's quantity picker so you can load only what fits. Open Fitting with the station panel's circled ship button to fit mining lasers or the civilian railgun, take them offline or online, and unfit them. Fitted modules and online state survive restarts. The server checks slots, required skills, CPU and power grid. Bring mined ore back to a station and drag it into Item Hangar to unload.

At a station with reprocessing, use the ore's **Reprocess** menu to preview mineral yields and convert complete batches. The server applies the catalog's station efficiency, material recipe and trained reprocessing skills, saves leftover ore, and checks output cargo capacity. Minerals survive restarts. Reprocessing currently supports fixed ore recipes; randomized outputs and equipment recycling remain unavailable. Reprocessing still uses the pinned client's missing-price fallback of zero ISK fees because Eden has no adjusted market-price feed. Other module families and strip/deep-core miners remain to be implemented.

Pilots can sell ore and minerals through **Sell This Item** with **Immediate** duration, then use the proceeds to buy starter equipment. ISK balances, sales tax, transactions, NPC demand and stock are saved with the inventory changes. Eden's starter market offers fixed local prices and finite quantities at visited market stations; these are emulator policy, not Tranquility prices. It buys Veldspar, Scordite, Tritanium and Pyerite, and sells minerals, Civilian Miners, Miner I, Miner II and the Venture. The native Regional Market browser finds stocked equipment and displays history from actual local trades in the region. Immediate personal trades at the docked station are supported. Player listings, corporation trading and remote trading remain separate work.

Mining income can buy a **Venture** for 10,000 ISK at the starter market. Assemble it in Ship Hangar, name it, and board it while docked. Each ship retains its own cargo, fitting and capacitor. The Venture has 50 m³ cargo and a separate 5,000 m³ mining hold; mined ore enters that hold and can be unloaded or reprocessed at a station. Unload ore to Item Hangar to use the native Sell This Item menu. Ore yield includes the hull's 100% role bonus and 5% per trained Mining Frigate level. Assembly currently supports the Venture; ship switching supports owned, assembled hulls in the current station. Repackaging, trading assembled ships and boarding in space remain future work.

The station's **Board my Corvette** button boards a free recovery ship for the pilot's race, fitted with an online Civilian Miner and Civilian Gatling Railgun. Eden reuses an undamaged corvette with that equipment at the current station, or creates a new kit. The previous ship, cargo and wallet stay intact. This is Eden's recovery policy; the same civilian gun currently serves every race.

The Character Sheet opens for Eden Pilot and shows the Jita home station. Each local pilot has a saved account profile, pilot identity, and the starting skills for their race from the pinned catalog. Character selection and the Skill Catalogue show 384,047 SP for the seeded Caldari pilots. Pilots can queue injected skills, pause, resume, and retain training progress across a restart. The local gateway answers the personal plan list and tracked-plan lookup. See the [character-sheet service roadmap](docs/protocol.md#character-sheet-service-roadmap).

One saved Guristas Rookie waits at each Perimeter belt. Fit an online **Civilian Gatling Railgun**, lock the NPC and activate the gun to fire repeatedly. Range, tracking, resistances, trained gunnery skills and capacitor affect the shots. Native combat messages show each hit or miss and the applied damage. Health rings update through shield, armor and structure; destroying a Rookie credits its **3,000 ISK** bounty and adds a personal wallet journal entry. Defeated NPCs remain saved and receive a replacement with a new identity after ten minutes. These stationary NPCs do not yet retaliate. Player ship destruction, capsule escape, ammunition-fed weapons, wrecks and loot remain future work. The one-time civilian railgun grant is Eden starter policy; it is not sold through the native market in this build.

Ship health is saved per hull, shields recharge across restarts, and the client receives existing armor and structure damage. Mechanics, Hull Upgrades, Shield Management and Shield Operation affect health and recharge.

## Local game data

Eden uses Entity Framework Core 10 with two SQLite files under `%LOCALAPPDATA%\Eden`. The read-only `catalog.sqlite` holds build 3662168 game definitions. The writable `world.sqlite` holds accounts, pilot profiles and trained skill points, owned ships, their positions and health, item stacks in cargo or station hangars, asteroid fields, wallets, market orders and trade records. Both files are generated locally.

On Windows, install 64-bit Python 3.11 or later and the pinned .NET SDK. Stage the matching client and resource cache, then run the setup command from the repository root.

```powershell
.\tools\DevelopmentClient.ps1 -PrepareFrom 'E:\Eve' -DevelopmentRoot 'E:\EdenDev\3662168'
.\tools\Setup-Catalog.ps1 -DevelopmentRoot 'E:\EdenDev\3662168'
```

Setup downloads and verifies CCP's exact-build export on the first run, decodes the staged client, and checks the replacement catalog before installing it. Normal play uses the installed catalog and save offline. See the [Windows client guide](docs/development-client.md#creating-and-recreating-the-databases) for offline setup, save migration, and verification.

## Build and run

Install the .NET SDK version in `global.json`. Run these commands from the repository root.

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes --no-restore
dotnet run --no-build --project src/Eden.Server
```

The tests use fixtures and loopback sockets. To try the real client, follow the [Windows client setup](docs/development-client.md). The [protocol notes](docs/protocol.md) describe the verified exchange and flight and two-pilot acceptance results.

## Projects and conventions

| Project | Responsibility |
| --- | --- |
| `src/Eden.Server` | Host the loopback session and own the SQLite world. |
| `src/Eden.Protocol` | Frame and decode messages, negotiate encrypted sessions, and serve character selection, inventory, and first flight. |
| `tests/Eden.Protocol.Tests` | Verify wire formats, login, ship movement, docking, and SQLite persistence. |

The server uses Microsoft DI to discover services marked with `[Injectable]` and creates one scope per client connection. The [contributor guide](AGENTS.md#services-and-protocol-calls) explains service lifetimes and how to add a protocol handler.

Use the root EditorConfig and shared build properties for new projects. Follow [Microsoft's C# conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions) and keep production and test projects separate as in [Microsoft's project layout guide](https://learn.microsoft.com/en-us/dotnet/core/tutorials/libraries).

Eden's original source and documentation are available under the [MIT License](LICENSE). Supply the EVE client and its resource cache separately.
