# Netcode design specification

> Design and roadmap document. The WebSocket v1 described here is implemented,
> but later transport work and some acceptance targets remain aspirational. See
> [MULTIPLAYER.md](MULTIPLAYER.md) for the exact behavior of the current server.

Companion to [`DESIGN-SPEC.md`](DESIGN-SPEC.md). This document defines the first deployable
multiplayer version and a later transport-optimization path. Where this document
and the original single-player spec differ, this addendum governs online play.

## 1. Version 1 scope

Version 1 is a dedicated-server game for 2–16 players:

- Free for all: every other player is hostile; first to 30 kills wins.
- Team deathmatch: Allies versus Axis; first team to 75 kills wins.
- Ten-minute matches, ten-second warmup and results phases, five-second respawns.
- Join in progress, two-second spawn protection, reconnect reservations, and
  complete scoreboards.
- Both modes are hosted concurrently by one process. The client's `hello.mode`
  selects the room.
- Campaign remains offline and local-authoritative.

Domination, accounts, progression, text chat, matchmaking services, and player
administration are outside v1.

## 2. Authority and simulation

The server is authoritative for movement, view angles, stance, weapon state,
ammunition, spread, hits, penetration, grenades, damage, deaths, respawns, scores,
match phase, and RNG. Clients send `InputFrame` intentions and never send positions,
hits, health, or ammunition.

The server runs at 60 Hz and reuses the pure core modules (`Movement`, `Weapons`,
`Ballistics`, `Grenades`, and `Damage`). Multiplayer state is represented by
`MatchState` and `NetworkPlayer`; campaign state remains `World`. This deliberate
split avoids forcing scripted AI and campaign objectives into the network schema
while retaining the same deterministic mechanics.

The server retains 200 ms of player-position history. A fire input carries the
client's latest estimated server tick, clamped to that history window. Character
positions rewind for hit validation; static level brushes never rewind. Grenades
enter the authoritative present and are not lag compensated.

## 3. Transport

Version 1 uses one secure WebSocket per client at `/play`:

- Production default: `wss://fsharp-of-duty.fly.dev/play`.
- Override: `IRONSIGHT_SERVER`.
- Server tick rate: 60 Hz.
- Full snapshot rate: 20 Hz.
- Payload: UTF-8 JSON, protocol version 1.
- Maximum client message size: 16 KiB.
- Maximum accepted input-message rate: 120 per second.
- WebSocket keepalive detects dead connections.

WebSockets are intentional for the first Fly.io version: Fly Proxy terminates TLS,
WebSocket upgrades need no public UDP allocation, and a complete 16-player state is
small enough for full snapshots during early iteration.

## 4. Protocol

The first client message is:

```json
{
  "type": "hello",
  "version": 1,
  "name": "Player",
  "mode": "TeamDeathmatch",
  "weapon": "Thompson",
  "sessionToken": ""
}
```

The server responds with `welcome`, including player ID, random session token,
tick rate, and snapshot rate. The client then sends `ready` and begins sending
inputs:

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

Snapshots include:

- server tick, game mode, match phase, and team scores;
- connected players, authoritative state, score, and acknowledged input sequence;
- live grenade positions and fuse times;
- a retained, monotonically identified window of combat events.

Unknown JSON fields are ignored for forward compatibility. Incompatible versions,
non-text frames, oversized messages, malformed JSON, and excessive message rates
receive a policy close. Names are trimmed and limited to 24 Unicode scalar values.

## 5. Client prediction and presentation

The desktop client immediately applies local movement inputs and keeps up to four
seconds of unacknowledged commands. On an authoritative snapshot it:

1. restores the local authoritative player state;
2. discards acknowledged commands;
3. replays newer movement commands;
4. renders the reconciled result.

Remote players render from a snapshot history approximately 100 ms behind the
newest state. Position and angles interpolate between snapshots; short gaps use
bounded extrapolation and longer gaps freeze.

Replicated combat events have unique IDs. The client deduplicates them before
driving tracers, impacts, explosions, synthesized audio, subtitles, and hit feedback.
Local cosmetic prediction may be added without changing server authority.

## 6. Sessions and lifecycle

The lifecycle is:

`Waiting -> Warmup(10 s) -> Playing -> Results(10 s) -> Warmup`

Two connected, ready players start a room. On disconnect, the server reserves the
identity and score for 30 seconds. A new `hello` containing the session token
reclaims it. The desktop client reconnects automatically with a two-second retry
backoff. Tokens exist only in memory and are never logged.

Friendly fire is disabled in TDM. Spawn protection ends after two seconds or when
the player fires or throws a grenade. A dead player respawns after five seconds with
fresh ammunition and grenades.

## 7. Fly.io deployment

The first app is named `fsharp-of-duty` and listens on `0.0.0.0:8080`. `fly.toml`
keeps one Machine running, terminates HTTPS at Fly Proxy, forwards WebSocket
upgrades, and checks:

- `/health/live`
- `/health/ready`

Match state and reconnect reservations are process-local. V1 therefore runs a
single Machine while testing. Horizontal room placement and shared session state
are later operational work.

## 8. Acceptance requirements

1. Two clients can select the same mode, become ready, enter play, exchange inputs,
   observe each other, score a kill, respawn, and see identical scores.
2. FFA increments only the killer. TDM increments the killer and their team;
   teammates cannot damage one another.
3. Grenades are cooked, thrown, simulated, occluded, replicated, and scored by the
   server. Throwing cancels spawn protection.
4. Invalid, stale, far-future, non-finite, oversized, or excessive inputs cannot
   alter authoritative state.
5. Local prediction reconciles to acknowledged inputs, remote state interpolates,
   replicated events do not play twice, and hitscan rewind is capped at 200 ms.
6. Reconnecting within 30 seconds restores identity and score.
7. The headless container starts without graphics/audio dependencies, reports
   healthy, and drains on termination.

## 9. Version 2 transport roadmap

The following are performance upgrades, not prerequisites for the Fly/WebSocket
release:

- LiteNetLib or another UDP transport with unreliable-sequenced state and a
  reliable-ordered control channel.
- Quantized `UserCmd` values and a hand-written binary codec.
- Per-client snapshot baselines, acknowledgements, dirty masks, and delta encoding.
- RTT-derived view-time and full historical hit-capsule rewind.
- Artificial latency, jitter, reordering, and loss injection in the network harness.
- Regional room placement or a server directory when one Fly Machine is no longer
  sufficient.

These optimizations preserve the v1 authority model and gameplay protocol. They do
not require merging campaign AI and online players into one domain record first.
