# Plan: kill feed completion, server extensibility, chat + op commands, reload-bar fix

Tracking document for issues **#10** (kill feed / round summary), **#9** (extensible server), **#4** (chat + op commands), plus one bug: the reload progress bar never appears in online play.

Status legend: `[ ]` todo · `[~]` in progress · `[x]` done

---

## 0. Already landed (previous session)

- [x] `GameEvent.Kill / PlayerJoined / PlayerLeft / PhaseChanged` (`Domain.fs:232-239`)
- [x] Emission from `MatchHost` at both kill sites, join/leave, phase transitions
- [x] Wire encode/decode (`Protocol.eventSnapshot`, `OnlineWorld.eventToGameEvent`)
- [x] Client `Feedback.applyFeed` + top-right kill feed + Results winner/top-fragger
- [x] Tests: kill emission, lifecycle events, phase-transition-once, wire round trip, feed cap/expiry

This closed #9 Phase 0 item 3. Everything below is the remainder.

---

## 1. BUG — reload bar invisible online

### Root cause

The server simulates reload correctly (`MatchHost.stepFrame` runs the same `Sim.stepLocomotion` as offline, writing `Weapon = result.Weapon` at `MatchHost.fs:341`), but **`Protocol.PlayerSnapshot` carries no weapon-state field** (`Protocol.fs:20-42` — only `ammo`, `reserve`, `weapon`). So `OnlineWorld.weaponFor` (`OnlineWorld.fs:43-47`) rebuilds every online `WeaponSlot` from `Tuning.weaponSlot`, which hardcodes `State = Ready` (`Tuning.fs:306-312`).

`Hud.drawReloadBar` (`Hud.fs:242-254`) matches on `weapon.State with Reloading remaining` — a case that can never occur online. Reconciliation would clobber a locally-predicted timer anyway: `OnlineWorld.localPlayer` replaces `Slots` wholesale every snapshot (`OnlineWorld.fs:66`).

Two latent siblings from the same cause, both fixed by the same change:
- Reload **SFX** never plays online — `Program.fs:510-511` (`| Ready, Reloading _ -> PlayReload`) is inside the offline branch only.
- Client fire prediction gates on `localWeapon.State = Ready` (`Program.fs:467`), which is *always* true online, so it never suppresses the predicted muzzle flash mid-reload.

### Fix (wire the truth the server already has)

- [x] `Protocol.fs:20-42` — add `reloadRemaining: float32` to `PlayerSnapshot`
- [x] `Protocol.fs:200-202` — populate: `match player.Weapon.State with Reloading r -> Units.raw r | _ -> 0.0f`
- [x] `NetworkClient.fs:27-29` + `:116-118` — add and parse the field (`getFloat` defaults to `0.0f`, so old servers stay compatible)
- [x] `OnlineWorld.fs:47` — **the single choke point**: `State = if r > 0.0f then Reloading(Units.seconds r) else Ready`. Both `localPlayer` and `remoteSoldier` route through `weaponFor`, so HUD, viewmodel and remote soldiers all fix at once
- [x] `Program.fs:510-511` — hoist the reload-SFX edge detection so the online branch gets it too. Landed as a shared `lastActiveWeaponState` edge check in the once-per-tick active-slot block that already tracks `lastActiveWeaponName`/`lastActiveInMag`, which runs after both the offline `Sim.step` and the online reconcile

Rejected: client-side reload prediction. The server already knows the truth; a predicted timer needs a reconciliation rule for disagreement. The wire field is the smaller, correct diff.

**Test:** extend the existing wire round-trip test (`ServerTests.fs:412`) to assert `reloadRemaining` survives; add a `MatchHost` test that pressing reload puts a non-zero `reloadRemaining` on the snapshot.

- [x] Round-trip test asserts the field mirrors `server.Weapon.State`
- [x] `pressing reload puts a non-zero reloadRemaining on the wire` (ServerTests) — fire, wait out the bolt cycle, reload, assert the wire value matches the server timer
- [x] `a reloading snapshot rebuilds the weapon slot as Reloading` (ClientTests) — covers the `weaponFor` choke point for both local player and remote soldier

---

## 2. #10 remainder — killstreaks

