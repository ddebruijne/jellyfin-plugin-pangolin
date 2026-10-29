# Jellyfin Pangolin plugin

Lets a Jellyfin server reach private resources on a [Pangolin](https://github.com/fosrl/pangolin) network, for example an LDAP server used by the LDAP Authentication plugin over LDAPS.

The plugin runs the official [Pangolin CLI](https://github.com/fosrl/cli) as a machine client next to Jellyfin. It:

- downloads the CLI for your platform from GitHub and checks it against the SHA-256 digest GitHub publishes (or uses one you installed)
- starts `pangolin up client` when Jellyfin starts, restarts it with backoff if it exits, and stops it cleanly on shutdown so routes and DNS are restored
- restarts the tunnel when you save changed settings
- shows connection and per-site status plus recent client output on the settings page
- has a connection test that resolves a host, connects, and does an LDAPS-style TLS handshake, reporting the certificate and why a client would reject it

Requires Jellyfin 12.x on Linux (macOS works too). Windows isn't supported, because the CLI can only run the tunnel as a Windows service there.

## Why it needs root

Pangolin has no userspace client. The CLI creates a WireGuard TUN interface and installs routes, and it refuses to start unless it runs as root (it checks `euid == 0`, so `CAP_NET_ADMIN` alone isn't enough). Pick whichever matches your install.

### Docker (official image)

The official `jellyfin/jellyfin` image runs as root unless you set `user:`. Add the TUN device and `NET_ADMIN`:

```yaml
services:
  jellyfin:
    image: jellyfin/jellyfin:12.4
    cap_add:
      - NET_ADMIN
    devices:
      - /dev/net/tun:/dev/net/tun
    # no `user:` line, the CLI must run as root
```

The tunnel, its routes and the DNS override then live in the container's network namespace and don't touch the host (unless the container uses `network_mode: host`).

### Docker (linuxserver.io image)

The linuxserver.io image runs Jellyfin as the unprivileged `abc` user and doesn't include sudo. Run Jellyfin as root by setting `PUID`/`PGID` to 0, and add the same device and capability:

```yaml
services:
  jellyfin:
    image: lscr.io/linuxserver/jellyfin:latest
    environment:
      - PUID=0
      - PGID=0
    cap_add:
      - NET_ADMIN
    devices:
      - /dev/net/tun:/dev/net/tun
```

Leave **Launch through sudo** off and **Binary path** empty, and the plugin downloads the CLI itself. Files Jellyfin creates from then on (in `/config`, and next to your media if you save artwork or NFOs there) will be owned by root on the host.

### Bare metal (Jellyfin runs as `jellyfin`)

Install the CLI somewhere root-owned and let the `jellyfin` user start it through sudo:

```sh
sudo install -o root -g root -m 0755 pangolin-cli_linux_amd64 /usr/local/bin/pangolin
```

`/etc/sudoers.d/jellyfin-pangolin` (edit with `visudo -f`):

```
Defaults:jellyfin env_keep += "PANGOLIN_ENDPOINT PANGOLIN_CLIENT_ID PANGOLIN_CLIENT_SECRET NO_COLOR"
jellyfin ALL=(root) NOPASSWD: /usr/local/bin/pangolin up client --attach *
```

Then on the settings page set **Binary path** to `/usr/local/bin/pangolin` and enable **Launch through sudo**. The plugin refuses sudo with a downloaded binary, because those live in a directory the `jellyfin` user can write to.

If Jellyfin's systemd unit sets `NoNewPrivileges=true`, sudo can't elevate. Remove that line with a drop-in (`systemctl edit jellyfin`).

The credentials go to the CLI through environment variables, so the secret doesn't appear in `ps`. That's why the sudoers rule needs `env_keep`.

## Pangolin setup

1. Make sure the network the LDAP server is on has a site (Newt) connected to Pangolin.
2. Create a private resource for the LDAP server, either a host resource with its IP or a CIDR resource, and give it an alias such as `ldap.home.internal`.
3. Create a machine client, note its ID and secret, and give it access to the resource.

## Install

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**, add a repository and use this URL:
   ```
   https://raw.githubusercontent.com/ddebruijne/jellyfin-plugin-pangolin/main/manifest.json
   ```
2. Find **Pangolin** in the plugin catalog, install it and restart Jellyfin.

## Jellyfin setup

1. Install the plugin (see above).
2. Open **Dashboard → Plugins → Pangolin**, enter the Pangolin URL, client ID and secret, tick **Enable tunnel** and save.
3. Wait until the status shows the client connected and the site as `up`.
4. Under **Connection test**, enter the LDAP host and port 636 and run the test.
5. In the LDAP Authentication plugin, use the same host name and port 636 with **Secure LDAP** on.

### LDAPS certificates and DNS

The LDAP plugin validates the server certificate, so the name you connect to has to be in the certificate. Connecting by IP usually fails. Two ways to get a matching name:

- **Resource alias:** leave **Resolve resource aliases** on and connect to the alias. The client then takes over DNS for the whole container or host. Set **Match domains** (for example `*.home.internal`) so only those lookups go through Pangolin. The certificate must be issued for the alias.
- **Your own name:** turn alias resolution off and map the certificate's name to the resource IP yourself, e.g. `extra_hosts: ["ldap.example.com:192.168.1.10"]` in Compose or `/etc/hosts` on bare metal.

If the certificate comes from a private CA, the connection test lists a chain error. Point the LDAP plugin's **Root CA path** at your CA bundle, or add the CA to the system trust store.

**Prefer local routes** is on by default. If a CIDR resource overlaps the LAN Jellyfin is on, LAN traffic stays local so your clients can still reach Jellyfin. It doesn't affect host (/32) resources.

## Build

```sh
dotnet build -c Release
```

To install a local build instead of the release, copy `Jellyfin.Plugin.Pangolin/bin/Release/net10.0/Jellyfin.Plugin.Pangolin.dll` into a new folder `plugins/Pangolin_1.0.0.0/` inside Jellyfin's config directory (`/config` in Docker, `/var/lib/jellyfin` on Debian/Ubuntu) and restart Jellyfin.

To publish a new version, bump the version in `Directory.Build.props` and `build.yaml`, attach a zip containing the DLL and a `meta.json` to a GitHub release, and add an entry to `manifest.json` with the release URL and the zip's MD5 checksum.

## Troubleshooting

- **"Another Pangolin client is already running"**: only one client can run per host or container, because they share `/var/run/olm.sock`. Stop the other one. The plugin keeps retrying and takes over once the socket is free.
- **"elevated permissions are required"** in the client output: the CLI isn't running as root. See [Why it needs root](#why-it-needs-root).
- **`sudo: a password is required`**: the sudoers rule doesn't match. The command must start with exactly `/usr/local/bin/pangolin up client --attach`.
- **DNS broken after a crash**: the CLI normally restores DNS itself, and a watchdog covers crashes. If DNS still looks wrong, run `pangolin reset-dns` as root (inside the container for Docker).
- The full client output also goes to the Jellyfin log, prefixed with `pangolin:`.

The client secret is stored in the plugin's XML config like other plugins' passwords, and only administrators can read it through the API.
