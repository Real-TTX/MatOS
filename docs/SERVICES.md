# MatOS Services (`services.mattiverse.de`)

MatOS is for people who do not know Docker, and most of them have no domain of their own. MatOS Services gives such an installation a
**free address**: the person picks a name in the setup assistant (`anna`), the service checks that it is free, and from then on

| | |
|---|---|
| the installation | `anna.my.mattiverse.de` |
| MatOS itself | `matos.anna.my.mattiverse.de` |
| an app | `<app>.anna.my.mattiverse.de` (`matnote.anna.my.mattiverse.de`) |

with a real HTTPS certificate, reachable from anywhere, **without a domain, a DNS account or a router setting**.

It is one container (`ghcr.io/real-ttx/matos-services`, project `src/MatOS.Services`) that runs on the owner's server1. One instance serves
every MatOS installation. It is the provider of the hosted address that MatDesign's `docs/APP-MODEL.md` foresees ("a hosted `dns.record` for people without
their own domain").

## Two ways in: tunnel and direct

The service manages both, per installation (`mode`):

- **Tunnel** (the default; works behind every firewall and CGNAT, like Synology QuickConnect). MatOS keeps an outgoing connection to the service.
  `*.anna.my.mattiverse.de` points to server1; the service's **edge** takes a connection on 443 (or 80), reads the host name (TLS SNI, or the HTTP `Host`
  header) **without decrypting anything**, and passes the raw bytes through the tunnel to Matcad's Caddy in the installation. The certificate lives in the
  installation (Caddy gets it by TLS-ALPN-01 or HTTP-01 through the tunnel), so server1 never sees the content. Costs: the traffic goes through server1.
- **Direct**. `*.anna.my.mattiverse.de` points to the public IP of the installation (kept current by Matddns through the DynDNS endpoint of the service).
  Nothing goes through server1, but the router must forward 80 and 443 to the host. Needs the zone `my.mattiverse.de` to be delegated to the service (below).

The setup assistant always starts with the tunnel. *Settings → Address* shows the state and offers the switch to direct after a check that the
installation is reachable from outside.

## The container

| Port (inside) | What |
|---|---|
| `8080` | HTTP: the API below, the tunnel endpoints (WebSocket) and a small status page; published behind server1's existing proxy as `https://services.mattiverse.de` |
| `8443` | the **edge** for TLS (SNI passthrough): names below the zone go into the tunnel of their installation, any other name to `Services__Edge__FallbackHttps` (host:port) if set, else the connection is closed |
| `8081` | the **edge** for plain HTTP (`Host` header): the same; ACME HTTP-01 and the redirect to https |
| `5353` (UDP + TCP) | the authoritative **DNS** for the zone (only needed for the direct mode) |

Configuration (environment, section `Services`):