- [x] `Multiplayer.fs` — `NetworkPlayer` gains `Streak: int` and `BestStreak: int`
- [x] `recordKill` — `Streak + 1`, `BestStreak = max BestStreak Streak` on the killer; `markDead` resets the victim's `Streak` to 0 (the one place a player is marked dead, so both kill paths are covered)
- [x] Round reset (`MatchHost.fs:296-299`, the `Results -> Warmup` arm that already zeroes `Kills`/`Deaths`) must zero both
- [x] `Protocol.fs` player DTO + `NetworkClient.OnlinePlayer` — replicate `bestStreak` (only the high-water mark; nothing renders the live streak)
- [x] `Hud.fs` Results card — third line: `BEST STREAK  {name}  {n}`; summary panel grows 76 instead of 52

**Tests:**

- [x] `consecutive kills build a streak that dying resets` (CoreTests) — two kills, then both death paths (`markDead` directly and via `recordKill`)
- [x] `a kill streak reaches the wire and is cleared at round end` (ServerTests) — real rifle kill, `bestStreak` round trip, then the full round back into Warmup
- [x] Round-trip test asserts `bestStreak` mirrors the server player
- [x] `TestKit.rifleShot` extracted from `authoritative rifle hit ...` so both tests share one aim-and-fire sequence

---

## 3. #9 Phase 0 remainder — the seams

- [x] **Dispatch extraction.** `Program.fs`'s inline match becomes a `dispatch extensions host playerId message` function. A per-connection context record was tried and removed again in the review pass (§8) — one construction site, one consumer. Dispatch returns `bool` (false = the client said goodbye) because `connected` is a mutable local a closure cannot capture. Still synchronous. A command router is one more arm.
- [x] **Public `MatchHost.Enqueue(event, ?recipient)`** — lock-guarded, wrapping the private `enqueue` helper added last session (`MatchHost.fs:21-29`), which now takes the recipient itself (`None` at all three existing join/leave call sites) so there is exactly one implementation.
- [x] `an enqueued event broadcasts unless it names a recipient` (ServerTests) — asserts both the broadcast and the whisper-targeted `Recipient`

### Locking rules (load-bearing — from the issue and confirmed in code)

- Every public member takes `lock gate` at its top level and does not nest into another locked member.
- `AdvanceTick` is driven by exactly one `PeriodicTimer` thread; extension tick hooks run inside/after that call, never on their own timer.
- .NET `lock` *is* reentrant on the same thread, so a member calling `Snapshot()` inside its own lock won't deadlock — but **never hold the gate across socket I/O**; it blocks the whole room's tick. Build a list under the lock, send after.

---

## 4. #9 Phase 1 — extension layer

Idiomatic here means a **record of functions**, not an interface hierarchy (the only `interface` usage in `src/` is `IDisposable`).

Landed in `src/Ironsight.Server/Commands.fs` (compiled between `MatchHost.fs` and
`Program.fs`), trimmed to what has a consumer:

```fsharp
type CommandLevel = Everyone | Op

type CommandContext =
    { PlayerId: EntityId
      Host: MatchHost
      Reply: string -> unit          // whisper via Enqueue(_, recipient = playerId)
      Visible: Command list }        // what this caller may run — /help's source

and Command =
    { Verb: string; Level: CommandLevel; Usage: string; Run: CommandContext -> string list -> unit }

type ServerExtension = { Name: string; Commands: Command list }
```

- [x] Registered as a `ServerExtension list` passed into `build` (`main` passes `[ Commands.builtins ]`) and threaded through `dispatch` as a plain parameter
- [x] Command→permission mapping declared per-command; `Visible` is filtered **once** in `Commands.run` and drives both `/help`'s listing and lookup, so a command the caller can't run answers "unknown command" rather than "forbidden"
- [x] Extensions are plain records, so `ServerTests.fs` keeps constructing `MatchHost` bare and calls `Commands.handleChat [ Commands.builtins ] host id line` without ASP.NET Core

**Dropped from the sketch:** `Broadcast` (one consumer, `/say`, which is a
one-liner through `Host.Enqueue`), `OnEvent` and `OnTick` (no consumer at all —
so no tick-loop hook inside `tickSafely` either). All three are additive when a
real consumer shows up. `Visible` was added because `/help` needs it and it is
what makes visibility and execution share one filter.

