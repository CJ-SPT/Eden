# Publish the Eden downpatcher from Ubuntu

The publisher runs on Ubuntu 24.04 LTS with the system Python and systemd. Players use its generated ZIP on Windows x64.
On each new public Tranquility build, it checks the Windows manifest and `start.ini` through CCP's CDN, verifies Eden's
pinned manifest and required files by checksum, and parses the pinned resource indexes. It assembles the ZIP with Eden's
patcher, instructions, and a checksum-pinned Windows Python runtime. After the HTTPS download serves the correct bytes,
the publisher posts the link and SHA-256 once per Tranquility and Eden build pair. The first run announces the current build.

The Ubuntu check covers the manifests, live `start.ini`, and the pinned client files with SHA-256 recorded in the patcher.
Players' Windows patchers verify every file as they stage their clients. The ZIP contains the patcher and runtime, and the
game files come from CCP. [EVE status API](https://developers.eveonline.com/blog/the-ship-of-thesius) ·
[Windows Python release](https://www.python.org/downloads/release/python-31410/) ·
[Discord webhook API](https://docs.discord.com/developers/resources/webhook#execute-webhook)

## Install the service

Extract the publisher bundle or put a checkout containing this guide under `/opt/eden-downpatcher`. A sparse checkout can
use `git sparse-checkout set tools docs`, which also retains the root `LICENSE`. Run the following from that directory.

```bash
getent passwd eden-downpatcher >/dev/null || sudo useradd --system --no-create-home --shell /usr/sbin/nologin eden-downpatcher
sudo install -d -m 0755 -o eden-downpatcher /var/www/eden-downpatcher
sudo install -d -m 0700 /etc/eden-downpatcher
sudo test -e /etc/eden-downpatcher/discord-webhook || sudo install -m 0600 /dev/null /etc/eden-downpatcher/discord-webhook
sudo install -m 0644 tools/client-downpatcher/eden-downpatcher.{service,timer} /etc/systemd/system/
```

Create an incoming webhook in the Discord channel that should receive announcements. Use
`sudoedit /etc/eden-downpatcher/discord-webhook` to put its URL on a single line. Systemd loads it as a service credential.

Serve `/var/www/eden-downpatcher` at a public HTTPS path. For example, add this block to your existing TLS virtual host in Nginx.

```nginx
location /downpatchers/ {
    alias /var/www/eden-downpatcher/;
}
```

After changing Nginx, run `sudo nginx -t && sudo systemctl reload nginx`.

Use `sudoedit /etc/eden-downpatcher/publisher.env` to enter your public URL.

```ini
EDEN_DOWNPATCHER_PUBLIC_URL=https://downloads.example.com/downpatchers
```

The following commands verify CCP's current files, make the first publication, and start the ten-minute schedule.
Starting the service is the step that sends the first Discord announcement.

```bash
python3 -B tools/Watch-ClientDownpatch.py --check-only
sudo systemctl daemon-reload
sudo systemctl start eden-downpatcher.service
sudo systemctl enable --now eden-downpatcher.timer
sudo journalctl -u eden-downpatcher.service -n 40 --no-pager
```

## Monitor and recover

The journal records failed EVE, CDN, download, and Discord checks. The next timer tick retries failures before posting and
definite Discord rejections, including rate limits. During downtime the current Windows manifest can briefly lag ESI.

Keep `/var/lib/eden-downpatcher` across deployments. Each `LIVE-to-EDEN.discord` receipt stores the confirmed Discord
message ID. If it contains `pending`, inspect the channel for that build. If the announcement exists, replace `pending`
with its message ID. If it is absent, remove only that receipt and start the service to retry. The public ZIP names include
their SHA-256 so links in older announcements still address the original packages.
