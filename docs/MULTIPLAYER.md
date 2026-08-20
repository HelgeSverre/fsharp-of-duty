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
Paintball Killhouse; `IRONSIGHT_LEVEL` selects another: `training`, `depot`,
`canal`, or `omaha`.

## Rooms

A server hosts a list of rooms, each with its own map, mode and rules. With no
config file it hosts the two it always did — Team Deathmatch and Free For All,
both on the `IRONSIGHT_LEVEL` map — so an existing deployment needs no changes.

`server.json` beside the binary, or wherever `IRONSIGHT_CONFIG` points:

```json
{
  "name": "Helge's Bunker",
  "motd": "Welcome. No camping the spawn.",
  "rooms": [
    { "id": "tdm-canal", "name": "Canal Yard TDM", "mode": "TeamDeathmatch", "level": "canal" },
    { "id": "ffa-depot", "name": "Depot Deathmatch", "mode": "FreeForAll", "level": "depot",
      "scoreLimit": 20, "timeLimit": 300, "maxPlayers": 8 }
  ]
}
```

`name` is what the server browser lists, overriding whatever a player called
this server in their own bookmarks; `motd` is whispered to each joiner as a
system chat line when they connect. Both are optional and both go through the
same sanitizer as names and chat.

`id` and `mode` are required; everything else falls back to the old hardcoded
values — the `IRONSIGHT_LEVEL` map, 75 kills for TDM and 30 for FFA, ten
minutes, sixteen players. `level` takes the builtin aliases only, because the
client resolves a builtin by name while a custom map has to be downloaded by
content hash and only the boot map's bytes are served.

A bad config **stops the server at boot** rather than being ignored: a silently
dropped config would leave it running on rules nobody chose. Empty room lists,
unknown modes or levels, missing or duplicate ids are all errors.

Rooms appear as separate rows in the server browser, labelled by `name`, and
joining one sends its `id`. A client that sends no id — anything built before
rooms existed — gets the first room of the mode it asked for that has a free
slot, so old clients keep working against a multi-room server.

Every room is otherwise independent: its own players, scores, phase, chat and
kick list. Ops are server-wide, since `IRONSIGHT_OP_KEY` is read once per
process. All rooms tick on one thread, so their cost adds up on one core —
measured, the tick itself is ~0.03 ms per player, and it is snapshot fan-out
rather than simulation that sets the ceiling.

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
`level`, `mapHash`,
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

Implemented client message types are `hello`, `ready`, `input`, `loadout`,
`chat`, and `leave`. A `loadout` message (`{"type":"loadout","weapon":"STG-44"}`)
changes the player's **primary** with no restrictions: instantly outside live
play, otherwise on the next spawn.

## Loadout

An online player carries a primary and a sidearm. The primary is the weapon
picked in the menu, from any of the twelve; the sidearm is issued by team —
M1911 for the Allies, Luger P08 for the Axis — and is not a choice, the way
Counter-Strike and Battlefield hand out a pistol.

The number keys select by category, the same keys and the same grouping the
offline sandbox uses, so a gun sits under the same key in both:

| Key | Category |
| --- | --- |
| 1 | Bolt and semi-auto rifles |
| 2 | Automatics (SMGs and the STG-44) |
| 3 | Pistols |
| 4 | Scoped |
| 5 | Heavy (shotgun, BAR) |

The category comes from the weapon's own kind and fire mode rather than a table
of inventory positions, so it describes a two-weapon kit and the twelve-weapon
offline loadout alike. A key holding nothing is a no-op. Switching takes 0.35 s,
during which the weapon clock is frozen — no firing, no reloading — and the
switch is replicated, so other players see the raise rather than a gun that
teleports into your hands.

Each slot keeps its own ammunition: rounds spent on the rifle are still spent
when you switch back to it.

## Chat and op commands

`{"type":"chat","text":"on your left"}` broadcasts one sanitized line, throttled
to one per second per player. Lines are replicated as ordinary events on the
snapshot, so no second transport exists.

A `text` that starts with `/` is a command instead: it is never broadcast, never
spends the chat cooldown, and its output is whispered back to the caller alone.
Commands come from a registry of `ServerExtension` records (`Commands.fs`), so
adding one is a list entry rather than a new message type.

| Command | Level | Effect |
| --- | --- | --- |
| `/help` | everyone | Lists only the commands the caller may run |
| `/op <key>` | everyone | Elevates on a match with `IRONSIGHT_OP_KEY` |
| `/say <text>` | op | Broadcasts a highlighted server line |
| `/kick <name>` | op | Drops a connected player, who may rejoin |
| `/ban <name>` | op | Drops a player and refuses his address from then on |
| `/map <alias>` | op | Queues a builtin map for the next round |
| `/restart` | op | Ends the round now and clears scores |

`/kick` is deliberately soft — it ends the session now and nothing stops a
rejoin. `/ban` is the durable one, and an address is the only durable identity
the server has: names are free to pick and session tokens only resume a slot.

Behind a proxy the socket's peer is the proxy, so a naive ban would refuse
every player at once. The address is taken from `Fly-Client-IP` when present
(the edge sets it and a client cannot forge it), otherwise the first hop of
`X-Forwarded-For`, otherwise the socket peer. A shared or carrier-NAT address
bans everyone behind it and a residential lease eventually moves, so this stops
a specific nuisance now rather than a determined one forever.

Bans live in memory and, if `IRONSIGHT_BAN_LIST` names a file, are appended to
it and reloaded at start. On Fly the filesystem is ephemeral without a volume,
so a ban list there does not survive a deploy.

