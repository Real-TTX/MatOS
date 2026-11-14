<div align="center">

<img src="src/Mattiverse.Web/wwwroot/img/logo.svg" width="96" alt="Mattiverse" />

# Mattiverse

**Your Docker host as a home screen. Install, update and back up your apps with a tap.**

Mattiverse OS is a home screen for your server, like the one on your phone: widgets on a wallpaper, your apps as icons, a dock,
and windows for everything else. Pick an app in the store and Mattiverse installs it, gives it an address, keeps it up to date and backs it up.
Underneath, five programs do the real work and keep their own part of the configuration: [MatDock](https://github.com/Real-TTX/MatDock) (stacks, backups),
[Matcad](https://github.com/Real-TTX/Matcad) (addresses, HTTPS), [Matddns](https://github.com/Real-TTX/Matddns) (DNS records),
[MatIdentity](https://github.com/Real-TTX/MatIdentity) (one sign-in) and [MatSMTP](https://github.com/Real-TTX/MatSMTP) (the mail of all apps).
They live in the folder *System*; open one and you see what Mattiverse set up there.

</div>

![Mattiverse OS: the status bar, widgets on the Orbit wallpaper, the apps, the search and the dock](docs/images/home.png)

---

## What this is about

Self-hosting usually means a terminal, a pile of `docker-compose.yml` files and a browser full of `:8080` tabs. Mattiverse is a calm
layer on top: a **home screen** that feels like a phone or a tablet, a **store** with one-click installs, a **setup wizard** that asks for your
domain and DNS account once and wires the engines together, and **backups** you can actually restore.

It is the small, mobile-friendly sibling of **MatOS**. Its home screen is a phone's, its windows are a desktop's: several open side by side over the home screen, and the dock is the taskbar. Every app is a plain `docker compose` stack in MatDock:
you can edit it there, and Mattiverse keeps your change when it updates the app.

## At a glance

**Mattiverse OS**
- A **home screen**: a status bar with the time, running tasks, the theme and you; widgets of glass for today, the engines, the apps
  that run, the last backup, the activity and Docker; the apps as icons (a badge when one is not running), a search (key `/`) and a
  **dock** with the store, the apps, the backups and the settings
- **Windows like a desktop**: the store, an app, the backups, the settings ... each open in a window of their own over the home screen, with a title, Back,
  Reload, *open in a new tab* and three lights: close, put away (the app stays open, its icon shows a dot) and fill the screen. Move a window by
  its bar, size it at its edges, drop it at the top or a side of the screen to fill the screen or a half. The dock is the taskbar, the windows
  come back after a reload, and every app remembers where its window was. On a phone a window fills the screen, with a bar at the bottom to go home
- **The engines are part of the system**: a widget with their state, the folder *System*, and a window for each that shows what it keeps
  for which app. An engine whose image allows it (`MAT_FRAME_ANCESTORS`, see [How it works](#how-it-works)) opens right there in its window
- An installed app opens in a window too, or in a new tab when you choose that in its menu (right click or long press)
- **Apps bring their own icons and widgets**: an app can put several icons on the home screen (MatOffice: Docs, Sheets, Slides, Base), add entries to
  the menu of an icon (*New document*) and widgets with its numbers, all from its own description: Mattiverse has no code for any app
- **Apps connect themselves**: install MatDrive and MatOffice, and MatDrive opens its office files in MatOffice, with no key to copy. Each app says what it
  offers and what it uses; Mattiverse connects them, and every app shows its connections on one page (and works without Mattiverse, connected by hand)
- **Wallpapers** drawn for Mattiverse: *Orbit* (the planet of the logo, light or dark with the theme; the default), the Earth from orbit,
  a jungle, a lagoon and mountains seen from above, or none. Each person picks their own
- Inside a window Mattiverse looks and behaves like every other Mat app: it is built on **MatUi**, the toolkit of
  [MatDesign](https://github.com/Real-TTX/MatDesign), with its lists, forms, dialogs and dark mode

**Setup wizard** (first start, and again whenever you like)
- Installs the five **engines**: MatDock (required), Matcad, Matddns, MatIdentity (one sign-in) and MatSMTP (the mail of all apps); skip one if something else on the host does its job
- Your **domain** and your **DNS provider** (Cloudflare, Hetzner, Netcup), asked once: Matcad gets one wildcard certificate through the
  DNS challenge and a route `app.example.com` per app, Matddns keeps the DNS records on your home IP
- Everything the wizard created carries a **Managed by Mattiverse** badge in the engine

**Store**
- Catalog of the Mat apps (MatMail, MatFile, MatDrive, MatOffice, MatPaper, MatPlay, MatSplit, MatTrakr, Matmon, MatCMS, MatDo, MatBoard, MatShape, MatBu),
  [MatDesign](https://github.com/Real-TTX/MatDesign) (the design and identity of all of them) and a few infrastructure apps
- **Passwords are generated** and shown (hidden until you ask) on the app's page
- Add your **own apps** by dropping a compose file into `/data/catalog` (see [docs/CATALOG.md](docs/CATALOG.md))

**Operations**
- **Install, update, start, stop, restart, uninstall**: every long task is a job with a **live log**, and the status bar shows that it runs
- **Update** pulls newer images, re-creates only what changed, and backs up first if you want
- **Backups** are MatDock's: compose + `.env` + all volumes, optionally with the app stopped (consistent databases),
  **schedules with retention**, restore even after the app was uninstalled, download as `.tar`. They show up in MatDock too
- If you **change a stack in MatDock**, the next update keeps your version
- **Shared links** let someone without an account open one app

**Users**
- Local sign-in or **one sign-in through MatIdentity**, roles **Admin / User / Anonymous (shared link)**, sessions that **survive a restart**

## Screenshots

### The home screen

| Light | On a phone | A window on a phone |
|---|---|---|
| ![The home screen in the light theme](docs/images/home-light.png) | ![The home screen on a phone: widgets, apps and the dock](docs/images/mobile-home.png) | ![The store in a window on a phone: it fills the screen, with the bar to go home](docs/images/mobile-window.png) |

The picture at the top is the dark theme; *System* follows the device. On a phone the widgets stack, a window fills the screen and the
bar at the bottom goes home.

| The folder *System* | The menu of an app | Your wallpaper |
|---|---|---|
| ![The folder System: the five engines and all engines](docs/images/folder-system.png) | ![The menu of an app: open, in a new tab, always in a new tab, details, manage, and where it sits on the home screen](docs/images/app-menu.png) | ![Your account, Home screen: the wallpapers to choose from](docs/images/wallpapers.png) |

| Orbit | Earth | Jungle | Lagoon | Mountains |
|---|---|---|---|---|
| ![Orbit](src/Mattiverse.Web/wwwroot/img/wallpapers/orbit-dark-thumb.jpg) | ![Earth](src/Mattiverse.Web/wwwroot/img/wallpapers/earth-thumb.jpg) | ![Jungle](src/Mattiverse.Web/wwwroot/img/wallpapers/jungle-thumb.jpg) | ![Lagoon](src/Mattiverse.Web/wwwroot/img/wallpapers/lagoon-thumb.jpg) | ![Mountains](src/Mattiverse.Web/wwwroot/img/wallpapers/mountains-thumb.jpg) |

### Windows

![Three windows over the home screen: the store as the left half, the activity and the settings beside it; the dock marks the open ones](docs/images/windows.png)

Everything that is not the home screen opens in **a window**: the store above, an app, the activity, the settings. Several are open side by side:
move one by its bar, size it at its edges, bring one to the front with a click. The yellow light puts a window away and keeps the app (its icon
gets a dot), the red one closes it, the green one fills the screen.
Let go at the top of the screen a window fills it, at the left or right edge it takes that half. The dock is the taskbar: a dot under every
app that has a window, a click brings it to the front. The windows come back after a reload, and every app remembers where its window was.

| One window | The settings, with their sidebar | The menu of a window |
|---|---|---|
| ![The store in a window: the traffic lights, Back, the title, Reload and open in a new tab](docs/images/store.png) | ![The settings in a window: General, Engines, System and the setup wizard in a sidebar](docs/images/settings.png) | ![A right click on the bar of a window: reload, a new tab, fill the screen, a half, put away, close](docs/images/window-menu.png) |

| An app | Installing, with a live log |
|---|---|
| ![App details: address, credentials, stack and actions](docs/images/app-details.png) | ![The live log of an install](docs/images/install-log.png) |

Buttons read from positive to negative: **Open, Stop, Restart, Update, Back up**, then a gap, then **Uninstall**.

### The engines

| An engine in a window | Settings, Engines |
|---|---|
| ![MatDock in a window: its state and what it keeps for each app](docs/images/engine-window.png) | ![Settings, Engines: the five engines, their version and state, and what Mattiverse hands over](docs/images/engines.png) |

The window of an engine says what it keeps for which app (a stack in MatDock, a route in Matcad, a record in Matddns, a client in
MatIdentity, a relay user in MatSMTP). It opens the engine itself in a window when the engine allows Mattiverse to frame it, otherwise in a
new tab. The *Engines* tab of the settings hands the domain and the DNS account over again (*Connect again*).

![Settings, System: Docker as MatDock reports it, with processor, memory and disk, and the version and data folder of Mattiverse](docs/images/system.png)

*System* shows Docker as MatDock reports it: the load of the processor, the memory and the disk of the host, and how many containers run.

### Manage an app

| Overview | Settings |
|---|---|
| ![Manage MatDock: the tiles and actions its /api describes](docs/images/manage.png) | ![Manage MatDock: its settings as a form](docs/images/manage-settings.png) |

The *Manage* tab of an app that speaks the Mat platform contract is drawn from the app's own `/api`: the tiles with its numbers,
the actions it offers and its settings as a form. Mattiverse knows nothing about the app beforehand.

### Activity and backups

![The activity: installs, updates and backups with their state and duration](docs/images/jobs.png)

| Backups | A schedule |
|---|---|
| ![Backups: app, time, kind, size and state](docs/images/backups.png) | ![A backup schedule: apps, time, how many are kept](docs/images/backup-schedules.png) |

One list for all backups: those MatDock made (also visible in MatDock) next to those of Mattiverse's own runner.
Every list has its toolbar on top (a search field, *Filter* and *Sort* open dialogs), its actions below and pagination directly under the table.

| Apps | The tools of a list |
|---|---|
| ![The apps list](docs/images/apps.png) | ![The filter dialog of a list](docs/images/toolbar-filter.png) |

### The setup wizard

| The engines | The DNS account |
|---|---|
| ![Setup wizard: the five engines, MatDock required](docs/images/wizard.png) | ![Setup wizard: the DNS provider and its API token](docs/images/wizard-dns.png) |

The wizard warns when a port it needs is taken (for example when another proxy holds 80 and 443) and leaves
alone what is installed already. The DNS account is asked once and handed to Matcad and Matddns.

## Quick start

One command. The Docker socket is how Mattiverse installs and manages your apps.

```bash
docker run -d --name mattiverse --restart unless-stopped -p 8030:8030 \
  -v mattiverse-data:/data \
  -v /var/run/docker.sock:/var/run/docker.sock \
  ghcr.io/real-ttx/mattiverse:latest
```

or with Compose:

```yaml
name: mattiverse

services:
  mattiverse:
    image: ghcr.io/real-ttx/mattiverse:latest
    container_name: mattiverse
    restart: unless-stopped
    ports:
      - "8030:8030"
    volumes:
      - mattiverse-data:/data
      - /var/run/docker.sock:/var/run/docker.sock

volumes:
  mattiverse-data:
```

Open **http://localhost:8030**. The first start asks for the administrator account and then walks you through the
setup wizard. An update is `docker compose pull && docker compose up -d`; everything lives in the `mattiverse-data` volume.

| Tag | Built from | Use it for |
|---|---|---|
| `ghcr.io/real-ttx/mattiverse:latest` | `main` | releases |
| `ghcr.io/real-ttx/mattiverse:nightly` | `dev` | the newest features |

> **The Docker socket is root on the host.** Mattiverse needs it, so keep the UI off the public internet:
> put it behind your reverse proxy with the apps' own logins, or on a VPN.

### Settings that matter

| Variable | Default | Meaning |
|---|---|---|
| `Mattiverse__DataPath` | `/data` | Data folder: database, settings, compose files, backups |
| `Mattiverse__SessionLifetimeDays` | `30` | How long a sign-in stays valid |
| `Mattiverse__NetworkName` | `mattiverse` | Shared Docker network of the engines and the apps' UIs |
| `Mattiverse__NetworkSubnet` | – | Subnet (like `10.253.200.0/24`) for that network, if Docker's default address pools are used up |
| `Mattiverse__ProjectPrefix` | `mattiverse` | The start of the compose project names (`mattiverse-<app>`); a second Mattiverse on the same host needs its own |
| `Mattiverse__HostPortRangeStart` / `End` | `8100` / `8999` | Pool for app ports when the preferred port is taken |
| `Mattiverse__Admin__Seed` | `false` | Create the admin from `__Username` / `__Password` (headless installs; the password must be changed at the first sign-in) |
| `Mattiverse__DockerHost` | – | Use another Docker endpoint than the mounted socket |
| `MATTIVERSE_PORT` | `8030` | Host port in the compose files |
| `TZ` | – | Time zone for schedules and the apps |

## How it works

```
/data
├─ mattiverse.db        SQLite: users, sessions, apps, jobs, shared links (and the backups of apps Mattiverse runs itself)
├─ config/system.json   JSON: name, domain, DNS provider, the wizard's choices
├─ dataprotection-keys/ keys for the cookies and the secrets (so sessions survive restarts)
├─ catalog/             your own app definitions (*.yml)
└─ apps/<app>/          only for stacks Mattiverse runs itself (MatDock, and apps while MatDock is not up): docker-compose.yml, .env
```

- An app is a **compose project** named `mattiverse-<app>`. Mattiverse renders the catalog entry into one compose file (container
  names, labels, the shared network, the host port of the UI, an `x-matdock` block) plus a `.env` with your answers and creates it as a
  **stack in MatDock** through MatDock's REST API. The stack is yours to edit there; an update keeps your version.
- Status, containers and logs are read straight from Docker, so the home screen stays correct when an engine is down.
- Containers of an app are only attached to the **shared network** `mattiverse` when they must be reachable by
  the proxy (the UI service); databases stay private to their stack.
- With a domain, every install creates the route `<app>.<domain>` in **Matcad** and the record in **Matddns**; uninstalling removes both.
  Mattiverse itself gets a route too. With a DNS account Matcad issues one wildcard certificate through the DNS challenge.
- **Backups** are made by MatDock (a bundle of volumes, compose and `.env`) and listed in MatDock too; the apps that Mattiverse runs itself
  are backed up by Mattiverse's own runner. One page shows both.
- **The window** is a frame of Mattiverse's own pages; a page in it drops its menu and header and keeps only its content. An engine is shown
  in a window when it allows it: Mattiverse passes its own addresses in **`MAT_FRAME_ANCESTORS`** (its route, this machine with its port,
  the addresses people signed in from), and the engine answers with `frame-ancestors` for them (MatPlatform 1.5.0). Mattiverse checks the
  headers first and offers a new tab, with the reason, when an engine does not allow it.
- Without an engine's API (an older image, or not installed yet) Mattiverse falls back to its own `docker compose` runner, so nothing stops working.
- **Apps connect through bindings** (MatDesign [docs/PLATFORM.md](https://github.com/Real-TTX/MatDesign/blob/dev/docs/PLATFORM.md), contract 2): an app says in its `/api`
  what it provides and consumes (`office.edit`, ...). After an install Mattiverse asks a provider for the binding of every consumer (`PUT /api/bindings/...`)
  and passes the answer to the consumer as variables (`MAT_OFFICE_EDIT_URL`, `_KEY`, ...), with no code for any app. The same description gives the icons,
  widgets and menu entries of the app on the home screen.
- The design decisions (what the engines own, where the compose files are, the home screen, how this relates to MatOS) are in
  [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md); the contract with the engines is [docs/ENGINES.md](docs/ENGINES.md).

## Roles

| Role | Can |
|---|---|
| **Admin** | everything: store, installs, updates, backups, users, settings, activity, the engines |
| **User** | see and open the apps (no credentials, no actions) |
| **Anonymous** | open the one app behind a **shared link** (`/s/<token>`); links expire and can be revoked |

## Development

Stack: ASP.NET Core 10 (Razor Pages), C#, SQLite via EF Core, the `docker` CLI against the mounted socket, plain
JavaScript. No Node build step.

```bash
dotnet build Mattiverse.slnx          # 0 warnings expected
dotnet test                           # unit tests (catalog, installer files, backups, schedules, parsing, frames, the markup of the pages)
./scripts/redeploy.ps1                # rebuild the container and redeploy the dev stack
./scripts/redeploy.ps1 -Fresh         # ... with a clean data volume (the first-run setup appears again)
(cd tests/e2e && npm install && node run.js)   # the pages and the home screen in a real browser against the dev stack
./scripts/update-matui.ps1            # bring a newer MatUi from a running MatDesign into wwwroot/matui
node scripts/wallpapers/render.js     # draw the wallpapers again
```

The dev stack is the app plus a SQLite web viewer (the SQLite counterpart of pgAdmin) on
`http://localhost:8021`. In `Development` a ready-made account **`admin@example.com` / `Start12345`** exists, so testing never
touches the real administrator. `MATTIVERSE_PORT` changes the host port when 8030 is taken.

Testing means rebuilding the container and redeploying the stack, nothing else.

### Branches and versions

| Branch | Purpose | Image tag | Version |
|---|---|---|---|
| `main` | Release | `latest`, `release`, `<version>` | `<major>.<minor>.<build>-<yyyyMMdd>` |
| `dev` | Development | `nightly`, `<version>` | `nightly-<build>-<yyyyMMdd>` |
| local | – | – | `local-<yyyyMMdd>` |

`major` and `minor` live in [`Directory.Build.props`](Directory.Build.props); the build number is the run number of the
GitHub Action, which also builds the multi-arch image (`amd64`, `arm64`).

## Status

| Area | Status |
|---|---|
| Mattiverse OS: home screen, widgets, windows (move, size, halves, taskbar), the folder *System*, wallpapers, phone layout | ✅ |
| The look of the Mat family inside a window (MatUi of MatDesign: lists, forms, dialogs, dark mode) | ✅ |
| Store, setup wizard, themes | ✅ |
| Install / update / uninstall / start / stop with live logs | ✅ |
| Backups: manual, scheduled, retention, restore, download (MatDock's, merged with Mattiverse's own) | ✅ |
| Local users, roles, persistent sessions, shared links | ✅ |
| Apps as MatDock stacks, routes in Matcad, DNS records in Matddns, domain and DNS account asked once | ✅ |
| Stack edited in MatDock survives an update | ✅ |
| Single sign-on through MatIdentity (installed and wired by Mattiverse: the apps and Mattiverse itself) | ✅ |
| Mail through MatSMTP (installed by the wizard; a relay user and the `MATSMTP_*` variables for every app that sends mail) | ✅ (the apps still have to read the variables) |
| Apps that describe themselves (platform contract): the tab Manage with their tiles, settings and buttons | ✅ |
| The engines inside a window (`MAT_FRAME_ANCESTORS`, MatPlatform 1.5.0) | ✅ in Mattiverse; the engines on their `feature/frame-ancestors` branches, in their images with the next release |
| Apps that connect themselves (bindings) and bring their own icons, widgets and menu entries (contract 2, MatPlatform 2.0) | ✅ MatOffice and MatDrive; the engines move onto the bindings next |
| Widgets you arrange yourself, widgets with the data of the person who is looking, a search across the apps | planned |
| Microsoft Entra ID sign-in | planned (the login is already behind `AuthService` / `SessionService`) |
| Remote backup targets (SMB, off-site) | MatDock's backup targets |

## License

No license file is included yet. If you intend others to reuse this, add a `LICENSE` (for example MIT) at the repo root.
