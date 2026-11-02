# Startup protocol and milestones

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

The client uses AES-256-CBC with per-packet padding. Challenge hashes cover a Blue-marshaled one-element tuple. The signed client expression is fixed to an empty dictionary. The outer MachoNet envelope may be zlib-compressed and contains another Blue stream for service arguments. The built-in string table comes from MIT-licensed Carbon Blue. [Carbon crypto](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/crypto.cpp) · [Carbon license](carbon-license.md)

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

## Attribute-based DI acceptance

**Automated checks.** With SDK 10.0.403, restore succeeded, the build produced zero warnings and errors, all 32 tests passed, and formatting verification passed. The production registrations share one SQLite world across pilot lookup and position writes and isolate sessions between connections. Loopback and packet tests cover admission after failed login, bound inventory calls, successful None replies, unsupported calls, and the order of flight notifications.

**Staged Windows client, build 3662168.** Selecting Eden Pilot and opening Inventory showed **Ibis (Ibis)**. After undocking, Overview listed Jita Trade Hub at 453 m. Double-clicking open space accelerated the ship to an observed 267 m/s. **Stop My Ship** brought the HUD to 0.0 m/s at 924 m. Restarting the client and server restored the stopped ship at 924 m. Selecting **Dock** on the station returned the Ibis to the Caldari Navy Assembly Plant, where Inventory again showed **Ibis (Ibis)**. After the second restart, the host loaded pilot `90000001` at station `60003760` in ship `1000000000001`, and the client showed the same active Ibis in Inventory.

## Two pilots in Jita, November 2, 2026

World version 5 adds a second local account, Eden Wing, with its own pilot and Ibis. The signed ticket identifies the account. Both accounts can connect concurrently, and a second session for the same account is rejected.

Each client receives its own ship as the ballpark ego. The initial snapshot includes other connected ships in the same system. Once the observer has bound the ballpark, Eden sends `AddBalls` when another ship appears and `RemoveBall` when it docks or disconnects. Movement and stop commands use the observing client's simulation tick.

**Live observations on earlier revisions.** Both staged clients selected their own pilot and undocked in Jita. With Wing stopped, its Overview showed Eden Pilot moving from 1,687 m to 2,144 m. With Pilot stopped, its Overview showed Wing moving from 11 km to 12 km. That run exposed a speed discrepancy near the station hull. After correcting station contact and course updates, Wing observed Pilot at 294 m/s and later at rest 23 km away. Disconnecting Wing removed it from Pilot's Overview, but the displayed station range jumped. That prompted the switch to individual ship arrival and departure messages.

**Automated checks on the committed revision.** Restore and build passed with zero warnings or errors, all 34 tests passed, and formatting verification passed. The flight tests cover peer arrival, course changes in both directions, departure, and stopping at the station surface.

**Maintainer live verification on `64360ce`.** The maintainer confirmed that each pilot saw the other Ibis move and stop in Overview. When one pilot disconnected, only that ship disappeared. The stationary observer's range to Jita Trade Hub stayed steady.

## Build 3662168 game catalog

The import inventory contains **4,913,627 source records across 442 datasets and supporting indexes, schemas, and metadata entries**. The total includes both representations when the client and export describe the same object. The universe projection includes 114 regions, 1,184 constellations, 8,490 systems, 5,210 NPC stations, and 53,299 item types.

| Source family | Entries | Records |
| --- | ---: | ---: |
| CCP JSON Lines export, including build metadata | 102 | 676,965 |
| Client definitions, localization, and loose game-data files | 211 | 3,244,505 |
| Client cache indexes | 14 | 55,478 |
| Client map database | 2 | 481,069 |
| Regional planet attribute files | 97 | 409,423 |
| External FSD schema descriptors | 6 | 6 |
| Exoplanet sample light curves | 10 | 46,181 |

Setup verifies the archive and client resource index against pinned SHA-256 digests. It resolves indexed files from the local cache, checks their content hashes, and uses the staged client's loaders for binary definitions. The [source inventory](../tools/catalog/build-3662168.json) records the source, content hash, row count, and ordered key digest for every catalog entry. Models, textures, audio, navigation meshes, and other engine assets remain in the staged installation.

`SourceDataset` identifies each source inside the catalog. `SourceOverlap` counts matching record keys for paired datasets. `SourceDifference` identifies the keys and shared fields with different values. Both representations are available from `SourceRecord`. Client values supply positions, item physics, and gate destinations for gameplay. The export also supplies data absent from the client.

`CatalogDbContext` maps the universe and item relationships and exposes typed views of the decoded datasets. For example, an importer or server service can read a blueprint's manufacturing materials with the following query.

```csharp
using CatalogDbContext catalog = new(catalogPath);
var blueprint = CatalogData.Sde.Blueprints.Get(catalog, 681);
var materials = blueprint.Activities?.Manufacturing?.Materials;
```

The source models are generated from the pinned record shapes. Scalar fields, arrays, nested objects, and language variants retain their types. Heterogeneous source fields use `JsonElement`. To reproduce the model file after verifying the catalog, run `python tools/catalog/generate_models.py "$env:LOCALAPPDATA\Eden\catalog.sqlite" src/Eden.Server/Catalog/CatalogData.g.cs` from the repository root.

World schema version 6 stores each owned ship's catalog type and home station by ID. Opening the save verifies its ship types, stations, and pilot corporations. The protocol receives a pilot snapshot with flight and hull attributes, the station designation, and the saved location. The host creates a short-lived EF context for each save operation.

### Catalog acceptance, November 2, 2026

**Automated checks.** Restore, build, and formatting verification passed with zero build warnings or errors. All 41 .NET tests and all three catalog setup tests passed. The documented setup command built and activated the local catalog after its verifier read all 4,913,627 records through their typed mappings.

**Live client.** Eden Pilot and Eden Wing each saw the other Ibis in Overview. Pilot docked at the Caldari Navy Assembly Plant, where Inventory showed **Ibis (Ibis)** with 125 m³ capacity. After undocking, Overview showed the station at 453 m. The maintainer confirmed the final restart check with Pilot docked and Wing in space.