| Key | Default | |
|---|---|---|
| `Services__Zone` | `my.mattiverse.de` | the zone the names live in |
| `Services__PublicUrl` | `https://services.mattiverse.de` | how installations reach the API |
| `Services__Edge__PublicIpv4` / `PublicIpv6` | – | server1's address: the answer for installations in tunnel mode and for the zone itself |
| `Services__Edge__FallbackHttps` / `FallbackHttp` | – | where other host names go (so the edge can sit in front of an existing proxy) |
| `Services__Dns__NameServers` | `services.mattiverse.de` | the NS names the zone answers with |
| `Services__AdminEmail` / `AdminPassword` | – | the first administrator of the admin area (`/admin`), both or neither: created once, on a start without any account (in `Development` the test account `admin@example.com` / `Start12345` instead); after that the accounts live on the page Users. Without an account there is no admin area (`404`) |
| `Services__RegistrationsPerIpPerDay` | `5` | successful registrations in the last 24 h per IPv4 address, or per /48 of an IPv6 address (a home or a server usually holds a whole /56 or /48) |
| `Services__RegistrationsPerHour` | `60` | successful registrations of everybody in the last hour: the ceiling for somebody with very many addresses |
| `Services__ExpireUnusedAfterDays` | `7` | a name that was never used (no tunnel ever connected, no address ever came through `/nic/update`) is given back this many days after its registration; `0` keeps every name. Names in use never expire |
| `Services__ProofOfWorkBits` | `16` | the proof of work the public DynDNS page asks for before it registers a name (about 65,000 SHA-256 hashes, a second in a browser); `0` asks for none |
| `Services__DataPath` | `/data` | SQLite database `services.db` |
| `Services__TrustedProxies` | `10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,127.0.0.0/8,::1/128,fc00::/7` | whose `X-Forwarded-For`/`-Proto` counts (the client address decides the rate limits and the address of `/nic/update`); a header from anybody else is ignored |
| `Services__ListenAddress` | `::` | where the edge and the DNS bind (`::` takes IPv4 too and falls back to `0.0.0.0` without IPv6) |
| `Services__Edge__HttpsPort` / `HttpPort` | `8443` / `8081` | |
| `Services__Edge__MaxStreamsPerInstance` / `MaxConnections` / `MaxFallbackConnections` | `256` / `4096` / `4096` | visitors of one installation at once / connections into all tunnels at once / connections to the fallback at once (a budget of its own, so the installations and the existing sites cannot crowd each other out); beyond that a visitor is turned away (`503` on 80, closed on 443) |
| `Services__Edge__MaxPendingConnections` | `1024` | connections whose host name is not known yet (the first 5 s), counted apart from the established ones; beyond that a new connection is closed at once |
| `Services__Edge__MaxConnectionsPerAddress` / `NewConnectionsPerAddressPerMinute` | `64` / `600` | connections of one visitor address (IPv4 address, IPv6 /64) at once / new ones per minute. Not applied to a trusted proxy in front of the edge (it is the address of every visitor) unless it sends the PROXY protocol |
| `Services__Edge__IdleTimeoutSeconds` | `300` | a connection without a byte in either direction for this long is closed |
| `Services__Edge__ProxyProtocol` / `HttpProxyProtocol` | `false` / `false` | every connection to the TLS edge / the HTTP edge starts with a PROXY protocol header (v1 or v2) from a trusted proxy, which names the visitor (Option A); anything else is closed |
| `Services__Edge__FallbackProxyProtocol` | `false` | the edge sends a PROXY protocol v2 header to the fallback before the visitor's bytes (Option B), so the existing proxy sees the visitor and not the container. **Required for Option B** (see "Setting it up") |
| `Services__Dns__Enabled` / `Port` / `Ttl` | `true` / `5353` / `60` | |
| `Services__Tunnel__PingSeconds` / `PongTimeoutSeconds` / `StreamTimeoutSeconds` | `30` / `90` / `10` | |

Data: one volume (`/data`), tables `Instance`, `Registration` and `CustomDomain` (EF Core migrations, applied on start). The key of an installation is stored as
a SHA-256 hash only.

Project: `src/MatOS.Services` (minimal APIs, no Razor), tests `tests/MatOS.Services.Tests`, image `Dockerfile.services`, server1 setup `deploy/services/`.

## API v1

JSON, `camelCase`, `null` fields left out. An installation authenticates with `Authorization: Bearer <key>` (the key it got at registration);
without a valid key the answer is `401`. Errors are `{error}` with the code named below.

