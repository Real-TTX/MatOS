# MatOS Services on server1

How to run `ghcr.io/real-ttx/matos-services` on server1 so that every MatOS installation can get a free address `<name>.my.mattiverse.de`.
What the service does and why is in [docs/SERVICES.md](../../docs/SERVICES.md); this page is only the setup.

The examples use `203.0.113.10` for server1's public IPv4 address. Replace it.

**Before the service is opened to the public**, `my.mattiverse.de` must be on the [Public Suffix List](https://publicsuffix.org): until then all
installations share one cookie site and one Let's Encrypt quota with each other and with every other site on `mattiverse.de` (docs/SERVICES.md,
"Certificates and cookies").

## What has to arrive where

| From the internet | To the container | What |
|---|---|---|
| `https://services.mattiverse.de` (through the existing proxy, TLS ends there) | `8080` | the API, the tunnel (WebSockets) and the status page |
| `*.my.mattiverse.de` on **443**, **passed through unencrypted** (no TLS termination) | `8443` | the edge for TLS: it reads the SNI and passes the bytes into the tunnel |
| `*.my.mattiverse.de` on **80** | `8081` | the edge for HTTP (ACME HTTP-01, the redirect to https) |
| `https://my.mattiverse.de` (the zone itself: the public DynDNS page; through the existing proxy, TLS ends there) | `8080` | the same HTTP port: the service shows the DynDNS page for this host |
| `53/udp` and `53/tcp` (direct mode and DynDNS names) | `5353` | the DNS of the zone |

The important part is 443: the certificate of an installation lives in the installation (its Caddy). The proxy on server1 must **not** terminate TLS
for `*.my.mattiverse.de`; it may only look at the SNI and pass the connection on. That is what the examples below do.

Three rules hold for every variant below:

1. **Both depths.** The names are `<name>.my.mattiverse.de` and `<app>.<name>.my.mattiverse.de`. A wildcard `*.my.mattiverse.de` matches one label
   only (in Caddy, caddy-l4 and Traefik alike): list both depths or use a regular expression.
2. **No idle connections to 8081.** A proxy in front of port 80 must not reuse a connection to the edge for another visitor. The edge reads every
   request and closes a connection that is reused for another installation (the client then retries on a new one), so this is no longer a hole,
   but every reuse costs a retry. Caddy: `transport http { keepalive off }`; Traefik: a `serversTransport` with `maxIdleConnsPerHost: -1`;
   nginx: the default (no `keepalive` in an `upstream` for 8081). Better still, pass port 80 through at layer 4 (Option B).
3. **The client address must survive.** The service's limits (registrations per address, API calls per minute) and the address `/nic/update`
   stores are the client address the proxy in front of 8080 reports. Whatever passes TLS at layer 4 *in front of* the existing proxy hides the
   visitor from it, unless it sends the PROXY protocol and the existing proxy reads it. Without that, every visitor of the world is one client
   (five registrations a day for everybody), a forged `X-Forwarded-For` passes as the client, and the IP allow-lists of server1's other sites see
   a private address and let everybody in.

## 1. DNS of mattiverse.de (at its provider)

The wildcard `*.mattiverse.de → 203.0.113.10` exists already. It covers `services.mattiverse.de`, the zone `my.mattiverse.de` itself and every
name below it, so **the tunnel mode and the DynDNS page need nothing more**.

For the **direct mode** and **DynDNS for everybody** (the names then point to people's own addresses) delegate the zone to the service, once:

```
my.mattiverse.de.   3600  IN  NS  services.mattiverse.de.
```

(No glue record is needed: `services.mattiverse.de` lies outside the delegated zone and keeps resolving through the wildcard.) From then on the
service answers for `my.mattiverse.de` and everything below it (the wildcard no longer applies there):

- `my.mattiverse.de` itself (the DynDNS page) and every installation in tunnel mode: server1's address, `Services__Edge__PublicIpv4` (and
  `PublicIpv6`), which must be set;
- a DynDNS name or an installation in direct mode: the IPv4/IPv6 its router reported.

**53/udp and 53/tcp must reach the container** (port 5353 inside; step 4). Check with `dig +short @203.0.113.10 my.mattiverse.de SOA`.

## 2. Run the container

```sh
mkdir -p /opt/matos-services && cd /opt/matos-services
curl -fsSLO https://raw.githubusercontent.com/Real-TTX/MatOS/main/deploy/services/docker-compose.yml   # or copy the file
cat > .env <<'EOF'
SERVER1_IPV4=203.0.113.10
# SERVER1_IPV6=2001:db8::10
MATOS_SERVICES_ADMIN_EMAIL=you@example.com
MATOS_SERVICES_ADMIN_PASSWORD=a-long-random-password
EOF
docker compose up -d
curl -s http://127.0.0.1:8080/healthz    # ok
```

