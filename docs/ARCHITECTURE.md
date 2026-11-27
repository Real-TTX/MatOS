# Architecture and decisions

This page records the questions that shaped MatOS and the answers, so they do not have to be argued again.

## What MatOS is: a layer on top of five engines

MatOS is **not** a second Docker manager with its own copy of everything. It is the thin, friendly layer on top of five programs
that each **own** one part of the configuration:

| Engine | Owns | MatOS adds |
|---|---|---|
| **MatDock** | stacks (compose + env), containers, backups, schedules, updates | the store, one-click installs, the launcher |
| **Matcad** | routes, certificates, DNS providers for certificates, logins | a route `app.example.com` for every app |
| **Matddns** | sources of the IP, DNS records, rules | the DNS record `app.example.com` for every app |
| **MatIdentity** | people, groups, the apps and who may open them | one sign-in for every app (a client per app) and for MatOS |
| **MatSMTP** | relay users, the accounts mail leaves through, rules, the queue | one relay user per app that sends mail (`mail: {}`) |

MatOS itself keeps what no engine knows: users, roles and sessions, shared links, the catalog, the wizard's choices, which catalog
entry an app came from, and where it sits on the home screen. Everything else lives in the engines and **is visible and editable there**: open
MatDock and the apps are stacks marked *Managed by matos* (`managedBy: matos`), open Matcad and they are routes, open Matddns and they are records, open MatIdentity and they are apps, open MatSMTP and they are relay users.

The five are **fixed parts** of MatOS (`EngineIds.All`, `core: true`; the wizard installs them in that order, MatDock first because it deploys the others; they cannot be uninstalled from MatOS except a failed install, to start over).
Other tools in the store (Nginx Proxy Manager, Portainer, ...) are plain apps without deep integration. The contract between MatOS and the
engines is [ENGINES.md](ENGINES.md).

| | MatOS (this app) | matOS (the older desktop, now Real-TTX/MatOS-legacy, retired) | MatDock |
|---|---|---|---|
| Idea | A launcher: one app at a time, like a phone | A desktop in the browser: windows, taskbar | A Docker manager for many hosts over SSH |
| Strength | Installing the Mat apps, the setup wizard, mobile | Rich desktop, file explorer, task manager | Stacks, volumes, backups, terminal, many hosts |

## How an app is installed, updated and removed

1. The page validates the form and records the app (`InstalledApp`, state *installing*); a **job** does the rest (see below).
2. `ComposeRenderer` turns the catalog entry into **one compose file**: the entry's services plus what only MatOS knows (container
   names, labels, the shared network, the published port of the UI) and an `x-matdock` block, so MatDock's launchpad shows the stack as an app.
   Next to it a `.env` with the answers of the form.
3. `StackEngines` picks the engine that **runs** the stack: **MatDock** once its API answers (`apiVersion >= 1`), else MatOS's own
   `docker compose` runner (`LocalComposeEngine`). MatDock itself is always deployed by the local runner, because nothing can deploy it before it exists.
   The app remembers its engine (`InstalledApp.Engine`).
4. **The images first** (`ImagePreflight`): MatOS pulls every image of the stack itself. One that cannot be pulled stops the install there, with what the
   registry said and what to do ("it is private, or it was not published yet"), before the engine has a stack of the app: a retry or a removal finds nothing half made.
   An image this machine has a copy of is used from there when the registry refuses it or does not answer (built on the host, no internet); `pull_policy: never` is
   taken from this machine only.
5. The stack is created in MatDock (`PUT /stacks/matos-<app>`, `managedBy: matos`) and deployed without a second pull; the output of MatDock's operation is
   forwarded to the job's log. A deploy for new variables (a binding) does not pull either: only *Update* brings newer images.
6. The provisioners and publishers then connect it: the route in Matcad, the record in Matddns.
7. The **broker** binds it (MatDesign docs/PLATFORM.md "Bindings", docs/APP-MODEL.md): see "Bindings between the apps" below.

**A port that another container holds does not fail the install** when a variable names it (MatSMTP's `SUBMISSION_PORT`): the app gets the next free port and the log says so; a port written as a number (Matcad's 80 and 443) stays, with a warning.

**A failed install can always be tried again or removed.** *Try again* runs the install once more (a port taken since is planned anew). *Uninstall* of an app
whose install failed does not depend on what is missing: an app without containers has no final backup (there is nothing to back up), a backup that fails is a
warning, a route or DNS record that cannot be removed is a warning, and when the engine cannot remove the stack but no container of the app is left, MatOS
removes the app all the same (and its volumes when the data is to go). A job that MatOS's restart interrupted leaves the app *failed*, not *installing*.

**Reading goes through MatDock too** (the owner's rule of 2026-11-14: MatOS asks the engine and does nothing itself; before that every read went
straight to Docker). What the pages show of the runtime comes from `RuntimeReader`: the containers and the state of the apps (`GET /stacks`, the stacks named
`<prefix>-<app>`), the log of a container (`GET /containers/{name}/logs`) and the host (`GET /environments`: the version of Docker, processor, memory, disk).
Docker is read directly (the labels `matos.managed`, `matos.app`, `matos.instance`, `matos.service`) only while there is nobody to ask:
before MatDock is installed, while it does not answer, and inside a job (the runner checks what it just deployed), so the launcher never goes blind.

**The user may change the stack in MatDock.** MatOS stores a semantic hash of the compose it deployed (`ComposeSha`: parsed YAML, key order
and formatting do not matter). An update compares it with the stack as MatDock has it now; if they differ the update **keeps the user's version**
(pull and re-create, no rewrite) and says so in the log. In the Files tab the app shows the stack as its engine has it.

**Without an engine API** (an older released image, or MatDock not installed yet) everything keeps working through the local runner; the
Engines tab in the settings says which engine is connected.

**After a lost database or a restore in MatDock**, `AppReconciler` takes the stacks that MatDock holds with `managedBy: matos` back as apps:
the stack carries everything (catalog block, compose, variables).

## The first start

