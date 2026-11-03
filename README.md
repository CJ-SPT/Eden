# Eden

Eden is a C# server emulator for EVE Online 24.01, Windows build **3662168**. Two local pilots can fly together using separate Windows clients.

## Current status

On the local server at `127.0.0.1:26000`, **Eden Pilot** can undock the Ibis from Jita IV – Moon 4, fly on a heading, stop, and dock at the Caldari Navy Assembly Plant. **Eden Wing** has a separate account and Ibis at the same station. Both pilots can warp to catalog objects within Jita, see each other leave and arrive in Overview, and coast to a stop under hull inertia. Planets and large moons have repeatable warp-in points. Ships align on their actual velocity, coast out of warp, and stop at solid catalog objects during sublight flight. Jita contains 18 stations, seven stargates, eight planets, 33 moons, and one star. Pilots can jump through paired stargates. The Windows client plays the gate transition and loads the destination system. A restart restores each ship in its hangar or at its last saved system and position in space.

Each account has three character slots. Choose **Add Character** to create a pilot in the stock client. New pilots start docked at their school's AIR station with their race's corvette and starting skills. The location panel shows the system, security status, constellation, region, and station.

Pilots can lock and unlock nearby ships using the stock target controls. Locks take time based on scan resolution and the target's signature, obey the hull and pilot's target limit, and clear when a ship leaves range, warps, docks, jumps or disconnects. The other pilot sees the targeted indicator in Overview.

Use **Orbit** or **Keep at Range** on a nearby ship, NPC, asteroid or wreck to control positioning. Orbits account for the two objects' radii and the ship's speed, mass and agility; tight or boosted orbits can settle wider than the requested range. Both controls track moving targets. Stop, a new heading, warp and docking cancel the course. When the target leaves, an orbiting ship continues on its current heading while a Keep at Range follower coasts to a stop. Restart retains position and returns the ship to rest.

Perimeter's three asteroid belts contain saved Veldspar and Scordite fields, including their ore variants. Warp to a belt, lock an asteroid within the fitted mining laser's range, and collect ore in the corvette's cargo. Civilian Miner, Miner I and Miner II use the pinned catalog's cycle, yield, capacitor and skill requirements. The laser stops when its target disappears, the deposit empties or cargo has no room. Ore stacks and remaining deposits survive restarts. Belt locations, ore types and counts come from the pinned catalog; Eden generates and saves individual rock positions and quantities. Fields load and unload as the ship travels.

Pilots receive one Civilian Miner and one Civilian Gatling Railgun in their station's Item Hangar. Drag equipment into the active ship's cargo and back while docked. Owned item stacks and cargo locations survive restarts; the server checks ownership, quantity and the hull's cargo capacity. A stack too large for the hold opens the client's quantity picker so you can load only what fits. Open Fitting with the station panel's circled ship button to fit mining lasers or the civilian railgun, take them offline or online, and unfit them. Drag an already fitted module to another compatible slot to move it, or onto a fitted module in the same rack to exchange their slots. Loaded rounds and paid cycle/reload deadlines stay with their module. Fitted modules and online state survive restarts. The server checks slots, required skills, CPU and power grid. Bring mined ore back to a station and drag it into Item Hangar to unload.

At a station with reprocessing, use the ore's **Reprocess** menu to preview mineral yields and convert complete batches. The server applies the catalog's station efficiency, material recipe and trained reprocessing skills, saves leftover ore, and checks output cargo capacity. Minerals survive restarts. Reprocessing currently supports fixed ore recipes; randomized outputs and equipment recycling remain unavailable. Reprocessing still uses the pinned client's missing-price fallback of zero ISK fees because Eden has no adjusted market-price feed. Other module families and strip/deep-core miners remain to be implemented.

Use the station's **Industry** window to manufacture small Tech I hybrid ammunition and turrets, Tech I mining lasers, shield boosters and afterburners from owned original blueprints and minerals in Item Hangar. One Iron Charge S run consumes **27 Tritanium and 21 Pyerite** and produces **100 rounds** after **4 minutes 48 seconds** with Industry I. Installation consumes materials and reserves the blueprint and a manufacturing slot; deliver the finished job to receive usable ammunition. Deadlines and paid jobs survive restarts. Cancellation releases the blueprint and slot without refunding materials. Industry skills affect time and available slots. A **75mm Gatling Rail I** takes **231 Tritanium and 220 Pyerite**, with **14 minutes 24 seconds** at Industry I; delivered modules can be fitted and used. Installation currently uses the missing-adjusted-price fallback of zero ISK fees. Research, copies, corporation jobs and remote manufacturing remain future work.

