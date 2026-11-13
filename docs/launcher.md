# Desktop launcher hosting

[Documentation index](README.md) / [Windows client setup](development-client.md) / [Docker deployment](docker.md)

The separate [Eden Launcher](https://github.com/CJ-SPT/Eden-Launcher) uses API v1 to create accounts and launch build 3662168. The server runs separately. Defaults are game TCP 26000, public gateway HTTP/2 26001, and launcher HTTP API 26002, all bound to loopback.

## Contents

- [Existing accounts](#existing-accounts)
- [Developer accounts](#developer-accounts)
- [Remote hosting](#remote-hosting)
- [CA certificate downloads](#ca-certificate-downloads)
- [Authentication](#authentication)
- [Native chat trust](#native-chat-trust)

## Existing accounts

Run `dotnet run --project src/Eden.Server -- --set-account-password Eden` in an interactive terminal. For the second account, use `--set-account-password 'Eden Wing'`. Passwords are entered twice without echo. No default passwords are assigned. Password changes revoke launcher sessions and preserve account IDs, characters, possessions and progress.

## Developer accounts

Set `EDEN_DEVELOPER_INVITE_CODE` in the server environment. Leave it unset to disable developer registration. In launcher account creation, select Developer account and enter the invitation. Standard accounts cannot supply arbitrary role bits.

Developer accounts receive PLAYER, WORLDMOD, PROGRAMMER, QA, GMH and GML. These enable Insider, QA/programmer tools and GM/WM menu entry points. Missing slash commands and privileged operations remain unsupported; exposing controls does not implement their server actions.

## Remote hosting

Pass `--config C:\Eden\server.json` to the server, optionally alongside `--world`. The configuration shape is:

```json
{
  "ServerId": "my-eden-server",
  "Name": "My Eden Server",
  "ListenAddress": "0.0.0.0",
  "GamePort": 26000,
  "GatewayPort": 26001,
  "ApiPort": 26002,
  "GameAddress": "eden.example.com:26000",
  "GatewayAddress": "https://eden.example.com:26001",
  "CertificatePath": "C:\\Eden\\certificates\\server.pfx",
  "DevelopmentLogin": false
}
```

Supply a PFX password through `EDEN_CERTIFICATE_PASSWORD` if needed. Certificate subject alternative names must match the advertised API/gateway host. Configure the host/network to allow the three selected ports. Enter `https://eden.example.com:26002` in the launcher.

For a private CA, select its public certificate in the launcher server profile. Existing setups can still use `EDEN_LAUNCHER_CA_FILE`.
Hostname, chain, validity and server-authentication verification remain required. The launcher relays the native client's cleartext
loopback gateway connection over HTTP/2 TLS upstream and must remain running while games are open.

### CA certificate downloads

To let players download a private CA, add `"CertificateAuthorityPath": "/config/ca.cer"` to the hosting configuration and put the
public CA certificate at that path. The file can be DER or PEM. Keep `CertificatePath` pointing to the server's separate PFX.
Restart Eden and get the **Launcher CA SHA-256 fingerprint** from its startup log. Share the fingerprint with players through a
channel they already trust.

In Launcher Settings, players enter the HTTPS API address, choose **Download CA**, paste the fingerprint they got from the
operator, and choose **Verify and connect**. The launcher checks the fingerprint and validates the HTTPS API with the CA before
saving it to the selected server profile. To rotate the CA, update the hosting files, restart Eden, and share the new fingerprint.

When configured, the launcher API serves the public CA in DER form at `GET /api/v1/ca-certificate` with media type
`application/pkix-cert`. The request is anonymous and needs no existing launcher session. Fingerprints are SHA-256 of the DER
certificate, even when the server reads it from a PEM file.

## Authentication

API v1 provides metadata, registration, sign-in, refresh, logout and launch tickets. Access tokens last 15 minutes; optional remembered sessions rotate and expire after 30 days. Refresh replay revokes the token family. Game tickets are single-use with a 180-second lifetime. Credentials and token hashes use additive Launcher tables in the world database, independent of gameplay migration numbering.

The password hasher uses ASP.NET Core's versioned format with 210,000 iterations. Passwords contain 6–128 characters. Usernames contain 3–32 ASCII letters, digits, spaces, dots, underscores or hyphens and are unique without case distinctions. New standard and developer accounts are Omega and have three empty character slots. The server assigns the clone grade during registration; existing launcher builds use this default without an update. Registration does not change existing accounts' clone grades.

API responses disable caching. Registration and authentication are rate limited. Manifest signing validates the exact pinned prefix and permits the chat bundle and active client package hashes to vary. Windows uses the local Windows key store. On Linux, Eden generates a process-local signing key and embeds the public verification key in each signed manifest. Legacy development tickets require Windows and explicit `--development-login` on the host and provisioning command, with loopback-only hosting.

## Native chat trust

The server also listens on direct-TLS TCP **5222**, fixed by client build 3662168. Keep this port distinct from the configured game, gateway and API ports and expose it on remote hosts. The API server descriptor adds optional `ChatCertificatePem`; the companion launcher installs that public root only into the prepared client's `tq/bin64/packages/certifi/cacert.pem`. Existing certificate roots are preserved, repeated launches do not duplicate the root, and servers without the metadata remain compatible. The chat private key stays in `chat-tls.pfx` beside the world save. Keep that file private and out of version control. No OS trust-store installation or TLS-disable option is required.

Because this certificate bundle is manifest-covered, the launcher sends its prospective SHA-256 with each launch request. The server signs that one updated file entry; every other build fingerprint stays fixed. Both Play and launch groups verify the matching signed manifest before changing files or starting the client. Update/restart both components together. A prepared copy affected by the earlier chat trust bug is repaired on its next corrected launch.

Hosting options MaximumActiveSessions, MaximumPendingLogins and MaximumPendingLoginsPerAddress default to 8, 16 and 4. All must be positive, and the per-address allowance must not exceed the global pending allowance. Loopback may use every pending slot. Incomplete native frames and native/chat writes expire after 30 seconds; an idle authenticated reader has no silence deadline.

Expired launch tickets and expired/revoked launcher session families are removed in bounded batches at startup and every 15 minutes. Active remembered sessions retain refresh replay detection. Large backlogs drain over multiple intervals. Cleanup failures are visible in the console and retain the normal checkpoint retry behavior.
