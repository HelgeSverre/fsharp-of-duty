namespace Ironsight.Tests

open System.Numerics
open Ironsight
open Ironsight.ProcGen
open Ironsight.Shell
open Xunit

module ClientTests =
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
        Assert.Equal(AudioSynth.SampleRate, shot.SampleRate)
        Assert.True(shot.Samples.Length > 10000)
        Assert.Contains(shot.Samples, fun sample -> abs (int sample) > 1000)

    [<Fact>]
    let ``start menu supports keyboard map selection and the Fly server`` () =
        let idle =
            { Up = false
              Down = false
              Activate = false
              Back = false
              Backspace = false
              TextInput = ""
              Pointer = None
              Clicked = false }
        let activate = { idle with Activate = true }
        let struct (mapChoice, noAction) = StartMenu.update 1280 720 activate { StartMenu.initial with Selected = 2 }
        Assert.Equal(OfflineMaps, mapChoice.Page)
        Assert.True(noAction.IsNone)
        let struct (_, mapAction) = StartMenu.update 1280 720 activate mapChoice
        Assert.Equal(Some(StartOffline "paintball"), mapAction)
        let struct (servers, _) = StartMenu.update 1280 720 activate { StartMenu.initial with Selected = 3 }
        Assert.Equal(ServerList, servers.Page)
        let struct (loadout, _) = StartMenu.update 1280 720 activate servers
        Assert.Equal(OnlineLoadout, loadout.Page)
        let struct (_, onlineAction) = StartMenu.update 1280 720 activate { loadout with Selected = 3 }
        Assert.Equal(Some(StartOnline "Kar98k Sniper"), onlineAction)

        let struct (editing, _) = StartMenu.update 1280 720 activate (StartMenu.create "Old")
        let struct (edited, _) =
            StartMenu.update 1280 720 { idle with Backspace = true; TextInput = "X" } editing
        Assert.Equal("OlX", edited.PlayerName)
        let struct (named, _) = StartMenu.update 1280 720 activate edited
        Assert.Equal(Main, named.Page)
        Assert.Equal("OlX", named.PlayerName)

        let pointer =
            { idle with
                Pointer = Some(Vector2(640.0f, 327.0f))
                Clicked = true }
        let struct (_, pointerAction) = StartMenu.update 1280 720 pointer StartMenu.initial
        Assert.Equal(Some(StartOffline "paintball"), pointerAction)

        let struct (_, exitAction) = StartMenu.update 1280 720 { idle with Back = true } StartMenu.initial
        Assert.Equal(Some ExitGame, exitAction)

    [<Fact>]
    let ``lethal headshot pose removes head geometry`` () =
        let world = Sim.createPaintballWorld 73UL
        let soldier = world.Soldiers[0]
        let normal = Humanoid.pose soldier
        let headless = Humanoid.pose { soldier with Behavior = DyingHeadshot(Units.seconds 0.1f) }
        Assert.True(headless.Vertices.Length < normal.Vertices.Length)
        Assert.True(headless.Indices.Length < normal.Indices.Length)

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
        let meshes = [| MeshGen.box (Vector3.One) Wood; MeshGen.cylinder 10 0.2f 1.0f Metal |]
        for mesh in meshes do
            for triangle in 0..mesh.Indices.Length / 3 - 1 do
                let a = mesh.Vertices[int mesh.Indices[triangle * 3]]
                let b = mesh.Vertices[int mesh.Indices[triangle * 3 + 1]]
                let c = mesh.Vertices[int mesh.Indices[triangle * 3 + 2]]
                let geometric = Vector3.Cross(b.Position - a.Position, c.Position - a.Position)
                Assert.True(Vector3.Dot(geometric, a.Normal) > 0.0f)

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
                     Yaw = 0.0f
                     Pitch = 0.0f
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
                     Yaw = 3.14f
                     Pitch = 0.0f
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
    let ``Fly hostname is the online default`` () =
        let previous = System.Environment.GetEnvironmentVariable "IRONSIGHT_SERVER"
        try
            System.Environment.SetEnvironmentVariable("IRONSIGHT_SERVER", null)
            Assert.Equal("wss://fsharp-of-duty.fly.dev/play", OnlineDefaults.serverUri().AbsoluteUri)
        finally
            System.Environment.SetEnvironmentVariable("IRONSIGHT_SERVER", previous)

    [<Fact>]
    let ``player shot origin is in front of the weapon hand position`` () =
        let world = Sim.createTrainingWorld 11UL
        let player = { world.Player with Yaw = 0.4f; Pitch = -0.1f; Ads = 0.0f }
        let weapon = player.Slots[player.Active].Class
        let origin = Ballistics.playerMuzzleOrigin player weapon.Name
        let eye = player.Position + Vector3(0.0f, 1.62f, 0.0f)
        let forward = Ballistics.directionFromAngles player.Yaw player.Pitch Vector2.Zero
        Assert.True(Vector3.Dot(origin - eye, forward) > 0.5f)
        Assert.True(Vector3.Distance(origin, eye) > 0.5f)