Pilots can sell ore and minerals through **Sell This Item** with **Immediate** duration, then use the proceeds to buy starter equipment. ISK balances, sales tax, transactions, NPC demand and stock are saved with the inventory changes. Eden's starter market offers fixed local prices and finite quantities at visited market stations; these are emulator policy, not Tranquility prices. It buys Veldspar, Scordite, Tritanium and Pyerite, and sells minerals, Civilian Miners, Miner I, Miner II, the Venture, Shield Operation skillbooks and Small Shield Booster I. Iron and Antimatter Charge S, Miner I, 75mm Gatling Rail I, Small Shield Booster I and 1MN Afterburner I blueprint originals cost **1,000 ISK each**. Mexallon costs **60 ISK per unit** and Nocxium costs **100 ISK per unit** for recipes that require them. The native Regional Market browser finds stocked equipment and displays history from actual local trades in the region. Immediate personal trades at the docked station are supported. Player listings, corporation trading and remote trading remain separate work.

Mining income can buy a **Venture** for 10,000 ISK at the starter market. Assemble it in Ship Hangar, name it, and board it while docked. Each ship retains its own cargo, fitting and capacitor. The Venture has 50 m³ cargo and a separate 5,000 m³ mining hold; mined ore enters that hold and can be unloaded or reprocessed at a station. Unload ore to Item Hangar to use the native Sell This Item menu. Ore yield includes the hull's 100% role bonus and 5% per trained Mining Frigate level. Assembly currently supports the Venture; ship switching supports owned, assembled hulls in the current station. Repackaging, trading assembled ships and boarding in space remain future work.

The station's **Board my Corvette** button boards a free recovery ship for the pilot's race, fitted with an online Civilian Miner and Civilian Gatling Railgun. Eden reuses an undamaged corvette with that equipment at the current station, or creates a new kit. The previous ship, cargo and wallet stay intact. This is Eden's recovery policy; the same civilian gun currently serves every race.

