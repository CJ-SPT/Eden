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

## Next milestones

**First flight.** Add undocking, a nearby-object snapshot, movement, stopping and docking. Validate motion and state updates against the matching Destiny revision. Verify that a disconnect or restart preserves a consistent ship location. [Build-matched Destiny tests](https://github.com/carbonengine/destiny/tree/27ee40b38a563d29054b32f64c3ebc87fabaec1f/python/destiny/test/ballpark)

## Encrypted local authentication

The development account uses the client's SSO launch path. After its version reply the client sends a VIP-key request and a pair of RSA-encrypted AES parameters. Eden acknowledges key negotiation, decrypts the local ticket with the session cipher, and validates its signature and expiry. Each side proves receipt of the other's random challenge before the server issues the account session. Two fresh client processes reported `LLV Succeeded` and sent the authenticated `machoNet.GetServiceInfo` call.

The client uses AES-256-CBC with per-packet padding. Challenge hashes cover a Blue-marshaled one-element tuple. The signed client expression is fixed to an empty dictionary. The outer MachoNet envelope may be zlib-compressed and contains another Blue stream for service arguments. The built-in string table comes from MIT-licensed Carbon Blue. [Carbon crypto](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/crypto.cpp) Â· [Carbon license](carbon-license.md)

## Character selection

After authentication Eden returns service information and an initial player session. The client requests `charUnboundMgr.GetCharacterSelectionData`, primes its owner cache, then builds the pilot card. The seeded roster contains one player account and one pilot with ID 90000001, an Ibis, and the Caldari Navy Assembly Plant at Jita IV â€“ Moon 4. The portrait uses the client silhouette.

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
