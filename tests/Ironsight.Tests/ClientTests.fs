namespace Ironsight.Tests

open System
open System.Numerics
open Ironsight
open Ironsight.ProcGen
open Ironsight.Shell
open Xunit

module ClientTests =
    [<Theory>]
    [<InlineData(0.0f, 1.0f)>]
    [<InlineData(0.5f, 0.75f)>]
    [<InlineData(1.0f, 0.5f)>]
    [<InlineData(-1.0f, 1.0f)>]
    [<InlineData(2.0f, 0.5f)>]
    let ``gamepad look sensitivity eases to half speed during ADS`` ads expected =
        Assert.Equal(expected, InputTuning.gamepadAdsScale ads)

    [<Fact>]
    let ``only a whole wheel detent cycles a weapon`` () =
        // A mouse wheel clicks in whole units. A macOS trackpad reports tenths,
        // and every one of those used to be a weapon switch — brush the pad
        // while aiming and the gun changed in your hands.
        Assert.Equal(1, InputTuning.wheelDetents 1.0f)
        Assert.Equal(-1, InputTuning.wheelDetents -1.0f)
        // A fast flick is several clicks at once and must not be lost.
        Assert.Equal(3, InputTuning.wheelDetents 3.0f)
        Assert.Equal(-2, InputTuning.wheelDetents -2.0f)
        Assert.Equal(1, InputTuning.wheelDetents 1.5f)
        for precise in [ 0.03f; -0.03f; 0.4f; -0.4f; 0.999f; -0.999f; 0.0f ] do
            Assert.Equal(0, InputTuning.wheelDetents precise)

    [<Fact>]
    let ``a scrolling surface accumulates into whole rows`` () =
        // Lists still scroll on a trackpad, but by distance travelled rather
        // than one row per hardware event, which sent the picker flying.
        let mutable residue = 0.0f
        let mutable rows = 0
        for _ in 1..30 do
            let stepped, next = InputTuning.scrollRows residue 0.03f
            residue <- next
            rows <- rows + stepped
        // Thirty tenths of a row is nine tenths of a row, not thirty rows.
        Assert.Equal(0, rows)
        let crossed, afterCrossing = InputTuning.scrollRows residue 0.2f
        Assert.Equal(1, crossed)
        Assert.InRange(afterCrossing, -1.0f, 1.0f)
        // A wheel click is exactly one row and leaves nothing behind.
        let click, leftover = InputTuning.scrollRows 0.0f -1.0f
        Assert.Equal(-1, click)
        Assert.Equal(0.0f, leftover)

    [<Fact>]
    let ``katana viewmodel travels left to right and up to down`` () =
        let tip = -Vector3.UnitZ
        let primaryStart = Vector3.Transform(tip, Matrix4x4.CreateRotationY(ViewmodelAnimation.katanaYaw true (Some 0.0f) (Some KatanaSweep)))
        let primaryEnd = Vector3.Transform(tip, Matrix4x4.CreateRotationY(ViewmodelAnimation.katanaYaw true (Some 0.71f) (Some KatanaSweep)))
        Assert.True(primaryStart.X < primaryEnd.X, $"primary moved {primaryStart.X} -> {primaryEnd.X}, expected left -> right")

        let overheadStart = Vector3.Transform(tip, Matrix4x4.CreateRotationX(ViewmodelAnimation.katanaPitch (Some 0.0f) (Some KatanaOverhead)))
        let overheadEnd = Vector3.Transform(tip, Matrix4x4.CreateRotationX(ViewmodelAnimation.katanaPitch (Some 0.71f) (Some KatanaOverhead)))
        Assert.True(overheadStart.Y > overheadEnd.Y, $"alternate moved {overheadStart.Y} -> {overheadEnd.Y}, expected up -> down")

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
    let ``render interpolation carries projectiles forward on their velocity`` () =
        // Online, projectiles only move when a snapshot lands. Without this an
        // arrow at 88 m/s steps four metres at a time between frames.
        let world = Sim.createTrainingWorld 907UL
        let arrow =
            SpecialProjectiles.spawnArrow (EntityId 1) (Units.health 90.0f) 1.0f Vector3.Zero -Vector3.UnitZ
        let flying = { world with SpecialProjectiles = [| arrow |] }
        let at alpha = (RenderInterpolation.world alpha flying flying).SpecialProjectiles[0].Position.Z
        Assert.Equal(arrow.Position.Z, at 0.0f)
        Assert.True(at 0.5f < at 0.0f, "half a frame moved the arrow nowhere")
        Assert.True(at 1.0f < at 0.5f, "the carry is not monotonic")
        // A whole frame of carry is one tick of travel, no more.
        Assert.InRange(arrow.Position.Z - at 1.0f, 0.0f, arrow.Velocity.Length() * Units.raw Tuning.TickDuration * 1.01f)

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
        let katanaSwing = AudioSynth.katanaSwing ()
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
        Assert.InRange(katanaSwing.Samples.Length, 7900, 8000)
        Assert.Contains(katanaSwing.Samples, fun sample -> abs (int sample) > 1000)

    [<Fact>]
    let ``the gun registry covers the arsenal without being hand-checked`` () =
        // Guns.names and Tuning.onlineWeapons are two hand-kept lists of the
        // same set, and meshFor silently falls back to the Kar98k for anything
        // missing — so adding a weapon and forgetting its mesh looks like a
        // working gun that is secretly someone else's model.
        let registered = Set.ofArray Guns.names
        for weapon in Tuning.onlineWeapons do
            Assert.True(
                Set.contains weapon.Name registered,
                $"{weapon.Name} is selectable but has no entry in Guns.names")
        // And nothing lingers in the registry that no longer exists.
        let selectable =
            Array.concat [ Tuning.onlineWeapons; Tuning.specialWeapons ]
            |> Array.map _.Name
            |> Set.ofArray
            |> Set.add Tuning.mg42.Name
        for name in Guns.names do
            Assert.True(Set.contains name selectable, $"Guns.names lists {name}, which is not a weapon any more")

    [<Fact>]
    let ``the shader has a branch for every material, by index`` () =
        // Materials.all is the index, and the shader matches those indices as
        // bare numbers in GLSL — a second hand-kept table with a silent
        // fallback, which is this codebase's recurring bug shape. Appending a
        // material or reordering the array renders the new one flat grey
        // instead of failing, and adding one to the shader that no material
        // uses is dead code nobody notices.
        let branches =
            Text.RegularExpressions.Regex.Matches(Shaders.levelFragment, @"vMaterial == (\d+)")
            |> Seq.map (fun found -> int found.Groups[1].Value)
            |> Set.ofSeq
        let expected = Set.ofList [ 0 .. Materials.all.Length - 1 ]
        Assert.Equal<Set<int>>(expected, branches)

    [<Fact>]
    let ``every weapon aims from a sight line near its bore`` () =
        // The aim pose lifts the viewmodel by exactly this height, so a value
        // that is not on the gun aims through the receiver or above the sights.
        for name in Guns.names do
            let sight = Guns.sightHeight name
            // Never below the bore, and never far above it.
            //
            // Not "a point on the model": you sight a bow along the arrow,
            // which is not part of the bow, and a harpoon gun's authored aim
            // sits a centimetre over its own topmost part. The band is the
            // invariant that matters, and the one that catches a real mistake —
            // the bow briefly aimed from its upper limb tip at 0.857.
            Assert.InRange(sight, 0.0f, 0.35f)

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
    let ``the wheel scrolls the picker without dragging the cursor`` () =
        // A mouse user must be able to reach rows the keyboard cursor has not
        // walked to; scrolling the window is not the same as moving selection.
        let idle = TestKit.idleMenuInput
        let start = LoadoutMenu.create ()
        let struct (scrolled, _) = LoadoutMenu.update 1280 720 { idle with Scroll = 3 } start
        Assert.Equal(start.Selected, scrolled.Selected)
        if LoadoutMenu.rows.Length > LoadoutMenu.MaxVisibleRows then
            Assert.True(scrolled.FirstVisible > start.FirstVisible, "the wheel did not scroll the window")
        // It stops at both ends rather than running off the list.
        let mutable state = start
        for _ in 1 .. LoadoutMenu.rows.Length + 5 do
            let struct (next, _) = LoadoutMenu.update 1280 720 { idle with Scroll = 1 } state
            state <- next
        Assert.True(state.FirstVisible <= max 0 (LoadoutMenu.rows.Length - LoadoutMenu.MaxVisibleRows))
        for _ in 1 .. LoadoutMenu.rows.Length + 5 do
            let struct (next, _) = LoadoutMenu.update 1280 720 { idle with Scroll = -1 } state
            state <- next
        Assert.Equal(0, state.FirstVisible)
        // Moving the keyboard cursor still pulls the window back to it.
        let struct (stepped, _) = LoadoutMenu.update 1280 720 { idle with Down = true } scrolled
        Assert.True((LoadoutMenu.weaponAt stepped.Selected).IsSome)

    [<Fact>]
    let ``every weapon can be reached and clicked with the mouse alone`` () =
        // The window used to be re-centred on the keyboard cursor every frame,
        // so the wheel could never carry it past the first row's group and the
        // last weapons in the list were unclickable.
        let idle = TestKit.idleMenuInput
        let rowsRect = LoadoutMenu.rowsRect (LoadoutMenu.panelRect 1280 720)
        let mutable state = LoadoutMenu.create ()
        let reached = System.Collections.Generic.HashSet<string>()
        for _ in 1 .. LoadoutMenu.rows.Length + 5 do
            let first, visible = LoadoutMenu.visibleRows state
            visible
            |> List.iteri (fun slot (index, row) ->
                match row with
                | LoadoutMenu.Weapon _ ->
                    let pointer = TestKit.rowMiddle LoadoutMenu.RowHeight slot rowsRect
                    let struct (_, action) =
                        LoadoutMenu.update 1280 720 { idle with Pointer = Some pointer; Clicked = true } state
                    // The row the pointer sits on is the one that gets equipped.
                    Assert.Equal(LoadoutMenu.Chosen (LoadoutMenu.weaponAt index).Value.Name, action)
                    reached.Add (LoadoutMenu.weaponAt index).Value.Name |> ignore
                | LoadoutMenu.Header _ -> ())
            ignore first
            let struct (next, _) = LoadoutMenu.update 1280 720 { idle with Scroll = 1 } state
            state <- next
        for weapon in Tuning.onlineWeapons do
            Assert.True(reached.Contains weapon.Name, $"{weapon.Name} is unreachable in the picker")

    [<Fact>]
    let ``the picker tracks which row the pointer is over`` () =
        let idle = TestKit.idleMenuInput
        let rows = LoadoutMenu.rowsRect (LoadoutMenu.panelRect 1280 720)
        let start = LoadoutMenu.create ()
        Assert.True(start.Hovered.IsNone)
        // A pointer on a weapon row reports that row.
        let _, visible = LoadoutMenu.visibleRows start
        let weaponSlot =
            visible |> List.findIndex (fun (_, row) -> match row with LoadoutMenu.Weapon _ -> true | _ -> false)
        let struct (hovering, _) =
            LoadoutMenu.update 1280 720 { idle with Pointer = Some(TestKit.rowMiddle LoadoutMenu.RowHeight weaponSlot rows) } start
        Assert.Equal(Some(fst visible[weaponSlot]), hovering.Hovered)
        // A pointer on a heading reports nothing, so no highlight is drawn on it.
        match visible |> List.tryFindIndex (fun (_, row) -> match row with LoadoutMenu.Header _ -> true | _ -> false) with
        | Some headingSlot ->
            let struct (onHeading, _) =
                LoadoutMenu.update 1280 720 { idle with Pointer = Some(TestKit.rowMiddle LoadoutMenu.RowHeight headingSlot rows) } start
            Assert.True(onHeading.Hovered.IsNone)
        | None -> ()
        // A pointer off the panel clears it.
        let struct (away, _) = LoadoutMenu.update 1280 720 { idle with Pointer = Some(Vector2(5.0f, 5.0f)) } hovering
        Assert.True(away.Hovered.IsNone)

    [<Fact>]
    let ``picker hover still matches the drawn row after scrolling`` () =
        // The off-by-one this pattern invites: hover is computed from the drawn
        // window, so it has to be offset by the scroll position.
        let idle = TestKit.idleMenuInput
        let rows = LoadoutMenu.rowsRect (LoadoutMenu.panelRect 1280 720)
        let scrolled = { LoadoutMenu.create () with Selected = LoadoutMenu.rows.Length - 1 }
        let first, visible = LoadoutMenu.visibleRows scrolled
        // Hover the last drawn weapon slot and confirm it selects the row drawn
        // there. The last slot outright may be a category header, which is not
        // a hover target — where it falls depends on the arsenal's grouping.
        let slot = visible |> List.findIndexBack (fun (_, row) -> match row with LoadoutMenu.Weapon _ -> true | _ -> false)
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