| Method and path | |
|---|---|
| `GET /api/v1/names/{name}` | anonymous. `200 {name, available, reason?, address?}`; `reason` is `invalid`, `reserved`, `taken` or `blocked` (an administrator blocked the name; a client treats it like `taken`); `address` = `{name}.{zone}` (left out for an invalid name); `name` is the lowered form |
| `POST /api/v1/instances` | anonymous, rate-limited. `{name, version?}` → `201 {name, zone, key, mode}`. The key is returned **once**. `409` `taken`/`reserved`/`blocked`, `400` `invalid`, `429` `too-many-registrations` from this address (or from everybody in the last hour) |
| `GET /api/v1/instance` | `{name, zone, mode, publicIp, publicIpv4, publicIpv6, tunnelConnected, lastSeen}`; `publicIp` is the IPv4 address, else the IPv6 one; `lastSeen` is ISO 8601 in UTC with its `Z` (`2026-11-26T16:02:18.1234567Z`), as every time of the API |
| `PUT /api/v1/instance` | `{mode: "tunnel" \| "direct"}` → the same as `GET`; direct is refused (`409` `no-public-ip`) while no public IP is known; tunnel is refused for a DynDNS-only name (`409` `dyndns-only`); another mode is `400` `invalid-mode` |
| `POST /api/v1/instance/check` | the service tries to reach the installation directly on 80 and 443 (the IP of the request if it is a public one, else the stored one): `{publicIp, http, https, detail}`; `http` is true only when the answer comes from Caddy (`Server: Caddy`), `https` when a TLS handshake for `{name}.{zone}` succeeds (`detail` says whether the certificate is valid yet). Addresses that are not public are never probed |
| `DELETE /api/v1/instance` | `204`: gives the name back (the tunnel is closed, the records disappear) |
| `GET /nic/update?hostname=…&myip=…` | the DynDNS2 protocol for Matddns: Basic auth `name:key` (the user may also be `{name}.{zone}`); answers `good <ip>`, `nochg <ip>`, `badauth`, `nohost`, `notfqdn` (one line per host name; `numhost` for more than 10), and `dnserr` for an address that is not a public one or not an address (also one line per host name: `dnserr` for the own names, `nohost`/`notfqdn` for the others). `myip` may hold an IPv4 and an IPv6 address (comma-separated), `myipv6` is accepted too; a family that is not sent stays as it is. Without either the IP of the request. `hostname` is `{name}.{zone}` or `*.{name}.{zone}`. Without credentials `401` with a Basic challenge |
| `GET /healthz` | anonymous `ok` |
| `POST /api/v1/ports/check` | anonymous, or with the Bearer key of an installation. "Check my ports" (below): optional body `{"port": 8443}` (one more port, 1–65535; else `400 invalid-port`). `200 {ipv4, ipv6, checkedAt, ports: [{port, ipv4, ipv6, web}]}`: 80 and 443 always first, then the extra port; per family `open`, `closed` (refused) or `filtered` (no answer in 3 s), `null` for the family that was not checked; `web` is `caddy`, `other` or `null`. Null fields are written (`"ipv6": null`). `422 not-public` when the caller's address is not a public one; `429 {error: "too-many-checks", retryAfter}` (seconds, also as `Retry-After`) beyond 6 checks per address (IPv6 /64) or 20 per installation in 10 minutes. With a key the result is kept as the installation's last check; a wrong key is `401` |
| `GET /api/v1/instance/domains` | the installation's own domains on the tunnel: `[{domain, status, checkedAt, detail}]`, `status` `pending`, `verified` or `failed` |
| `GET /api/v1/instance/domains/{domain}` | one of them, the same shape; `404 unknown-domain` (what MatOS polls while it waits for the verification) |
| `POST /api/v1/instance/domains` | `{domain}` → `201 {domain, status, checkedAt, detail}`, checked at once (a second POST of the same domain checks again). `400 invalid-domain` / `forbidden-domain` (the zone, its parent `mattiverse.de` and every name below them), `409 domain-taken` (verified by another installation), `too-many-domains` (5), `dyndns-only` |
| `DELETE /api/v1/instance/domains/{domain}` | `204`; `404 unknown-domain` |
| `GET /ip` | anonymous: the caller's address as plain text (for ddclient's `use=web` and scripts) |

Rate limits per client address (an IPv6 address counts as its /64) and minute: 120 for the name check, 60 for the rest of the API and
`/nic/update`; beyond that `429`. The sign-in of `/admin` allows 20 requests per minute per IPv4 address or IPv6 /48 (the rest of the admin area 300), 5 wrong passwords per e-mail address in 15 minutes (then that address is locked for 15 minutes, whether an account has it or not; a wrong current password on Change password counts the same), and 30 wrong passwords per minute of everybody
together (then `429` without looking at the password). The tunnel endpoints have no limit per request (every visitor is a stream request), but
12 control connections per installation and minute, and 30 failed sign-ins (a key that does not exist) per address and minute, after which that
address gets `429` on both tunnel endpoints for the rest of the minute. The client address is the one the trusted proxy names; see
`Services__TrustedProxies` and "Setting it up" for how it must reach the service.