MatOS is for people who cannot use Docker: the release compose file has only MatOS, and everything else comes from **one continuous assistant**
in the browser (the owner's choice of 2026-11-26). It replaced the old path (`/Setup` for the account, then the five steps of the wizard, then a list of jobs).

| Step | Page | What happens |
|---|---|---|
| 1. Welcome | `/Setup` | A browser that came as `localhost` goes on at `127.0.0.1` first (`SetupFlow.LoopbackOf`, before the account cookie exists: Docker Desktop may stop answering `localhost` once MatOS joined its network). The check of Docker in plain words (`Core/Setup/DockerAdvice.cs`): "This computer is ready", or what is wrong and the one line that fixes it (the socket line of the compose file when the socket is missing, the socket of rootless Docker or Podman when the path is an empty folder, `label=disable` for SELinux, starting Docker). |
| 2. Your account | `/Setup` | Name, e-mail (the user name), password twice, "Show the password". The password follows MatIdentity's rules (`AccountRules`: 8 to 128 characters, not one of the passwords tried first, not the e-mail), so MatIdentity takes it later. Creates the administrator and signs them in. |
| 3. Engines | `/Setup/Engines` | Fixed rows, nothing to tick: MatDock, MatIdentity and MatSMTP in plain words (`SetupPlan.EngineLabel`, `EngineDoes`; *There already* for an installed one) and MatAi greyed out (*Coming later*, nothing installed); a note that the web addresses and, when the internet address changes, the address updater follow from the next screen. Without Docker the check says why and *Next* waits. *Next* queues MatDock and MatSMTP (`SetupService.BeginAsync`, phase 1). No *Back*: the account exists. |
| 4. How do you reach your apps? | `/Setup/Choose` | Three cards of one form, all on the dot of this step (`data-ob-dot`). **(a) The tree**: a **free address** (recommended; the name only, the test reserves it), **my own domain** (the domain and `_DnsProviderChoice`: the provider as radios, Netcup, Cloudflare, Hetzner, or *I create the records myself*; for a provider its account, the zone and the question whether the connection has a fixed IP address, "Domain and DNS"; by hand the records to create, `SetupPlan.RecordsFor`: `A` for `matos.<domain>` and `*.<domain>`, `AAAA` optional, `CNAME` when a host name is given, filled with the address MatOS Services saw through `/api/setup/ip`; the same records, with the fixed IP, show for a provider and a fixed IP, since no Matddns writes them then; a fixed IP and records by hand carry a CGNAT/DS-Lite note), **only here at home**. The engines follow from the answers (`SetupPlan.EnginesFor`: always MatDock, Matcad, MatIdentity and MatSMTP, Matcad also at home where it publishes the ports of the apps; Matddns only for an own domain with a DNS provider and an internet address that changes or may change). Records by hand and a fixed IP are reached only through the router (`SetupPlan.Normalize`: nothing points their records at the tunnel); *Advanced* offers how an own domain is reached from outside (*MatOS takes care of it* or *Only through my router*, "Reached from anywhere") only for a provider and a changing IP, and the address of this machine (prefilled from the browser; opened as `localhost` it is asked for in *Only here at home* instead). **(b) The certificates**: an own domain with a provider chooses one certificate for all apps (recommended, the wildcard route) or one each (`SystemConfig.Certificates`, `ProxyPublisher.WantsWildcard`); by hand and with a free address every app gets its own (the free address names the limits of Let's Encrypt); at home plain http. **(c) The test** (`POST /api/setup/test`, `SetupService.TestAsync`, rows `SetupTest`): the ports 80 and 443 here (required: a container that holds them is named), MatOS Services and the free name (required; the name is **reserved** here, `HostedAddressService.ReserveAsync`, and an earlier name of the same unfinished run is given back; *Set up the rest* with another way gives a name only reserved back, `SetupService.ContinueAsync`), the domain exists (its name servers, `DnsCheck`; only "does not exist" fails), the records by hand (advisory) and the ports from outside (advisory; one check of MatOS Services in five minutes, shared with `/api/setup/ip`; before Matcad nothing answers there). It runs when its card is shown and after a change, one request at a time; *Set up the rest* stays enabled, the failed required rows are said beside it, and the server tests again. One line under the dots says how far MatDock and MatSMTP are (`/api/setup/progress` every 5 seconds; a failed one links to the progress screen). |
| 5. Progress | `/Setup/Progress` | One line per step in plain words, live, an overall bar and an estimate; a failed line has *Try again* and *Show details* (its job log), and *Try all again* retries every failed line in the order of the plan (both take a local `returnUrl`, so a screen that waits for a line comes back to itself); at the end "Your MatOS is ready", whether the apps can be reached from the internet (the last line checks it, "Reached from anywhere"; `_Reach` with *Check again*) and the address MatOS is opened at from anywhere (`SetupPlan.SelfUrl`, with a copy button). In every phase the button to the next screen, *Next: your sign-in* (`/Setup/Account`, or *Next: mail* `/Setup/Mail` when the run has no sign-in line; a build without those screens leads to the home screen instead, never to a page that is not there), with "You can go on, it keeps working.": nothing goes on by itself. A screen reader hears every line that ends (a polite live region); when MatOS does not answer a few rounds in a row, or the session ended, the card says so (and offers `127.0.0.1` when the browser came as `localhost`). |
| 6. The tour | `/` | Four coach marks on the first visit of the home screen: the dock, the store, windows, the settings (a user: the dock, windows, the account). |

- **Set up the rest** (`Core/Setup/SetupService.ContinueAsync`) first settles the way to the apps: the free name the test reserved is **used now**
  (`HostedAddressService.UseAsync`: the base domain, no DNS account, the tunnel starts; a name not reserved yet is reserved first); a name taken meanwhile or a
  service that cannot be reached keeps the person on step 4 with the message. An own domain stores its DNS account (records by hand: none, so no line writes
  records), where its records point, the route policy and the certificates. Then it keeps what the wizard keeps (the address of this machine, the engines, `WizardCompleted`) and queues
  **one job per line** (`SetupPlan.Steps`): the engines that are missing in the order of `EngineIds.All` (prepared like the store does:
  `AppService.PrepareInstallAsync` and `JobKind.Install`), engines that were there already are connected
  (`ConnectEngines`), the sign-in of the person when MatIdentity is part of it (`AppJobs.HandOverOwner`, below), the records of an own domain with a DNS account (`AppJobs.DomainRecords`, its line names where they point), the free address
  (`AppJobs.FreeAddress`: the tunnel is up), with an address on the internet and Matcad the check of the reachability (`AppJobs.CheckReach`), and last, for
  every way, the self check (`SetupPlan.SelfCheckKey`, `AppJobs.CheckSelf`: MatOS answers where the person opens it). The lines and their jobs are `SystemConfig.Setup` (`SetupRun`). An engine that is there already counts as chosen.
  The first-apps picker is gone: the store has every app once MatOS is set up.
- **Two phases.** `SetupService.BeginAsync` (phase 1) queues MatDock and MatSMTP first (`SetupPlan.FirstSteps`), so their downloads run while the person
  still answers (leaving `/Setup/Engines`; `/Setup/Choose` calls it too when it was skipped); `ContinueAsync` (phase 2) checks the answers, sets the way and
  queues the rest. One lock covers both and *Try again*, so a double submit or a second tab starts no second run. Only phase 2 sets `WizardCompleted`:
  a problem with the answers (a name taken meanwhile) leaves the run in phase 1, and `/Setup/Choose` and the *Setup* widget stay on the choices until the
  way is confirmed (`SetupPlan.Continued`). Every screen of it, the *Setup* widget and `/Wizard` go where the run stands (`Core/Setup/SetupFlow.NextPage`:
  no run the engines, phase 1 the choices, phase 2 the progress screen, a MatOS set up before the runs the home screen); `/Wizard` sends an unfinished run
  there instead of starting installs beside it. What was queued is stored even when a later line could not be (no line is queued twice on a resubmit).
- **Every line names the lines it runs on** (`SetupRunStep.Needs`, `SetupPlan.NeedsOf`, only the keys of the run): its job is queued behind their jobs and
  `AppJobs.Gated` refuses to run while one of them still waits or runs (it fails, and an install's app is marked failed, so nothing stays "installing"); a
  need that is over, done or failed, lets it run. *Try again* of a line takes the lines that follow from it (`SetupPlan.Dependents`, the closure) off the
  queue first and queues them, and the failed ones among them, behind its new job. A waiting line says what it waits for.
- **The direct way of a free address** is offered only with `MatOS__ServicesDirectAvailable` (`HostedAddressService.DirectAvailable`); until then a free
  address stays in the tunnel, and Settings, Address does not switch to direct.
- **The progress screen** asks `GET /api/setup/progress` (administrators) every two seconds and exchanges only what changed (`SetupProgress.Of`: waiting,
  working, done, failed; the percent and the estimate come from what a step usually takes). A failure is said in plain words with what to do
  (`SetupProgress.Plain`: a restart, Docker did not answer, a program that is not available for download, no internet, a busy download service, which port of
  this computer is taken, it did not start in time; the free address has its own words); the technical rest is in the details. *Try again* queues the line
  once more (`SetupService.RetryAsync`, an install the way the store's *Try again* does it) and keeps the line.
- **It goes on by itself.** The *Setup* widget of the home screen (an administrator's, a plain link: the first start takes the whole tab) leads to the engines
  while there is no run, to the choices ("Not finished") until the way to the apps is confirmed, back to the progress screen while it works or a line failed, and goes once all is done. A restart of MatOS in the middle (the worker
  marks waiting and running jobs failed) queues the interrupted lines again in the order of the plan (`SetupService.ResumeAsync`, from `JobRunnerService`).
- **The same sign-in afterwards.** MatIdentity seeds only its own `admin` with a generated password, and once it is connected MatOS's sign-in page goes
  there: the person who created the account would stand before a sign-in without an account. So the first start keeps the e-mail and the password (protected,
  `SystemConfig.Owner` and the secret `owner`) until MatIdentity signs people in to MatOS; then `OwnerHandover` (called by `AppService` right after
  `IdentityLink.ConnectSelfAsync`) creates the person through SCIM (an administrator, the same e-mail and password, no change asked), links the local account
  (`ExternalSubject`) and forgets the password. A person with that user name or e-mail in MatIdentity already is only linked. A failure is a warning in the
  job's log and is tried again with the next `ConnectEngines`; so that it cannot go unnoticed, the first start has a line of its own for it ("Your sign-in for
  all apps", `AppJobs.HandOverOwner`: asks MatIdentity a few times and fails with the reason when it still does not know the person). While the person waits,
  the sign-in page does not go to MatIdentity by itself (it shows MatOS's own form and *Sign in with MatIdentity*, `?via=identity`). A new password chosen
  before (Account, Password) goes along and must follow `AccountRules` too. Without MatIdentity the password is forgotten at once. The last screen says it in
  one sentence.
- **The tour** is `User.HomeTourSeenAt` (null: due; the accounts that existed before the column have seen it). `Pages/Index.cshtml` draws the marks, the layout
  loads js/onboarding.js and css/onboarding.css only then, and `POST /api/home/tour` records it (Next to the end, Skip or Escape). `/?tour=1` shows it again.
  The test account of the development stack has seen it, so the browser checks are not in its way.
- **The look** is MatOS OS (`Pages/Shared/_Onboarding.cshtml`): the wallpaper *Orbit*, a card of glass, the planet of the logo slowly floating, one step at a
  time with a dot per screen (Welcome, the account, the engines, the way to the apps, setting up, the sign-in, mail); the toolkit's controls inside the card. Its classes are `ob-*` in `css/onboarding.css` (tokens only: `--os-*` and
  `--mat-*`, `WebUiTests`), its behaviour `js/onboarding.js`; every animation stops with `prefers-reduced-motion`. The pages are `data-top-level` (opened in a
  window of the home screen they take the whole tab).
- **Later changes** are the setup wizard's (`/Wizard`, Settings): welcome, the addresses with the engines under *Advanced*, the DNS account, the summary.
  `/Setup/Choose` goes to the progress screen once the way is confirmed, and to the home screen on a MatOS set up with the wizard (its progress screen while it has lines to finish).

## Domain and DNS

The wizard asks **once** for the domain of the apps and the account of the DNS provider (Cloudflare, Hetzner, Netcup: the providers that both Matcad and
Matddns support). The credentials are stored protected (Data Protection) and handed to both engines:

- **Matcad** gets the provider and a wildcard route `*.domain` with it: one certificate through the DNS challenge, no open port 80 needed (`ProxyPublisher`: the base
  domain, the provider, the wildcard route and MatOS's own route `matos.domain`). Every app gets a route `app.domain` served from that certificate: a binding
  of `web.route` the broker makes for every app with a web page (Matcad is the provider; the app gets `MAT_PUBLIC_URL`, MatOS keeps it as the app's public address).
  An older Matcad without the bindings gets the routes the old way. Without a DNS account only the single routes exist (HTTP challenge).
- **Matddns** gets one source, a domain group `MatOS` with a record for every app that has a route (and `matos.domain`) and a rule per record that ties it to
  that source (`DnsPublisher`, after the bindings). Every record carries `recordName` and `domain` (the zone): Matddns guesses the zone from the last two labels
  otherwise. Netcup is Matddns's kind `Netcup` (the CCP API) with `netcup: {customerNumber, apiKey, apiPassword, allowDynamic}`, the same three fields Matcad
  gets as `customer_number`, `api_key`, `api_password`. The records are not a capability: the account, the zone and the list belong to MatOS's domain, and a
  record gives an app nothing.

**Where the records point** (the owner, 2026-11-27: "the DynDNS client, always, unless I say the IP is fixed"). After the DNS fields the first start, the
wizard and Settings, General ask in plain words *"Does your internet connection have a fixed IP address?"*: *No, it changes* / *I'm not sure* (the default)
/ *Yes, it is fixed*, with the hint that most home connections change and MatOS keeps it up to date when in doubt. *Yes* asks for the address (prefilled with
the public IP Matddns's source saw last, `DnsPublisher.DetectPublicIpAsync`; empty in the first start, nothing is asked of a third party) and accepts only a
public IPv4 or IPv6 address (`DnsIp.IsPublic`: no private, CGNAT, loopback, link-local, ULA, documentation or reserved range). Stored as
`SystemConfig.Dns.IpMode` (`dynamic` | `fixed`; "not sure" is `dynamic`) and `Dns.FixedIp`; a config without them (an install before the question) is
`dynamic`, today's behaviour. The forms preselect *I'm not sure* for `dynamic` (it means the same as *No*). `DnsTarget` turns it into the source:

| Setting | Source in Matddns | Records |
|---|---|---|
| dynamic | `MatOS public IP` (kind `PublicIp`): Matddns is the DynDNS client and follows every change | A |
| fixed | `MatOS fixed IP` (kind `Static`, `static: {ip, ipv6}`) | A, or AAAA for a fixed IPv6 address |
| through the tunnel (below) | `MatOS tunnel` (kind `Dns`: the address of the tunnel name) | A, plus `*.domain` as a CNAME to the tunnel name |

A switch (`DnsPublisher.ApplyAsync`) writes the new source first, then the group with the complete list (the rules of the records that leave go first:
Matddns refuses to drop a record a rule uses; a record whose type changes is a new one), then one rule per record with the one new source, and only then
removes the sources of the other kinds, and only when no rule uses them any more (a rule made by hand keeps its source). So there is always one group, one
source and one rule per record, and no name has two records in the list. Matddns never deletes a record at the provider: a record that leaves the group
stays there as it was (the wildcard CNAME after the tunnel; it is harmless, every app has its own record).

**Every app is reached through Matcad** (the owner's decision, 2026-11-09), with or without a domain. Under a domain the binding of `web.route` is a host route
`app.domain`; without one it is a **port route**: Matcad's Caddy listens on the app's host port (picked by MatOS, `PortPlanner`; nobody chooses it) and
proxies to the app's internal address, with `publicHost` (the address of the settings) when there is one, so the app learns its address only when it is real. An app
with a route publishes no port of its own (`BindingBroker.PortsAsync`, `ComposePorts`); Matcad publishes the ports of its port routes on its route service
(`routes.service` of its catalog entry, `caddy`). A redeploy deploys the apps first and Matcad last, so an app gives up its port before Matcad publishes it, and
deploys Matcad whenever the ports it publishes are not those of its port routes (an app came, went, or moved under a domain). A route Matcad refuses (an older Matcad
without port routes, a port taken by a route made by hand) is dropped and the app publishes its own port again. Matcad's own UI keeps its own port. Installed apps open in
the window of the home screen (`js/os.js`; *Always open in a new tab* in the menu of an app), the address served by Matcad.

When the domain or the account changes (also when the domain is removed), or from the Engines tab (*Connect again*), the job `ConnectEngines` brings both in line
again and removes what MatOS created earlier and does not want any more (routes of an old domain; without a domain every route MatOS made, and the bindings
of `web.route` move from hosts to ports and back; the old cleanup leaves routes that are bindings alone).

### The free address

Who has no domain gets one from the address service (`services.mattiverse.de`, docs/SERVICES.md; `MatOS__ServicesUrl`): a name, checked while typing
(`GET /api/address/name`, open during the first-run setup and to administrators; `data-name-check` in `js/matos.js`), becomes `<name>.my.mattiverse.de`.
`SystemConfig.Hosted` keeps the name, the zone, the key (protected) and the mode. It is **a base domain without a DNS account**: the base domain is set to
it and the DNS provider to none, so Matcad gets one route per host and Caddy gets every certificate itself (HTTP or TLS challenge); `ProxyPublisher` never
gives Matcad a DNS account for it, `DnsPublisher` never writes records for it, and the wizard and the settings refuse a DNS account for it and any other name
below the zone. `SystemConfig.UsesHostedAddress` says whether the apps use it (it is the base domain); another domain in General leaves it registered and unused.

- **Tunnel** (the default): `TunnelClient` (a hosted service) keeps the control WebSocket while the address is used in tunnel mode, answers the pings, reconnects
  after 1, 2, 5, 10, 30, 60 s, and for every `open` connects the stream WebSocket to Caddy on the shared network (`TunnelTarget`: the route service of the proxy,
  ports 80/443 inside) through `TunnelPipe`. Nothing goes to Matddns.
- **Direct**: only after the service reached the install on 80 and 443 (*Check direct access*). Matddns gets a domain group `MatOS address` of kind `DynDns`
  (the service's `/nic/update`, the name and the key), the record follows the public IP, the tunnel stops; going back removes the group. `ConnectEngines` keeps
  Matddns in line with the mode (`HostedAddressService.SyncAsync`).

*Settings → Address* shows it all; the switch and *Give the name back* run as jobs.

### Reached from anywhere

The owner's rule (2026-11-27): **"The routing is different then, but the result must always be the same."** Direct or through the tunnel is MatOS's own
business: the addresses never change when it switches, the person sees one status ("Your apps can be reached from anywhere" / "Right now your apps can only
be reached at home", `Reachability.Sentence`), and the words tunnel, direct, CNAME and ports appear only under *Details* of Settings, Address (and in the
forwarding help of an own domain that is reached only at home).

- **The check** (`RouteService.CheckAsync`): `POST <ServicesUrl>/api/v1/ports/check` (anonymous, with the key when there is one; `ServicesClient.CheckPortsAsync`,
  never an exception) says per port whether 80 and 443 answer on IPv4/IPv6 and whether Caddy answers. It runs only after Matcad is there (before, nothing
  listens): the last line of the first start ("Checking whether your apps can be reached from the internet", `SetupPlan.ReachKey`, after the free address and
  the records), *Check again* (`POST /api/reach/check` queues the job, `_Reach` and js/matos.js `data-reach` ask `GET /api/reach` until the status changed,
  in a live region), and `ReachWatcher` every day (an hour after a check that could not be made), always as the job `AppJobs.CheckReach`, one at a time. At
  home (no address on the internet) there is nothing to check. The result is `SystemConfig.Reach`.
- **The decision** (`Reachability`, pure, tested): 80 and 443 open with Caddy behind them is **direct**; anything else (closed, filtered, another web server)
  is **the tunnel**; a service that cannot be asked (or says `too-many-checks`) changes nothing and is no error for the person (the free address of the first
  start stays in the tunnel, an own domain stays direct). The status is "from anywhere" when the route is the tunnel or the ports are open, "only at home" when
  the ports are closed and no tunnel carries the apps.
- **The free address**: the decision switches the mode through `HostedAddressService.SetModeAsync` (direct after the service reached Caddy, Matddns's DynDNS
  group; the tunnel otherwise).
- **An own domain** (`SystemConfig.DomainRoute`, `direct` by default): when the ports are closed MatOS falls back to **the tunnel of MatOS Services** instead of
  leaving the apps at home, when it may (`SystemConfig.RoutePolicy`: `auto`, the default, or `direct` = *Only through my router*, under *Advanced* of the
  first start and in *Details* of Settings, Address) and can (a DNS account and Matddns: the records must be pointed). `RouteService.UseTunnelAsync`: a tunnel
  name registered quietly when there is none (`Reachability.TunnelName`: the first label of the domain, a short suffix when taken; `Hosted.ForDomain`: the
  person never chose it, Settings, Address calls it the tunnel name and offers no free address on top), `POST /api/v1/instance/domains {domain}` (`Hosted.Domain`),
  the tunnel up (`SystemConfig.TunnelWanted`), the records pointed to the tunnel name (the table above: the same names, the source `MatOS tunnel`, plus
  `*.domain` CNAME the tunnel name, which is how the service verifies the domain), then the wait for `verified` (`GET .../domains/{domain}`, three minutes;
  longer is said plainly, the service keeps checking). A failure before the records were written goes back to direct. Back to direct (`UseDirectAsync`, the
  ports opened, or the policy `direct`): the records follow the IP again, the tunnel closes, `DELETE .../domains/{domain}`; the tunnel name stays for the next
  time. Matcad keeps its wildcard certificate through the DNS account either way. `ConnectEngines` keeps the registration in line with the domain
  (`RouteService.SyncAsync`: a new domain is registered, the old one taken off; without an own domain the route is direct again).
- **The pages**: the last screen of the first start (`_Reach` in `Setup/Progress`, asked again when the screen turns "ready"), Settings, General (*From the
  internet*) and Settings, Address (the same, plus *Details*). An own domain reached only at home shows the ports to forward to the address of this computer, a
  short FRITZ!Box how-to (English with the German menu names: Internet > Freigaben > Portfreigaben), and that with a DNS account the certificates work even
  without forwarding.

## Where are the backups?

**In MatDock**, which owns backups and schedules: a bundle of all volumes, the compose and the `.env`, with retention. They show up in MatDock's
backup and schedule lists and are started from MatOS's *Backups* page (`BackupHub`, ids `matdock:<id>`).

The apps that MatOS runs itself (MatDock, and everything while MatDock does not answer yet) are backed up by MatOS's own runner
(`LocalBackupProvider`, ids `local:<id>`): each volume is copied with a throw-away `busybox` container, the manifest keeps the compose files, the
variables and the image digests, so a local backup restores into a fresh MatOS. The two lists are merged on one page; an *All apps* schedule runs
in MatDock, a schedule for an app runs where the app runs.

- **Consistent by default.** Containers are stopped while the volumes are copied and started again afterwards, whatever happens. A live copy is
  possible and marked as such.
- **Restore** first backs up the current state (switchable) and re-creates the app even when it was uninstalled.
- **Download** as a `.tar` to copy a backup off the host. Remote targets (SMB, NAS) are MatDock's backup targets.
- Limits: backups contain secrets (the `.env`), so protect the location like the volumes; no encryption.

## Where are the compose files?

Of the stacks that MatDock runs: **in MatDock** (below its data volume), one text per stack, which is what the user sees and edits there.
Of the stacks that MatOS runs itself: on the MatOS data volume, `/data/apps/<app>/` with `docker-compose.yml` (the rendered file) and
`.env`, so `docker compose -p matos-<app> -f ... up -d` works by hand.

**Rule for catalog entries:** use named volumes, not relative bind mounts. `docker compose` runs inside a container (MatOS's or MatDock's), so a relative
path would resolve inside that container and not on the host.

## Where does the catalog live?

**In MatOS** (`src/MatOS.Core/catalog/apps/*.yml`, embedded, plus `/data/catalog/*.yml`). A catalog entry is a compose file with an
`x-matos` block ([CATALOG.md](CATALOG.md)): data, not code. The older matOS desktop (Real-TTX/MatOS-legacy) kept its catalog in C# code, which makes every new app a release; here it is a file.
A user file with the same `id` replaces the built-in entry. The three engines carry `core: true` and a `provision` key that names what connects them.

All apps and engines share the Docker network `matos` with MatOS itself (it attaches itself on start), so they reach each other by container name
(`matos-<app>-<service>`). If Docker's address pools are used up, `MatOS__NetworkSubnet` gives the network an explicit subnet.

## How a long task runs

Nothing slow runs inside a web request. The page validates and records, then queues a **job** (`Job` table): a single
background worker runs jobs one after the other (Docker operations on one host must not overlap), keeps the output in
memory while it runs (the page polls `/api/jobs/<id>/log`) and writes it to the database every few seconds and at the end.
A job that was running when the container stopped is marked failed on the next start, and so are the apps that were
being installed.

## Authentication and roles

- Local accounts, passwords with PBKDF2 (210,000 iterations) and a constant-time comparison; the sign-in does not reveal
  whether a user exists.
- A **session** is a row in `UserSession` (a `Token` UUID in the cookie). Sessions live in SQLite on the volume and the
  Data Protection keys are persisted next to it, so **sessions survive a container restart**.
- Roles: `Admin`, `User`, `Anonymous`. Anonymous is not an account: it is the visitor of a **shared link**
  (`/s/<token>`), which shows one app. Pages are secure by default (`FallbackPolicy` requires a user); the
  admin pages are listed in `Program.cs`.
- Microsoft Entra ID: the sign-in is isolated in `AuthService` and `SessionService`; adding an OpenID Connect scheme
  next to the cookie and mapping the claims to `User` is the planned step.
- The engines' API keys are generated by MatOS when it installs them, stored with the app's variables (protected) and sent as `X-Api-Key`.
  An engine without a key answers `503`, so its API is never open.

## Database conventions

SQLite (`matos.db`) holds the logic, JSON (`config/system.json`) the configuration.

- Tables are PascalCase and singular (`User`, `UserSession`, `InstalledApp`, `Job`, `Backup`, `BackupSchedule`, `ShareLink`).
- The primary key is always `Id` (64 bit). A table with a security key has a `Token` (UUID): `UserSession`, `ShareLink`.
- Every row has `CreateDate`, `CreateUserId`, `UpdateDate`, `UpdateUserId` and `UpdateState` (0 deleted, 1 created, 2 updated).
  `Remove` becomes a soft delete and a global query filter hides deleted rows; unique indexes ignore deleted rows.
- Schema changes are EF Core migrations (`src/MatOS.Core/Data/Migrations`), applied at start.
- `Backup` and `BackupSchedule` only hold the backups and schedules of MatOS's own runner; those of MatDock live in MatDock.

## UI conventions

MatOS uses **MatUi**, the UI toolkit of [MatDesign](https://github.com/Real-TTX/MatDesign), so that it looks and behaves like the other Mat apps; the rules are MatDesign's rulebook.

- **The toolkit is a copy** in `src/MatOS.Web/wwwroot/matui` (CSS, one script, the icon sprite): the app works offline and as a PWA, and nothing is loaded from another site.
  `scripts/update-matui.ps1` brings a newer release from MatDesign. The app adds only what is its own: `mv-*` classes (app icons, the launcher of the home screen, the app frame, the wizard) in `matos.css`
  and a small script (clock, the search of the home screen, job log, wizard).
- **Shell:** the layout draws the menu (one menu for a user; Administration is a second menu behind the switcher for an administrator; Settings is a submenu), the user menu, the 3-state theme button,
  the version and the breadcrumb. Sign-in, setup, shared links and errors have no menu (`_Bare`). The theme is kept on the account (`/api/theme`) and in the browser.
- **Controls:** the tag helpers `<icon>` (a use of the sprite), `<app-icon>`, `<mat-field>` and `<tab-bar>` and the partials `_Toolbar`, `_Pager`, `_ValidationSummary` and `_StatusPill` are the one code base for them.
- **Lists:** the page model builds a `ListView` (the rows of the page, a `ToolbarModel`, a `PagerModel`) from the query string. The toolbar has a search field, Filter and Sort open dialogs. Every list has its toolbar
  on top, the pager directly under the table and its actions below, left-aligned.
- **Forms:** CRUD pages are a list and a separate page for create/edit. Buttons stand in one line from positive to negative: *Save, Back, (space) Delete*. The page validates (`novalidate`: no bubbles of the browser);
  a summary at the top links to the fields. After a save the page redirects and shows a toast. Dependent fields are shown only when needed (`data-show-if`).
- **The logo** stays the planet with its orbit, not the flat glyph tile that MatDesign's rule IDENT-03 describes: the owner prefers it.
- Mobile first where it counts: a drawer for the menu, tables become cards, dialogs are full screen, safe-area insets for notches.

### The home screen: MatOS

The home screen is a **small operating system** (the owner's choice of 2026-11-08/09, modelled on iOS, umbrelOS and the older matOS desktop), not a page of the toolkit:

- **Its own look.** `Pages/Shared/_Launcher.cshtml` with `css/os.css` and `js/os.js`: a status bar (logo, running tasks, theme, the person, date and time), the wallpaper over
  the whole screen, widgets of glass, app icons, a search and a dock of glass. Glass, colour gradients on the icons of the system and the system font are allowed there on
  purpose; the colours are the `--os-*` properties of `os.css`. This is a deliberate exception to MatDesign (SHELL-01, SHELL-03, COLOR-06) for the home screen,
  approved by the owner like the logo: do not bring the menu of the toolkit back to it.
- **Windows, like a desktop** (the owner's choice of 2026-11-11; before that there was one window at a time). Anything with `data-window` opens that page in a window
  of its own over the home screen: an iframe (`js/os.js` makes it from the template in `_Launcher`). In there a page of MatOS keeps MatUi for its content and drops
  the menu, the header and the breadcrumb (`js/window-mode.js` sets `mv-in-window` on `<html>` before the page is drawn; `mv-window-only` shows what only makes sense
  there, such as the tabs of the settings). A window has a title (the page's), Back (inside the window), Reload, Open in a new tab, and three lights: red closes it,
  yellow puts it away and keeps the app as it is, green fills the screen.
  - **A page looks like a part of its window** (`css/matos.css`, under `.mv-in-window`): rounder and tighter through the tokens of the toolkit, the title of the layout is
    left to the bar of the window (it stays for a screen reader), tabs are a segmented control, and the tabs of the settings and of the account (`mv-winnav`) are the
    sidebar of a window at least 720 px wide, like the settings of a desktop.
  - **Several at once.** The bar moves a window, its edges and corners size it, a click anywhere in it (also inside its page) brings it to the front; a double click on
    the bar fills the screen. Let go at the top of the screen it fills it, at the left or right edge it takes that half (the place is shown while dragging); dragged
    away it has its own size again. A new window opens a step beside the last one. A window that fills the screen ends above the dock.
  - **The dock is the taskbar** and stays above the windows: a dot under an app that has a window (a longer one for the window in front), and an icon for every
    window whose app is not in the dock, for as long as it is open. A click brings the window to the front; a click on the one in front puts it away. The logo in the
    status bar shows the desktop (every window is put away), and the status bar names the window in front.
  - **What is kept.** Every app remembers the place and size of its window in this browser (`localStorage`), and the windows of a tab come back after a reload
    (`sessionStorage`: page, place, order, put away or not). The address keeps the page of the window in front (`/#/Store`), so a link with it opens that window; the
    browser's Back shows the desktop. An address in the hash is a path of this MatOS only.
  - **The home screen refreshes itself in place** (`refresh()` in `js/os.js`: the things are fetched and exchanged; the rate is `data-autorefresh` while something
    runs, and a page that loads in a window asks for it once): a reload would take the windows with it.
  - **On a phone** a window fills the screen, with a bar at the bottom to go home; the windows are switched with the dock.
- **Pages that belong to the whole tab** (sign-in, a shared link, an error: `_Bare`, `<html data-top-level>`) leave the window; a link to `/` inside it puts the window away, and a page that
  lands on the home screen closes it. Opened directly (a new tab), every page still has the shell of the toolkit with its menu.
- **Everything is an app.** The parts of MatOS (`Support/Launcher.cs`): Activity and Users on the screen, Store, Apps, Backups and Settings in the dock (a user gets
  Apps and the account). An installed app opens in a new tab by default (its own address); its menu (a right click, a long press) opens it in the window instead
  (`/Apps/Open`, which falls back to a button when the app forbids a frame) and can make that its default in this browser (localStorage).
- **The engines are part of the system**, not apps between the others: a widget with the five and their state (from Docker), and the folder *System* with them as apps.
  An engine opens its own window in MatOS (`/Engines?id=`, `Support/EngineOverview.cs`): its state and version (its API), what it keeps for MatOS and which of
  your apps it serves (a stack, a route, a DNS record, the sign-in, a relay user), and its own pages: **here in the window** when the engine lets MatOS frame it,
  else in a new tab. An engine allows the addresses in `MAT_FRAME_ANCESTORS` (MatDesign docs/PLATFORM.md, "Shown inside another app"), which MatOS fills before
  it deploys an app whose compose file names it (`Core/Integrations/FrameLink.cs`: the route behind the proxy, `localhost`, `127.0.0.1` and the configured address with the port
  MatOS is published on, the addresses people signed in from). `Support/FrameProbe.cs` asks the app first (`FramePolicy`: `frame-ancestors` wins over
  `X-Frame-Options`, judged for the address of the page) and offers a new tab otherwise. Older engine images refuse frames: they show it after an update.
- **Every app in the window** (the owner's question of 2026-11-27: every app runs through Matcad, so Matcad can set the frame header). An app whose compose file does
  **not** name `MAT_FRAME_ANCESTORS` (MatMail, Matmon, a third-party app, ...; `FrameHeaders.TakesOrigins`) gets response headers on its Matcad route, host route or
  port route alike (`Core/Integrations/RouteFrameHeaders.cs`, decision in `FrameHeaders.cs`): `X-Frame-Options` removed and
  `Content-Security-Policy: frame-ancestors 'self' <the origins of FrameLink>`, so MatOS (and the app itself) may frame it and no other site. **Only when the app
  sends no content security policy of its own**, asked at its address inside Docker (the sign-in page a redirect leads to counts). The decision: Matcad sets a
  header of the route as a whole (Caddy's deferred `set`) and cannot add a directive to the app's policy, so writing ours would drop the app's other directives
  (scripts, styles, ...), and removing only `X-Frame-Options` would let every site frame it. An app with its own policy therefore keeps both untouched and opens
  in a new tab when that policy refuses the frame; its page (*About this app*, "Window") says so with the reason. (Accepted: an app that sends a policy only on
  some pages, not on its start or sign-in page, loses it on those pages.) Written with every binding of `web.route` the broker makes or renews (install, update,
  redeploy, *Connect the engines*), only on a Matcad with the capability `responseHeaders` (0.13.0), only on routes marked `managedBy: matos`; headers an
  administrator set on the route stay, and a frame header of their own (an `X-Frame-Options` with a value, a policy that is more than `frame-ancestors`) wins:
  MatOS leaves the route alone. They come off when the app takes `MAT_FRAME_ANCESTORS` or sends a policy, or when Matcad's own *Allow embedding* is on, and go
  with the route when the app is removed. While the app does not answer (three tries) the route keeps what it has until the next connect. `FrameProbe` judges
  the answer the browser gets: the app's own headers with those of the route applied (`FrameHeaders.Through`, also *Allow embedding*), because the route's
  public address cannot be reached from inside Docker.
- **Widgets** are cards in the grid of the apps (two cells, or two by two): today (the time, the greeting: the page's h1), the engines, apps running, the last backup
  (amber after seven days or with none), the activity, Docker. The search above the dock narrows the apps (key `/`, Enter opens the first); the widgets step aside.
- **The wallpaper** is a picture of the account (`User.Wallpaper`, the claim `matos:wallpaper`, `Core/Users/Wallpapers.cs`, the tab *Home screen* of the account).
  The default is *Orbit*, the planet of the logo, light or dark with the theme; Earth, Jungle, Lagoon and Mountains are views from above; *None* is the plain background.
  The pictures are drawn, not downloaded: `scripts/wallpapers` (SVG with noise filters, rendered to JPEG with Edge; `node scripts/wallpapers/render.js`).
- **What the apps add** (contract 2, `contributes` of their `/api`; `Support/HomeContributions.cs`, `Core/Integrations/ShellRules.cs`): an app with launchers gets
  one icon per launcher instead of its own (its picture at the app, `/Apps/Open?id=&path=` in the window, its address in a tab; a badge from a key of its state), its quick
  actions are entries of the menu of the icon (`data-quick`), and its widgets stand after the ones of the system (`stat`, `meter`, `tiles` from `/api/state`, `list` from
  `/api/widgets/{id}`; `_AppWidgetBody`). Who sees what follows `audience` (`admin` for administrators); widgets with the values of the `person` who is looking are left out
  until MatOS can ask an app on a person's behalf. The definitions and values are asked with a short timeout and kept a little while (`AppManifests`, `ManifestCache`),
  so a slow or stopped app does not hold the home screen up; a widget with `refresh` makes the home screen reload itself that often (at least every 30 s).
- **The person arranges it, the account keeps it** (`js/os.js`, `Core/Users/HomeLayout.cs`, `User.HomeLayout` as JSON, `PUT` / `DELETE /api/home` with the anti-forgery token
  in the header `RequestVerificationToken`). Every thing of the home screen has a key (`w:today`, `w:<slug>/<widget>`, `a:<slug>`, `a:<slug>/<launcher>`, `s:jobs`, `f:system`;
  `IndexModel.ArrangeAsync`); the layout is the pages with their keys in order, the keys taken off the screen, and the size chosen for a widget. The page draws it as the
  person left it: what is new (an app installed since) joins the end of the first page, what is gone drops out, an empty page goes, and a layout that cannot be read is the
  home screen as it comes. What is off the screen is drawn out of sight (`.os-hidden`), so that it comes back without a reload.
  - **A right click everywhere** (a long press on a touch screen; Shift with the right click is the browser's own menu, a field keeps the browser's menu): an app (open, its quick
    actions, details), a widget (open it or the row under the pointer, its size: small, medium, wide as far as it offers them), every thing of a page (one place back or on,
    to another page or a new one, off the screen, edit the home screen), the dock, the bar of a window and its icon in the dock (back, reload, a tab, fill the screen, a half, put away, close, close all) and the empty screen
    (edit, add a page, show again what is off it, the wallpaper, the store and the settings for an administrator, reload, reset).
  - **Dragging**: the mouse drags a thing at once (a copy follows the pointer, the others glide out of the way); a finger drags while editing, else it swipes the pages.
    Waiting at the edge of the screen or over a dot turns the page and takes the thing along; past the last page a new one begins.
  - **The home screen never scrolls**: it is exactly the window (status bar, pages, dock). A page shows what fits on the screen; the first thing that reaches below it and all
    after it continue on a page of their own right after it (`data-continues`, `flow()` in `js/os.js`; the grid places things in order, so an earlier thing never moves for a
    later one, and a later icon never fills a hole between widgets). A widget that follows icons starts a row of its own at the left, or else on a pair of columns
    (`os-at-N`). The home screen as it comes (`data-os-default`, the person never arranged it) lets later things fill what is left of a full screen before the next page
    begins; the person's own order is never changed. The account keeps the person's pages, not the screens, so the same arrangement fits a phone and a large screen; a new size of the window lets them flow again.
  - **Pages** side by side, a sideways strip that snaps (`.os-pages`, `.os-page`): a swipe, the dots, the arrow keys. A reload (the home screen refreshes itself) comes back to
    the same page; it waits while the person edits or drags (`os-hold`). The search shows the apps of all pages in one grid.
  - **Editing** ("Edit the home screen"): the things wiggle, each has a minus that takes it off the screen (Delete does it by keyboard), the bar instead of the search has
    *Add a page*, *Show again* and *Done*; a tap beside the things ends it.
  - The dock and the folder *System* stay as they are (the dock is the parts of MatOS, the folder the engines).

## Bindings between the apps

The apps say what they **provide** and **consume** (capabilities, MatDesign docs/PLATFORM.md "Capabilities"); MatOS is the **broker** that connects them, with one
class for every capability and no code that knows an app (`Core/Integrations/BindingBroker.cs`):

- **After an install or an update** (`AppService.ConnectBindingsAsync`): the broker reads the app's `/api` (it waits up to a minute for a fresh app), binds what it consumes
  to an installed provider (`PUT /api/bindings/{capability}/{slug}` at the provider, `managedBy: matos`; a consumer keeps the provider it has), and binds the installed
  consumers of what it provides. Every app whose variables changed is deployed once more.
- **The variables** of a binding are kept in `AppBinding` (protected as a whole: they hold a key) and go, with `MAT_INTERNAL_URL` and `MAT_PUBLIC_URL` (only a real address),
  into the `.env` and the web service of the consumer (`ComposeFiles.Build(..., injected)`, `ComposeRenderer` adds `NAME: ${NAME:-}`); a variable of the catalog entry wins.
  A secret the provider does not hand out again (`{"isSet": true}`) is kept; one MatOS lost is renewed.
- **Uninstall**: the app's bindings are removed at their providers; the consumers of what it provided get another provider or lose the variables, and are deployed again.
- The job that connects the engines also binds every app again (`ConnectAllBindingsAsync`). A provider that is deployed again in that pass may answer other values
  afterwards (MatIdentity's issuer follows its new public address when a domain comes or goes): the broker waits until it runs and binds its consumers once more
  (at most two rounds); an install or update does the same. The tab *Connections* of an app's Manage page shows `GET /api/connections` of the
  app with what MatOS bound.
- **The engines are providers like any app** (docs/APP-MODEL.md step 5): MatIdentity (`identity.oidc`), MatSMTP (`mail.smtp`) and Matcad (`web.route`) answer
  `/api/bindings`; MatOS provides `shell.embed` itself (`MAT_FRAME_ANCESTORS` for every app of the contract, `FrameLink`). What an app consumes comes from its catalog entry
  (`consumes`, for images that do not say it; the old keys `identity: {}` and `mail: {}` are read into it) and its `/api`, plus the implicit `web.route`.
- **Engines older than the bindings** keep working: the variables an earlier MatOS filled (`MATIDENTITY_*`, `MATSMTP_*`) are taken over as bindings at the app's next deploy
  (`BindingBroker.AdoptAsync`), and a binding whose provider is still installed but does not answer `/api/bindings` is kept. A new app gets such a binding only from an engine
  image with the bindings.
- What stays engine code in MatOS is the **setup of the engines themselves**: MatDock as the runtime, Matcad's domain, DNS account and wildcard route, Matddns' records,
  MatSMTP's first-run setup and sender domain (`MailLink.ConfigureRelayAsync`), MatOS's own sign-in and the issuer of MatIdentity (`IdentityLink`). MatAi
  (`ai.openai`) follows when its branch is merged.

## What is deliberately not here

- **No `mssql-server` / `pgadmin` service.** The project template names them; with SQLite neither is needed. The release
  stack is the single service `matos`; the dev stack adds a SQLite web viewer.
- No multi-host management (that is MatDock), no file explorer or terminal (the older matOS desktop / MatDock).
- No deep integration of alternatives (Nginx Proxy Manager, Portainer): they are installed as plain apps.

## MatIdentity, the single sign-on

MatIdentity is an engine (the fourth line of the first start, `core: true`), and **MatOS manages all of it**. People, groups and who may open which app live in
MatIdentity; MatOS administers them in its area **People** (below) and MatIdentity's own pages do the same.

- **Its address.** MatOS gives MatIdentity its public address as the fixed issuer (`MATIDENTITY_ISSUER`: the route of the proxy, else this host and
  its port). Inside Docker the apps reach it as `http://<project>-matidentity-matidentity:2300` (`MATIDENTITY_INTERNAL_URL`); MatIdentity names the public
  address in its discovery document whichever way it is read, so the browser always goes to the public one.
- **The apps.** An app that consumes `identity.oidc` (its catalog entry, or its `/api`) is bound by the broker: MatIdentity makes the client (id = the slug,
  `managedBy: matos`, redirect addresses for every public address of the app, the back-channel logout at its internal address) and answers the `MATIDENTITY_*`
  variables (MatDesign docs/PLATFORM.md "`identity.oidc`"); the binding is removed with the app. When MatIdentity is installed (or removed) the apps that are there are
  bound (or lose it) and redeployed; *Connect again* on the Engines page does it for all.
- **Back-channel logout.** Signing out at MatIdentity (or an administrator ending a person's sessions there) ends the person's sessions in every app: MatIdentity
  posts a signed logout token (OpenID Connect Back-Channel Logout 1.0) to the client's `backchannelLogoutUri`. MatIdentity registers it for every bound app as the
  app's **internal** address on the shared network, `<consumer.internalUrl><backchannel>` (the param, default `/signout-backchannel`), since it calls it server to
  server; an app without a web service or port gets none. The
  apps on MatPlatform 1.4.0 answer it (MatDesign `src/MatPlatform`, `IMatIdentitySessions`); an older app answers 404 and keeps its session until it expires.
  MatOS registers its own as `http://<its container>:<its port>/signout-backchannel` (`SelfBackchannelLogoutUriOf`; none when it runs outside Docker).
- **MatOS itself** is the client `matos` (it may call every app). The person who set MatOS up becomes a person of MatIdentity with the same e-mail and password once it is connected (`OwnerHandover`, "The first start"). The login page goes straight to MatIdentity when it is connected and the person who set MatOS up is there (`?local=1`
  keeps the local form); `/Account/External` turns the answer into a session: the person is found by `sub` (`User.ExternalSubject`), else by the user
  name, else created, and the role follows the claim `role`. Signing out ends the session at MatIdentity too. The OpenID Connect options are read from the
  settings when they are built, so nothing needs a restart (`MatIdentitySignIn`). **The sign-in follows MatIdentity's address:** MatIdentity's issuer is its public
  address (the address of this machine with its port, or its name under the domain), so an install or an update of MatIdentity registers MatOS again
  (`IdentityLink.ConnectSelfAsync`). The login page first checks that the discovery document and the keys can be read (`MatIdentitySignIn.ReadyAsync`); when not,
  and MatIdentity answers, MatOS registers itself again and the page comes once more (`?reconnected=1`; the sign-in of a request is set up when it begins);
  a MatIdentity that does not answer leaves the settings alone and the page offers the local sign-in with a message instead of an error. The session keeps the claim `sid` of the ID token (`UserSession.IdentitySessionId`);
  `POST /signout-backchannel` (`Infrastructure/BackchannelLogout.cs`, anonymous, no anti-forgery) checks the logout token (`Core/Auth/LogoutToken.cs`, the same checks
  as MatPlatform's: signature with the keys of the handler's discovery document, `typ` `logout+jwt`, `iss`, `aud` = `matos`, `iat` at most five minutes old,
  `exp`, the back-channel event, `sub`/`sid`, no `nonce`, a `jti` replay cache in memory) and ends the sessions (`SessionService.EndIdentitySessionsAsync`): with `sid`
  the sessions that began with it, with `sub` alone every session of the linked user, local ones too. Sessions live in SQLite and the cookie is checked against
  its session on every request, so the next request is signed out. `200`; `400` for an invalid token; `404` while MatOS is not connected; `no-store`.
- **People** (MatDesign docs/ACCESS.md, PLATFORM.md "People, permissions and quotas"). `/People` (administrators) is a front end of MatIdentity, MatOS keeps no people
  of its own there: `Core/Integrations/MatIdentityPeople.cs` speaks SCIM 2.0 with MatIdentity (`/scim/v2`: users, groups, the extension `urn:mat:scim:schemas:extension:access:1.0`
  with the grants and what a person ends up with per app) and `/api/v1` (the apps with their roles, permissions and quotas; the actions on a person), with MatIdentity's key,
  naming the person who acts (`X-Mat-Actor`). The pages: People (list, a person: details, groups, the section Apps, the actions), Groups, and Access (what a person or group
  is given in one app: whether at all, the role, the permissions, the quotas, the usage the app reports at `/api/quotas/{subject}`, `PlatformClient.GetQuotasAsync`). Every
  form is drawn from what MatIdentity keeps of the app, which it read from the app's `/api`: **no page knows an app**. MatOS's own accounts (the local administrator,
  the way in when MatIdentity is down) are the tab *MatOS accounts* (`/Users`). Without MatIdentity the area says what is missing.
- **What each person sees.** `PersonAccess` asks MatIdentity (SCIM) which apps a person may use and their role in each, and keeps it a minute (the People pages forget
  it after a change). The home screen and the list of apps show a person only those apps (an app that does not sign in through MatIdentity stays, it has users of its own),
  `/Apps/Open` refuses the others, and the launchers and widgets follow the person's role in the app: `roles` of a contribution decide when the role is known, `admin` is
  the app's first role (`ShellRules.Sees`). An administrator of MatOS sees every app; a local account and a person MatIdentity says nothing about see what
  they always saw (the rules of `audience`).
- **Manage.** An app with `platform: true` gets the tab *Manage*: `PlatformClient` reads its MatApiDefinition (`GET /api`) and its values with `MAT_API_KEY`,
  and the page draws its pages (tiles, settings form, buttons) with MatUi without knowing the app.
- **MatOS's sign-in looks like MatOS** (the owner, 2026-11-27: "I do not want to SEE that it is MatIdentity, but a normal login on MatOS; in the apps it is then
  *Sign in with MatIdentity*"). The single sign-on stays: the person still signs in **at MatIdentity**, so its session lets the apps in without a password. But
  MatIdentity draws the sign-in of MatOS's client in the look of MatOS (MatIdentity docs/ARCHITECTURE.md "Branded sign-in"): `IdentityLink.BrandSelfAsync`, after
  every registration (`ConnectSelfAsync`: the first start, *Connect again*, an update of MatIdentity, the rename follow-up, a new address on the login page) and
  when the instance name changes in the settings, sends `branding` on MatOS's own client: the instance name, the accent `Ui.App.Accent`, `hideIdentity: true`,
  the planet `img/logo.svg` and the light picture of the default wallpaper `img/wallpapers/orbit-light.jpg` (one picture for both themes; `SelfBrandAssets`,
  registered by `Program.cs`). It asks first (`GET /api/v1/clients/{id}`): a MatIdentity without the field `branding` is older and gets nothing (its status still says
  `apiVersion: 1`, so the field is the gate), and the brand is sent only when it differs (`IdentityBranding.Payload`; a picture whose `?v=` is the hash of MatOS's
  bytes is left out). A failure is a line in the log; the sign-in works without the look. So a person who signs in sees: `/Account/Login` (no page in between, it
  sends them on to MatIdentity with the `returnUrl`) → MatIdentity's page with the planet, "Sign in to MatOS", the wallpaper, no word of MatIdentity (also
  Forgot → the mail → Reset) → back in MatOS. The local form (`?local=1`, and the page while MatIdentity does not answer or the person who set MatOS up still
  waits for it) is not advertised and does not name MatIdentity either ("Single sign-on", "The sign-in does not answer right now"). **The sign-out** sends the
  id token of the sign-in as `id_token_hint` (`External` keeps it in the session cookie, `Logout` hands it to `MatIdentitySignIn`), so MatIdentity ends its
  session without a confirmation page of its own, and ends on MatOS's `/Account/SignedOut` ("You are signed out", *Sign in again*); a session from before this
  has no token and MatIdentity asks once. **The apps are unchanged:** their sign-in is MatIdentity's own look ("Sign in with MatIdentity"), and an app opened from
  MatOS starts at its `ui.signIn` (`AppService.StartPathAsync`), finds MatIdentity's session and lets the person in without a click. An administrator still sees
  MatIdentity as an engine in *System* and the settings.

## MatSMTP, the mail relay

MatSMTP is the fifth engine (the last card of the wizard, `core: true`): every app that sends mail hands it to MatSMTP, and MatSMTP delivers it through the account set
in its own UI. **MatOS manages the connection**; the account, the rules and the queue stay in MatSMTP's pages (MatDesign docs/PLATFORM.md "Mail through MatSMTP", the
API in [ENGINES.md](ENGINES.md)).

- **Its setup.** The catalog entry generates `MATSMTP_API_KEY` and the administrator's password (`MATSMTP_ADMIN_PASSWORD`); after the install `MatSmtpProvisioner` sets
  `setupCompleted` and the domain of the senders (the domain of the wizard). Only the submission port is published, for devices in the network; the SMTP port of the apps is
  reached on the shared network only, so MatSMTP is never an open relay.
- **The apps.** An app that consumes `mail.smtp` (its catalog entry, or its `/api`) is bound by the broker: MatSMTP makes a relay user (id = the slug, `managedBy: matos`,
  the sender `<sender>@<domain>` as the only allowed one) and answers the `MATSMTP_*` variables (its container name, port and TLS mode, the user, the password once, the
  sender; MatDesign docs/PLATFORM.md "`mail.smtp`"). MatOS keeps the password and asks for a new one only when it has none. Without MatSMTP the variables are emptied and
  the app sends its mail itself; the relay user is removed with the app. When MatSMTP is installed (or removed) the apps are bound (or lose it) and redeployed.
- **The apps' side.** With the variables set an app sends all its mail through MatSMTP and shows its own SMTP settings as fixed (in its pages and with `fixed: true` in its
  `/api/configure`, which the Manage tab draws as disabled fields and never sends back). MatPlatform 1.3.0 (`MatSmtpSettings`) does this for the .NET apps.
- **MatOS itself** sends no mail today, so it has nothing to connect.

## Renamed from Mattiverse

MatOS was called **Mattiverse** until 2026-11. Everything was renamed (projects, namespaces, image `ghcr.io/real-ttx/matos`, compose, variables, the mark on what is
written to the engines), but an install of Mattiverse keeps working: what it persisted or sent over the wire under the old name is still accepted. The old names are in
one place, `Core/Configuration/Mattiverse.cs`; the mark is `Core/Owner.cs`. Accepted from before, and why:

- **The data volume.** At its first start, before the options and the database are used, MatOS takes over the volume (`Configuration/RenameUpgrade.cs`, called from
  `Program.cs`): `mattiverse.db` (with `-wal`, `-shm`) becomes `matos.db` when there is no `matos.db` yet (an empty one, which a SQLite viewer on the same volume
  creates, does not count), and the move is logged. The volume itself has a new name; the
  README ("Upgrading from Mattiverse") copies it once.
- **The names the apps run under.** The shared Docker network and the prefix of the compose projects stay `mattiverse` for an install of Mattiverse (its database or its
  `system.json` was there, or apps are in `mattiverse-` projects), otherwise its apps and engines would be left on another network and in projects nobody knows; a new
  install is `matos`. They are fixed once in `system.json` (`naming`) and every consumer reads them through the options (`MatOSOptions.NetworkName`, `ProjectPrefix`); an
  explicit `MatOS__NetworkName` / `MatOS__ProjectPrefix` still wins.
- **The options.** The section `MatOS` (`MatOS__X`) and the old section `Mattiverse` (`Mattiverse__X`) both bind; `MatOS` wins (`Configuration/MatOSConfiguration.cs`).
- **The Docker labels.** New containers carry `matos.managed`, `matos.app`, `matos.instance`, `matos.service`. The containers of an upgraded install carry `mattiverse.*`
  until they are deployed again, so everything that reads labels asks for both (`docker ps` cannot OR two filters: one call per set, merged by id).
- **The catalog block.** A catalog file is `x-matos`; `x-mattiverse` (a catalog copy in `/data/catalog`, the compose file of a stack Mattiverse gave MatDock, which
  `AppReconciler` adopts) is read the same way, and `x-matos` wins when a file has both. What MatOS writes (the compose file of an app, the stack in MatDock) carries
  `x-matos`, in the place of the old block.
- **`managedBy`.** What MatOS writes to an engine carries `matos`; every ownership check (what it may change or delete: routes and providers in Matcad, sources, domain
  groups and rules in Matddns, stacks in MatDock, clients in MatIdentity, relay users in MatSMTP, the bindings of the broker) accepts `matos` and `mattiverse`
  (`Owner.IsOurs`). A removal that names the mark (MatIdentity, MatSMTP, the bindings) tries `matos` and then `mattiverse`. The next write gives an object the new mark.
  The engines are not changed: they show `mattiverse` as their badge and other values as they are (*Managed by matos*).
  Where an engine compares the mark itself, mixed marks would break things, so the old marks are replaced once (below, *The engines follow*): MatDock backs up
  with a schedule for all apps only the stacks whose mark is the schedule's, and Matcad turns a route made through `/api/v1` into the `web.route` binding
  of an app only when both carry the same mark (`ProxyPublisher.PublishAllAsync` gives such routes the mark of MatOS before the broker binds them).
- **The things it named.** The Matcad provider *Mattiverse DNS*, the Matddns source *Mattiverse public IP* and its domain group *Mattiverse* are taken over (by their id)
  and renamed, so no second one with the same records is made; only those that carry the mark of MatOS.
- **Its own address.** MatOS's host under the domain is `matos.<domain>`. The route and the record of `mattiverse.<domain>` carry the old mark, so the next publication
  removes them like anything MatOS does not want any more; the redirect addresses in MatIdentity drop the old host (unless a person still comes from it).
  The container is `matos` now (it was `mattiverse`); on the network of an install of Mattiverse it also answers to its old name (`mattiverse`, `mattiverse-dev`;
  `DockerService.ConnectArguments`: `--alias`), so Matcad's route and MatIdentity's back-channel address keep reaching it until they are written again.
- **The engines follow** (`Configuration/RenameFollowUp.cs`). An install of Mattiverse is marked once (`naming.finishRename`); at every start while the mark is there
  a job waits for the engines (up to ten minutes; MatDock and Matcad must answer, else the next start tries again) and then: gives the stacks and schedules of
  Mattiverse in MatDock the mark of MatOS together (written back as they are, `deploy: false`), connects the engines again (`AppJobs.ConnectEngines`: MatOS's own
  route to the container `matos` under `matos.<domain>`, the DNS records, the back-channel address, the bindings), and with a domain deploys the apps of the
  contract once more, so `MAT_FRAME_ANCESTORS` names the new address (Compose recreates only what changed). Then it clears the mark.
- **Its client in MatIdentity.** An install that registered `mattiverse` keeps it (`system.json`: `identity.clientId`, `naming.identityClientId`); a new install is `matos`.
  The sign-in always uses the stored one (`SystemConfig.SelfClientId`).
- **The instance name.** A stored *Mattiverse* (the old default) becomes *MatOS*, once, with the upgrade; a name the person chose stays, and so does *Mattiverse*
  given again later.
- **The home screen.** The places of the windows (`localStorage`) and the open windows of a tab (`sessionStorage`) move from `mattiverse.os.*` to `matos.os.*` once
  (`js/os.js`). The browser keeps them per address, so this works only where the address stays: `http://<host>:8030` keeps them, but under a domain MatOS moves
  from `mattiverse.<domain>` to `matos.<domain>`, which cannot read what the old address kept: there the windows open in their default places once. Accepted.

Kept on purpose: the Data Protection application name `Mattiverse` (`Program.cs`) and the purpose of the secret protector `Mattiverse.Secrets.v1`
(`DataProtectionSecretProtector.Purpose`): both are part of the key derivation, another name would make every protected secret unreadable; the domain
`mattiverse.de`, the branches `mattiverse-integration` of the engines and the old name of the repository (`Real-TTX/Mattiverse`, renamed to `Real-TTX/MatOS` on 2026-11-26; GitHub forwards the old address). Not carried over: the cookie names (`matos.auth`): a person
signs in once more after the upgrade.

Decided (the owner, 2026-11-26): the older matOS desktop is retired. Its repository becomes `Real-TTX/MatOS-legacy` (archived) and its images move to
`ghcr.io/real-ttx/matos-legacy`; this app is `Real-TTX/MatOS` and publishes `ghcr.io/real-ttx/matos` (CI names it explicitly) and, for a while, the same
build as `ghcr.io/real-ttx/mattiverse` too (`LEGACY_IMAGE_NAME` in `build.yml`), so an install that still runs the old image gets MatOS with its next update.
The Docker names `matos` (project, container, volume, network) are this app's now; see [INTEGRATION.md](INTEGRATION.md).