`IRONSIGHT_OP_KEY` is a single shared secret read at server start. If it is
unset or empty, `/op` always fails — there is no default-op path. Elevation is
held in process memory per player and dropped on disconnect; failed guesses are
throttled to one per second. The tradeoffs are deliberate: no per-person audit
trail, revocation means changing the variable and restarting, and the key
travels in plaintext like the rest of the protocol (fine behind `wss`). Good
enough for a small dedicated server, not for per-op accountability.

Chat is transcribed to stdout as `<timestamp> [<mode>] <name>: <line>`, which is
the sink that survives on Fly. Setting `IRONSIGHT_CHAT_LOG` adds a file copy for
hosts with somewhere durable to write. Command lines are never transcribed —
they are whispered back to the caller, and `/op` carries the key.

### Extensions

A server extension is a plain record, registered by passing it to `build`:

```fsharp
{ ServerExtension.empty "name" with
    Commands = [ ... ]                       // routed by /verb, gated by level
    OnEvent = Some(fun host event -> ...)    // each replicated event, after its tick
    OnTick  = Some(fun host state -> ...) }  // once per room, per tick
```

`OnEvent` runs outside the room gate, so a hook may call back into the host.
Both hooks run inside the tick loop's fault isolation: a throwing hook logs and
the room keeps ticking. The chat transcript is itself an extension (`ChatLog`),
which is the shape to copy.

`/kick` sets a flag the victim's own receive loop polls; nothing prevents an
immediate rejoin, because there is no durable identity to ban against. `/map`
accepts builtin aliases only and applies at the next warmup, because the client
hot-swaps a level by name and custom maps travel by content hash.
## Server directory

The server browser is a Half-Life-style table: one row per room with the
server host, mode, player count, phase and ping as columns. The list of
servers comes from `servers.json`, merged in precedence order: the
`IRONSIGHT_SERVER` environment override, the user's own copy at
`<appdata>/ironsight/servers.json`, the community master list fetched from the
repo (`raw.githubusercontent.com/HelgeSverre/fsharp-of-duty/main/servers.json`,
cached for offline runs), the copy packaged beside the executable, and a
compiled-in default. Hosting your own server means adding one JSON entry —
or PRing it into the repo's list so every installed client picks it up.

## Maps and map download

Maps are specs (DSL items), stored as versioned binary `.ironmap` files (encode
and decode live in `Ironsight.Core/ProcGen/MapFile.fs`). A map's identity is the
SHA-256 of its encoded bytes. The design is the GoldSrc/Half-Life resource flow
cut to its essentials: the server announces the map's name and hash in
`welcome`; a client that has neither a matching built-in nor a cached copy
fetches `GET /maps/{hash}` over the server's existing HTTP listener (the modern
`sv_downloadurl`/FastDL path rather than the slow in-game channel), verifies the
hash, caches it, and compiles it. The cache lives at
`<appdata>/ironsight/maps/<hash>.ironmap` (override the root with
`IRONSIGHT_HOME`). Because the cache is keyed by content hash — not by map name —
GoldSrc's "your map differs from the server's" cannot happen: a changed map is
simply a new hash and a fresh download, and the immutable hash URL makes any
HTTP cache in the path safe.

Servers host custom maps with `IRONSIGHT_LEVEL=/path/to/map.ironmap`; the client
plays one offline with `--map /path/to/map.ironmap`. `tools/MapExport.fsx`
writes every built-in map to `.ironmap` files as reference material.

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

There are no accounts, persistent progression, or per-person authentication:
administration is a single shared op key (see above). Session tokens are random,
held only in process memory, and are not a security boundary. The JSON protocol uses full snapshots rather than delta
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

## Scripted match tests

`tests/Ironsight.Tests/MatchScript.fs` drives real `OnlineClient` bots over real
WebSockets from a list of `Act` values. Acts set intent — `Move`, `Press`,
`FaceEnemy` — and a background pump turns that intent into input frames at tick
rate, so a scenario never spells out individual frames.

```fsharp
MatchScript.run uri TeamDeathmatch [
    Join "Alpha"
    Join "Bravo"
    WaitUntil("the match reaches Playing", 30.0, fun snapshot -> snapshot.Phase = Playing)
    Move("Alpha", Vector2(0.0f, 1.0f))
    FaceEnemy "Alpha"
    Press("Alpha", InputButtons.Fire)
    WaitUntil("shot events reach the other client", 5.0, fun snapshot ->
        snapshot.Events |> Array.exists (fun event -> event.Kind = "shot"))
    Leave "Alpha"
]
```

A failed `Expect` or `WaitUntil` reports the label plus every bot's last snapshot.

```sh
just smoke          # scenarios in tests/Ironsight.Tests/IntegrationTests.fs
just test           # unit tests only; the socket tests are filtered out
```

`just smoke` boots the application in-process on an ephemeral port via
`Program.build`, so it needs no network and runs in CI. Reaching `Playing`
requires two ready players plus the ten-second warmup, so expect these to take
around fifteen seconds. Timing is wall-clock, so scenarios assert on outcomes
rather than exact ticks; `ServerTests.fs` covers tick-exact behaviour by driving
`MatchHost` directly.

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

`/health/ready` returns a constant, so it proves the process is listening and
nothing more. After a deploy, verify the match loop itself:

```sh
just smoke-remote
```

That connects a real bot to `wss://fsharp-of-duty.fly.dev/play` and asserts the
server tick advances, which exercises the handshake, the 60 Hz match loop and the
20 Hz snapshot pump. It joins the live public room, so it appears briefly on the
leaderboard. Pass a different URL as the argument to check another deployment.