`GET /` is a short public page (on the host of the zone itself, `my.mattiverse.de`, the DynDNS page below). `/admin` is the admin area: Razor Pages in the look of every Mat app (MatUi, linked from MatOS.Web), a sign-in with an e-mail address and a password (PBKDF2, a session per browser in the table `UserSession`, the cookie `HttpOnly`, `SameSite=Strict`, `Secure` behind https, an antiforgery token on every form, a strict content security policy, `noindex`); the installations with their facts, tunnel, domains and the actions disconnect, back to tunnel mode, block/unblock and give back, each written to the table `AdminAction`. A blocked name is refused by the API (`409 blocked` on registration, `403 blocked` on a change or a give-back), the tunnel (`403`), the edge (`403` over HTTP, closed over TLS), the DNS (`NXDOMAIN`) and `/nic/update` (`abuse`), and it never expires. The admin area answers only on the host of `Services__PublicUrl`, never on the zone or below it, and is `404` while no account exists (the Basic auth of before is gone).

**The statistics** (for the admin area): counted in memory and written every minute and when the service stops. Per installation, hour, host
(the installation itself, `<app>`, its own domain; more than 32 hosts of one installation in an hour count together as `*`) and way (TLS, HTTP), the table
`UsageHour` holds the connections, the refusals by reason (no tunnel, a limit, the stream not picked up in time, blocked), the bytes each way, the connection
seconds, the peak of open connections and the distinct visitors; the fallback to server1's proxy has rows of its own (an empty name). `TunnelSession` holds every
tunnel that ended (connected, disconnected, why, the address it came from, the MatOS version, streams and bytes), `DnsHour` the answers of the DNS per name
(A, AAAA, other) and the accepted `/nic/update` calls and address changes, with the NXDOMAIN answers of the zone only as a total, and `ApiDay` the
registrations, give-backs, port checks, name checks and rate-limit refusals per day (the API and the DynDNS page of the installations: a registration
refused by its limit counts as a rate-limit refusal; an administrator's give-back is in `AdminAction`, not here). The rows are keyed by the name; a name
given back and registered again by somebody else keeps the earlier holder's rows for the totals, and the views of one installation read only the rows
from its registration on (`Usage/InstanceUsage.cs`). **What is never stored**: a visitor's address, or a path or URL a
visitor opens (the edge does not decrypt TLS and reads nothing of HTTP but the host). Visitors are counted as HMAC-SHA256 hashes of the address with a
random salt per hour, kept in memory for that hour only and dropped (never written) with the hour's counters; only their number is stored, so the
same visitor in two hours counts twice in a day's total. The addresses of installations (`CreatedFromIp`, `PublicIpv4/6`, a tunnel's address) are the
operator's evidence and are stored. Retention (`Services__Usage__HourlyDays`, `DailyDays`, `TunnelSessionDays`): hourly rows 35 days, then rolled up into
`UsageDay` and `DnsDay`, which are kept 400 days like `ApiDay`; tunnel sessions 180 days. A daily job rolls up and removes them for real.
The admin area shows them for a period of 24 hours, 7 days, 30 days or 12 months (`?period=24h|7d|30d|12m`, UTC, up to a minute behind): the overview
(the numbers of now and today, the traffic, the installations with the most traffic and visitors, the latest registrations, the tunnels that ended three
times or more in 24 hours), Statistics (the traffic and visitors over time, by installation, by app, by way and by mode, the refusals by reason, the DNS and
the API per day), an installation's own page (its traffic and visitors per app, its tunnel sessions, its DNS answers and DynDNS updates) and Tunnels (the
connected ones, refreshed every 5 seconds from `GET /admin/api/tunnels`, signed in only, and the ones that ended last). The charts are SVG drawn on the
server (`Admin/Charts.cs`, the colours in `wwwroot/css/services.css`), each with the table of its numbers.

**Names**: 3 to 32 characters, `a-z`, `0-9` and `-`, not at the start or the end, not `--` in positions 3–4 (punycode). Reserved: `www`, `mail`, `smtp`, `imap`,
`ns`, `ns1`, `ns2`, `dns`, `api`, `admin`, `root`, `services`, `service`, `matos`, `my`, `mattiverse`, `status`, `support`, `help`, `test`, `demo`, `app`, `apps`,
`login`, `auth`, `id`, `cdn`, `static`, `assets`, `ftp`, `vpn`, `tunnel`, `edge`, `relay`, `localhost`. Upper case input is lowered.
A name that is never used expires (`Services__ExpireUnusedAfterDays`).

