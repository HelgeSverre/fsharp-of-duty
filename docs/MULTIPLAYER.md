# Multiplayer and deployment

This document describes the multiplayer implementation currently in the
repository. For future transport ideas and broader acceptance goals, see the
[netcode design specification](NETCODE-SPEC.md).

## Current scope

One server process hosts a Team Deathmatch room and a Free For All room. Each
room accepts up to 16 connected/reserved player records, which supports an 8v8
TDM match when all slots are active.

- TDM: Allies versus Axis, first team to 75 kills.
- FFA: every other player is hostile, first player to 30 kills.
- Ten-minute rounds, ten-second warmup/results phases, five-second respawns.
- Two seconds of spawn protection, cancelled by firing or throwing a grenade.
- Join in progress, in-memory reconnect reservations, scoreboards, and online
  selection of all five weapons.

Campaign and bot matches remain local-authoritative. The default online level is
Paintball Killhouse; the server can instead select Training Yard, Stalingrad
Street, or Normandy Battlefield with `IRONSIGHT_LEVEL`.

## Authority and timing

The server owns movement, view angles, stance, weapon state, ammunition, spread,
hits, penetration, grenades, damage, deaths, spawns, scores, match phase, and RNG.
Clients send input intentions rather than positions or hit claims.

- Simulation: fixed 60 Hz.
- Snapshot transmission: approximately 20 Hz.
- Client input: sampled at 60 Hz. The server buffers up to four frames per
  player and applies them at one per tick on average (a banked catch-up frame
  absorbs network jitter), so bursty delivery no longer drops inputs. A player
  whose stream stalls keeps coasting: gravity, friction, weapon timers, and a
  cooking grenade continue.
- Local player: predicted and reconciled from acknowledged input sequences.
- Remote players: interpolated from a 32-snapshot history.
- Hitscan rewind: estimated server ticks are clamped to 12 ticks (200 ms) of
  player-position history. Static level geometry is not rewound.

## WebSocket protocol v1

The endpoint is `/play`, using UTF-8 JSON over one WebSocket. Production uses
TLS through Fly Proxy:

`wss://fsharp-of-duty.fly.dev/play`

The first client message is a `hello`:

```json
{
  "type": "hello",
  "version": 1,
  "name": "Player",
  "mode": "TeamDeathmatch",
  "weapon": "Kar98k Sniper",
  "sessionToken": ""
}
```

The server answers with `welcome`, containing `playerId`, `sessionToken`,
`tickRate`, and `snapshotRate`. The client sends `ready`, then `input` messages:

```json
{
  "type": "input",
  "version": 1,
  "sequence": 42,
  "estimatedServerTick": 1812,
  "moveX": 0.0,
  "moveY": 1.0,
  "lookX": 0.012,
  "lookY": -0.004,
  "buttons": 3
}
```

Implemented client message types are `hello`, `ready`, `input`, and `leave`.
Implemented server message types are `welcome` and `snapshot`; combat events are
carried inside snapshots with monotonically increasing IDs.

Snapshots include match metadata, scores, connected player state, selected
weapon/ammunition, acknowledged input sequences, grenades, and recent presentation
events. Unknown JSON fields and unknown post-handshake message types are ignored.

## Validation and limitations

The server currently enforces:

- 16 KiB maximum client messages and text-only frames;
- compatible protocol version and a valid initial `hello`;
- callsigns trimmed to 24 Unicode scalar values;
- a fixed maximum of 120 received messages per second;
- finite, clamped movement/look values and bounded input sequence advances;
- a whitelist of accepted input-button flags;
- server-side fire rate, ammunition, movement, and hit validation;
- team hostility and spawn-protection checks.

There are no accounts, administration controls, persistent progression, text
chat, or application-level authentication. Session tokens are random and held
only in process memory. The JSON protocol uses full snapshots rather than delta
compression, and the rate limit is a simple per-second counter rather than a
token bucket.

## Sessions and shutdown

Disconnecting marks a player unavailable and reserves its identity, team, score,
and session token for 30 seconds. Automatic reconnect reuses that token. A clean
menu exit sends `leave` and closes the client loops before disposing the socket.

All room state is process-local. A server restart or Fly rollout ends active
matches; graceful match migration and shared session storage are not implemented.

## Local server

```sh
just server
just online-local Player
```

Equivalent direct commands:

```sh
dotnet run --project src/Ironsight.Server/Ironsight.Server.fsproj

IRONSIGHT_SERVER=ws://127.0.0.1:8080/play \
  dotnet run --project src/Ironsight/Ironsight.fsproj -- \
  --online --name Player --weapon Thompson
```

The server listens on `0.0.0.0:8080` and exposes:

- `GET /` → static project website and live war room
- `GET /arsenal.html` → weapon and damage-model statistics
- `GET /api/leaderboard` → current connected-player rankings for both rooms
- `GET /api/arsenal` → weapon values generated from core tuning records
- `GET /health/live` → `ok`
- `GET /health/ready` → `ready`
- WebSocket `/play`

The leaderboard is a process-local view of current match state, not a durable
account leaderboard. It exposes public callsigns, teams, scores, life state, and
weapon names but never session tokens. The arsenal endpoint is cacheable for five
minutes; leaderboard responses explicitly disable caching.

## Fly.io

The Fly app is `fsharp-of-duty` in region `arn`. The checked-in configuration
keeps one Machine running because matches are in memory.

```sh
flyctl auth login
just fly-validate
just fly-deploy
flyctl status --app fsharp-of-duty
flyctl checks list --app fsharp-of-duty
```

The deployment uses a rolling Machine update. A brief failed health check while
the new process starts is expected; traffic becomes available after
`/health/ready` passes.