**Phase 2 (out-of-tree plugin API, ALC loading) is NOT built.** The issue itself scopes it "only when someone actually wants out-of-tree plugins", and there is no plugin to load. Building an `AssemblyLoadContext` loader + contract assembly now is speculative work whose shape is decided by its first real consumer. Phase 1's record is versioned-in-place and can be lifted into a contract assembly unchanged when that consumer exists.

---

## 5. #4 — chat + op commands

### 5a. Chat transport (no new transport needed)

Chat rides the **existing event stream**: `MatchHost` state mutates under the gate, and every client picks it up ≤50 ms later via the snapshot. `Recipient`/`emitOnly` already gives whisper targeting (`recipientId`, `Protocol.fs:219`).

- [x] `Domain.fs` — `Chat of sender: EntityId option * name: string * text: string` (`None` sender = server/system message; name baked in for the same reason `Kill` names resolve at receipt)
- [x] `Protocol.fs` / `OnlineWorld.fs` — `"chat"` kind, `entityId` = sender (0 = system), `text` = `$"{name}\t{body}"`. Tab is a safe separator precisely because `sanitizeText` strips control scalars from both halves; the comment says so identically on both sides
- [x] `Program.fs` (server) — `| Some "chat"` dispatch arm
- [x] `Multiplayer.fs` — a `sanitizeText maxLength` generalized from the existing `sanitizeName` (trim, drop control runes, truncate). Control scalars are dropped *before* the cap so padding cannot eat a real message's budget
- [x] Per-player chat cooldown of 1/sec, enforced in `MatchHost.Chat` under the gate. Measured in **ticks** (`state.Tick - last >= Tuning.TickRate`), not wall clock, so the test needs no `Thread.Sleep`. The existing 120 msg/s limiter is not enough — it hard-closes the socket, so a chat flood would *drop the player* rather than throttle them

### 5b. Client chat UI

- [x] `Screen.Chat of draft: string` added to the `Screen` DU. Reuses the `Loadout` precedent: world keeps simulating, input frame masked to zero (`wasLoadout`/`isLoadout` generalized to `wasOverlay`/`isOverlay`), menu-mode input active
  - [x] **Divergence from Loadout:** solved by splitting the pointer out of menu mode. `InputSampler.SetMode(value, releasePointer)` is the one implementation; `SetMenuActive value` = `SetMode(value, value)` and the new `SetTextCapture value` = `SetMode(value, false)`, so chat gets menu key routing with the cursor still grabbed
- [x] Text editing copies the callsign editor (`Menu.fs:229-239`): backspace first, then fold printable chars, capped at 120
- [x] Open with `Y` (`ConsumeChatToggle`, latched alongside `ConsumeLoadoutToggle`), send with Enter, cancel with Esc. Only opens online — offline there is nobody to talk to
- [x] `NetworkClient.SendChat` — a `ConcurrentQueue` drained in the single `senderLoop`, so the socket never sees concurrent sends and two quick lines can't clobber each other
- [x] `Hud.fs` — `drawChat`, bottom-left: draft line at `height-142`, log stacking upward 20px per row, all of it above the subtitle band
- [x] Chat history reuses `FeedItem` / `Feedback.tick` / `List.truncate` (12s, 6 rows) and is picked up in the same `applyFeed` scan as the kill feed. `FeedItem.Headshot` became `Highlight` — for a chat row it marks a server line

**Tests:**

- [x] `sanitized text is trimmed truncated and stripped of control characters` (CoreTests) — tab and newline stripped, null-safe, control scalars dropped before the truncation cap
- [x] `chat is sanitized and throttled to one line per second` (ServerTests) — a second line inside the cooldown is dropped, a blank-after-sanitize line sends nothing, an unknown id is a no-op
- [x] Wire round-trip test covers both chat shapes (a player line and a system line with an empty name)
- [x] `chat log keeps the newest lines and expires them` (ClientTests) — 6-row cap, 12s lifetime, and chat rows never leak into the kill feed
- [x] `MatchScript.Talk` + the two-player integration test — proves the `"chat"` message and its `text` field survive a real socket

### 5c. Op authentication