## The tunnel

1. MatOS opens `wss://services.mattiverse.de/api/v1/tunnel` (Bearer key): the **control** connection. One per installation; a new one replaces the old.
   The service sends `{"type":"ping"}` every 30 s, MatOS answers `{"type":"pong"}`; MatOS reconnects with a back-off of 1, 2, 5, 10, 30, 60 s.
2. A visitor connects to the edge on 443 (or 80). The edge reads the ClientHello (or the request head) up to 16 KiB / 5 s, finds the installation by the
   host name (`anna.my…` or `*.anna.my…`) and sends on the control connection `{"type":"open","id":"<uuid>","port":443}` (or `80`).
3. MatOS opens `wss://services.mattiverse.de/api/v1/tunnel/stream/{id}` (Bearer key), connects to Matcad's Caddy on its shared Docker network
   (port 443 or 80) and copies bytes both ways as binary WebSocket frames. The edge first sends the bytes it has read already. A stream that MatOS does not
   pick up within 10 s is closed.
4. Either side closing ends the stream (a close of one direction is passed on as a half-close first). An installation that is not connected:
   the edge closes the connection (443) or answers `502` with a short page "This MatOS is offline" (80). A stream that is not picked up in time:
   closed (443) or `502` "did not answer" (80). A name in the zone that nobody holds: closed (443) or `404` (80).

Which names go where: `{name}.{zone}` and `<app>.{name}.{zone}` go to the installation `{name}`; a name deeper than that
(`a.b.{name}.{zone}`) is held by nobody (closed on 443, `404` on 80): MatOS never uses one, and every further level would be one more name a tenant
could order certificates for against the shared quota (below). The zone itself, names outside it, a ClientHello without SNI and a request without
`Host` go to the fallback (`Services__Edge__FallbackHttps`/`FallbackHttp`, raw TCP, the peeked bytes first) or are closed (`404` on 80) when none is
set. What is not TLS on 8443 or not HTTP on 8081, is larger than 16 KiB before the name or slower than 5 s, is closed.

