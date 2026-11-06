# Desktop launcher hosting

[Documentation index](README.md) / [Windows client setup](development-client.md)

The separate [Eden Launcher](https://github.com/CJ-SPT/Eden-Launcher) uses API v1 to create accounts and launch build 3662168. The server runs separately. Defaults are game TCP 26000, public gateway HTTP/2 26001, and launcher HTTP API 26002, all bound to loopback.

## Contents

- [Existing accounts](#existing-accounts)
- [Developer accounts](#developer-accounts)
- [Remote hosting](#remote-hosting)
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

For a private CA, set `EDEN_LAUNCHER_CA_FILE` to its DER certificate before opening the launcher. Trust is scoped to that process; hostname, chain, validity and server-authentication verification remain required. Windows trust is unchanged. Unencrypted remote API/gateway connections are rejected. The launcher relays the native client's cleartext loopback gateway connection over HTTP/2 TLS upstream and must remain running while games are open.

## Authentication

API v1 provides metadata, registration, sign-in, refresh, logout and launch tickets. Access tokens last 15 minutes; optional remembered sessions rotate and expire after 30 days. Refresh replay revokes the token family. Game tickets are single-use with a 180-second lifetime. Credentials and token hashes use additive Launcher tables in the world database, independent of gameplay migration numbering.

The password hasher uses ASP.NET Core's versioned format with 210,000 iterations. Passwords contain 6–128 characters. Usernames contain 3–32 ASCII letters, digits, spaces, dots, underscores or hyphens and are unique without case distinctions. New standard and developer accounts are Omega and have three empty character slots. The server assigns the clone grade during registration; existing launcher builds use this default without an update. Registration does not change existing accounts' clone grades.

API responses disable caching. Registration and authentication are rate limited. Manifest signing validates the exact pinned prefix and permits only the explicit chat-bundle hash to vary; private keys remain in the server's Windows key store. Legacy development tickets require explicit `--development-login` on the host and provisioning command, with loopback-only hosting.

## Native chat trust

The server also listens on direct-TLS TCP **5222**, fixed by client build 3662168. Keep this port distinct from the configured game, gateway and API ports and expose it on remote hosts. The API server descriptor adds optional `ChatCertificatePem`; the companion launcher installs that public root only into the prepared client's `tq/bin64/packages/certifi/cacert.pem`. Existing certificate roots are preserved, repeated launches do not duplicate the root, and servers without the metadata remain compatible. The chat private key stays in `chat-tls.pfx` beside the world save. Keep that file private and out of version control. No OS trust-store installation or TLS-disable option is required.

Because this certificate bundle is manifest-covered, the launcher sends its prospective SHA-256 with each launch request. The server signs that one updated file entry; every other build fingerprint stays fixed. Both Play and launch groups verify the matching signed manifest before changing files or starting the client. Update/restart both components together. A prepared copy affected by the earlier chat trust bug is repaired on its next corrected launch.