- [x] `IRONSIGHT_OP_KEY` env var, read once in the `MatchHost` constructor as the default of a `?opKey` parameter (so tests configure a key without mutating process env). `MatchHost.TryElevate` adds the `EntityId` to an in-memory `Set<EntityId>`; `RemovePlayer` clears it, so a resumed session must say `/op` again
- [x] Blank or unset key: `TryElevate` returns false before comparing anything — never "everyone is op"
- [x] `/op` replies are whispers, never broadcast (the key must not land in anyone else's chat log)
- [x] Failed guesses are throttled to one per second per player, in ticks like the chat cooldown. The connection-level 120 msg/s limiter is a flood guard, not a rate a shared secret should be guessable at

Documented tradeoffs: shared key = no per-person audit trail; revocation = change the env var and restart; it travels plaintext like everything else in this protocol (fine over wss). Good enough for a small dedicated server; not good enough for per-op accountability. **`sessionOwners` cannot be the basis for this** — its own comment (`MatchHost.fs:44-46`) says session tokens are not a security boundary.

### 5d. Built-in commands (first consumers of the registry)

| Command | Level | Notes |
|---|---|---|
| `/help` | Everyone | Lists only what the caller may run |
| `/op <key>` | Everyone | Whisper-only reply |
| `/say <text>` | Op | Broadcast, styled distinctly from player chat |
| `/kick <name>` | Op | See below |
| `/map <alias>` | Op | See below |
| `/restart` | Op | Reset scores + phase back to Warmup |

- [x] All six commands land as `Commands.builtins`, the registry's first and only extension. `Commands.handleChat` is the single entry point the `"chat"` dispatch arm calls: a leading `/` routes to the command router, anything else to `MatchHost.Chat`, so the routing decision is unit-testable without ASP.NET Core
- **`/kick`** — there is **no socket registry**; sockets are `use!` bindings local to `handleSocket` and nothing stores them. Landed as a `kicked` set on `MatchHost` polled in the receive loop's `while` condition (bounded by the 50 ms snapshot delay), with `RemovePlayer` in the `finally` doing slot cleanup and clearing the flag. A kicked player can trivially rejoin — there is no durable identity to ban against, and the `/help` usage line says so.
- **`/map`** — `MatchHost.level` became mutable and a `pendingLevel` applies inside `warmupReset`, the shared between-rounds reset extracted from the `Results -> Warmup` arm (`/restart` calls the same function, which is why a `/map` + `/restart` pair applies immediately). Restricted to the builtin alias table (`Levels.specByAlias` → `Levels.byName`, reusing the already-compiled levels) because the client hot-swaps by name and custom maps travel by content hash.
- Docs: `README.md` gained the chat/`/op` paragraph, `docs/MULTIPLAYER.md` a "Chat and op commands" section with the command table and the `IRONSIGHT_OP_KEY` tradeoffs; its stale "there is no text chat" limitation is corrected.

**Tests (ServerTests):**

- [x] `help lists only the commands the caller may run` — op verbs hidden before elevation, listed after, and an invisible verb answers "unknown command"
- [x] `op elevates only on the configured key` — wrong key fails, the throttle blocks the immediate retry, the right key lands a second later, disconnect drops elevation
- [x] `op never elevates when no key is configured`
- [x] `a command answers the caller alone and is never broadcast` — every chat event from `/op` is whispered and none contains the key; a plain line still broadcasts
- [x] `say broadcasts a server line and restart ends the round`
- [x] `kick flags the named player for his own loop to drop` — case-insensitive name match, unknown name is `None`, `RemovePlayer` clears the flag
- [x] `map accepts builtin aliases only and applies between rounds`

---

## 6. Ordering

These slices overlap heavily on `Domain.fs`, `Protocol.fs`, `MatchHost.fs`, `Program.fs` (both), and `Hud.fs`, so they land **sequentially**, each ending green:

1. Reload-bar bug (§1) — self-contained, unblocks nothing, ships value immediately
2. Killstreaks (§2) — closes #10
3. ~~Phase 0 seams (§3) — dispatch extraction + public `Enqueue`~~ done
4. ~~Chat core (§5a, §5b) — the first thing that needs the seams~~ done
5. ~~Extension layer + commands (§4, §5c, §5d) — the registry, dogfooded by the built-ins~~ done

Review and adversarial verification fan out in parallel at the end.

## 7. Verification

- `just test` (fast suite) green after **every** slice, `just check` (lint + build + test + smoke) at the end
- New tests per slice, following house conventions (xunit, backtick-sentence names, `MatchHost` constructed bare)
- Wire round-trip test extended for every new field and event kind — the writer and reader are hand-maintained mirrors and the reader defaults missing fields to zero, so only a round trip catches drift
- [x] `MatchScript.fs` gained a `Talk` act (named around the existing `ScriptAction.Say`), used by the two-player integration test to prove the `"chat"` message survives the real socket
- Manual: `just server` + two `just online-local` clients — reload bar, kill feed, chat, `/op` + `/kick`, Results card

---

## 8. Review findings fixed — defects repaired after the slices landed

Adversarial review of the landed code. Each item is the *root cause*, not the symptom.

**Throttle and amplification** — the whole class came from one shared entry point letting a sibling branch escape the guard.

- [x] `Commands.handleChat` sanitizes **before** the slash test. A leading C0 scalar dodged `TrimStart().StartsWith '/'` and got republished as broadcast chat with the op key in plaintext
- [x] Commands share chat's one-line-per-second budget. The cooldown moved out of `MatchHost.Chat` into a private `tryChatCredit` that both `Chat` and the new public `TryChatCredit` spend, so the command branch pays it too
- [x] `Protocol.snapshotFor viewer` filters recipient-tagged events out of each socket's snapshot (Program.fs's send loop calls it instead of `Protocol.snapshot`). `Reply`'s "never reaches anyone else" was a client-side display filter, not a boundary; whispers were on the wire for all 16 clients and were the amplification multiplier
- [x] Join/leave feed rows go through an `announce` helper on the same 1/s per-player cooldown. A client holding a session token could cycle its slot indefinitely, two broadcast rows per handshake, drowning a 5-row feed. **Tradeoff:** a join and a leave inside the same second announce once
- [x] `lastChatTick` / `lastOpAttemptTick` / `lastAnnounceTick` are pruned in `AdvanceTick`'s grace-expiry sweep. Deliberately *not* in `RemovePlayer` — the slot is still resumable there, so clearing would let a flooder reset both throttles with a leave/rejoin