**Port 80 is routed per request, 443 per connection.** A TLS connection cannot change its SNI. A plain HTTP connection can carry many requests, and a
proxy in front of the edge (server1's Caddy or Traefik) reuses its idle connections for any host. So the HTTP edge reads every request head and the
framing of its body (`Content-Length`, `chunked`) and ends the connection at the first request for another destination (another installation, or
the fallback): a client retries such a request on a new connection, and no request ever reaches an installation it was not meant for (otherwise
installation A could answer the ACME HTTP-01 challenge of B's name). Anything two servers could read differently (two `Host` headers, both body
framings, a bare line feed, `CONNECT`) ends the connection. A protocol switch (a WebSocket on port 80) works as the first request of a connection
only; after its `101` the connection passes unread. The proxy in front should still not keep idle connections to 8081 (`deploy/services/README.md`).

A connection without a byte in either direction for 5 minutes is closed (`Services__Edge__IdleTimeoutSeconds`), and each visitor address has its share
of the edge (`MaxConnectionsPerAddress`, `NewConnectionsPerAddressPerMinute`), so one host can neither fill the edge nor all streams of one installation.

A control connection may be open in either mode (the edge passes on whatever arrives); MatOS stops it in direct mode. One that does not answer
the pings is closed at the last ping tick before 90 s without a pong would be passed (with pings every 30 s: 90 s after the last pong).

Limits: the visitors' IP addresses are not passed on to the installation (Caddy sees the tunnel); a later PROXY protocol header in the stream can
change that. The edge itself reads the PROXY protocol from a proxy in front of it (`Services__Edge__ProxyProtocol`) and writes it to the fallback
(`Services__Edge__FallbackProxyProtocol`).

## DNS (direct mode)

The service is the name server of the zone when it is delegated: `SOA`, `NS` (the configured names), and for every installation
`{name}.{zone}` and `*.{name}.{zone}` (`A`/`AAAA`, TTL 60): its public IP in direct mode, the edge (server1) in tunnel mode. The zone itself answers the edge.
Unknown names are `NXDOMAIN`, a type without data `NOERROR` with the `SOA` (negative caching), other zones and classes `REFUSED` (no recursion),
other opcodes `NOTIMP`, malformed queries `FORMERR`, zone transfers (`AXFR`, `IXFR`) `REFUSED`. A name server inside the zone
(`ns1.my.mattiverse.de`) answers the edge's address. Answers over UDP are at most 512 bytes (else `TC`, the client retries over TCP). A label that
holds a byte a name cannot (a `.` inside a label above all) matches no name. Names deeper than `<app>.{name}.{zone}` are `NXDOMAIN`, as on the edge.
The names and addresses are kept in memory (refreshed by every change through the API, reloaded every 5 minutes), so a flood of random names
never reaches the database. TCP: at most 8 connections per address, each ends after 2 minutes or 100 queries.

## DynDNS for everybody (`https://my.mattiverse.de`)

The same names, for people **without MatOS**: a FritzBox, ddclient, Matddns, a script. The page is the zone itself, `https://my.mattiverse.de`
(any request whose `Host` is `Services__Zone`; the same page is at `/dyndns` on every host, so `https://services.mattiverse.de/dyndns` works too).
Plain HTML from code with one script of the service's own (`/dyndns/app.js`); the policy is `default-src 'none'; script-src 'self'; connect-src 'self';
style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'`. No cookies, no third party, no captcha service.

- **Pick a name**: a field with a live check (`GET /api/v1/names/{name}`, the same name rules); *Get my name* registers it through the same
  registration as the API (the same limits per address and per hour). Such a name is marked **DynDNS-only** (`Instance.DynDnsOnly`), starts in
  **direct** mode without an address (the DNS answers it with no data until the first update) and never uses the tunnel: the tunnel endpoint
  answers `403`, `PUT /api/v1/instance {mode: "tunnel"}` `409 dyndns-only`, custom domains `409 dyndns-only`.
- The answer shows the **key once** (with a copy button) and the settings:
  - FritzBox (*Internet > Permit Access > DynDNS*, provider *User-defined*): Update URL
    `https://services.mattiverse.de/nic/update?hostname=<domain>&myip=<ipaddr>&myipv6=<ip6addr>`, domain name `name.my.mattiverse.de`, user name
    `name`, password the key. A FritzBox without IPv6 sends `myipv6=` empty; the stored IPv6 then stays.
  - Matddns: a domain group of the kind `DynDns`, `updateUrl` `…/nic/update?hostname={hostname}&myip={ipv4}&myipv6={ipv6}`, user `name`, password the key.
  - ddclient: `protocol=dyndns2`, `use=web, web=https://services.mattiverse.de/ip`, `server=services.mattiverse.de`, `ssl=yes`, `login`, `password`, the name.
  - curl: `curl -u 'name:key' 'https://services.mattiverse.de/nic/update?hostname=name.my.mattiverse.de'`.
- **Manage your name** (`/dyndns/manage`): name and key in a POST form, nothing kept after the page (each further form carries them again as hidden
  fields; the page is `no-store`). It shows IPv4, IPv6, the last update and the last change, *Update now from this browser* (the browser's own
  address, only a public one), the port check (only when the browser comes from the name's own address), and *Give the name back* (a checkbox
  confirms). A name of a MatOS installation is refused there ("manage it in MatOS").
- **Rules** (`/dyndns/rules`): what a name is, the limits, the expiry, no abuse, what is stored.

**Abuse protection.** Every form is a POST that must pass the **same-origin check** (`Origin`, else `Referer`, must name the request's host; else
`403`). The registration form carries a **signed one-time token** (HMAC with a key made at start, 30 minutes, used once) with a **proof of work** on
it: SHA-256 of `token:nonce` must start with `Services__ProofOfWorkBits` zero bits (the page's script finds the nonce, about a second), and a
**honeypot** field people never see. On top of that the registration limits of the API (5 names per address and day, 60 of everybody per hour)
and the rate limits per minute. A name that receives **no address within 7 days** of its registration is given back automatically: the same
`InstanceExpiry` as for MatOS names, whose "used" is a tunnel or an address through `/nic/update` (a DynDNS name has no tunnel, so it is its first
address). A name that is used stays.

**DNS.** A DynDNS-only name is an instance in direct mode, so the authoritative DNS answers `name.my.mattiverse.de` and `*.name.my.mattiverse.de`
with its stored IPv4 (`A`) and IPv6 (`AAAA`); names in tunnel mode still answer the edge, and the zone itself (`my.mattiverse.de`, the page) the
edge's address (server1) too. The page is reached through the normal reverse proxy (Matcad terminates TLS for `my.mattiverse.de` and proxies to
port 8080): the edge's passthrough only takes names **with** a label below the zone (`anna.my…`, `x.anna.my…`), never the apex.

## Check my ports

"Is my router forwarding?" for everybody, on the page and as `POST /api/v1/ports/check` (MatOS's setup assistant calls it from the home server, so
the caller is the home connection). The service connects to the **caller's own address** (the client address after the trusted-proxy rules; one
request has one address, IPv4 or IPv6) and **never** to an address from the request, never to one that is not public (`422 not-public`). Ports 80
and 443 always, plus one port the person chooses; a TCP connect with a 3 s timeout (`open`, `closed` when refused, `filtered` when nothing
answers), and on 80/443 a `GET /` (over TLS on 443) whose `Server` header says whether a web server answers and whether it is Caddy (MatOS).
Limits: 6 checks per address (IPv6 /64) in 10 minutes, 20 per installation when MatOS signs in with its key; the installation's last result is
kept (`Instance.LastPortCheck`). The page explains the result in plain words and how to forward a port on a FritzBox.

