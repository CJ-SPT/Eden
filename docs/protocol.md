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

1. **Station hangar.** Import a checksummed subset of the matching static data. Persist one pilot, owned ship, station and location in SQLite. Verify the real client loads the hangar and finds the same ship after restart. [EVE static data formats](https://developers.eveonline.com/docs/services/static-data/)
2. **First flight.** Add undocking, a nearby-object snapshot, movement, stopping and docking. Validate motion and state updates against the matching Destiny revision. Verify that a disconnect or restart preserves a consistent ship location. [Build-matched Destiny tests](https://github.com/carbonengine/destiny/tree/27ee40b38a563d29054b32f64c3ebc87fabaec1f/python/destiny/test/ballpark)

## Encrypted local authentication

The development account uses the client's SSO launch path. After its version reply the client sends a VIP-key request and a pair of RSA-encrypted AES parameters. Eden acknowledges key negotiation, decrypts the local ticket with the session cipher, and validates its signature and expiry. Each side proves receipt of the other's random challenge before the server issues the account session. Two fresh client processes reported `LLV Succeeded` and sent the authenticated `machoNet.GetServiceInfo` call.

The client uses AES-256-CBC with per-packet padding. Challenge hashes cover a Blue-marshaled one-element tuple. The signed client expression is fixed to an empty dictionary. The outer MachoNet envelope may be zlib-compressed and contains another Blue stream for service arguments. The built-in string table comes from MIT-licensed Carbon Blue. [Carbon crypto](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/crypto.cpp) · [Carbon license](carbon-license.md)

## Character selection

After authentication Eden returns service information and an initial player session. The client requests `charUnboundMgr.GetCharacterSelectionData`, primes its owner cache, then builds the pilot card. The seeded roster contains one player account and one pilot with ID 90000001, an Ibis, and the Caldari Navy Assembly Plant at Jita IV – Moon 4. The portrait uses the client silhouette.

Selecting the card checks the single-login policy, resolves the population service and its load-slot request, checks character lock status, and sends `SelectCharacterID`. Eden validates ownership and records the selected ID for the lifetime of the connection. Unsupported service calls receive a MachoNet error. The next station milestone supplies the world-session transition and inventory.

### Live verification

On November 1, 2026, the staged build 3662168 client displayed the account's pilot name, ship, and Jita station. A click on the card sent the selection request, and the C# host accepted character 90000001. A second login to the same running host fetched the same pilot in a fresh session. The character-selection tests cover the roster, valid and invalid selections, and unsupported methods.