The Character Sheet opens for Eden Pilot and shows the Jita home station. Each local pilot has a saved account profile, pilot identity, and the starting skills for their race from the pinned catalog. Character selection and the Skill Catalogue show 384,047 SP for fresh seeded Caldari pilots. Buy a physical skillbook, right-click **Inject Skill**, then train it through the existing queue. Injection consumes one book and starts at zero SP; training checks prerequisites and the local Alpha limits. Queue progress, pause and resume survive restarts. The local gateway answers the personal plan list and tracked-plan lookup. See the [character-sheet service roadmap](docs/protocol.md#character-sheet-service-roadmap).

One saved Guristas Rookie guards each Perimeter belt. Fit an online **Civilian Gatling Railgun** or supported small Tech I hybrid turret, lock the NPC and activate the gun to fire repeatedly. Range, tracking, resistances, trained gunnery skills and capacitor affect the shots. Native combat messages show each hit or miss and the applied damage. Health rings update through shield, armor and structure; destroying a Rookie credits its **3,000 ISK** bounty and adds a personal wallet journal entry. Defeated NPCs remain saved and receive a replacement with a new identity after ten minutes. An idle Rookie detects the closest eligible connected ship within **20 km**, waits a **7.5-second** reaction delay, then approaches at the catalog's **220 m/s** cruise speed. Inside **1,875 m** it starts circling at **1,250 m** surface range; pulling beyond **3,125 m** switches it back to pursuit. A player attack can start retaliation before detection. The NPC keeps its chosen target, fires within **3,125 m** and pursues outside weapon range. Warping, docking, disconnecting or leaving the encounter's **50 km belt leash** ends the fight; the same damaged NPC returns toward its saved belt anchor before detecting again. Incoming beams, red damage messages and saved health show its attacks. Capsules are exempt, and reconnecting can start a fresh reaction delay without offline shots. Detection range, target selection and leash are Eden policy. NPC obstacle avoidance remains future work. The one-time civilian railgun grant is Eden starter policy; it is not sold through the native market in this build.

Buy a **75mm Gatling Rail I** for **1,000 ISK** and **Antimatter Charge S** or **Iron Charge S** for **1 ISK per round** at the starter market. Fit the gun while docked and load it from current-station hangar or ordinary cargo. Its magazine holds **200 rounds**, with a **5-second reload**. Firing spends real ammunition, including misses. Antimatter trades range for damage; Iron provides longer range and reduced capacitor cost. Docked and in-space weapon readouts include earned gunnery bonuses. Unload rounds to cargo or the docked hangar; unfitting returns the remaining rounds with the gun. Magazines and reload deadlines persist through reconnects, and loaded rounds participate in the hull's wreck/drop accounting. The civilian gun continues to use its built-in charge. Other weapon families, Tech II hybrid ammunition and grouped weapon banks remain separate work.

Defeated Rookies leave a saved catalog wreck with **100 Tritanium**, an explicit starter loot rule for Eden. Use **Open Cargo** to approach within 2,500 m, then move the contents into the active ship’s cargo or press **Loot All**. The server checks loot rights, range and capacity. The emptied wreck remains until its two-hour expiry; remaining contents expire with it. Pickup, empty state and cargo survive restarts. Salvaging, theft and fleet loot sharing remain future work.

Losing all structure destroys the active hull and places the pilot in a capsule beside its catalog wreck. Each fitted module or cargo/mining-hold stack has an independent **50% chance to survive**; survivors retain their identities as offline wreck contents. The saved loss record retains the original inventory and the drop result. Wallet, skills, hangar contents and other ships remain intact. NPCs do not shoot capsules in this initial encounter policy. Fly the pod back to a station, use **Board my Corvette**, and return to recover surviving contents before the wreck's two-hour expiry. Insurance, native killmails, rigs and seasonal drop modifiers remain separate work.

Ship health is saved per hull, shields recharge across restarts, and the client receives existing armor and structure damage. Mechanics, Hull Upgrades, Shield Management and Shield Operation affect health and recharge. The starter market sells **Shield Operation** skillbooks for **5,000 ISK** and a **Small Shield Booster I** for **1,000 ISK**. Train Shield Operation I, fit the booster in a medium slot, and activate it in space to exchange capacitor for shield charge. The catalog's Small Shield Booster I restores up to **26 HP** for **20 capacitor** every **2 seconds** before trained modifiers. It works during warp, finishes its paid cycle on manual stop, and stops on docking, loss or insufficient capacitor. Shields and cooldowns are saved; reconnecting does not simulate missed boosts.

Buy a **1MN Afterburner I** for **1,000 ISK**, fit it in a medium slot and activate it in space to travel faster while spending capacitor. Navigation increases the ship's normal speed; Afterburner, Fuel Conservation and Acceleration Control apply their earned catalog bonuses. The propulsion bonus accounts for the module's added mass and thrust. Manual shutdown finishes the paid cycle, and repeated cycles retain one speed bonus. Warp prevents another cycle; docking, hull loss and disconnecting remove the effect. Saved capacitor and cycle deadlines prevent reconnecting for a free activation. Microwarpdrives and overheating remain separate work.

Rejected gameplay actions show their reason in the client's native **Information** dialog, including missing skills, insufficient ISK and unavailable fitting actions.

At a station with **Repair Facilities**, quote owned assembled ships and pay for full or partial armor/hull repairs. Eden charges **1 ISK per rounded damage point**; partial payments repair the same fraction of each selected hull's damage. Payments and wallet journal entries survive restarts. Repairs preserve shields, capacitor, cargo and fitting. Module heat/damage and structure repair services remain future work.

## Local game data

Eden uses Entity Framework Core 10 with two SQLite files under `%LOCALAPPDATA%\Eden`. The read-only `catalog.sqlite` holds build 3662168 game definitions. The writable `world.sqlite` holds accounts, pilot profiles and trained skill points, owned ships, their positions and health, item stacks in cargo or station hangars, asteroid fields, wallets, market orders, trade records and manufacturing jobs. Both files are generated locally.

On Windows, install 64-bit Python 3.11 or later and the pinned .NET SDK. Stage the matching client and resource cache, then run the setup command from the repository root.

```powershell
.\tools\DevelopmentClient.ps1 -PrepareFrom 'E:\Eve' -DevelopmentRoot 'E:\EdenDev\3662168'
.\tools\Setup-Catalog.ps1 -DevelopmentRoot 'E:\EdenDev\3662168'
```

Setup downloads and verifies CCP's exact-build export on the first run, decodes the staged client, and checks the replacement catalog before installing it. Normal play uses the installed catalog and save offline. See the [Windows client guide](docs/development-client.md#creating-and-recreating-the-databases) for offline setup, save migration, and verification.

## Build and run

Install the .NET SDK version in `global.json` and Python 3.11 or later for the policy checks. Run these commands from the repository root.

```powershell
dotnet tool restore
dotnet restore
dotnet build --no-restore
dotnet test --no-build
python tests/Eden.Protocol.Tests/Persistence/catalog_setup_test.py
python tests/Eden.Protocol.Tests/Formatting/formatting_policy_test.py
.\tools\Format.ps1 -Check
dotnet run --no-build --project src/Eden.Server
```

The tests use fixtures and loopback sockets. To try the real client, follow the [Windows client setup](docs/development-client.md). The [protocol notes](docs/protocol.md) describe the verified exchange and flight and two-pilot acceptance results.

## Formatting

Run `.\tools\Format.ps1` to apply formatting after restoring tools and the solution. Run `.\tools\Format.ps1 -Check` to verify without editing files. Both commands run .NET style rules, CSharpier, then strict line-width validation. Verification also runs in the Windows GitHub Actions workflow on pull requests and pushes to `main`.

The root `.editorconfig` is the source of truth. CSharpier 1.3.0 is pinned in the local tool manifest and owns layout; .NET analyzers enforce required braces, file-scoped namespaces and import placement. Do not run the full `dotnet format` command, whose whitespace formatter can disagree with CSharpier. Use `dotnet format style --no-restore --severity warn` for style-only cleanup.

Follow Microsoft's C# conventions with four spaces, Allman braces and a maximum line width of **140 characters**, including indentation. Expand nonempty executable brace bodies. Auto-properties, empty bodies, initializers, patterns and expression-bodied members may remain compact. Prefer expression-bodied trivial accessors because CSharpier otherwise places their statements beside their braces. Project/configuration files use two spaces. Preserve existing line endings.

The strict width check covers authored C#, `.csproj`, `.props`, `.targets`, `.slnx`, root `global.json`, JSON under `.config` and `.vscode`, and `appsettings*.json`/`launchSettings.json`. Generated C# (including the catalog models), SDK outputs, build artifacts and runtime/data JSON are excluded. Documentation and development scripts retain the basic UTF-8, final-newline and trailing-whitespace rules without a width limit.

Wrap long code first. Split literals only when their runtime value remains identical. For content that cannot safely be split, add an entry to `tools/formatting-exceptions.json` with its repository-relative `path` (forward slashes), exact full `line` (including indentation), and a specific `reason`. The checker rejects duplicate or unused exceptions, including exceptions whose content changes or no longer exceeds the limit. The allowlist itself is data and is excluded from width checking.

For format-on-save in Visual Studio, VS Code or Rider, install the CSharpier editor integration and select the repository-local tool. Keep EditorConfig support enabled and avoid running a second whitespace formatter on save. Restore with `dotnet tool restore` after cloning or changing the pinned formatter version.

## Projects and conventions

| Project | Responsibility |
| --- | --- |
| `src/Eden.Gameplay` | Typed gameplay rules, state, commands and persistence interfaces. |
| `src/Eden.Server` | Host the loopback session and own the SQLite world. |
| `src/Eden.Protocol` | Frame and decode messages, negotiate encrypted sessions, and adapt client calls and notifications to typed gameplay operations. |
| `tests/Eden.Protocol.Tests` | Verify wire formats, login, ship movement, docking, and SQLite persistence. |

Known protocol data uses named immutable models: inventory/dogma state, character cards, flight commands, combat notifications, market and industry replies, skill queues and brain effects. Handlers return `PilotCallResult`, distinguishing successful Python None, unsupported calls and errors. Protocol adapters use named properties such as `DogmaAllInfo.ShipState` and `PilotBrainState.ShipEffects`; explicit `BlueModel.ToBlue()` adapters preserve the native client formats. The low-level Blue codec retains its flexible Python value graph. See the [type-boundary audit and acceptance results](docs/protocol.md#named-protocol-models-final-audit).

The server uses Microsoft DI to discover services marked with `[Injectable]` and creates one scope per client connection. The [contributor guide](AGENTS.md#services-and-protocol-calls) explains service lifetimes and how to add a protocol handler.

Use the root EditorConfig and shared build properties for new projects. Follow [Microsoft's C# conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions) and keep production and test projects separate as in [Microsoft's project layout guide](https://learn.microsoft.com/en-us/dotnet/core/tutorials/libraries).

Eden's original source and documentation are available under the [MIT License](LICENSE). Supply the EVE client and its resource cache separately.

## Architecture

Eden.Gameplay owns rules and feature models. Eden.Protocol references Gameplay to translate client requests and results. Eden.Server references both to compose the host and implement catalog and SQLite operations. Gameplay references neither adapter assembly. Combat health, fitting and module modifiers, targeting, mining yield, propulsion, skill planning, character rules and feature persistence contracts use this boundary. Client effect names and notification construction stay in Protocol. Ship health becomes a native damage-state value only in the Protocol adapter. Database transactions remain owned by Server.

Manufacturing illustrates the persistence boundary. The Server store loads the recipe, skills, active jobs and materials inside its existing transaction. Gameplay calculates a typed manufacturing plan and validates the client quote. Server then consumes materials, reserves the blueprint and saves the job in that transaction. Protocol builds the native reply and queues inventory notifications from the resulting changes. Pure calculations can be tested without a database, and storage tests verify transactional behavior.