## Your own domain through the tunnel

For people with a domain of their own whose router cannot forward ports: the domain's names reach the installation through the tunnel, like the
free names. MatOS registers a **suffix** (`POST /api/v1/instance/domains {"domain": "apps.example.com"}`); the person sets at their DNS provider

```
*.apps.example.com.   CNAME   anna.my.mattiverse.de.
```

(the apex of a domain cannot be a CNAME; MatOS uses names below it: `matnote.apps.example.com`). The service then **verifies** the record: it
asks for a name only a wildcard answers (`matos-check-<random>.apps.example.com`), else the domain itself, and follows up to 5 CNAMEs (DnsClient
against the system's name servers, 3 s a question, 10 s in all); verified when the chain reaches `{name}.{zone}` of that installation. Only a
verified domain is routed: the edge (SNI on 443, `Host` on 80) sends the domain and **every name below it** into the installation's tunnel
(the longest verified suffix wins); everything else as before. The DNS of the service is not involved: the domain lives at the person's provider,
its CNAME leads to the installation's free name, which in tunnel mode answers the edge. The certificates are the installation's (Caddy's
TLS-ALPN-01/HTTP-01 through the tunnel).

Checks run again every 6 hours (a pending domain every 15 minutes); a check that fails marks a verified domain `failed` (no longer routed), and a
domain whose checks fail for **3 days** (or that is never verified in 3 days) is removed. At most **5** domains per installation; a domain belongs
to one installation at most (unique). A verified domain cannot be taken by another installation; a pending or failed one goes to whoever proves
the CNAME (nobody can block a domain by registering it first). Never the zone, its parent (`mattiverse.de`) or a name below them. A given-back
name takes its domains with it.

The traffic for these names must reach the edge on server1: with Option B (the container holds 80/443) it does; with Option A the layer-4 routing
must send SNI it does not know to the edge (deploy/services/README.md, "Custom domains").

## Setting it up on server1 (once)

1. DNS of `mattiverse.de` (at its provider): the wildcard `*.mattiverse.de → server1` exists already; it covers `services.mattiverse.de` **and every name
   below `my.mattiverse.de`**, so the tunnel mode needs nothing more. For the direct mode and DynDNS for everybody add `my  NS  services.mattiverse.de` (the service answers for the zone
   from then on; the wildcard no longer applies below it).
2. Run the container (`deploy/services/docker-compose.yml`) with `Services__Edge__PublicIpv4` = server1's IP.
3. Publish `https://services.mattiverse.de` **and `https://my.mattiverse.de`** (the DynDNS page; a normal route with TLS ending at the proxy, Matcad on server1) → container port 8080 through the existing proxy, and the names below `my.mattiverse.de` (not the apex) on 80/443 **unencrypted
   / passthrough** to 8081/8443: either the container takes 80/443 itself and passes every other name to the existing proxy (`FallbackHttps`/`FallbackHttp`),
   or the existing proxy routes by SNI (Caddy `layer4`, nginx `stream` with `ssl_preread`, Traefik `HostSNI`; examples in `deploy/services/README.md`).
4. For the direct mode and the DynDNS names: 53/udp and 53/tcp → container port 5353.

**The client address must survive the way in.** The registration limits, the API's rate limits and the address `/nic/update` stores all use the
client address that the proxy in front of port 8080 reports in `X-Forwarded-For`. If that proxy only sees a constant address (`127.0.0.1` behind an
nginx `stream` server, the container's own address when the edge passes `services.mattiverse.de` to it as a fallback in Option B), every visitor of the
world becomes one client: five registrations a day for everybody, and a forged `X-Forwarded-For` passes as the client (the constant address is a
trusted private one). So whatever passes TLS at layer 4 in front of the existing proxy must send the PROXY protocol, and the existing proxy must read
it: in Option B `Services__Edge__FallbackProxyProtocol=true`, in the nginx variant of Option A a `stream` server with `proxy_protocol on`. The same
keeps the IP allow-lists of server1's other sites working. Examples in `deploy/services/README.md`.

**Certificates and cookies: `my.mattiverse.de` must be on the [Public Suffix List](https://publicsuffix.org) before the service is opened to the
public.** Until browsers and Let's Encrypt know that entry, every installation is a subdomain of the one registrable domain `mattiverse.de`, shared by
strangers and by the owner's own sites:

- Let's Encrypt allows 50 new certificates per registered domain and week. One tenant can order 50 certificates for app names of its own through its
  tunnel and leave no certificate for any other installation, nor for the owner's own sites on `mattiverse.de`, for the rest of the week. The edge
  only serves `<app>.{name}.{zone}`, no deeper names, which narrows this but does not prevent it.
- Cookies: a page on `evil.my.mattiverse.de` may set a cookie for `Domain=my.mattiverse.de` or `Domain=mattiverse.de`. Every installation then receives
  it with its own requests (session fixation, a login as the attacker, or a cookie bomb that makes every installation answer `431` for that
  browser), and every cookie one of the owner's sites scopes to `.mattiverse.de` is sent to every installation.

With the entry each installation is a site of its own for browsers (cookies stay inside it) and counts on its own for Let's Encrypt. Better still,
serve the free names from a registrable domain that is used for nothing else (`Services__Zone`), so the owner's sites never share a cookie scope or a
certificate quota with strangers.

## MatOS's side

- `SystemConfig.Hosted`: `name`, `zone`, the key (protected), `mode`. `MatOS__ServicesUrl` (default `https://services.mattiverse.de`).
- The setup assistant: *How do you reach your apps?* — **a free address** `<name>.my.mattiverse.de` (the default; the name is checked while typing),
  **my own domain** (with the DNS account), or **only here at home** (no address: every app gets a port of this machine). For a free address it registers the
  name, sets the base domain to `<name>.my.mattiverse.de` without a DNS account (Matcad then gets single host routes with the HTTP/TLS challenge) and starts the tunnel.
- `TunnelClient` (a hosted service in MatOS): keeps the control connection while `Hosted.Mode` is `tunnel` and pipes the streams to Matcad's Caddy.
- Direct mode: a Matddns domain group of kind `DynDns` with
  `updateUrl` = `<ServicesUrl>/nic/update?hostname={hostname}&myip={ipv4}&myipv6={ipv6}` (Matddns takes the URL as a template: it fills the
  placeholders and appends nothing, and drops the parameter of the family a record does not have; a bare `/nic/update` would arrive without
  `hostname` and get `notfqdn`), `username` = name, `password` = key, the record `<name>.my.mattiverse.de`, `managedBy: matos`. MatOS creates the
  group first and waits for its first `good`: `PUT /api/v1/instance {mode: "direct"}` is refused (`409 no-public-ip`) until an address is stored.
  Then the tunnel stops.
- *Settings → Address*: the address, the mode, whether the tunnel is connected, *Check direct access*, the switch, *Give the name back*.