**Correctness**

- [x] `recordKill` credits the kill but not the streak when the killer is already dead (`if killer.Alive then killer.Streak + 1 else 0`). The shots loop runs before the explosions loop, so a dead grenade owner resurrected his own streak
- [x] `markDead` clears `Weapon.State` to `Ready`. Nothing steps a dead player's weapon, so `reloadRemaining` froze on the wire and the online HUD showed a motionless reload bar for the whole 5 s respawn
- [x] `Protocol.welcomeFor` names `host.Snapshot().LevelName`, not the build-time level, and sends an empty hash when they differ. After `/map` a joiner was told the old name and downloaded the old (possibly custom) map bytes for nothing

**Overengineering — deletions only**

- [x] `CommandContext.IsOp` — written, never read. `Visible` is already filtered by it
- [x] The `headshots` `List.pairwise`/`Map.ofList` block — `Ballistics` already stamps `DyingHeadshot` on the hit soldier, which is `after` at the emit site. Also drops a per-shot allocation from the hot path
- [x] `Connection` record in the server's `Program.fs` — one construction site, one consumer; `dispatch` takes plain parameters
- [x] `MenuNav.editText` — the callsign and chat editors were a character-for-character copy differing only in the cap
- [x] Three design-rationale comments that duplicated this document

**Tests:** `commands share the chat cooldown instead of bypassing it`, `a command hidden behind a control character is not republished as chat`, `reconnect cycling cannot flood the feed with lifecycle rows` (ServerTests); posthumous-streak assertions on the existing streak test and `dying cancels an interrupted reload` (CoreTests). The command tests gained a shared `waitOutCooldown` because commands are now throttled.

**Gate after the review pass:** `dotnet build Ironsight.sln` — 0 warnings, 0 errors
(`TreatWarningsAsErrors=true`); 185 unit tests and 5 integration tests, all passing.
Nothing outstanding from §8; Phase 2 (§4) remains deliberately unbuilt.
