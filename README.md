# IRONSIGHT — F# of Duty

IRONSIGHT is an experimental, asset-free WWII first-person shooter written in
F#. Every level, weapon, character mesh, material, font glyph, sound effect, and
particle is generated in code. The same deterministic gameplay core powers the
offline game and an authoritative multiplayer server.

[![CI](https://github.com/HelgeSverre/fsharp-of-duty/actions/workflows/ci.yml/badge.svg)](https://github.com/HelgeSverre/fsharp-of-duty/actions/workflows/ci.yml)

The project is a playable prototype rather than a finished commercial game. It
is intentionally small, direct, and engine-free: Silk.NET provides the window,
input, OpenGL, and OpenAL layers while ordinary F# records and discriminated
unions model gameplay.

## Highlights

- Fully procedural rendering and audio with no runtime content assets.
- Fixed 60 Hz deterministic simulation with a functional core and imperative
  desktop shell.
- Paintball Killhouse, Training Yard, Stalingrad Street, and a large generated
  Normandy battlefield.
- Kar98k, Thompson, M1911, scoped Kar98k, and M1897 Trench Gun.
- ADS, sprinting, stances, recoil, penetration, grenades, regeneration, blood,
  headshot gibs, and reload feedback.
- Navgraph/A* bot navigation, cover behavior, suppression, and automatic reloads.
- Authoritative 8v8-capable multiplayer with TDM and FFA rooms, prediction,
  interpolation, reconciliation, lag-compensated hits, and reconnect tokens.
- A live Fly.io server and deliberately overqualified project website at
  [fsharp-of-duty.fly.dev](https://fsharp-of-duty.fly.dev/).

## Requirements

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0). The checked-in
  `global.json` also permits a newer installed SDK.
- An OpenGL 4.1-capable desktop for the graphical client.
- OpenAL support. If audio initialization fails, the game continues silently.
- Optional: [`just`](https://github.com/casey/just), Docker, and `flyctl` for the
  convenience, container, and deployment commands.

The dedicated server is headless and does not require OpenGL or OpenAL.

## Quick start

```sh
git clone https://github.com/HelgeSverre/fsharp-of-duty.git
cd fsharp-of-duty
just run
```

Without `just`:

```sh
dotnet restore Ironsight.sln
dotnet run --project src/Ironsight/Ironsight.fsproj
```

Launching without flags opens the menu. Set your callsign, choose Quick Play or
an offline map, or select the Fly.io server and an online loadout.

## Common commands

Run `just` to list every recipe.

```sh
just run                         # open the game menu
just training                    # launch Training Yard directly
just stalingrad                  # launch Stalingrad Street directly
just battlefield                 # launch the large generated battlefield
just server                      # run a local Paintball server
just online-local Player         # connect to the local server
just online Player               # connect to Fly.io TDM
just ffa Player                  # connect to Fly.io FFA
just check                       # format check, build, and all tests
just docker-build                # build the headless server image
just fly-validate                # validate fly.toml
just fly-deploy                  # deploy the server
```

Direct online launch supports a callsign, mode, and weapon:

```sh
dotnet run --project src/Ironsight/Ironsight.fsproj -- \
  --online --name Player --weapon "Kar98k Sniper"

dotnet run --project src/Ironsight/Ironsight.fsproj -- \
  --online --ffa --name Player --weapon Thompson
```

Set `IRONSIGHT_SERVER=ws://127.0.0.1:8080/play` to use another server. Set
`IRONSIGHT_LEVEL` to `paintball`, `training`, `stalingrad`, or `battlefield` on
the server.

## Controls

| Input | Action |
| --- | --- |
| `WASD` | Move |
| Mouse | Look |
| Left / right mouse | Fire / aim down sights |
| Left Shift | Sprint |
| Space | Jump |
| Left Control / `Z` | Crouch / prone |
| `R` | Start reload; restart after campaign death |
| `G` | Hold to cook grenade, release to throw |
| `1`–`5` | Select an offline weapon |
| Tab | Hold the online scoreboard |
| Escape | Return to the menu; quit from the main menu |

Menus support the mouse or Up/Down and Enter. The callsign editor accepts normal
typing and Backspace.

## Architecture

```text
src/Ironsight.Core/    deterministic simulation, procedural assets, AI, levels
src/Ironsight/         Silk.NET desktop client, renderer, audio, HUD, networking
src/Ironsight.Server/  ASP.NET Core WebSocket server and authoritative matches
tests/Ironsight.Tests/ headless xUnit simulation, client, and server tests
docs/                  current guides plus preserved design specifications
website/               static project site, live leaderboard, and arsenal UI
```

F# compile order enforces the dependency direction. `Ironsight.Core` has no
graphics or server dependency. The client samples input and presents
`GameEvent`s; the server accepts intentions and recomputes movement, weapon state,
hits, damage, and scores.

See [the multiplayer guide](docs/MULTIPLAYER.md) for the current protocol and
deployment model. The [documentation index](docs/README.md) links the original
design and netcode specifications.

## Testing

```sh
just check
```

The suite exercises deterministic generation, movement and collision, weapon
state machines, ballistics, AI/navigation, menus, online reconciliation,
authoritative scoring, grenades, reconnects, and loadout replication. Rendering
still benefits from a manual OpenGL smoke test because CI is headless.

## Server deployment

The checked-in `Dockerfile` publishes only the headless server. `fly.toml` runs
one 256 MB shared-cpu Machine in Stockholm and checks `/health/ready`. Match and
session state are in memory, so the current deployment intentionally keeps one
Machine running and loses active matches during a rollout.

```sh
just docker-build
just fly-deploy
curl https://fsharp-of-duty.fly.dev/health/ready
```

The public deployment is for development playtests and carries no uptime or data
retention guarantee.

## Website and public telemetry

The dependency-free files in `website/` can be opened directly or hosted by any
static file server; Fly serves the same directory through the headless server.
The landing page shows the connected players in both live rooms; the arsenal page
loads damage and handling statistics generated from the actual
`Ironsight.Core.Tuning` weapon definitions.

- `GET /api/leaderboard` — live connected players, scores, and selected loadouts
- `GET /api/arsenal` — player and mounted-weapon tuning values

Leaderboard state is intentionally volatile. It records the active server
process, not accounts or historical rankings, and resets whenever Fly deploys or
restarts the Machine.
