namespace Ironsight.Tests

open System
open System.Numerics
open Ironsight
open Ironsight.ProcGen
open Ironsight.Shell
open Xunit

module ClientTests =
    [<Fact>]
    let ``hud ui scale maps framebuffer pixels to logical window units`` () =
        Assert.Equal(1.0f, HudLayout.uiScale 1280 1280)
        Assert.Equal(2.0f, HudLayout.uiScale 2560 1280)
        Assert.Equal(1.5f, HudLayout.uiScale 2880 1920)
        Assert.Equal(4.0f, HudLayout.uiScale 4000 100)
        Assert.Equal(0.5f, HudLayout.uiScale 50 100)
        // Degenerate sizes fall back to 1x instead of dividing by zero.
        Assert.Equal(1.0f, HudLayout.uiScale 0 1280)
        Assert.Equal(1.0f, HudLayout.uiScale 1280 0)
        Assert.Equal(1.0f, HudLayout.uiScale -2560 1280)

    [<Fact>]
    let ``render interpolation blends transforms without changing current gameplay state`` () =
        let previous = Sim.createTrainingWorld 4UL
        let movedPlayer = { previous.Player with Position = Vector3(10.0f, 2.0f, -4.0f); Health = Units.health 37.0f; Ads = 1.0f }
        let movedSoldier = { previous.Soldiers[0] with Position = previous.Soldiers[0].Position + Vector3(4.0f, 0.0f, 2.0f) }
        let current = { previous with Player = movedPlayer; Soldiers = Array.append [| movedSoldier |] previous.Soldiers[1..] }
        let rendered = RenderInterpolation.world 0.5f previous current
        Assert.Equal(Vector3.Lerp(previous.Player.Position, movedPlayer.Position, 0.5f), rendered.Player.Position)
        Assert.Equal(0.5f, rendered.Player.Ads)
        Assert.Equal(Units.health 37.0f, rendered.Player.Health)
        Assert.Equal(Vector3.Lerp(previous.Soldiers[0].Position, movedSoldier.Position, 0.5f), rendered.Soldiers[0].Position)

    [<Fact>]
    let ``generated font atlas contains rasterized glyphs without assets`` () =
        let font = FontGen.create ()
        Assert.Equal(256, font.Width)
        Assert.Equal(144, font.Height)
        Assert.Equal(font.Width * font.Height, font.Pixels.Length)
        Assert.True(font.Pixels |> Array.filter ((=) 255uy) |> Array.length > 500)
        let glyph character =
            let index = int character - 32
            let left = (index % 16) * font.CellWidth
            let top = (index / 16) * font.CellHeight
            Array.init (font.CellWidth * font.CellHeight) (fun offset ->
                let x, y = offset % font.CellWidth, offset / font.CellWidth
                font.Pixels[(top + y) * font.Width + left + x])
        Assert.NotEqual<byte array>(glyph 'B', glyph '8')
        Assert.NotEqual<byte array>(glyph 'S', glyph '5')
        Assert.Contains(255uy, glyph '?')
        Assert.Contains(255uy, glyph '<')
        Assert.Contains(255uy, glyph '>')
        // The space cell must be fully transparent: the solid-rectangle texel
        // once lived at (0,0) inside it and drew a tick on every padding space.
        Assert.DoesNotContain(255uy, glyph ' ')
        Assert.Equal(255uy, font.Pixels[font.Width * font.Height - 1])

    [<Fact>]
    let ``generated viewmodels and audio contain usable procedural data`` () =
        let rifle = Guns.forWeapon "Kar98k"
        let sniper = Guns.forWeapon "Kar98k Sniper"
        let shotgun = Guns.forWeapon "M1897 Trench Gun"
        let shot = AudioSynth.gunshot true
        Assert.True(rifle.Vertices.Length > 100)
        Assert.True(rifle.Indices.Length > 100)
        Assert.True(sniper.Vertices.Length > rifle.Vertices.Length)
        Assert.True(shotgun.Vertices.Length > 100)
        // Every selectable weapon must have a substantial dedicated mesh; the
        // Kar98k comparison guards against silently falling back to it.
        for weapon in Tuning.onlineWeapons do
            let mesh = Guns.forWeapon weapon.Name
            Assert.True(mesh.Vertices.Length > 100, $"{weapon.Name} viewmodel too small")
            if weapon.Name <> "Kar98k" then
                Assert.True(mesh.Vertices.Length <> rifle.Vertices.Length, $"{weapon.Name} fell back to the Kar98k mesh")
        let ping = AudioSynth.garandPing ()
        Assert.True(ping.Samples.Length > 5000)
        Assert.Contains(ping.Samples, fun sample -> abs (int sample) > 1000)
        Assert.Equal(AudioSynth.SampleRate, shot.SampleRate)
        Assert.True(shot.Samples.Length > 10000)
        Assert.Contains(shot.Samples, fun sample -> abs (int sample) > 1000)

    [<Fact>]
    let ``start menu supports keyboard map selection and the Fly server`` () =
        let idle = TestKit.idleMenuInput
        let activate = { idle with Activate = true }
        let struct (mapChoice, noAction) = StartMenu.update 1280 720 activate { StartMenu.initial with Selected = 2 }
        Assert.Equal(OfflineMaps, mapChoice.Page)
        Assert.True(noAction.IsNone)
        let struct (_, mapAction) = StartMenu.update 1280 720 activate mapChoice
        Assert.Equal(Some(StartOffline "paintball"), mapAction)
        let struct (servers, _) = StartMenu.update 1280 720 activate { StartMenu.initial with Selected = 3 }
        Assert.Equal(ServerList, servers.Page)
        let struct (_, settingsAction) = StartMenu.update 1280 720 activate { StartMenu.initial with Selected = 4 }
        Assert.Equal(Some OpenSettings, settingsAction)
        // While the directory probe is still out, activating does not join.
        let struct (waiting, waitingAction) = StartMenu.update 1280 720 activate servers
        Assert.Equal(ServerList, waiting.Page)
        Assert.True(waitingAction.IsNone)

        // The populated table shows one row per room with the server host as a
        // column; picking a row carries both server and mode to the hello.
        let official = { Name = "Official"; Url = Uri "wss://fsharp-of-duty.fly.dev/play" }
        let community = { Name = "Community"; Url = Uri "ws://lan.example:8080/play" }
        let rows =
            [| { Server = official; Mode = TeamDeathmatch; Phase = "Playing"; Players = 3; Capacity = 16; PingMs = 42; Online = true }
               { Server = official; Mode = FreeForAll; Phase = "Waiting"; Players = 1; Capacity = 16; PingMs = 42; Online = true }
               { Server = community; Mode = TeamDeathmatch; Phase = ""; Players = 0; Capacity = 0; PingMs = 0; Online = false } |]
        let listed = { StartMenu.initial with Page = ServerList; ServerRows = Some rows }
        let items = StartMenu.items listed
        Assert.Equal(4, items.Length)
        Assert.Contains("FSHARP-OF-DUTY.FLY.DEV", items[0])
        // Columns are structured cells drawn at fixed x offsets, not padded text.
        let cells = (StartMenu.serverCells listed).Value
        Assert.Equal(3, cells.Length)
        Assert.Contains(cells[0], fun (_, text) -> text = "3/16")
        Assert.Contains(cells[1], fun (_, text) -> text = "FREE FOR ALL")
        Assert.Contains(cells[2], fun (_, text) -> text = "OFFLINE")
        // Offline rows are not joinable.
        let struct (still, offlineAction) = StartMenu.update 1280 720 activate { listed with Selected = 2 }
        Assert.Equal(ServerList, still.Page)
        Assert.True(offlineAction.IsNone)
        let struct (ffaLoadout, _) = StartMenu.update 1280 720 activate { listed with Selected = 1 }
        Assert.Equal(OnlineLoadout, ffaLoadout.Page)
        Assert.Equal(FreeForAll, ffaLoadout.OnlineMode)
        Assert.Equal(official.Url, ffaLoadout.OnlineServer)
        let struct (_, ffaAction) = StartMenu.update 1280 720 activate { ffaLoadout with Selected = 1 }
        Assert.Equal(Some(StartOnline("Thompson", FreeForAll, official.Url)), ffaAction)
        let struct (_, onlineAction) = StartMenu.update 1280 720 activate { ffaLoadout with Selected = 3 }
        Assert.Equal(Some(StartOnline("Kar98k Sniper", FreeForAll, official.Url)), onlineAction)

        let struct (editing, _) = StartMenu.update 1280 720 activate (StartMenu.create "Old")
        let struct (edited, _) =
            StartMenu.update 1280 720 { idle with Backspace = true; TextInput = "X" } editing
        Assert.Equal("OlX", edited.PlayerName)
        let struct (named, _) = StartMenu.update 1280 720 activate edited
        Assert.Equal(Main, named.Page)
        Assert.Equal("OlX", named.PlayerName)

        let pointer =
            { idle with
                Pointer = Some(Vector2(640.0f, 300.0f))
                Clicked = true }
        let struct (_, pointerAction) = StartMenu.update 1280 720 pointer StartMenu.initial
        Assert.Equal(Some(StartOffline "paintball"), pointerAction)

        // Escape on the root page does nothing; quitting is the explicit item.
        let struct (_, backAction) = StartMenu.update 1280 720 { idle with Back = true } StartMenu.initial
        Assert.Equal(None, backAction)
        let struct (_, quitAction) = StartMenu.update 1280 720 activate { StartMenu.initial with Selected = 5 }
        Assert.Equal(Some ExitGame, quitAction)

    [<Fact>]
    let ``long menu pages scroll a window that keeps the selection in view`` () =
        let idle = TestKit.idleMenuInput
        let server index = { Name = $"S{index}"; Url = Uri $"ws://server-{index}.example:8080/play" }
        let rows =
            Array.init 14 (fun index ->
                { Server = server index; Mode = TeamDeathmatch; Phase = "Waiting"; Players = 0; Capacity = 16; PingMs = 10; Online = true })
        let listed = { StartMenu.initial with Page = ServerList; ServerRows = Some rows }
        // 14 rows + BACK = 15 items; the window shows MaxVisibleRows of them.
        Assert.Equal(15, (StartMenu.items listed).Length)
        Assert.Equal((0, StartMenu.MaxVisibleRows), StartMenu.visibleRange listed)
        // Walk the selection to the bottom: the window follows.
        let mutable state = listed
        for _ in 1..14 do
            let struct (next, _) = StartMenu.update 1280 720 { idle with Down = true } state
            state <- next
        Assert.Equal(14, state.Selected)
        let first, visible = StartMenu.visibleRange state
        Assert.Equal(15 - StartMenu.MaxVisibleRows, first)
        Assert.Equal(StartMenu.MaxVisibleRows, visible)
        Assert.True(state.Selected >= first && state.Selected < first + visible)
        // Wrapping back to the top snaps the window back with it.
        let struct (wrapped, _) = StartMenu.update 1280 720 { idle with Down = true } state
        Assert.Equal(0, wrapped.Selected)
        Assert.Equal((0, StartMenu.MaxVisibleRows), StartMenu.visibleRange wrapped)
        // Short pages never scroll.
        Assert.Equal((0, 6), StartMenu.visibleRange StartMenu.initial)

    [<Fact>]
    let ``lethal headshot pose removes head geometry`` () =
        let world = Sim.createPaintballWorld 73UL
        let soldier = world.Soldiers[0]
        let normal = Humanoid.pose soldier
        let headless = Humanoid.pose { soldier with Behavior = DyingHeadshot(Units.seconds 0.1f) }
        Assert.True(headless.Vertices.Length < normal.Vertices.Length)
        Assert.True(headless.Indices.Length < normal.Indices.Length)

    [<Fact>]
    let ``soldier model faces its movement direction at every yaw`` () =
        let soldierAt yaw = { TestKit.soldier 1 Vector3.Zero with Facing = yaw }
        for yaw in [ 0.0f; MathF.PI / 2.0f; MathF.PI; -MathF.PI / 2.0f ] do
            let posed = Humanoid.pose (soldierAt yaw)
            let forward = MathEx.yawForward yaw
            let projection = posed.Vertices |> Array.map (fun vertex -> Vector3.Dot(vertex.Position, forward))
            // The weapon barrel reaches ~1.26 units along the facing axis; the
            // mesh must extend much further forward than backward.
            let front = Array.max projection
            let back = -Array.min projection
            Assert.True(front > 0.9f, $"yaw {yaw}: front extent {front}")
            Assert.True(front > back, $"yaw {yaw}: front {front} not ahead of back {back}")

    [<Fact>]
    let ``noise and mesh combinators are deterministic asset sources`` () =
        let point = Vector2(1.25f, -4.75f)
        Assert.Equal(Noise.fbm2 42 5 point, Noise.fbm2 42 5 point)
        let mesh =
            MeshGen.union
                [| MeshGen.wedge (Vector3(1.0f, 0.5f, 2.0f)) Brick
                   MeshGen.lathe 10 [| Vector2(0.2f, -0.5f); Vector2(0.4f, 0.0f); Vector2(0.1f, 0.5f) |] Metal |]
            |> MeshGen.paint Wood
        Assert.NotEmpty mesh.Vertices
        Assert.NotEmpty mesh.Indices
        Assert.All(mesh.Vertices, fun vertex -> Assert.Equal(Materials.id Wood, vertex.MaterialId))

    [<Fact>]
    let ``procedural weapon primitives use outward counterclockwise winding`` () =
        // The lathe sample is the real helmet profile: it shipped upside down
        // and holed precisely because nothing asserted its winding or closure.
        let meshes =
            [| MeshGen.box (Vector3.One) Wood
               MeshGen.cylinder 10 0.2f 1.0f Metal
               MeshGen.lathe 12
                   [| Vector2(0.19f, -0.035f); Vector2(0.19f, 0.01f); Vector2(0.17f, 0.08f)
                      Vector2(0.11f, 0.14f); Vector2(0.02f, 0.17f) |]
                   Metal |]
        for mesh in meshes do
            for a, b, c in TestKit.triangles mesh.Vertices mesh.Indices do
                let geometric = Vector3.Cross(b.Position - a.Position, c.Position - a.Position)
                Assert.True(Vector3.Dot(geometric, a.Normal) > 0.0f)

    [<Fact>]
    let ``a standing soldier wears a closed helmet crown, dome up`` () =
        // Regression for the see-through helmet: the crown must be the highest
        // surface, upward-facing (outward-wound, not culled), and closed. This
        // fails independently on the upside-down rotation, the missing lathe
        // caps, and any winding flip.
        let soldier = { TestKit.soldier 1 Vector3.Zero with Facing = 0.7f; AnimPhase = 0.3f }
        let vertices, indices = Humanoid.mesh [| soldier |]
        let highest = vertices |> Array.maxBy (fun vertex -> vertex.Position.Y)
        // The top of the model is helmet, not bare head poking through a hole.
        Assert.Equal(Materials.id UniformFeldgrau, highest.MaterialId)
        Assert.InRange(highest.Position.Y, 1.9f, 2.05f)
        // Looking straight down at the crown must hit an upward-facing surface
        // near the top — a hole or an inward-wound dome both fail this.
        let origin = Vector3(0.0f, 3.0f, -0.025f)
        let topHit =
            [ for a, b, c in TestKit.triangles vertices indices do
                match MathEx.rayTriangle origin -Vector3.UnitY a.Position b.Position c.Position with
                | ValueSome distance when Vector3.Cross(b.Position - a.Position, c.Position - a.Position).Y > 0.0f -> yield 3.0f - distance
                | _ -> () ]
            |> function [] -> 0.0f | hits -> List.max hits
        Assert.True(topHit > 1.9f, $"highest upward-facing surface over the head is at {topHit}, expected a closed crown above 1.9")

    [<Fact>]
    let ``online reconciliation drops acknowledged inputs and replays newer movement`` () =
        let world = Sim.createTrainingWorld 900UL
        let snapshot =
            { Tick = 120L
              Mode = TeamDeathmatch
              LevelName = "Training Yard"
              Phase = Playing
              AlliesScore = 2
              AxisScore = 1
              Players =
                [| { Id = 7
                     Name = "Local"
                     Team = Allies
                     Position = Vector3(-10.0f, 0.0f, 10.0f)
                     Velocity = Vector3.Zero
                     Yaw = 0.0f
                     Pitch = 0.0f
                     Stance = Standing
                     Health = 75.0f
                     Alive = true
                     Ready = true
                     Ads = 0.0f
                     Ammo = 20
                     Reserve = 60
                     WeaponName = "Kar98k Sniper"
                     Kills = 2
                     Deaths = 1
                     AcknowledgedInput = 10L }
                   { Id = 8
                     Name = "Remote"
                     Team = Axis
                     Position = Vector3(4.0f, 0.0f, -4.0f)
                     Velocity = Vector3.Zero
                     Yaw = 3.14f
                     Pitch = 0.0f
                     Stance = Standing
                     Health = 100.0f
                     Alive = true
                     Ready = true
                     Ads = 0.0f
                     Ammo = 5
                     Reserve = 20
                     WeaponName = "M1897 Trench Gun"
                     Kills = 1
                     Deaths = 2
                     AcknowledgedInput = 9L } |]
              Grenades = [||]
              Events = [||] }
        let pending =
            [ { Sequence = 10L; Move = Vector2.UnitY; Look = Vector2.Zero; Buttons = InputButtons.None }
              { Sequence = 11L; Move = Vector2.UnitY; Look = Vector2.Zero; Buttons = InputButtons.None } ]
        let reconciled, remaining = OnlineWorld.reconcile world.Level pending 7 world snapshot
        Assert.Single remaining |> ignore
        Assert.Equal(7, let (EntityId id) = reconciled.Player.Id in id)
        Assert.Equal(Units.health 75.0f, reconciled.Player.Health)
        Assert.True(reconciled.Player.Position.Z < 10.0f)
        Assert.Single reconciled.Soldiers |> ignore
        Assert.Equal(EntityId 8, reconciled.Soldiers[0].Id)

    [<Fact>]
    let ``reconciliation from full movement state reproduces local prediction exactly`` () =
        // The QuakeWorld property: rebasing on the snapshot (position AND
        // velocity AND stance) and replaying the unacknowledged inputs must land
        // exactly where continuous local prediction landed — including through a
        // jump. Without velocity on the wire this fails by design.
        let world = Sim.createTrainingWorld 700UL
        let inputAt sequence buttons = TestKit.input sequence buttons Vector2.UnitY
        let inputs =
            [ for sequence in 1L..30L ->
                inputAt sequence (if sequence = 10L then InputButtons.Jump else InputButtons.None) ]
        // Continuous local prediction over all 30 frames.
        let final =
            inputs
            |> List.fold (fun player input -> Movement.step Tuning.TickDuration input world.Level player) world.Player
        // Server-equivalent state after frame 18 (mid-flight from the jump).
        let atAck =
            inputs
            |> List.take 18
            |> List.fold (fun player input -> Movement.step Tuning.TickDuration input world.Level player) world.Player
        let snapshot =
            { Tick = 500L
              Mode = TeamDeathmatch
              LevelName = world.Level.Name
              Phase = Playing
              AlliesScore = 0
              AxisScore = 0
              Players =
                [| { Id = 1
                     Name = "Local"
                     Team = Allies
                     Position = atAck.Position
                     Velocity = atAck.Velocity
                     Yaw = atAck.Yaw
                     Pitch = atAck.Pitch
                     Stance = atAck.Stance
                     Health = 100.0f
                     Alive = true
                     Ready = true
                     Ads = atAck.Ads
                     Ammo = 30
                     Reserve = 60
                     WeaponName = "Thompson"
                     Kills = 0
                     Deaths = 0
                     AcknowledgedInput = 18L } |]
              Grenades = [||]
              Events = [||] }
        let reconciled, _ = OnlineWorld.reconcile world.Level inputs 1 world snapshot
        Assert.InRange(Vector3.Distance(reconciled.Player.Position, final.Position), 0.0f, 0.0001f)
        Assert.InRange(Vector3.Distance(reconciled.Player.Velocity, final.Velocity), 0.0f, 0.0001f)

    [<Fact>]
    let ``server directory parses tolerantly and merges by precedence`` () =
        // Junk documents and junk entries degrade to nothing, never throw.
        Assert.Empty(ServerDirectory.parse "not json at all")
        Assert.Empty(ServerDirectory.parse """{"servers": "wrong shape"}""")
        let parsed =
            ServerDirectory.parse
                """{"servers":[
                    {"name":"Good","url":"wss://one.example/play"},
                    {"name":"","url":"wss://nameless.example/play"},
                    {"name":"BadScheme","url":"https://web.example/"},
                    {"name":"Broken","url":123},
                    {"name":"Lan","url":"ws://192.168.1.10:8080/play"}]}"""
        Assert.Equal<string list>(
            [ "Good"; "Lan" ],
            parsed |> List.map (fun entry -> entry.Name))
        // First occurrence wins across lists: env/user entries shadow later ones.
        let one = { Name = "Mine"; Url = Uri "wss://one.example/play" }
        let shadowed = { Name = "Official copy"; Url = Uri "WSS://ONE.EXAMPLE/play" }
        let other = { Name = "Other"; Url = Uri "ws://two.example/play" }
        let merged = ServerDirectory.merge [ [ one ]; [ shadowed; other ] ]
        Assert.Equal<string list>([ "Mine"; "Other" ], merged |> List.map (fun entry -> entry.Name))

    [<Fact>]
    let ``Fly hostname is the online default`` () =
        let previous = System.Environment.GetEnvironmentVariable "IRONSIGHT_SERVER"
        try
            System.Environment.SetEnvironmentVariable("IRONSIGHT_SERVER", null)
            Assert.Equal("wss://fsharp-of-duty.fly.dev/play", ServerDirectory.defaultUri().AbsoluteUri)
        finally
            System.Environment.SetEnvironmentVariable("IRONSIGHT_SERVER", previous)

    [<Fact>]
    let ``player shot origin is in front of the weapon hand position`` () =
        let world = Sim.createTrainingWorld 11UL
        let player = { world.Player with Yaw = 0.4f; Pitch = -0.1f; Ads = 0.0f }
        let weapon = player.Slots[player.Active].Class
        let origin = Ballistics.playerMuzzleOrigin player weapon
        let eye = player.Position + Vector3(0.0f, 1.62f, 0.0f)
        let forward = Ballistics.directionFromAngles player.Yaw player.Pitch Vector2.Zero
        Assert.True(Vector3.Dot(origin - eye, forward) > 0.5f)
        Assert.True(Vector3.Distance(origin, eye) > 0.5f)
