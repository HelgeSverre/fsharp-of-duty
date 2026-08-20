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
    let ``every weapon mesh is one connected lump`` () =
        // Voxelise each triangle's bounding box and flood fill. A part that
        // floats clear of the gun body shows up as a second component — the
        // Kar98k butt stock did exactly that, sitting 0.15 behind and 0.21
        // below the receiver it was supposed to be bolted to, and four other
        // guns had the same hole.
        //
        // Guns.meshFor, not forWeapon: the arms are a genuinely separate lump.
        //
        // ponytail: triangle AABBs over-approximate the surface, so this
        // catches a part that has drifted off the body, not a hairline seam.
        // Tighten by rasterising the triangles properly if that ever matters.
        let cell = 0.02f
        let neighbours =
            [| struct (1, 0, 0); struct (-1, 0, 0); struct (0, 1, 0)
               struct (0, -1, 0); struct (0, 0, 1); struct (0, 0, -1) |]
        for name in Guns.names do
            let mesh = Guns.meshFor name
            let occupied = Collections.Generic.HashSet<struct (int * int * int)>()
            for triangle in 0 .. mesh.Indices.Length / 3 - 1 do
                let corners =
                    [| for offset in 0..2 -> mesh.Vertices[int mesh.Indices[triangle * 3 + offset]].Position |]
                let slot (pick: Vector3 -> float32) choose =
                    int (floor (choose (corners |> Array.map pick) / cell))
                for x in slot (fun p -> p.X) Array.min .. slot (fun p -> p.X) Array.max do
                    for y in slot (fun p -> p.Y) Array.min .. slot (fun p -> p.Y) Array.max do
                        for z in slot (fun p -> p.Z) Array.min .. slot (fun p -> p.Z) Array.max do
                            occupied.Add(struct (x, y, z)) |> ignore
            let reached = Collections.Generic.HashSet<struct (int * int * int)>()
            let pending = Collections.Generic.Queue<struct (int * int * int)>()
            let start = Seq.head occupied
            pending.Enqueue start
            reached.Add start |> ignore
            while pending.Count > 0 do
                let struct (x, y, z) = pending.Dequeue()
                for struct (dx, dy, dz) in neighbours do
                    let next = struct (x + dx, y + dy, z + dz)
                    if occupied.Contains next && reached.Add next then pending.Enqueue next
            Assert.True(
                reached.Count = occupied.Count,
                $"{name}: {occupied.Count - reached.Count} of {occupied.Count} voxels float free of the body")

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
            [| { Server = official; RoomId = "tdm"; RoomName = "Paintball TDM"; Mode = TeamDeathmatch; Phase = "Playing"; Players = 3; Capacity = 16; PingMs = 42; Online = true }
               { Server = official; RoomId = "ffa"; RoomName = "Omaha FFA"; Mode = FreeForAll; Phase = "Waiting"; Players = 1; Capacity = 16; PingMs = 42; Online = true }
               { Server = community; RoomId = ""; RoomName = ""; Mode = TeamDeathmatch; Phase = ""; Players = 0; Capacity = 0; PingMs = 0; Online = false } |]
        let listed = { StartMenu.initial with Page = ServerList; ServerRows = Some rows }
        let items = StartMenu.items listed
        Assert.Equal(4, items.Length)
        // Rooms are named by the server, so ten rooms on one host do not all
        // render as the same line. A server that names no room falls back to
        // the directory's display name.
        Assert.Contains("PAINTBALL TDM", items[0])
        Assert.Contains("OMAHA FFA", items[1])
        Assert.Contains("COMMUNITY", items[2])
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
        // The pre-join list is ordered by the in-game picker, so a row means
        // whatever that order says rather than a position in onlineWeapons.
        let atRow row = Tuning.onlineWeapons[LoadoutMenu.weaponOrder[row]].Name
        let struct (_, ffaAction) = StartMenu.update 1280 720 activate { ffaLoadout with Selected = 1 }
        Assert.Equal(Some(StartOnline(atRow 1, FreeForAll, "ffa", official.Url)), ffaAction)
        let struct (_, onlineAction) = StartMenu.update 1280 720 activate { ffaLoadout with Selected = 3 }
        Assert.Equal(Some(StartOnline(atRow 3, FreeForAll, "ffa", official.Url)), onlineAction)
        // Every weapon is reachable exactly once, whatever the grouping.
        Assert.Equal(Tuning.onlineWeapons.Length, LoadoutMenu.weaponOrder.Length)
        Assert.Equal(Tuning.onlineWeapons.Length, LoadoutMenu.weaponOrder |> Array.distinct |> Array.length)

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
    let ``the loadout picker fits on screen however many weapons there are`` () =
        // It used to size its panel by the *total* weapon count, so the arsenal
        // growing past about sixteen pushed the panel taller than a 720p window
        // with no way to scroll to the rest.
        let panel = LoadoutMenu.panelRect 1280 720
        Assert.True(panel.H <= 720.0f, $"picker panel is {panel.H}px tall in a 720px window")
        Assert.True(panel.Y >= 0.0f)
        // The window never claims more rows than exist, and never more than the cap.
        let _, visible = LoadoutMenu.visibleRows (LoadoutMenu.create ())
        Assert.True(visible.Length <= LoadoutMenu.MaxVisibleRows)
        Assert.True(visible.Length <= LoadoutMenu.rows.Length)

    [<Fact>]
    let ``picker headings group the weapons and are never selectable`` () =
        let idle = TestKit.idleMenuInput
        // Every weapon appears exactly once, under a heading for its own key.
        Assert.Equal(Tuning.onlineWeapons.Length, LoadoutMenu.weaponOrder.Length)
        Assert.Equal(Tuning.onlineWeapons.Length, LoadoutMenu.weaponOrder |> Array.distinct |> Array.length)
        let headings =
            LoadoutMenu.rows |> Array.choose (function LoadoutMenu.Header category -> Some category | _ -> None)
        Assert.Equal(headings |> Array.distinct |> Array.length, headings.Length)
        // Weapons following a heading all belong to that heading's key.
        let mutable current = -1
        for row in LoadoutMenu.rows do
            match row with
            | LoadoutMenu.Header category -> current <- category
            | LoadoutMenu.Weapon index -> Assert.Equal(current, Tuning.categoryOf Tuning.onlineWeapons[index])
        // Stepping through every row lands on weapons only, never a heading.
        let mutable state = LoadoutMenu.create ()
        for _ in 1 .. LoadoutMenu.rows.Length + 2 do
            let struct (next, _) = LoadoutMenu.update 1280 720 { idle with Down = true } state
            state <- next
            Assert.True((LoadoutMenu.weaponAt state.Selected).IsSome, "selection landed on a heading")

    [<Fact>]
    let ``typing a category number jumps to that group`` () =
        // Menu-mode keystrokes arrive as TextInput, so this needs nothing from
        // the input sampler.
        let idle = TestKit.idleMenuInput
        let start = LoadoutMenu.create ()
        for category in Tuning.categories do
            let occupied = Tuning.onlineWeapons |> Array.exists (fun weapon -> Tuning.categoryOf weapon = category)
            let struct (jumped, _) =
                LoadoutMenu.update 1280 720 { idle with TextInput = string (category + 1) } start
            if occupied then
                match LoadoutMenu.weaponAt jumped.Selected with
                | Some weapon -> Assert.Equal(category, Tuning.categoryOf weapon)
                | None -> failwith $"key {category + 1} did not land on a weapon"
            else
                // A key holding nothing must not move the cursor.
                Assert.Equal(start.Selected, jumped.Selected)

    [<Fact>]
    let ``picker hover still matches the drawn row after scrolling`` () =
        // The off-by-one this pattern invites: hover is computed from the drawn
        // window, so it has to be offset by the scroll position.
        let idle = TestKit.idleMenuInput
        let rows = LoadoutMenu.rowsRect (LoadoutMenu.panelRect 1280 720)
        let scrolled = { LoadoutMenu.create () with Selected = LoadoutMenu.rows.Length - 1 }
        let first, visible = LoadoutMenu.visibleRows scrolled
        // Hover the last drawn slot and confirm it selects the row drawn there.
        let slot = visible.Length - 1
        let struct (hovered, _) =
            LoadoutMenu.update 1280 720 { idle with Pointer = Some(TestKit.rowMiddle LoadoutMenu.RowHeight slot rows) } scrolled
        Assert.Equal(first + slot, hovered.Selected)

    [<Fact>]
    let ``loadout picker supports mouse hover and click-to-equip`` () =
        let idle = TestKit.idleMenuInput
        let rowsRect = LoadoutMenu.rowsRect (LoadoutMenu.panelRect 1280 720)
        let middleOf index = TestKit.rowMiddle LoadoutMenu.RowHeight index rowsRect
        // Row 3 of the drawn window; rows are category-grouped, so ask the
        // picker which weapon that row names rather than assuming an order.
        let start = LoadoutMenu.create ()
        let struct (hovered, browsing) =
            LoadoutMenu.update 1280 720 { idle with Pointer = Some(middleOf 3) } start
        Assert.Equal(3, hovered.Selected)
        Assert.Equal(LoadoutMenu.Browsing, browsing)
        let struct (_, equipped) =
            LoadoutMenu.update 1280 720 { idle with Pointer = Some(middleOf 3); Clicked = true } start
        Assert.Equal(LoadoutMenu.Chosen (LoadoutMenu.weaponAt 3).Value.Name, equipped)
        // A click with the pointer outside the rows equips nothing.
        let struct (_, outside) =
            LoadoutMenu.update 1280 720 { idle with Pointer = Some(Vector2(5.0f, 5.0f)); Clicked = true } start
        Assert.Equal(LoadoutMenu.Browsing, outside)

    [<Fact>]
    let ``dpad still navigates while the pointer rests on a row`` () =
        // A parked cursor must not eat Up/Down (gamepad dpad and arrow keys
        // share these flags): hover selects the row, then the step applies.
        let idle = TestKit.idleMenuInput
        let loadoutRows = LoadoutMenu.rowsRect (LoadoutMenu.panelRect 1280 720)
        let loadoutMiddle index = TestKit.rowMiddle LoadoutMenu.RowHeight index loadoutRows
        let struct (stepped, _) =
            LoadoutMenu.update 1280 720 { idle with Pointer = Some(loadoutMiddle 2); Down = true } (LoadoutMenu.create ())
        Assert.Equal(3, stepped.Selected)
        let mainCount = (StartMenu.items StartMenu.initial).Length
        let mainRows = MenuLayout.rowsRect (MenuLayout.panelRect 1280 720 mainCount) mainCount
        let mainMiddle = TestKit.rowMiddle MenuLayout.RowHeight 1 mainRows
        let struct (menuStepped, _) =
            StartMenu.update 1280 720 { idle with Pointer = Some mainMiddle; Up = true } StartMenu.initial
        Assert.Equal(0, menuStepped.Selected)

    [<Fact>]
    let ``a stalled connection smooths instead of snapping the player back`` () =
        // Measured against the deployed server: snapshot gaps are p50 54ms but
        // p99 125ms and max 391ms, because snapshots ride TCP and one
        // retransmit blocks the stream. The old flat 1m threshold called every
        // one of those a teleport.
        let sprint = Tuning.WalkSpeed * Tuning.SprintMultiplier
        let ticks seconds = int64 (seconds * float32 Tuning.TickRate)
        // One snapshot apart (~50ms): a normal correction is carried, not snapped.
        Assert.NotEqual(Vector3.Zero, Program.Prediction.carry (Vector3(0.3f, 0.0f, 0.0f)) (ticks 0.05f))
        // A 125ms stall at sprint speed is ~1.05m — over the old 1m threshold,
        // and exactly the hiccup that used to yank the player back.
        let drift = Vector3(sprint * 0.125f, 0.0f, 0.0f)
        Assert.True(drift.Length() > 1.0f, "the regression case must exceed the old flat threshold")
        Assert.NotEqual(Vector3.Zero, Program.Prediction.carry drift (ticks 0.125f))
        // A real teleport still snaps: a respawn across the map cannot be
        // explained by any amount of running.
        Assert.Equal(Vector3.Zero, Program.Prediction.carry (Vector3(60.0f, 0.0f, 0.0f)) (ticks 0.125f))
        // The budget is bounded, so a long stall does not smooth a teleport.
        Assert.Equal(Program.Prediction.teleportBudget (ticks 0.5f), Program.Prediction.teleportBudget (ticks 30.0f))
        // Before the first snapshot of a session the tick delta is meaningless.
        Assert.True(Program.Prediction.teleportBudget -1L > 0.0f)
        // Whole metres, so the number reads sensibly in a log.
        for gap in [ 0.0f; 0.016f; 0.05f; 0.125f; 0.391f; 0.5f; 5.0f ] do
            let budget = Program.Prediction.teleportBudget (ticks gap)
            Assert.Equal(budget, floor budget)
        // Laxer than the sprint distance it is derived from, at every gap.
        for gap in [ 0.016f; 0.05f; 0.125f; 0.391f; 0.5f ] do
            Assert.True(
                Program.Prediction.teleportBudget (ticks gap) > sprint * gap,
                $"budget must exceed bare sprint distance at {gap}s")
        // The whole measured range of deployed snapshot gaps stays generous:
        // even the 391ms worst case leaves metres of headroom.
        Assert.True(Program.Prediction.teleportBudget (ticks 0.391f) >= 6.0f)

    [<Fact>]
    let ``kill feed keeps the newest rows and expires them`` () =
        // Names are baked in when the event arrives: the server retains a kill
        // for ~200ms while the row lives for seconds, so a killer who leaves
        // meanwhile must still be named on screen.
        let mutable roster = Map.ofList [ EntityId 1, "Ally"; EntityId 2, "Axis" ]
        let nameOf id = roster |> Map.tryFind id |> Option.defaultValue "SOLDIER"
        let state =
            Program.Feedback.empty
            |> Program.Feedback.applyFeed nameOf
                [ Kill(Some(EntityId 1), EntityId 2, "Kar98k", true)
                  Kill(None, EntityId 2, "GRENADE", false)
                  PlayerLeft(EntityId 1, "Ally") ]
        Assert.Equal<string list>(
            [ "Ally LEFT"; "[GRENADE]  Axis"; "Ally  [Kar98k]  Axis" ],
            state.Feed |> List.map (fun item -> item.Text))
        Assert.Equal<bool list>([ false; false; true ], state.Feed |> List.map (fun item -> item.Highlight))
        // The killer is gone from the roster, but the formatted row is not.
        roster <- Map.remove (EntityId 1) roster
        let overflowed =
            state
            |> Program.Feedback.applyFeed nameOf (List.replicate 5 (PlayerJoined(EntityId 3, "Extra")))
        Assert.Equal(Program.Feedback.feedCapacity, overflowed.Feed.Length)
        Assert.True(overflowed.Feed |> List.forall (fun item -> item.Text = "Extra JOINED"))
        // 5s lifetime: still there just before, gone just after.
        let ticksFor seconds = int (seconds / Units.raw Tuning.TickDuration)
        let almost = List.fold (fun acc _ -> Program.Feedback.tick acc) state [ 1 .. ticksFor 4.9f ]
        Assert.Equal(3, almost.Feed.Length)
        let expired = List.fold (fun acc _ -> Program.Feedback.tick acc) almost [ 1 .. ticksFor 0.2f ]
        Assert.Empty expired.Feed

    [<Fact>]
    let ``chat log keeps the newest lines and expires them`` () =
        // The sender's name rides the event, so a line outlives its author's
        // connection exactly like a kill-feed row.
        let nameOf _ = "IGNORED"
        let state =
            Program.Feedback.empty
            |> Program.Feedback.applyFeed nameOf
                [ Chat(Some(EntityId 1), "Ally", "on your left")
                  Chat(None, "", "MATCH STARTING") ]
        Assert.Equal<string list>([ "MATCH STARTING"; "Ally: on your left" ], state.Chat |> List.map (fun item -> item.Text))
        // Server lines are highlighted, player lines are not.
        Assert.Equal<bool list>([ true; false ], state.Chat |> List.map (fun item -> item.Highlight))
        // Chat rows must not leak into the kill feed, or vice versa.
        Assert.Empty state.Feed
        let overflowed =
            state
            |> Program.Feedback.applyFeed nameOf (List.replicate 8 (Chat(Some(EntityId 2), "Axis", "spam")))
        Assert.Equal(Program.Feedback.chatCapacity, overflowed.Chat.Length)
        // 12s lifetime: still there just before, gone just after.
        let ticksFor seconds = int (seconds / Units.raw Tuning.TickDuration)
        let almost = List.fold (fun acc _ -> Program.Feedback.tick acc) state [ 1 .. ticksFor 11.9f ]
        Assert.Equal(2, almost.Chat.Length)
        let expired = List.fold (fun acc _ -> Program.Feedback.tick acc) almost [ 1 .. ticksFor 0.2f ]
        Assert.Empty expired.Chat

    [<Fact>]
    let ``row slot geometry round-trips between drawing and hit-testing`` () =
        let rows = { X = 100.0f; Y = 200.0f; W = 600.0f; H = 54.0f * 5.0f }
        for index in 0..4 do
            let slot = Rect.slot 54.0f index rows
            // A point in the middle of a drawn slot hits that same slot.
            let middle = Vector2(slot.X + slot.W * 0.5f, slot.Y + slot.H * 0.5f)
            Assert.Equal(Some index, Rect.slotAt 54.0f 5 middle rows)
        Assert.Equal(None, Rect.slotAt 54.0f 5 (Vector2(99.0f, 227.0f)) rows)
        Assert.Equal(None, Rect.slotAt 54.0f 5 (Vector2(400.0f, 199.0f)) rows)
        Assert.Equal(None, Rect.slotAt 54.0f 5 (Vector2(400.0f, 200.0f + 54.0f * 5.0f)) rows)
        // textY centers any scale on the slot midline (glyphs are 12px at scale 1).
        let slot = Rect.slot 54.0f 2 rows
        for scale in [ 1.0f; 1.15f; 1.25f; 1.3f ] do
            let y = Rect.textY scale slot
            Assert.Equal(slot.Y + slot.H * 0.5f, y + 6.0f * scale, 3)

    [<Fact>]
    let ``long menu pages scroll a window that keeps the selection in view`` () =
        let idle = TestKit.idleMenuInput
        let server index = { Name = $"S{index}"; Url = Uri $"ws://server-{index}.example:8080/play" }
        let rows =
            Array.init 14 (fun index ->
                { Server = server index; RoomId = $"r{index}"; RoomName = $"Room {index}"; Mode = TeamDeathmatch; Phase = "Waiting"; Players = 0; Capacity = 16; PingMs = 10; Online = true })
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
                [| { TestKit.onlinePlayer 7 "Local" Allies (Vector3(-10.0f, 0.0f, 10.0f)) with
                        Health = 75.0f
                        Ammo = 20
                        WeaponName = "Kar98k Sniper"
                        Kills = 2
                        Deaths = 1
                        AcknowledgedInput = 10L }
                   { TestKit.onlinePlayer 8 "Remote" Axis (Vector3(4.0f, 0.0f, -4.0f)) with
                        Yaw = 3.14f
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
    let ``a reloading snapshot rebuilds the weapon slot as Reloading`` () =
        // weaponFor is the only place an online WeaponSlot is built; before the
        // wire carried the timer it hardcoded Ready, so Hud.drawReloadBar's
        // Reloading case could never match online.
        let world = Sim.createTrainingWorld 900UL
        let snapshot =
            { Tick = 100L
              Mode = TeamDeathmatch
              LevelName = world.Level.Name
              Phase = Playing
              AlliesScore = 0
              AxisScore = 0
              Players =
                [| { TestKit.onlinePlayer 1 "Local" Allies world.Player.Position with ReloadRemaining = 1.5f }
                   { TestKit.onlinePlayer 2 "Remote" Axis (Vector3(4.0f, 0.0f, -4.0f)) with ReloadRemaining = 0.0f } |]
              Grenades = [||]
              Events = [||] }
        let reconciled, _ = OnlineWorld.reconcile world.Level [] 1 world snapshot
        Assert.Equal(Reloading(Units.seconds 1.5f), reconciled.Player.Slots[reconciled.Player.Active].State)
        Assert.Equal(Ready, reconciled.Soldiers[0].Weapon.State)

    [<Fact>]
    let ``the wire kit is rebuilt with the sidearm and the gun in hand`` () =
        // The client used to overwrite Slots with a single weapon every
        // snapshot, so no multi-slot state could survive reconciliation.
        let world = Sim.createTrainingWorld 901UL
        let kit =
            [| { WeaponName = "Kar98k"; Ammo = 3; Reserve = 15; ReloadRemaining = 0.0f }
               { WeaponName = "M1911"; Ammo = 7; Reserve = 14; ReloadRemaining = 0.0f } |]
        let withKit active switchTo switchRemaining =
            { TestKit.onlinePlayer 1 "Local" Allies world.Player.Position with
                Slots = kit
                Active = active
                SwitchTo = switchTo
                SwitchRemaining = switchRemaining }
        let snapshotOf player =
            { Tick = 100L; Mode = TeamDeathmatch; LevelName = world.Level.Name; Phase = Playing
              AlliesScore = 0; AxisScore = 0; Players = [| player |]; Grenades = [||]; Events = [||] }
        // Pistol in hand, rifle still remembering its three rounds.
        let reconciled, _ = OnlineWorld.reconcile world.Level [] 1 world (snapshotOf (withKit 1 -1 0.0f))
        Assert.Equal(2, reconciled.Player.Slots.Length)
        Assert.Equal(1, reconciled.Player.Active)
        Assert.Equal("M1911", reconciled.Player.Slots[1].Class.Name)
        Assert.Equal(3, reconciled.Player.Slots[0].InMag)
        Assert.Equal(15, reconciled.Player.Slots[0].Reserve)
        // A switch in flight arrives as Switching on the outgoing slot, so the
        // viewmodel plays the raise instead of popping to the new gun.
        let switching, _ = OnlineWorld.reconcile world.Level [] 1 world (snapshotOf (withKit 0 1 0.2f))
        Assert.Equal(Switching(1, Units.seconds 0.2f), switching.Player.Slots[0].State)
        // A server built before kits sends no slots at all; the flat fields it
        // does send must still produce the one-weapon inventory of that era.
        let legacy =
            { TestKit.onlinePlayer 1 "Local" Allies world.Player.Position with
                Slots = [||]; WeaponName = "BAR"; Ammo = 12; Reserve = 40 }
        let old, _ = OnlineWorld.reconcile world.Level [] 1 world (snapshotOf legacy)
        Assert.Equal(1, old.Player.Slots.Length)
        Assert.Equal(0, old.Player.Active)
        Assert.Equal("BAR", old.Player.Slots[0].Class.Name)

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
                [| { TestKit.onlinePlayer 1 "Local" Allies atAck.Position with
                        Velocity = atAck.Velocity
                        Yaw = atAck.Yaw
                        Pitch = atAck.Pitch
                        Stance = atAck.Stance
                        Ads = atAck.Ads
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

    [<Fact>]
    let ``ragdolled corpse collapses to the ground, keeps bone lengths, and prunes on respawn`` () =
        let world = Sim.createPaintballWorld 73UL
        let soldier = { world.Soldiers[0] with Behavior = Dying(Units.seconds 0.0f) }
        let ragdolls = Ragdoll.System()
        ragdolls.Spawn(soldier.Id, Humanoid.worldSkeleton soldier, Vector3(2.0f, 0.0f, 1.0f))
        for _ in 1..240 do
            ragdolls.Step(1.0f / 60.0f, world.Level)
        let skeleton = (ragdolls.TryGet soldier.Id).Value
        // The head starts ~1.68 above the feet; after the fall the whole body
        // lies near the floor instead of standing.
        let ground = soldier.Position.Y
        Assert.True(skeleton.Head.Y < ground + 0.6f)
        Assert.True(skeleton.Chest.Y < ground + 0.6f)
        Assert.True(skeleton.Pelvis.Y > ground - 0.5f)
        // Constraints hold: the pelvis-chest bone stays near its ~0.53 rest length.
        Assert.InRange(Vector3.Distance(skeleton.Pelvis, skeleton.Chest), 0.35f, 0.7f)
        // A respawned (alive-again) soldier takes its corpse with it.
        ragdolls.Prune [| { soldier with Behavior = Idle } |]
        Assert.True((ragdolls.TryGet soldier.Id).IsNone)