(The file is in the repository `Real-TTX/MatOS` (called Mattiverse until 2026-11). Until the branch `feature/matos` is merged into
`main`, copy the file instead.)

The admin area is `https://services.mattiverse.de/admin`. `MATOS_SERVICES_ADMIN_EMAIL` and `MATOS_SERVICES_ADMIN_PASSWORD` go together (set both
or neither; the service does not start with only one): they create the first administrator once, on the first start without any account. After
that, change the password and add or remove administrators in the admin area itself (Account, Users); changing the two variables changes
nothing. The old Basic auth of `/admin` is gone.

The compose file publishes 8080, 8443 and 8081 on `127.0.0.1` for a proxy on the host. If the proxy runs in Docker itself, put both on one network
instead (`docker network connect <proxy-network> matos-services`) and use `matos-services:8080` (and `:8443`, `:8081`) in the examples below.

The service trusts `X-Forwarded-For` and the PROXY protocol only from `Services__TrustedProxies` (default: the private ranges and loopback, which
covers a proxy on the host or in Docker). Make sure the proxy in front of 8080 **sets** `X-Forwarded-For` (Caddy and Traefik do by default; nginx:
`proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;`) and that it sees the real visitor (rule 3).

## 3. Publish it

### The API and the DynDNS page: `https://services.mattiverse.de` and `https://my.mattiverse.de` → 8080

An ordinary HTTPS site of the existing proxy. WebSockets must pass (the tunnel), and an idle timeout must be longer than the 30 s ping.

Caddy:

```
services.mattiverse.de, my.mattiverse.de {
	reverse_proxy 127.0.0.1:8080
}
```

nginx:

```nginx
server {
    listen 443 ssl;   # in the nginx variant of option A: listen 127.0.0.1:7443 ssl proxy_protocol;
    server_name services.mattiverse.de my.mattiverse.de;
    # ssl_certificate ... (as for the other sites)
    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;   # map $http_upgrade $connection_upgrade { default upgrade; '' close; }
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 120s;
        proxy_buffering off;
    }
}
```

Traefik: see the dynamic configuration under option A, router `matos-services-api`.

