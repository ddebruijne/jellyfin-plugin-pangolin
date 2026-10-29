# Jellyfin Pangolin plugin

Lets a Jellyfin server reach private resources on a [Pangolin](https://github.com/fosrl/pangolin) network, for example an LDAP server used by the LDAP Authentication plugin over LDAPS.

The plugin connects as a Pangolin machine client in one of two modes:

- **Userspace** (default) runs the tunnel inside a helper process, with no root, `NET_ADMIN` or `/dev/net/tun` needed. Resources are made available as local ports, such as `127.0.0.1:6360` forwarding to `ldap.home.internal:636`. This works on managed hosts and in unprivileged containers.
- **System** runs the official [Pangolin CLI](https://github.com/fosrl/cli), which creates a WireGuard interface. Resources are reachable by their own addresses and aliases, but it needs root.

In both modes the plugin:

- starts the tunnel when Jellyfin starts, restarts it with backoff if it exits, and stops it cleanly on shutdown
- restarts the tunnel when you save changed settings
- shows connection and per-site status plus recent client output on the settings page, with the client secret redacted
- has a connection test that connects to a host and does an LDAPS-style TLS handshake, reporting the certificate and why a client would reject it

Requires Jellyfin 12.x on Linux. Userspace mode ships builds for amd64 and arm64. Both modes need outbound UDP to your Pangolin server and sites, because the tunnel is WireGuard.

## Install

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**, add a repository and use this URL:
   ```
   https://raw.githubusercontent.com/ddebruijne/jellyfin-plugin-pangolin/main/manifest.json
   ```
2. Find **Pangolin** in the plugin catalog, install it and restart Jellyfin.

## Pangolin setup

1. Make sure the network the LDAP server is on has a site (Newt) connected to Pangolin.
2. Create a private resource for the LDAP server, either a host resource with its IP or a CIDR resource, and give it an alias such as `ldap.home.internal`.
3. Create a machine client, note its ID and secret, and give it access to the resource.

## Userspace mode

1. Open **Dashboard → Plugins → Pangolin**, enter the Pangolin URL, client ID and secret, and tick **Enable tunnel**.
2. Leave **Mode** on **Userspace** and add a port forward per line as `LOCALPORT=HOST:PORT`, for example:
   ```
   6360=ldap.home.internal:636
   ```
   The host can be a resource alias or IP. Aliases are resolved through Pangolin's DNS.
3. Save, and wait until the status shows the client connected and the site as `up`.
4. Under **Connection test**, test `127.0.0.1` port `6360` with TLS on. The certificate should be your LDAP server's.
5. In the LDAP Authentication plugin, set the server to `127.0.0.1` and the port to `6360`, and turn on **Secure LDAP**.

The LDAP plugin rejects a certificate that doesn't name the host it connected to, and yours names the LDAP server, not `127.0.0.1`. Pick one:

- Turn on **Skip SSL/TLS Verification** in the LDAP plugin. The connection is still TLS-encrypted end to end, and WireGuard authenticates the path to your Pangolin site. What you give up is protection against something impersonating the LDAP server on the site's own network.
- Forward plain LDAP instead (`6389=ldap.home.internal:389`, **Secure LDAP** off). WireGuard encrypts it as far as the site; from the site to the LDAP server it travels unencrypted.

The local ports only listen on 127.0.0.1, so only processes on the Jellyfin host (or in its container) can use them.

How it works: the plugin ships a small helper, `pangolin-userspace`, built from Pangolin's own client library ([olm](https://github.com/fosrl/olm)). It runs WireGuard over a [gVisor](https://gvisor.dev) network stack inside the process instead of a kernel interface, the same approach as Tailscale's userspace mode. Its source is in [`helper/`](helper), including the small patches applied to olm and newt so they never touch the host's interfaces, routes or DNS.

## System mode

Pick **System** under **Mode**. The plugin downloads the Pangolin CLI from GitHub and checks it against the SHA-256 digest GitHub publishes, or uses a binary you point it at.

The CLI creates a WireGuard TUN interface and installs routes, and it refuses to start unless it runs as root (it checks `euid == 0`, so `CAP_NET_ADMIN` alone isn't enough). Pick whichever matches your install.

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

Leave **Launch through sudo** off and **Binary path** empty. Files Jellyfin creates from then on (in `/config`, and next to your media if you save artwork or NFOs there) will be owned by root on the host. If you can't change the container, use userspace mode.

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

### LDAPS certificates and DNS in system mode

In the LDAP plugin, use the resource's host name and port 636 with **Secure LDAP** on. The name has to be in the certificate, so connecting by IP usually fails. Two ways to get a matching name:

- **Resource alias:** leave **Resolve resource aliases** on and connect to the alias. The client then takes over DNS for the whole container or host. Set **Match domains** (for example `*.home.internal`) so only those lookups go through Pangolin. The certificate must be issued for the alias.
- **Your own name:** turn alias resolution off and map the certificate's name to the resource IP yourself, e.g. `extra_hosts: ["ldap.example.com:192.168.1.10"]` in Compose or `/etc/hosts` on bare metal.

If the certificate comes from a private CA, the connection test lists a chain error. Point the LDAP plugin's **Root CA path** at your CA bundle, or add the CA to the system trust store.

**Prefer local routes** is on by default. If a CIDR resource overlaps the LAN Jellyfin is on, LAN traffic stays local so your clients can still reach Jellyfin. It doesn't affect host (/32) resources.

## Troubleshooting

- **The site stays `down`, or the client never connects:** check that the Jellyfin host can send UDP to your Pangolin server and site. Some managed hosts block outbound UDP, and then no WireGuard-based tunnel can work.
- **"Connection ... dropped: the tunnel is not connected yet"** (userspace): something used a forwarded port before the tunnel came up. Retry once the status shows the site `up`.
- **"Could not resolve ... through Pangolin DNS"** (userspace): the alias in the forward doesn't match a resource the machine client can access. Check the alias and the client's access in Pangolin, or forward to the IP instead.
- **"cannot listen on 127.0.0.1:PORT"** (userspace): another program already uses that local port. Pick a different one.
- **"Another Pangolin client is already running"** (system): only one CLI client can run per host or container, because they share `/var/run/olm.sock`. Stop the other one. The plugin keeps retrying and takes over once the socket is free.
- **"Jellyfin runs as user ID ..."** (system): the CLI needs root. Switch to userspace mode, or see [System mode](#system-mode).
- **`sudo: a password is required`** (system): the sudoers rule doesn't match. The command must start with exactly `/usr/local/bin/pangolin up client --attach`.
- **DNS broken after a crash** (system): the CLI normally restores DNS itself, and a watchdog covers crashes. If DNS still looks wrong, run `pangolin reset-dns` as root (inside the container for Docker).
- The full client output also goes to the Jellyfin log, prefixed with `pangolin:`.

The client secret is stored in the plugin's XML config like other plugins' passwords, and only administrators can read it through the API.

## Build

The plugin:

```sh
dotnet build -c Release
```

The userspace helper, without a local Go toolchain:

```sh
docker run --rm -v "$PWD/helper:/src" -w /src golang:1.26 ./build.sh dist
```

`helper/e2e/fakepangolin` is a stand-in Pangolin server and site for testing the helper without a real Pangolin install. Its header comment explains how to run it.

To install a local build, copy `Jellyfin.Plugin.Pangolin.dll` and the two `pangolin-userspace_linux_*` binaries into a new folder `plugins/Pangolin_<version>/` inside Jellyfin's data directory (`/config/data/plugins` on linuxserver.io, `/config/plugins` on the official image, `/var/lib/jellyfin/plugins` on Debian/Ubuntu) and restart Jellyfin.

To publish a new version, bump the version in `Directory.Build.props` and `build.yaml`, attach a zip with the DLL, the helper binaries, the helper license and a `meta.json` to a GitHub release, and add an entry to `manifest.json` with the release URL and the zip's MD5 checksum.

## License

The plugin is GPL-3.0. The userspace helper in `helper/` is AGPL-3.0, because it links olm, which is AGPL-3.0.
