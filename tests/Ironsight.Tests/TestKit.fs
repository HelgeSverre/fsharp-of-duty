namespace Ironsight.Tests

open System
open System.Numerics
open System.Text.Json
open Ironsight
open Ironsight.ProcGen
open Ironsight.Server
open Ironsight.Shell
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.Extensions.DependencyInjection

/// Shared test scaffolding: helpers duplicated verbatim (or near-verbatim)
/// across multiple test files live here instead of being copy-pasted.
module TestKit =
    /// Boots the real application on an ephemeral port and returns its ws://
    /// play endpoint. Each caller gets a fresh host: both rooms tick from
    /// process start, so a shared one would leak players and scores between
    /// tests.
    let startServer () =
        let app = Program.build [||] 0
        app.StartAsync().GetAwaiter().GetResult()
        let address =
            app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses
            |> Seq.head
        let port = Uri(address).Port
        app, Uri $"ws://127.0.0.1:%d{port}/play"

    /// Standard Axis soldier used as a baseline across combat/AI/client
    /// tests. Call sites that need different fields should use record-update
    /// syntax against this, e.g. `{ TestKit.soldier 9 pos with Facing = MathF.PI }`.
    let soldier (id: int) (position: Vector3) : Soldier =
        { Id = EntityId id
          Team = Axis
          Position = position
          Facing = 0.0f
          Stance = Standing
          Health = Units.health 100.0f
          Behavior = Idle
          Weapon = Tuning.weaponSlot Tuning.kar98k 2
          Squad = 1
          Contacts = Map.empty
          Suppression = 0.0f
          AnimPhase = 0.0f }

    /// Readies every given player and runs the match through warmup (10 s at
    /// the 72 Hz tick rate = 721 ticks) into Playing.
    let readyUp (host: MatchHost) (ids: EntityId list) =
        for id in ids do
            host.SetReady id
        for _ in 1..721 do
            host.AdvanceTick()

    /// One campaign input frame: buttons + move, no look.
    let input (sequence: int64) buttons (move: Vector2) : InputFrame =
        { Sequence = sequence; Move = move; Look = Vector2.Zero; Buttons = buttons }

    /// Run the campaign sim over the inclusive sequence range, holding the
    /// same buttons (and no movement) every tick; collects every event.
    let stepAll (firstSequence: int64) (lastSequence: int64) buttons (world: World) =
        let mutable current = world
        let collected = ResizeArray<GameEvent>()
        for sequence in firstSequence .. lastSequence do
            let struct (next, events) = Sim.step (input sequence buttons Vector2.Zero) current
            current <- next
            collected.AddRange events
        current, List.ofSeq collected

    /// As stepAll, discarding the events.
    let advance firstSequence lastSequence buttons world =
        stepAll firstSequence lastSequence buttons world |> fst

    /// Fold AiBrain.step over `ticks` ticks with a fresh RNG from `seed` and
    /// no squad blackboards, concatenating every emitted event.
    let runBrain (seed: uint64) (level: Level) (player: Player) (soldiers: Soldier array) ticks =
        let mutable rng = Rng.create seed
        let mutable currentPlayer = player
        let mutable current = soldiers
        let events = ResizeArray<GameEvent>()
        for _ in 1..ticks do
            let nextPlayer, next, emitted = AiBrain.step Tuning.TickDuration &rng level Map.empty currentPlayer current
            currentPlayer <- nextPlayer
            current <- next
            events.AddRange emitted
        currentPlayer, current, List.ofSeq events

    /// A menu input frame with nothing pressed; record-update what you need.
    let idleMenuInput: MenuInput =
        { Up = false
          Down = false
          Left = false
          Right = false
          Activate = false
          Back = false
          Backspace = false
          TextInput = ""
          Pointer = None
          Clicked = false }

    /// Triangle triples from an indexed mesh.
    let triangles (vertices: MeshVertex array) (indices: uint32 array) =
        indices
        |> Seq.chunkBySize 3
        |> Seq.map (fun triple -> vertices[int triple[0]], vertices[int triple[1]], vertices[int triple[2]])

    /// A bare open street arena of the given size, no spawns declared.
    let streetArenaSized name width depth =
        LevelDsl.level name [ LevelDsl.street width depth Mud ] |> LevelCompile.compile

    /// The default 50 x 20 open street arena.
    let streetArena name = streetArenaSized name 50.0f 20.0f

    /// The same street arena with an Allies squad north and an Axis squad south.
    let streetArenaWithSpawns name =
        LevelDsl.level name
            [ LevelDsl.street 50.0f 20.0f Mud
              LevelDsl.spawnSquad Allies 1 (Vector3(0.0f, 0.0f, 12.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(0.0f, 0.0f, -12.0f)) ]
        |> LevelCompile.compile

    let applyCustom sequence moveY lookX buttons (host: MatchHost) id =
        use document =
            JsonDocument.Parse(FormattableString.Invariant($"""{{"type":"input","sequence":{sequence},"moveX":0,"moveY":{moveY},"lookX":{lookX},"lookY":0,"buttons":{buttons}}}"""))
        host.ApplyInput(id, document.RootElement)

    let applyInput sequence host id = applyCustom sequence 1.0f 0.0f 4 host id
