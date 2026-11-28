<div align="center">

<img src="src/MatOS.Web/wwwroot/img/logo.svg" width="96" alt="MatOS" />

# MatOS

**Your Docker host as a home screen. Install, update and back up your apps with a tap.**

MatOS is a home screen for your server, like the one on your phone: widgets on a wallpaper, your apps as icons, a dock,
and windows for everything else. Pick an app in the store and MatOS installs it, gives it an address, keeps it up to date and backs it up.
Underneath, five programs do the real work and keep their own part of the configuration: [MatDock](https://github.com/Real-TTX/MatDock) (stacks, backups),
[Matcad](https://github.com/Real-TTX/Matcad) (addresses, HTTPS), [Matddns](https://github.com/Real-TTX/Matddns) (DNS records),
[MatIdentity](https://github.com/Real-TTX/MatIdentity) (one sign-in) and [MatSMTP](https://github.com/Real-TTX/MatSMTP) (the mail of all apps).
They live in the folder *System*; open one and you see what MatOS set up there.

</div>

![MatOS: the status bar, widgets on the Orbit wallpaper, the apps, the search and the dock](docs/images/home.png)

---

## What this is about

Self-hosting usually means a terminal, a pile of `docker-compose.yml` files and a browser full of `:8080` tabs. MatOS is a calm
layer on top: a **home screen** that feels like a phone or a tablet, a **store** with one-click installs, a **first start** that needs no Docker knowledge (an account, the engines, how you reach
your apps, one button) and wires the engines together, and **backups** you can actually restore.

It replaces **matOS**, the older desktop for the Docker host (now [Real-TTX/MatOS-legacy](https://github.com/Real-TTX/MatOS-legacy), retired), and was called **Mattiverse** until November 2026. Its home screen is a phone's, its windows are a desktop's: several open side by side over the home screen, and the dock is the taskbar. Every app is a plain `docker compose` stack in MatDock:
you can edit it there, and MatOS keeps your change when it updates the app.

## At a glance

**MatOS**
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
  the menu of an icon (*New document*) and widgets with its numbers, all from its own description: MatOS has no code for any app
- **Apps connect themselves**: install MatDrive and MatOffice, and MatDrive opens its office files in MatOffice, with no key to copy. Each app says what it
  offers and what it uses; MatOS connects them, and every app shows its connections on one page (and works without MatOS, connected by hand)
- **Wallpapers** drawn for MatOS: *Orbit* (the planet of the logo, light or dark with the theme; the default) and twelve more, from the Earth seen from orbit
  to a skyline at night and northern lights, or none. Each person picks their own
- Inside a window MatOS looks and behaves like every other Mat app: it is built on **MatUi**, the toolkit of
  [MatDesign](https://github.com/Real-TTX/MatDesign), with its lists, forms, dialogs and dark mode

**First start**, and the **setup wizard** to change it later
- One calm assistant: Welcome (is this computer ready?), your account, how you reach your apps (a free address `<name>.my.mattiverse.de`, your own domain, or only at home), one button, and a live progress screen with *Try again*
- Installs its **engines** by itself: MatDock, Matcad, MatIdentity (one sign-in) and MatSMTP (the mail of all apps), and Matddns when your own domain needs an address updater; which ones follows from your answers
- The account of the first start is the one sign-in afterwards: MatIdentity gets the same e-mail and password
- A short tour of the home screen on the first visit
- Your **domain** and your **DNS provider** (Cloudflare, Hetzner, Netcup), asked once: Matcad gets one wildcard certificate through the
  DNS challenge and a route `app.example.com` per app, Matddns keeps the DNS records on your home IP (or on your fixed IP, if you have one);
  when the router does not let the connections in, MatOS takes the tunnel of MatOS Services instead, under the same addresses
- Everything the wizard created carries a **Managed by matos** badge in the engine

**Store**
- Like an app store: the **app of the day** with a picture, **Made for MatOS** (the Mat apps that sign in with your MatOS account), a **shelf per category**
  with See all, a search, filters and sorting; every app a card with its icon, its line, marks for **the one sign-in** and **mail through MatOS**, and Get or Open
- The page of an app: its **pictures**, what it gets from MatOS, its facts, Open and **Update** when it is installed, and more apps of its category
- Catalog of the Mat apps (MatBoard, MatBu, MatCloud, MatCMS with MatCMS Cloud, MatContactHub, MatDo, MatDown, MatDrive, MatFile, MatGame, MatMail, MatOffice, MatPaper, MatPhoto, MatPlay,
  MatRegistry, MatShape, MatSplit, MatTrakr, and as first versions MatBill, MatCan, Matmon, MatNotes, MatPhone, MatTicket; announced: MatAi, MatBiz),
  [MatDesign](https://github.com/Real-TTX/MatDesign) (the design and identity of all of them), a few infrastructure apps, and **well-known apps**: Nextcloud,
  Immich, Jellyfin, Vaultwarden, Paperless-ngx, Uptime Kuma and Home Assistant (Nextcloud, Vaultwarden and Paperless-ngx sign in with your MatOS account, Nextcloud only under https, and mail through MatOS)
- Every app has a **state** (Integrated, Beta, Alpha, Preview, Third-party; see [Apps and their state](#apps-and-their-state)); the store shows it where it tells something
- **Passwords are generated** and shown (hidden until you ask) on the app's page
- Add your **own apps** by dropping a compose file into `/data/catalog` (see [docs/CATALOG.md](docs/CATALOG.md))

**Operations**
- **Install, update, start, stop, restart, uninstall**: every long task is a job with a **live log**, and the status bar shows that it runs
- **Update** pulls newer images, re-creates only what changed, and backs up first if you want; **automatic updates** do it at night (off, apps and engines, or MatOS too)
- **MatOS updates itself**: it shows a new version, and one click (or the night) restarts it on it, with the old version put back if the new one does not start
- **The updates of this computer** too (Settings, *Updates*, *This computer*): the system updates waiting (how many for security, a new Docker), *Install updates*
  with a live log, and *Restart the computer*, which waits on the page until it is back. MatDock does the work. MatOS updates Debian and Ubuntu; any other system (Docker Desktop too) it names and leaves to its maker
- **Backups** are MatDock's: compose + `.env` + all volumes, optionally with the app stopped (consistent databases),
  **schedules with retention**, restore even after the app was uninstalled, download as `.tar`. They show up in MatDock too.
  **Kept on another machine too**: an SFTP folder, a WebDAV folder (Nextcloud) or a network share (SMB), tested when it is saved, chosen per backup and per schedule
- If you **change a stack in MatDock**, the next update keeps your version
- The **Task Manager** (`/Tasks`): the processor and memory of the host, what its containers send and write, and how full its disk is, with live charts;
  every container grouped by app (the apps, the system, the rest of the host) with its CPU, memory, network and disk, sorted by what uses the most.
  The log of each; the apps can be started, stopped and restarted, the engines restarted
- **Nodes** (Settings, *Nodes*): more Docker hosts, like the VMs of a homelab, reached by SSH through MatDock. An app is installed on the node you choose,
  moved to another later, backed up and updated there; the Task Manager has a tab per node
- **Volumes** (`/Volumes`): the volumes of the apps and your own, with their size and who uses them; make one **on this machine or a network share** (SMB or NFS of
  your NAS), tested before it is saved, its password never shown again. An app can keep a **data folder** there (MatPhoto's photo library), chosen at the install or
  later on the app's page. A **file explorer** for every volume: folders, upload, download, rename, move, delete, a preview of pictures and texts
- **Shared links** let someone without an account open one app

**Users**
- Local sign-in or **one sign-in through MatIdentity**, roles **Admin / User / Anonymous (shared link)**, sessions that **survive a restart**
- **People**: the people, groups and what each may use, kept in MatIdentity and managed in MatOS
- **A second step for the sign-in**: your account has a tab *Security* that opens the page for the authenticator app, the recovery codes and the
  passkeys, in the look of MatOS and in a new tab (passkeys do not work inside a window). The tab *Password* says which password it changes:
  the one of the single sign-on, or MatOS's own, which is only the way in when the single sign-on does not answer

## Screenshots

All pictures come from a test install with three apps from the store (MatDo, MatDrive and Uptime Kuma), set up through its first start.

### The first start

| Welcome | Your account | The engines |
|---|---|---|
| ![Welcome: this computer is ready](docs/images/setup-welcome.png) | ![Your account: name, e-mail and password](docs/images/setup-account.png) | ![The engines MatOS installs, in plain words, and the question of the updates](docs/images/setup-engines.png) |

| How you reach your apps | Setting up | Ready |
|---|---|---|
| ![How do you reach your apps: a free address, your own domain, or only at home](docs/images/setup-choose.png) | ![Setting up your MatOS: a line per step, live](docs/images/setup-progress.png) | ![Your MatOS is ready: every step done](docs/images/setup-progress-done.png) |

*Next* on the engines starts the first installs while you answer the next question. The progress screen shows every step as it
happens, with *Try again* for one that did not work. Then come your sign-in for all apps and the mail, and the home screen opens with *Getting started*:

| Your sign-in | Mail | Getting started |
|---|---|---|
| ![Your account in MatIdentity: the same e-mail and password for every app](docs/images/setup-signin.png) | ![Notifications and mail: MatSMTP and where MatOS writes to you](docs/images/setup-mail.png) | ![Getting started: a few things to do next, in a window](docs/images/start.png) |

### The home screen

| Light | On a phone | A window on a phone |
|---|---|---|
| ![The home screen in the light theme](docs/images/home-light.png) | ![The home screen on a phone: widgets, apps and the dock](docs/images/mobile-home.png) | ![The store in a window on a phone: it fills the screen, with the bar to go home](docs/images/mobile-window.png) |

The picture at the top is the dark theme; *System* follows the device. On a phone the widgets stack, a window fills the screen and the
bar at the bottom goes home.

| The folder *System* | The menu of an app | Your wallpaper |
|---|---|---|
| ![The folder System: the five engines and their overview](docs/images/folder-system.png) | ![The menu of an app: open, in a new tab, always in a new tab, its own entries, details, manage, and where it sits on the home screen](docs/images/app-menu.png) | ![Your account, Home screen: the wallpapers to choose from](docs/images/wallpapers.png) |

| Orbit | Earth | Jungle | Lagoon | Mountains | Dawn | Coast |
|---|---|---|---|---|---|---|
| ![Orbit](src/MatOS.Web/wwwroot/img/wallpapers/orbit-dark-thumb.jpg) | ![Earth](src/MatOS.Web/wwwroot/img/wallpapers/earth-thumb.jpg) | ![Jungle](src/MatOS.Web/wwwroot/img/wallpapers/jungle-thumb.jpg) | ![Lagoon](src/MatOS.Web/wwwroot/img/wallpapers/lagoon-thumb.jpg) | ![Mountains](src/MatOS.Web/wwwroot/img/wallpapers/mountains-thumb.jpg) | ![Dawn](src/MatOS.Web/wwwroot/img/wallpapers/dawn-thumb.jpg) | ![Coast](src/MatOS.Web/wwwroot/img/wallpapers/coast-thumb.jpg) |

| Dunes | City | Forest | Aurora | Nebula | Marble |
|---|---|---|---|---|---|
| ![Dunes](src/MatOS.Web/wwwroot/img/wallpapers/dunes-thumb.jpg) | ![City](src/MatOS.Web/wwwroot/img/wallpapers/city-thumb.jpg) | ![Forest](src/MatOS.Web/wwwroot/img/wallpapers/forest-thumb.jpg) | ![Aurora](src/MatOS.Web/wwwroot/img/wallpapers/aurora-thumb.jpg) | ![Nebula](src/MatOS.Web/wwwroot/img/wallpapers/nebula-thumb.jpg) | ![Marble](src/MatOS.Web/wwwroot/img/wallpapers/marble-thumb.jpg) |

### Windows

![Three windows over the home screen: the store as the left half, the activity and the settings beside it; the dock marks the open ones](docs/images/windows.png)

Everything that is not the home screen opens in **a window**: the store above, an app, the activity, the settings. Several are open side by side:
move one by its bar, size it at its edges, bring one to the front with a click. The yellow light puts a window away and keeps the app (its icon
gets a dot), the red one closes it, the green one fills the screen.
Let go at the top of the screen a window fills it, at the left or right edge it takes that half. The dock is the taskbar: a dot under every
app that has a window, a click brings it to the front. The windows come back after a reload, and every app remembers where its window was.

| The settings, with their sidebar | The menu of a window |
|---|---|
| ![The settings in a window: General, Address, Engines, Nodes, Updates, System and the setup wizard in a sidebar](docs/images/settings.png) | ![A right click on the bar of a window: reload, a new tab, fill the screen, a half, put away, close](docs/images/window-menu.png) |

### The store

| The store | The page of an app |
|---|---|
| ![The store: the app of the day, Made for MatOS and a shelf per category](docs/images/store.png) | ![The page of MatPhoto in the store: its state, Install, what it gets from MatOS and its pictures](docs/images/store-app.png) |

| An app | Installing, with a live log |
|---|---|
| ![App details: address, the first sign-in, stack and actions](docs/images/app-details.png) | ![The log of an install](docs/images/install-log.png) |

Buttons read from positive to negative: **Open, Stop, Restart, Update, Back up**, then a gap, then **Uninstall**.

### The Task Manager

![The Task Manager: processor, memory, network, disk and storage of the host with charts, and every container grouped by app](docs/images/tasks.png)

The host at the top (live charts while the page is open), below it every container grouped by app: the apps, the system (MatOS and its engines)
and the rest of the host, sorted by what uses the most. Every container shows its log; an app can be started, stopped and restarted, an engine restarted.
With more nodes there is a tab per node.

### Volumes and files

| Volumes | The file explorer | A preview |
|---|---|---|
| ![Volumes: your own and those of the apps, with kind, app, users and size](docs/images/volumes.png) | ![The file explorer of a volume: folders and files, upload, new folder](docs/images/files.png) | ![The preview of a picture in the file explorer](docs/images/files-preview.png) |

A volume on this machine or a network share (SMB, NFS) of your NAS, tested before it is saved. An app keeps a data folder there when it offers one
(MatPhoto's photo library). The file explorer uploads, downloads, renames, moves and deletes, and shows pictures and texts.

### The engines

| An engine in a window | Settings, Engines |
|---|---|
| ![MatDock in a window: its state and what it keeps for each app](docs/images/engine-window.png) | ![Settings, Engines: the five engines, their version and state, and what MatOS hands over](docs/images/engines.png) |

The window of an engine says what it keeps for which app (a stack in MatDock, a route in Matcad, a record in Matddns, a client in
MatIdentity, a relay user in MatSMTP). It opens the engine itself in a window when the engine allows MatOS to frame it, otherwise in a
new tab. The *Engines* tab of the settings hands the domain and the DNS account over again (*Connect again*).

| Updates | Nodes | System |
|---|---|---|
| ![Settings, Updates: MatOS's own version and a newer one, this computer, and what updates by itself at night](docs/images/updates.png) | ![Settings, Nodes: this machine and how to add another Docker host](docs/images/nodes.png) | ![Settings, System: Docker as MatDock reports it, with processor, memory and disk, and the version and data folder of MatOS](docs/images/system.png) |

*Updates* has MatOS's own version (one click, or the night), the updates of this computer, and what updates by itself. *Nodes* adds more Docker hosts.
*System* shows Docker as MatDock reports it: the load of the processor, the memory and the disk of the host, and how many containers run.

### Manage an app

| Overview | Settings |
|---|---|
| ![Manage MatDock: the tiles and actions its /api describes](docs/images/manage.png) | ![Manage MatDock: its settings as a form](docs/images/manage-settings.png) |

The *Manage* tab of an app that speaks the Mat platform contract is drawn from the app's own `/api`: the tiles with its numbers,
the actions it offers and its settings as a form. MatOS knows nothing about the app beforehand.

### Activity and backups

![The activity: installs, updates and backups with their state and duration](docs/images/jobs.png)

| Backups | A schedule | A place on another machine |
|---|---|---|
| ![Backups: app, time, kind, size and state](docs/images/backups.png) | ![The backup schedules: apps, time, how many are kept](docs/images/backup-schedules.png) | ![A backup target: SFTP, WebDAV or SMB, tested when it is saved](docs/images/backup-target.png) |

One list for all backups: those MatDock made (also visible in MatDock) next to those of MatOS's own runner. A backup is kept on this machine
or at a target (an SFTP folder, a WebDAV folder such as Nextcloud, a network share), chosen per backup and per schedule.
Every list has its toolbar on top (a search field, *Filter* and *Sort* open dialogs), its actions below and pagination directly under the table.

| Apps | The tools of a list |
|---|---|
| ![The apps list](docs/images/apps.png) | ![The filter dialog of a list](docs/images/toolbar-filter.png) |

### People and the setup wizard

| People | The setup wizard | Its addresses |
|---|---|---|
| ![People: the people of MatIdentity, their role and what they may use](docs/images/people.png) | ![The setup wizard: run it again to change the addresses, the engines or the DNS account](docs/images/wizard.png) | ![The setup wizard: where your apps live](docs/images/wizard-addresses.png) |

*People* manages the people, groups and what each may use, kept in MatIdentity. The setup wizard changes later what the first start set up:
the addresses, the engines and the DNS account. It warns when a port it needs is taken and leaves alone what is installed already.

## Quick start

MatOS is for people who do not want to deal with Docker: you start **one** container, and MatOS sets up everything else itself (its
five engines, then your apps). One command; the Docker socket is how MatOS installs and manages your apps.

```bash
docker run -d --name matos --restart unless-stopped -p 8030:8030 \
  -v matos-data:/data \
  -v /var/run/docker.sock:/var/run/docker.sock \
  ghcr.io/real-ttx/matos:latest
```

or with Compose:

```yaml
name: matos

services:
  matos:
    image: ghcr.io/real-ttx/matos:latest
    container_name: matos
    restart: unless-stopped
    ports:
      - "8030:8030"
    volumes:
      - matos-data:/data
      - /var/run/docker.sock:/var/run/docker.sock

volumes:
  matos-data:
```

Open **http://localhost:8030** and the assistant of the first start takes over, in plain words:

1. **Welcome**: it checks that this computer is ready (and says the one line to add when the Docker socket is missing).
2. **Your account**: your name, your e-mail (it is your user name) and a password.
3. **Engines**: what MatOS installs, in plain words. *Next* starts the app manager and mail right away, while you answer the next question.
4. **How do you reach your apps?** A free address `<name>.my.mattiverse.de` (recommended: from anywhere, HTTPS included, no router settings),
   your own domain with the account of its DNS provider, or only at home. *Advanced* has the address of this computer.
5. **Set up MatOS**: one button. A calm screen shows every step (the app manager, web addresses and HTTPS, the address updater, one sign-in
   for all apps, mail, your address) as it happens, with *Try again* and the details for a step that did not work. The last step
   checks whether your apps can be reached from the internet, and the end says it in one sentence.

### Your own domain

With your own domain (Cloudflare, Hetzner or Netcup; for Netcup the customer number, the API key and the API password of the CCP), the
assistant asks one more question: **does your internet connection have a fixed IP address?** *No, it changes*, *I'm not sure* (the default)
or *Yes, it is fixed*. Most home connections change their address now and then; unless you say *Yes*, MatOS keeps your domain on it by itself
(Matddns is the DynDNS client: it notices a new address and updates the records). With *Yes* you enter the address (MatOS fills in the one
it sees, when it knows it), and the records point there and stay put. Settings, General changes the answer at any time.

Then MatOS checks whether your apps can be reached from the internet, and it **picks the way in by itself**: directly when your router lets
the ports 80 and 443 through to this computer, else through the tunnel of MatOS Services, which needs no router settings. The addresses of your
apps are the same either way, and MatOS checks again every day and switches when something changed. You see one status ("Your apps can be
reached from anywhere", or "only at home" with what to do about it, a FRITZ!Box included); Settings, Address has the details and *Only
through my router* for those who want to forward the ports themselves.

Then your home screen opens with a short tour. The same e-mail and password sign you in afterwards, also when MatIdentity (the one sign-in) is
there. To change the setup later: Settings, *Setup wizard*. **Updates:** MatOS shows when it has a new version (a mark on *Settings*) and updates
itself with one click in Settings, *Updates*; a version that does not start is put back by itself. It can also update your apps, the engines and
itself at night (the first start asks once; [how it works](docs/ARCHITECTURE.md#how-matos-updates-itself)). By hand it is still
`docker compose pull && docker compose up -d`; everything lives in the
`matos-data` volume.

| Tag | Built from | Use it for |
|---|---|---|
| `ghcr.io/real-ttx/matos:latest` | `main` | releases |
| `ghcr.io/real-ttx/matos:nightly` | `dev` | the newest features |

> **The Docker socket is root on the host.** MatOS needs it, so keep the UI off the public internet:
> put it behind your reverse proxy with the apps' own logins, or on a VPN.

### Settings that matter

| Variable | Default | Meaning |
|---|---|---|
| `MatOS__DataPath` | `/data` | Data folder: database, settings, compose files, backups |
| `MatOS__SessionLifetimeDays` | `30` | How long a sign-in stays valid |
| `MatOS__NetworkName` | `matos` | Shared Docker network of the engines and the apps' UIs (an install of Mattiverse keeps `mattiverse`) |
| `MatOS__NetworkSubnet` | – | Subnet (like `10.253.200.0/24`) for that network, if Docker's default address pools are used up |
| `MatOS__ProjectPrefix` | `matos` | The start of the compose project names (`matos-<app>`; an install of Mattiverse keeps `mattiverse`); a second MatOS on the same host needs its own |
| `MatOS__HostPortRangeStart` / `End` | `8100` / `8999` | Pool for app ports when the preferred port is taken |
| `MatOS__Admin__Seed` | `false` | Create the admin from `__Username` / `__Password` (headless installs; the password must be changed at the first sign-in) |
| `MatOS__DockerHost` | – | Use another Docker endpoint than the mounted socket |
| `MATOS_PORT` | `8030` | Host port in the compose files |
| `TZ` | – | Time zone for schedules and the apps |

### Upgrading from Mattiverse

MatOS was called Mattiverse until November 2026. An install of Mattiverse keeps working: MatOS takes over its data volume and keeps the
names its apps run under (the Docker network and the projects `mattiverse-<app>`), see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#renamed-from-mattiverse).
Only the data volume has a new name: copy the old one once (it stays as it is, as a backup) and start MatOS on the copy.

With Compose, in the folder of the new `docker-compose.yml`:

```bash
docker stop mattiverse && docker rm mattiverse   # Mattiverse must be gone before the copy; its volume and the apps stay
docker compose up --no-start                     # creates the volume of MatOS (matos_matos-data)
docker run --rm -v mattiverse_mattiverse-data:/from -v matos_matos-data:/to busybox cp -a /from/. /to/
docker compose up -d
```

(`docker compose -p mattiverse down` in this folder would not stop it: the new file has no service `mattiverse`, so Compose leaves the old
container running as an orphan.)

Started with `docker run`: `docker stop mattiverse && docker rm mattiverse`, then
`docker run --rm -v mattiverse-data:/from -v matos-data:/to busybox cp -a /from/. /to/`, then the command above.

At the first start MatOS moves `mattiverse.db` to `matos.db`. Variables of the old name (`Mattiverse__NetworkSubnet`, ...) still count
(`MatOS__...` wins); the host port variable is `MATOS_PORT` now. Then a job brings the engines along once they answer (Jobs shows it): the
stacks, schedules and routes of Mattiverse get the mark of MatOS, MatOS's own address under your domain becomes `matos.<domain>` (the route
and the DNS record of `mattiverse.<domain>` go), and the apps learn the new address that may show them in a window. Under a domain the
browser keeps the places of the windows per address, so at `matos.<domain>` they open in their default places once. Once everything runs,
`docker volume rm mattiverse_mattiverse-data` frees the old volume.

## How it works

```
/data
├─ matos.db        SQLite: users, sessions, apps, jobs, shared links (and the backups of apps MatOS runs itself)
├─ config/system.json   JSON: name, domain, DNS provider, the wizard's choices
├─ dataprotection-keys/ keys for the cookies and the secrets (so sessions survive restarts)
├─ catalog/             your own app definitions (*.yml)
└─ apps/<app>/          only for stacks MatOS runs itself (MatDock, and apps while MatDock is not up): docker-compose.yml, .env
```

- An app is a **compose project** named `matos-<app>`. MatOS renders the catalog entry into one compose file (container
  names, labels, the shared network, the host port of the UI, an `x-matdock` block) plus a `.env` with your answers and creates it as a
  **stack in MatDock** through MatDock's REST API. The stack is yours to edit there; an update keeps your version.
- Status, containers and logs are asked from MatDock; while MatDock is not there yet or does not answer, MatOS reads them straight from
  Docker, so the home screen stays correct when an engine is down.
- Containers of an app are only attached to the **shared network** `matos` when they must be reachable by
  the proxy (the UI service); databases stay private to their stack.
- With a domain, every install creates the route `<app>.<domain>` in **Matcad** and the record in **Matddns**; uninstalling removes both.
  MatOS itself gets a route too. With a DNS account Matcad issues one wildcard certificate through the DNS challenge.
- **Backups** are made by MatDock (a bundle of volumes, compose and `.env`) and listed in MatDock too; the apps that MatOS runs itself
  are backed up by MatOS's own runner. One page shows both.
- **The window** is a frame of MatOS's own pages; a page in it drops its menu and header and keeps only its content. An engine is shown
  in a window when it allows it: MatOS passes its own addresses in **`MAT_FRAME_ANCESTORS`** (its route, this machine with its port,
  the addresses people signed in from), and the engine answers with `frame-ancestors` for them (MatPlatform 1.5.0). MatOS checks the
  headers first and offers a new tab, with the reason, when an engine does not allow it.
- Without an engine's API (an older image, or not installed yet) MatOS falls back to its own `docker compose` runner, so nothing stops working.
- **Apps connect through bindings** (MatDesign [docs/PLATFORM.md](https://github.com/Real-TTX/MatDesign/blob/dev/docs/PLATFORM.md), contract 2): an app says in its `/api`
  what it provides and consumes (`office.edit`, ...). After an install MatOS asks a provider for the binding of every consumer (`PUT /api/bindings/...`)
  and passes the answer to the consumer as variables (`MAT_OFFICE_EDIT_URL`, `_KEY`, ...), with no code for any app. The same description gives the icons,
  widgets and menu entries of the app on the home screen.
- The design decisions (what the engines own, where the compose files are, the home screen, how this relates to the older matOS desktop) are in
  [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md); the contract with the engines is [docs/ENGINES.md](docs/ENGINES.md).

## MatOS Services

The free address `<name>.my.mattiverse.de` and the tunnel come from **MatOS Services** (`services.mattiverse.de`, its own project in
`src/MatOS.Services` and its own image `ghcr.io/real-ttx/matos-services`; the contract is [docs/SERVICES.md](docs/SERVICES.md)). It hands out
the names, answers the DNS of the zone `my.mattiverse.de`, passes visitors through to a MatOS behind a router (TLS is never decrypted on the
way), checks from outside whether a MatOS can be reached directly, and offers DynDNS for everybody at `my.mattiverse.de`.

Its operator has an **admin area** at `/admin`, in the same look as every Mat app: the installations with their tunnels and own domains,
block, disconnect or give back a name (each written to a history), the accounts, and **statistics** of the traffic, the visitors, the tunnels,
the DNS and the API per hour, day and month. A visitor's address or the pages they open are never stored: visitors are counted as a number
per hour. Setup on a server: [deploy/services/README.md](deploy/services/README.md).

## Apps and their state

Every app of the store has a state: how far it is along in MatOS. The store shows it on every app that is not yet a beta and filters by it; an alpha says so
before you install it, and a preview cannot be installed yet. Your own apps in `/data/catalog` are a beta unless their entry says otherwise
(`state` in the `x-matos` block, see [docs/CATALOG.md](docs/CATALOG.md)). The note says what is missing for the next state.

<!-- app-states:begin (generated from the catalog by AppStateTests; run the tests with MATOS_WRITE_README=1 to write it) -->
| State | Meaning |
|---|---|
| **Integrated** | Fully part of MatOS and tested through it: an engine that does its part for every app, or an app that uses all of them. |
| **Beta** | Works with MatOS and its one sign-in, but is not yet tested from start to end through MatOS. |
| **Alpha** | Installs and runs, but is not yet tied into MatOS: expect rough edges, and do not keep anything important only in it yet. |
| **Preview** | Announced, not ready to install yet. |
| **Third-party** | Not a Mat app: it runs as its maker built it. Where it can, MatOS connects it to the one sign-in and the mail; the note says what it has. |

| App | State | Note |
|---|---|---|
| [Matcad](https://github.com/Real-TTX/Matcad) | Integrated | Runs every route of MatOS. |
| [MatDock](https://github.com/Real-TTX/MatDock) | Integrated | Runs and backs up the apps; platform contract 1, no home-screen contributions yet. |
| [MatIdentity](https://github.com/Real-TTX/MatIdentity) | Integrated | One sign-in for all apps. |
| [MatSMTP](https://github.com/Real-TTX/MatSMTP) | Integrated | Sends the mail of the apps. |
| [MatBoard](https://github.com/Real-TTX/MatBoard) | Beta | Works with one sign-in; not yet tested end to end through MatOS. |
| [MatBu](https://github.com/Real-TTX/MatBu) | Beta | Works with one sign-in; not yet tested end to end through MatOS. |
| [MatCloud](https://github.com/Real-TTX/MatCloud) | Beta | Works with one sign-in; not yet tested end to end through MatOS. |
| [MatCMS](https://github.com/Real-TTX/MatCMS) | Beta | Joins a MatCMS Cloud installed beside it by itself, signs its admins in through MatIdentity and mails through MatSMTP; not yet tested end to end through MatOS. |
| [MatCMS Cloud](https://github.com/Real-TTX/MatCMS) | Beta | Works with one sign-in and mail through MatSMTP and links the MatCMS sites by itself; tested end to end through MatOS. |
| [MatContactHub](https://github.com/Real-TTX/MatContactHub) | Beta | Works with one sign-in; not yet tested end to end through MatOS. |
| [Matddns](https://github.com/Real-TTX/Matddns) | Beta | Works with one sign-in; not yet tested end to end through MatOS. |
| [MatDo](https://github.com/Real-TTX/MatDo) | Beta | Works with one sign-in; not yet tested end to end through MatOS. |
| [MatDown](https://github.com/Real-TTX/MatDown) | Beta | Works with one sign-in; not yet tested end to end through MatOS. |
| [MatDrive](https://github.com/Real-TTX/MatDrive) | Beta | Works with one sign-in; not yet tested end to end through MatOS. |
| [MatFile](https://github.com/Real-TTX/MatFile) | Beta | Works with one sign-in; not yet tested end to end through MatOS. |
| [MatGame](https://github.com/Real-TTX/MatGame) | Beta | Works with one sign-in; not yet tested end to end through MatOS. |
| [MatMail](https://github.com/Real-TTX/MatMail) | Beta | Not shown in the MatOS window yet. |
| [MatOffice](https://github.com/Real-TTX/MatOffice) | Beta | Works with one sign-in; not yet tested end to end through MatOS. |
| [MatPaper](https://github.com/Real-TTX/MatPaper) | Beta | Works with one sign-in; not yet tested end to end through MatOS. |
| [MatPhoto](https://github.com/Real-TTX/MatPhoto) | Beta | Works with one sign-in; not yet tested end to end through MatOS. |
| [MatPlay](https://github.com/Real-TTX/MatPlay) | Beta | Works with one sign-in; not yet tested end to end through MatOS. |
| [MatRegistry](https://github.com/Real-TTX/MatRegistry) | Beta | Works with one sign-in; not yet tested end to end through MatOS (docker push through Matcad). |
| [MatShape](https://github.com/Real-TTX/MatShape) | Beta | Works with one sign-in; not yet tested end to end through MatOS. |
| [MatSplit](https://github.com/Real-TTX/MatSplit) | Beta | Works with one sign-in; not yet tested end to end through MatOS. |
| [MatTrakr](https://github.com/Real-TTX/MatTrakr) | Beta | Works with one sign-in; not yet tested end to end through MatOS. |
| [MatBill](https://github.com/Real-TTX/MatBill) | Alpha | Installs and runs; no sign-in through MatIdentity and no platform contract yet. |
| [MatCan](https://github.com/Real-TTX/MatCan) | Alpha | Installs and runs; no sign-in through MatIdentity and no platform contract yet. |
| [MatDesign](https://github.com/Real-TTX/MatDesign) | Alpha | Installs, no MatOS integration yet. |
| [Matmon](https://github.com/Real-TTX/Matmon) | Alpha | Installs, no MatOS integration yet. |
| [MatNotes](https://github.com/Real-TTX/MatNote) | Alpha | Installs and runs; no sign-in through MatIdentity and no platform contract yet. |
| [MatPhone](https://github.com/Real-TTX/MatPhone) | Alpha | Installs and runs; no sign-in through MatIdentity and no platform contract yet. |
| [MatTicket](https://github.com/Real-TTX/MatTicket) | Alpha | Installs and runs; no sign-in through MatIdentity (only Microsoft Entra) and no platform contract yet. |
| [MatAi](https://github.com/Real-TTX/MatAi) | Preview | The image is not published yet. |
| [MatBiz](https://github.com/Real-TTX/MatBiz) | Preview | Its image is not published yet; the sign-in through MatIdentity and the platform contract are in the code. |
| [Home Assistant](https://www.home-assistant.io) | Third-party | Runs in a network of its own like every app, so devices it would find by itself on the home network (mDNS, Bluetooth, USB) are added by their address. |
| [Immich](https://immich.app) | Third-party | Its single sign-on and mail are set in its own administration (or a config file that locks every setting), so MatOS does not connect them yet. |
| [Jellyfin](https://jellyfin.org) | Third-party | Has its own accounts; the one sign-in only through a plugin of Jellyfin, which MatOS does not set up. |
| [Nextcloud](https://nextcloud.com) | Third-party | Signs in through MatIdentity (the app user_oidc, set up by MatOS, only under https) and sends its mail through MatSMTP; tested in the browser, not with the desktop and phone apps. |
| [Nginx Proxy Manager](https://nginxproxymanager.com) | Third-party | Needs the ports 80 and 443 that Matcad uses. |
| [Paperless-ngx](https://docs.paperless-ngx.com) | Third-party | Signs in through MatIdentity and sends its mail through MatSMTP; a person who comes through the single sign-on starts without rights until the administrator gives them some. |
| [Portainer](https://www.portainer.io) | Third-party |  |
| [Uptime Kuma](https://uptime.kuma.pet) | Third-party | Has its own account and no single sign-on; its notifications are set up in its own settings. |
| [Vaultwarden](https://github.com/dani-garcia/vaultwarden) | Third-party | Signs in through MatIdentity and sends its mail through MatSMTP; the vault itself still opens with each person's master password, as Bitwarden's design asks. |
<!-- app-states:end -->

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
dotnet build MatOS.slnx          # 0 warnings expected
dotnet test                           # unit tests (catalog, installer files, backups, schedules, parsing, frames, the markup of the pages)
./scripts/redeploy.ps1                # rebuild the container and redeploy the dev stack
./scripts/redeploy.ps1 -Fresh         # ... with a clean data volume (the first-run setup appears again)
(cd tests/e2e && npm install && node run.js)   # the pages and the home screen in a real browser against the dev stack
./scripts/update-matui.ps1            # bring a newer MatUi from a running MatDesign into wwwroot/matui
node scripts/wallpapers/render.js     # draw the wallpapers again
```

The dev stack is the app plus a SQLite web viewer (the SQLite counterpart of pgAdmin) on
`http://localhost:8021`. In `Development` a ready-made account **`admin@example.com` / `Start12345`** exists, so testing never
touches the real administrator. `MATOS_PORT` changes the host port when 8030 is taken.

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
| MatOS: home screen, widgets, windows (move, size, halves, taskbar), the folder *System*, wallpapers, phone layout | ✅ |
| The look of the Mat family inside a window (MatUi of MatDesign: lists, forms, dialogs, dark mode) | ✅ |
| Store, the first start (assistant, live progress, tour), setup wizard, themes | ✅ |
| Install / update / uninstall / start / stop with live logs | ✅ |
| Backups: manual, scheduled, retention, restore, download (MatDock's, merged with MatOS's own) | ✅ |
| Local users, roles, persistent sessions, shared links | ✅ |
| Apps as MatDock stacks, routes in Matcad, DNS records in Matddns, domain and DNS account asked once | ✅ |
| Stack edited in MatDock survives an update | ✅ |
| Single sign-on through MatIdentity (installed and wired by MatOS: the apps and MatOS itself) | ✅ |
| Mail through MatSMTP (installed by the first start; a relay user and the `MATSMTP_*` variables for every app that sends mail) | ✅ (the apps still have to read the variables) |
| Apps that describe themselves (platform contract): the tab Manage with their tiles, settings and buttons | ✅ |
| The engines inside a window (`MAT_FRAME_ANCESTORS`, MatPlatform 1.5.0) | ✅ in MatOS; the engines on their `feature/frame-ancestors` branches, in their images with the next release |
| Apps that connect themselves (bindings) and bring their own icons, widgets and menu entries (contract 2, MatPlatform 2.0) | ✅ MatOffice and MatDrive; the engines move onto the bindings next |
| MatOS updates itself; automatic updates of the apps, the engines and MatOS at night | ✅ |
| The free address and the tunnel of MatOS Services, reached from anywhere (direct or tunnel, chosen by itself) | ✅ |
| MatOS Services: admin area and statistics | ✅ |
| People, groups and access through MatIdentity; a second step for the sign-in (authenticator, passkeys) | ✅ |
| A home screen you arrange yourself (pages, dragging, sizes of widgets, kept with your account) | ✅ |
| Widgets with the data of the person who is looking | planned |
| Microsoft Entra ID sign-in | planned (the login is already behind `AuthService` / `SessionService`) |
| Backup targets: SFTP, WebDAV, SMB (create, test, choose per backup and schedule, restore from them) | ✅ with MatDock's `feature/backup-targets-api` (apiVersion 5) |
| Volumes (on this machine, SMB, NFS), data folders of the apps on them, a file explorer | ✅ with MatDock's `feature/volumes-api` (apiVersion 9) |
| The Task Manager: the host and every container live, with charts and actions | ✅ with MatDock apiVersion 6 |
| The updates of this computer (the system's packages, a restart) through MatDock | ✅ with MatDock apiVersion 7 (a Linux host with apt) |
| Nodes: more Docker hosts over SSH, an app installed on one and moved to another | ✅ with MatDock's `feature/nodes-api` (apiVersion 8) |
| Well-known apps in the store (Nextcloud, Immich, Jellyfin, Vaultwarden, Paperless-ngx, Uptime Kuma, Home Assistant) | ✅ |

## License

No license file is included yet. If you intend others to reuse this, add a `LICENSE` (for example MIT) at the repo root.