**Matcad** (when server1's proxy is a Matcad): two ordinary routes with TLS ending at Matcad, `services.mattiverse.de → matos-services:8080`
and `my.mattiverse.de → matos-services:8080` (the same container port; the service shows the DynDNS page for the host `my.mattiverse.de`).
Only the apex is such a route: the names below it (`anna.my.mattiverse.de`, `x.anna.my.mattiverse.de`) must stay passthrough to the edge (below).

### The edge: the names below `my.mattiverse.de` on 80/443

Two ways; pick one.

#### Option A: the existing proxy keeps 80/443 and routes by name

**Caddy** needs the [caddy-l4](https://github.com/mholt/caddy-l4) module (`xcaddy build --with github.com/mholt/caddy-l4`). As a listener wrapper it
looks at the SNI before Caddy's own TLS: connections for the zone leave untouched, everything else goes on to Caddy's sites as before (and keeps its
client address, so the API needs nothing more). With the PROXY protocol towards the edge, set `MATOS_SERVICES_PROXY_PROTOCOL=true` (and
`MATOS_SERVICES_HTTP_PROXY_PROTOCOL=true` for port 80) in `.env`, so the edge's limits per visitor work.

```
{
	servers :443 {
		listener_wrappers {
			layer4 {
				@matos tls sni_regexp ^([^.]+\.){1,2}my\.mattiverse\.de$
				route @matos {
					proxy {
						proxy_protocol v2
						upstream 127.0.0.1:8443
					}
				}
			}
			tls
		}
	}
}

# Port 80: plain HTTP, both depths, no idle connections to the edge (rule 2), the visitor's address by PROXY protocol.
http://*.my.mattiverse.de, http://*.*.my.mattiverse.de {
	reverse_proxy 127.0.0.1:8081 {
		transport http {
			keepalive off
			proxy_protocol v2
		}
	}
}
```

Where your caddy-l4 version has no `sni_regexp`, list both depths (`@matos tls sni *.my.mattiverse.de *.*.my.mattiverse.de`). The syntax of caddy-l4
has changed between versions: compare with its README.

**nginx** (`stream` with `ssl_preread`): the `stream` block takes 443 and passes everything that is not for the zone on to nginx's own HTTPS
sites, which move to an internal port (here `127.0.0.1:7443`). The stream server sends the PROXY protocol to both, so the existing sites, and
`services.mattiverse.de` among them, keep the visitor's address (rule 3); set `MATOS_SERVICES_PROXY_PROTOCOL=true` in `.env` for the edge.

```nginx
stream {
    map $ssl_preread_server_name $tls_upstream {
        ~^([^.]+\.){1,2}my\.mattiverse\.de$   127.0.0.1:8443;   # MatOS edge
        default                              127.0.0.1:7443;   # nginx's own HTTPS sites
    }

    server {
        listen 443;
        listen [::]:443;
        ssl_preread on;
        proxy_pass $tls_upstream;
        proxy_protocol on;
        proxy_connect_timeout 5s;
    }
}

http {
    # The existing sites, services.mattiverse.de included: "listen 443 ssl;" becomes
    #     listen 127.0.0.1:7443 ssl proxy_protocol;
    # and the address of the visitor comes from the header:
    set_real_ip_from 127.0.0.1;
    real_ip_header proxy_protocol;

    server {
        listen 80;
        listen [::]:80;
        server_name *.my.mattiverse.de;
        location / {
            proxy_pass http://127.0.0.1:8081;   # no upstream block with "keepalive" for it (rule 2)
            proxy_set_header Host $host;
            proxy_http_version 1.1;
            proxy_buffering off;
        }
    }
}
```

nginx cannot send the PROXY protocol on an HTTP route, so leave `MATOS_SERVICES_HTTP_PROXY_PROTOCOL` off: port 80 then counts nginx as one visitor
(the limits per address do not apply to a trusted proxy).

**Traefik** (v3, file provider; the entry points `web` :80 and `websecure` :443 exist):

```yaml
tcp:
  routers:
    matos-edge:
      entryPoints: [websecure]
      rule: "HostSNIRegexp(`^([^.]+\\.){1,2}my\\.mattiverse\\.de$`)"
      service: matos-edge
      tls:
        passthrough: true
  services:
    matos-edge:
      loadBalancer:
        proxyProtocol:
          version: 2          # with MATOS_SERVICES_PROXY_PROTOCOL=true; newer v3 releases set it in a TCP serversTransport
        servers:
          - address: "127.0.0.1:8443"

http:
  routers:
    matos-edge-http:
      entryPoints: [web]
      rule: "HostRegexp(`^([^.]+\\.){1,2}my\\.mattiverse\\.de$`)"
      service: matos-edge-http
    matos-services-api:
      entryPoints: [websecure]
      rule: "Host(`services.mattiverse.de`) || Host(`my.mattiverse.de`)"
      service: matos-services-api
      tls:
        certResolver: letsencrypt   # the resolver the other sites use
  serversTransports:
    no-keepalive:
      maxIdleConnsPerHost: -1       # rule 2
  services:
    matos-edge-http:
      loadBalancer:
        serversTransport: no-keepalive
        servers:
          - url: "http://127.0.0.1:8081"
    matos-services-api:
      loadBalancer:
        servers:
          - url: "http://127.0.0.1:8080"
```

Traefik's own routers keep the client address, so the API needs nothing more. A redirect of the whole `web` entry point to https
(`entryPoints.web.http.redirections`) would also catch `*.my.mattiverse.de`; the installations still get their certificates (TLS-ALPN-01 through 443),
but their own redirect is then Traefik's. Fine either way.

#### Option B: the container takes 80/443 and passes every other name to the existing proxy

The edge is in front: names in the zone go into the tunnels, every other name goes unchanged (raw TCP) to the existing proxy, which moves to other
ports. Port 80 passes the edge at layer 4 too, so rule 2 does not arise. In `docker-compose.yml` replace the edge ports and set the fallbacks:

```yaml
    ports:
      - "127.0.0.1:8080:8080"
      - "443:8443"
      - "80:8081"
    environment:
      # ... as before, plus:
      Services__Edge__FallbackHttps: "host.docker.internal:7443"
      Services__Edge__FallbackHttp: "host.docker.internal:7080"
      Services__Edge__FallbackProxyProtocol: "true"
    extra_hosts:
      - "host.docker.internal:host-gateway"
```

and let the existing proxy listen on 7080/7443 instead of 80/443 (on an address the container reaches: the Docker bridge, or a shared Docker
network with `proxy:443`/`proxy:80` as the fallbacks; keep 7080/7443 closed in the firewall). Its sites and its certificates keep working,
since the edge does not touch their bytes.

**`FallbackProxyProtocol` is required here** (rule 3): `services.mattiverse.de` itself reaches the existing proxy through the edge, and without the
header every visitor of every site of server1 is the container's address. The existing proxy must read the header on **both** ports, and only from
the container's network:

- Caddy: `servers { listener_wrappers { proxy_protocol { allow 172.16.0.0/12 } tls } }` (for :7443; :7080 the same without `tls`).
- nginx: `listen 7443 ssl proxy_protocol;` and `listen 7080 proxy_protocol;`, with `set_real_ip_from 172.16.0.0/12; real_ip_header proxy_protocol;`.
- Traefik: `entryPoints.websecure.proxyProtocol.trustedIPs: ["172.16.0.0/12"]`, the same for `web`.

One more consequence: when the MatOS Services container is down, every site of server1 is down with it.

#### The apex and custom domains

- **The apex `my.mattiverse.de` is not the edge's.** Every pattern above matches one or two labels **below** the zone (`^([^.]+\.){1,2}my\...`),
  never the zone itself, so `https://my.mattiverse.de` stays an ordinary site of the existing proxy (the route to 8080 above). In Option B the edge
  passes the apex on to the fallback like any other name outside the zone.
- **Custom domains** (docs/SERVICES.md, "Your own domain through the tunnel"): people's own names (`*.apps.example.com CNAME anna.my.mattiverse.de`)
  arrive at server1 with SNI/`Host` of *their* domain. With Option B they reach the edge by themselves. With Option A the layer-4 routing must
  send SNI it does not know to the edge (8443) instead of to the existing proxy, and list the existing proxy's own names instead (nginx: the
  `map` with the own sites → 7443 and `default 127.0.0.1:8443`; port 80 the same with a catch-all server to 8081). The edge routes only domains
  it has verified and passes anything else to its fallback, so set `Services__Edge__FallbackHttps`/`FallbackHttp` to the existing proxy then.

## 4. DNS (direct mode and DynDNS for everybody)

After the delegation (step 1), publish the DNS on server1's public address (systemd-resolved holds `127.0.0.53:53`, so not on all addresses):

```yaml
    ports:
      - "203.0.113.10:53:5353/udp"
      - "203.0.113.10:53:5353/tcp"
```

## Checks

```sh
curl -s https://services.mattiverse.de/healthz                                  # ok
curl -s https://services.mattiverse.de/api/v1/names/anna                        # {"name":"anna","available":true,...}
curl -s https://my.mattiverse.de/ | grep -o "Pick a name"                      # the DynDNS page
dig +short @203.0.113.10 my.mattiverse.de A                                     # the apex: server1 (the edge address)
curl -si -H 'Host: x.nobody.my.mattiverse.de' http://203.0.113.10/ | head -1    # HTTP/1.1 404 (the edge answers)
openssl s_client -connect 203.0.113.10:443 -servername example.mattiverse.de </dev/null | head -5   # an existing site's certificate
dig +short @203.0.113.10 my.mattiverse.de SOA                                   # direct mode: the zone answers
```

The client address must arrive (rule 3): register a test name from two different networks and look at the column *From* of
`https://services.mattiverse.de/admin/Installations` (sign in with `MATOS_SERVICES_ADMIN_EMAIL` and `MATOS_SERVICES_ADMIN_PASSWORD`; the Basic auth of before is gone). It must show the two public
addresses, not `127.0.0.1` or a `172.x` address. Give the test names back afterwards (or let them expire after 7 days unused).

## Update and backup

`docker compose pull && docker compose up -d`. The database migrates itself on start. Everything is in the volume `matos-services-data`
(`/data/services.db`, SQLite); the keys in it are hashes, so a copy of it opens no tunnel.

**Updating from the version with the Basic auth of `/admin`** (before the admin area had accounts): that version needed only
`MATOS_SERVICES_ADMIN_PASSWORD`. The new one refuses to start when only one of `MATOS_SERVICES_ADMIN_EMAIL` and
`MATOS_SERVICES_ADMIN_PASSWORD` is set, and with it the tunnel, the edge, the DNS and the API stay down. So **before** `docker compose pull`,
add `MATOS_SERVICES_ADMIN_EMAIL=<your e-mail address>` to the `.env` (it and the password become the first administrator on the first start),
or remove `MATOS_SERVICES_ADMIN_PASSWORD` (then no administrator is created, and `/admin` cannot be used until one is). Check with
`docker compose logs matos-services` after `up -d` that it started.
