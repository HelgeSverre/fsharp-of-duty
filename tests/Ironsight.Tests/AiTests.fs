namespace Ironsight.Tests

open System
open System.Numerics
open Ironsight
open Ironsight.ProcGen
open Xunit

module AiTests =
    [<Fact>]
    let ``owned sandbag cover stays on each team's defensive side`` () =
        let level = Levels.paintballArena
        let allied = level.Cover |> Array.filter (fun cover -> cover.Owner = Some Allies)
        let axis = level.Cover |> Array.filter (fun cover -> cover.Owner = Some Axis)
        let neutral = level.Cover |> Array.filter (fun cover -> cover.Owner.IsNone)

        Assert.NotEmpty allied
        Assert.NotEmpty axis
        Assert.True(allied |> Array.forall (fun cover -> cover.Pos.Z > 8.0f))
        Assert.True(axis |> Array.forall (fun cover -> cover.Pos.Z < -8.0f))
        Assert.Contains(neutral, fun cover -> cover.Pos.Z > 2.0f)
        Assert.Contains(neutral, fun cover -> cover.Pos.Z < 2.0f)

        let reversed =
            LevelDsl.level "Reversed owned line"
                [ LevelDsl.street 16.0f 8.0f Mud
                  LevelDsl.sandbags (Vector3(4.0f, 0.0f, 0.0f)) (Vector3(-4.0f, 0.0f, 0.0f)) (Some Axis)
                  LevelDsl.spawnSquad Axis 1 (Vector3(0.0f, 0.0f, -6.0f)) ]
            |> LevelCompile.compile
        let reversedAxis = reversed.Cover |> Array.filter (fun cover -> cover.Owner = Some Axis)
        Assert.True(reversedAxis |> Array.forall (fun cover -> cover.Pos.Z < 0.0f))

    [<Fact>]
    let ``axis soldier occupies axis side of owned barricade`` () =
        let level =
            LevelDsl.level "Owned cover lane"
                [ LevelDsl.street 20.0f 8.0f Mud
                  LevelDsl.sandbags (Vector3(-4.0f, 0.0f, 0.0f)) (Vector3(4.0f, 0.0f, 0.0f)) (Some Axis) ]
            |> LevelCompile.compile
        let baseline = Sim.createTrainingWorld 1201UL
        let player = { baseline.Player with Position = Vector3(0.0f, 0.0f, 5.0f) }
        let template = baseline.Soldiers |> Array.find (fun soldier -> soldier.Team = Axis)
        let enemy =
            { template with
                Position = Vector3(0.0f, 0.0f, -5.0f)
                Facing = MathF.PI
                Behavior = Idle
                Contacts = Map.ofList [ player.Id, struct (player.Position, Units.seconds 0.0f) ] }
        let mutable rng = Rng.create 1202UL
        let mutable soldiers = [| enemy |]
        let mutable occupied = None
        for _ in 1..300 do
            let _, next, _ = AiBrain.step Tuning.TickDuration &rng level Map.empty player soldiers
            soldiers <- next
            match soldiers[0].Behavior with
            | InCover(cover, _) -> occupied <- Some cover
            | _ -> ()
        Assert.True(occupied.IsSome, "AI never occupied the owned barricade")
        Assert.True(occupied.Value.Pos.Z < -0.55f, $"AI crossed to the exposed side at z={occupied.Value.Pos.Z}")

    [<Fact>]
    let ``a direct hit suppresses the target soldier`` () =
        let level = LevelDsl.level "Hit range" [ LevelDsl.street 30.0f 10.0f Mud ] |> LevelCompile.compile
        let world = Sim.createTrainingWorld 5UL
        let target =
            { Id = EntityId 44
              Team = Axis
              Position = Vector3.Zero
              Facing = MathF.PI
              Stance = Standing
              Health = Units.health 100.0f
              Behavior = Idle
              Weapon = Tuning.weaponSlot Tuning.kar98k 3
              Squad = 2
              Contacts = Map.empty
              Suppression = 0.0f
              AnimPhase = 0.0f }
        let player =
            { world.Player with
                Position = Vector3(0.0f, 0.0f, 5.0f)
                Yaw = 0.0f
                Pitch = 0.0f }
        let world = { world with Level = level; Player = player; Soldiers = [| target |] }
        let struct (after, events) = Sim.step { Sequence = 1L; Move = Vector2.Zero; Look = Vector2.Zero; Buttons = InputButtons.Fire } world
        Assert.Contains(events, function HitConfirmed(EntityId 44, false) -> true | _ -> false)
        let soldier = after.Soldiers |> Array.find (fun soldier -> soldier.Id = target.Id)
        Assert.True(soldier.Suppression >= 2.0f)
        match soldier.Behavior with
        | Suppressed _ -> ()
        | other -> Assert.Fail($"expected Suppressed, got {other}")

    [<Fact>]
    let ``a killed soldier stays down instead of standing back up`` () =
        let level = LevelDsl.level "Kill range" [ LevelDsl.street 30.0f 10.0f Mud ] |> LevelCompile.compile
        let world = Sim.createTrainingWorld 5UL
        let precise = { Tuning.kar98k with HipSpread = 0.0f; AdsSpread = 0.0f }
        let target =
            { Id = EntityId 44
              Team = Axis
              Position = Vector3.Zero
              Facing = MathF.PI
              Stance = Standing
              Health = Units.health 10.0f
              Behavior = Idle
              Weapon = Tuning.weaponSlot Tuning.kar98k 3
              Squad = 2
              Contacts = Map.empty
              Suppression = 0.0f
              AnimPhase = 0.0f }
        let slots = Array.copy world.Player.Slots
        slots[0] <- Tuning.weaponSlot precise 4
        let player =
            { world.Player with
                Position = Vector3(0.0f, 0.0f, 5.0f)
                Yaw = 0.0f
                Pitch = 0.0f
                Slots = slots
                Active = 0 }
        let mutable world = { world with Level = level; Player = player; Soldiers = [| target |] }
        let struct (after, _) = Sim.step { Sequence = 1L; Move = Vector2.Zero; Look = Vector2.Zero; Buttons = InputButtons.Fire } world
        Assert.True(after.Soldiers[0].Health <= Units.health 0.0f)
        match after.Soldiers[0].Behavior with
        | Dying _ | DyingHeadshot _ -> ()
        | other -> Assert.Fail($"expected dying behaviour, got {other}")
        let mutable stepped = after
        for tick in 2L..120L do
            stepped <-
                let struct (next, _) = Sim.step { Sequence = tick; Move = Vector2.Zero; Look = Vector2.Zero; Buttons = InputButtons.None } stepped
                next
        match stepped.Soldiers[0].Behavior with
        | Dying _ | DyingHeadshot _ -> ()
        | other -> Assert.Fail($"corpse stood back up as {other}")

    [<Fact>]
    let ``crouching player avoids standing-height shots`` () =
        let level = LevelDsl.level "Hit range" [ LevelDsl.street 30.0f 10.0f Mud ] |> LevelCompile.compile
        let world = Sim.createTrainingWorld 5UL
        let target = world.Soldiers |> Array.find (fun soldier -> soldier.Team = Axis && soldier.Health > Units.health 0.0f)
        let crouchedPlayer =
            { world.Player with
                Position = target.Position + Vector3(0.0f, 0.0f, -5.0f)
                Yaw = 0.0f
                Pitch = 0.0f
                Stance = Crouched }
        let shots = ResizeArray<Vector3>()
        let soldier =
            { Id = target.Id
              Team = Axis
              Position = target.Position
              Facing = MathF.PI
              Stance = Standing
              Health = Units.health 100.0f
              Behavior = Idle
              Weapon = Tuning.weaponSlot Tuning.kar98k 3
              Squad = 2
              Contacts = Map.empty
              Suppression = 0.0f
              AnimPhase = 0.0f }
        let origin = Ballistics.soldierMuzzleOrigin soldier
        let targetPoint = crouchedPlayer.Position + Vector3(0.0f, 1.05f, 0.0f)
        let direction = MathEx.normalizedOrZero (targetPoint - origin)
        // The AI aims at the crouched torso; the shot must connect with the
        // crouched hitbox, whereas a standing-height shot would pass overhead.
        let hit = AiBrain.playerHitDistance origin direction crouchedPlayer
        Assert.True(hit.IsSome)
        shots.Add origin
        Assert.NotEmpty shots

    [<Fact>]
    let ``AI aim cone tightens at range`` () =
        let closeFactor = Math.Clamp(6.0f / MathF.Max(1.0f, 5.0f), 0.35f, Tuning.EnemyAimSpreadMultiplier)
        let farFactor = Math.Clamp(6.0f / MathF.Max(1.0f, 40.0f), 0.35f, Tuning.EnemyAimSpreadMultiplier)
        Assert.True(closeFactor > farFactor)
        Assert.True(farFactor >= 0.35f)

    [<Fact>]
    let ``ai capsule movement cannot walk through a level wall`` () =
        let level =
            LevelDsl.level "Blocked lane"
                [ LevelDsl.street 24.0f 8.0f Mud
                  LevelDsl.block (Vector3(0.0f, 1.5f, -4.0f)) (Vector3(8.0f, 3.0f, 1.0f)) Brick ]
            |> LevelCompile.compile
        let baseline = Sim.createTrainingWorld 901UL
        let player = { baseline.Player with Position = Vector3.Zero }
        let template = baseline.Soldiers |> Array.find (fun soldier -> soldier.Team = Axis)
        let enemy =
            { template with
                Position = Vector3(0.0f, 0.0f, -8.0f)
                Facing = MathF.PI
                Behavior = AdvancingTo(player.Position, [])
                Contacts = Map.ofList [ player.Id, struct (player.Position, Units.seconds 0.0f) ] }
        let mutable rng = Rng.create 902UL
        let mutable soldiers = [| enemy |]
        for _ in 1..180 do
            let _, next, _ = AiBrain.step Tuning.TickDuration &rng level Map.empty player soldiers
            soldiers <- next
        Assert.True(soldiers[0].Position.Z <= -4.84f, $"AI crossed wall boundary at z={soldiers[0].Position.Z}")

    [<Fact>]
    let ``friendly advance uses nav graph to route around blocking cover`` () =
        let level =
            LevelDsl.level "Nav detour"
                [ LevelDsl.street 32.0f 10.0f Mud
                  LevelDsl.block (Vector3(0.0f, 1.5f, 0.0f)) (Vector3(8.0f, 3.0f, 1.2f)) Brick ]
            |> LevelCompile.compile
        let baseline = Sim.createTrainingWorld 905UL
        let allyTemplate = baseline.Soldiers |> Array.find (fun soldier -> soldier.Team = Allies)
        let axisTemplate = baseline.Soldiers |> Array.find (fun soldier -> soldier.Team = Axis)
        let ally = { allyTemplate with Position = Vector3(0.0f, 0.0f, 8.0f); Behavior = Idle }
        let axis = { axisTemplate with Position = Vector3(0.0f, 0.0f, -12.0f); Facing = 0.0f }
        let deadPlayer = { baseline.Player with Health = Units.health 0.0f }
        let mutable rng = Rng.create 906UL
        let mutable soldiers = [| ally; axis |]
        let mutable lateralDetour = 0.0f
        for _ in 1..600 do
            let _, next, _ = AiBrain.step Tuning.TickDuration &rng level Map.empty deadPlayer soldiers
            soldiers <- next
            lateralDetour <- max lateralDetour (abs soldiers[0].Position.X)
        Assert.True(lateralDetour > 4.3f, $"AI never took the nav route around the wall; max x={lateralDetour}")
        Assert.True(soldiers[0].Position.Z < -1.0f, $"AI did not clear the wall; z={soldiers[0].Position.Z}")

    [<Fact>]
    let ``ai versus ai damage does not emit player hit confirmation`` () =
        let level = LevelDsl.level "Feedback lane" [ LevelDsl.street 30.0f 10.0f Mud ] |> LevelCompile.compile
        let baseline = Sim.createTrainingWorld 903UL
        let allyTemplate = baseline.Soldiers |> Array.find (fun soldier -> soldier.Team = Allies)
        let axisTemplate = baseline.Soldiers |> Array.find (fun soldier -> soldier.Team = Axis)
        let ally = { allyTemplate with Position = Vector3(0.0f, 0.0f, 3.0f); Weapon = Tuning.weaponSlot Tuning.thompson 4 }
        let axis = { axisTemplate with Position = Vector3(0.0f, 0.0f, -3.0f); Facing = MathF.PI; Weapon = Tuning.weaponSlot Tuning.kar98k 4 }
        let deadPlayer = { baseline.Player with Position = Vector3(20.0f, 0.0f, 20.0f); Health = Units.health 0.0f }
        let mutable rng = Rng.create 904UL
        let mutable soldiers = [| ally; axis |]
        let mutable hitConfirmations = 0
        for _ in 1..180 do
            let _, next, events = AiBrain.step Tuning.TickDuration &rng level Map.empty deadPlayer soldiers
            soldiers <- next
            hitConfirmations <- hitConfirmations + (events |> List.filter (function HitConfirmed _ -> true | _ -> false) |> List.length)
        Assert.Equal(0, hitConfirmations)
        Assert.Contains(soldiers, fun soldier -> soldier.Health < Units.health 100.0f)

    [<Fact>]
    let ``suppression requires accumulated near misses within the decay window`` () =
        let level = LevelDsl.level "Suppression lane" [ LevelDsl.street 40.0f 12.0f Mud ] |> LevelCompile.compile
        let baseline = Sim.createTrainingWorld 808UL
        let precise = { Tuning.m1911 with HipSpread = 0.0f; AdsSpread = 0.0f }
        let player =
            { baseline.Player with
                Position = Vector3.Zero
                Yaw = 0.0f
                Slots = [| Tuning.weaponSlot precise 4 |]
                Active = 0 }
        let enemyTemplate = baseline.Soldiers |> Array.find (fun soldier -> soldier.Team = Axis)
        let enemy =
            { enemyTemplate with
                Position = Vector3(0.75f, 0.0f, -10.0f)
                Facing = 0.0f
                Behavior = Idle
                Contacts = Map.empty
                Suppression = 0.0f }
        let mutable world =
            { baseline with Player = player; Soldiers = [| enemy |]; Level = level; Objectives = [||]; Script = { baseline.Script with Rules = [||] } }
        for tick in 1L..23L do
            let buttons = if tick = 1L || tick = 12L || tick = 23L then InputButtons.Fire else InputButtons.None
            let frame = { Sequence = tick; Move = Vector2.Zero; Look = Vector2.Zero; Buttons = buttons }
            let struct (next, _) = Sim.step frame world
            world <- next
        Assert.True(world.Soldiers[0].Suppression >= 2.0f)
        Assert.Matches("Suppressed.*", string world.Soldiers[0].Behavior)

    [<Fact>]
    let ``axis soldier perceives advances and shoots player through deterministic AI`` () =
        let level = LevelDsl.level "AI range" [ LevelDsl.street 40.0f 10.0f Mud ] |> LevelCompile.compile
        let world = Sim.createTrainingWorld 88UL
        let player = { world.Player with Position = Vector3.Zero }
        let enemy =
            { Id = EntityId 44
              Team = Axis
              Position = Vector3(0.0f, 0.0f, -12.0f)
              Facing = MathF.PI
              Stance = Standing
              Health = Units.health 100.0f
              Behavior = Idle
              Weapon = Tuning.weaponSlot Tuning.kar98k 3
              Squad = 2
              Contacts = Map.empty
              Suppression = 0.0f
              AnimPhase = 0.0f }
        let mutable rng = Rng.create 99UL
        let mutable currentPlayer = player
        let mutable soldiers = [| enemy |]
        let mutable shots = 0
        for _ in 1..180 do
            let nextPlayer, nextSoldiers, events = AiBrain.step Tuning.TickDuration &rng level Map.empty currentPlayer soldiers
            currentPlayer <- nextPlayer
            soldiers <- nextSoldiers
            shots <- shots + (events |> List.filter (function ShotFired _ -> true | _ -> false) |> List.length)
        Assert.True(shots > 0)
        Assert.True(currentPlayer.Health < Units.health 100.0f)
        Assert.True(soldiers[0].Position.Z > enemy.Position.Z)
        Assert.True(soldiers[0].Contacts.ContainsKey player.Id)

    [<Fact>]
    let ``bolt action bot cycles the bolt and fires repeatedly under sustained contact`` () =
        let level = LevelDsl.level "Cycle range" [ LevelDsl.street 40.0f 10.0f Mud ] |> LevelCompile.compile
        let world = Sim.createTrainingWorld 288UL
        let player = { world.Player with Position = Vector3.Zero }
        let template = world.Soldiers |> Array.find (fun soldier -> soldier.Team = Axis)
        let enemy =
            { template with
                Position = Vector3(0.0f, 0.0f, -10.0f)
                Facing = MathF.PI
                Behavior = Idle
                Weapon = Tuning.weaponSlot Tuning.kar98k 3
                Contacts = Map.ofList [ player.Id, struct (player.Position, Units.seconds 0.0f) ] }
        let mutable rng = Rng.create 289UL
        let mutable currentPlayer = player
        let mutable soldiers = [| enemy |]
        let mutable shots = 0
        for _ in 1..300 do
            let nextPlayer, nextSoldiers, events = AiBrain.step Tuning.TickDuration &rng level Map.empty currentPlayer soldiers
            currentPlayer <- nextPlayer
            soldiers <- nextSoldiers
            shots <-
                shots
                + (events
                   |> List.filter (function
                       | ShotFired(Some shooter, _, _, _) when shooter = enemy.Id -> true
                       | _ -> false)
                   |> List.length)
        // A bolt action that never releases the trigger fires once and stalls.
        // The AI must cycle the bolt and keep firing on its natural cadence.
        Assert.True(shots >= 2, $"expected the bolt to cycle, fired {shots} times")

    [<Fact>]
    let ``enemy reloads an empty weapon and resumes firing`` () =
        let level = LevelDsl.level "Reload range" [ LevelDsl.street 40.0f 10.0f Mud ] |> LevelCompile.compile
        let world = Sim.createTrainingWorld 188UL
        let player = { world.Player with Position = Vector3.Zero }
        let template = world.Soldiers |> Array.find (fun soldier -> soldier.Team = Axis)
        let enemy =
            { template with
                Position = Vector3(0.0f, 0.0f, -10.0f)
                Facing = MathF.PI
                Behavior = Idle
                Weapon = { Tuning.weaponSlot Tuning.kar98k 1 with InMag = 0 }
                Contacts = Map.ofList [ player.Id, struct (player.Position, Units.seconds 0.0f) ] }
        let mutable rng = Rng.create 189UL
        let mutable currentPlayer = player
        let mutable soldiers = [| enemy |]
        let mutable sawReload = false
        let mutable resumedFire = false
        for _ in 1..220 do
            let nextPlayer, nextSoldiers, events = AiBrain.step Tuning.TickDuration &rng level Map.empty currentPlayer soldiers
            currentPlayer <- nextPlayer
            soldiers <- nextSoldiers
            sawReload <- sawReload || (match soldiers[0].Weapon.State with Reloading _ -> true | _ -> false)
            resumedFire <-
                resumedFire
                || (events |> List.exists (function ShotFired(Some shooter, _, _, _) when shooter = enemy.Id -> true | _ -> false))
        Assert.True(sawReload)
        Assert.True(resumedFire)

    [<Fact>]
    let ``perception contact expires after eight seconds without sight`` () =
        let level = LevelDsl.level "Memory range" [ LevelDsl.street 40.0f 10.0f Mud ] |> LevelCompile.compile
        let world = Sim.createTrainingWorld 1UL
        let hiddenPlayer = { world.Player with Position = Vector3(0.0f, 0.0f, 10.0f) }
        let template = world.Soldiers |> Array.find (fun value -> value.Team = Axis)
        let soldier =
            { template with
                Position = Vector3.Zero
                Facing = 0.0f
                Contacts = Map.ofList [ hiddenPlayer.Id, struct (hiddenPlayer.Position, Units.seconds 7.99f) ] }
        let updated = Perception.updateContacts false (Units.seconds 0.02f) level hiddenPlayer soldier
        Assert.False(updated.Contacts.ContainsKey hiddenPlayer.Id)

    [<Fact>]
    let ``friendly squad advances and engages visible axis soldiers`` () =
        let level = LevelDsl.level "Squad range" [ LevelDsl.street 50.0f 14.0f Mud ] |> LevelCompile.compile
        let baseWorld = Sim.createTrainingWorld 144UL
        let player = { baseWorld.Player with Position = Vector3(10.0f, 0.0f, 20.0f); Health = Units.health 0.0f }
        let template = baseWorld.Soldiers[0]
        let ally = { template with Id = EntityId 10; Team = Allies; Position = Vector3(0.0f, 0.0f, 8.0f); Weapon = Tuning.weaponSlot Tuning.thompson 4 }
        let enemy = { template with Id = EntityId 11; Team = Axis; Position = Vector3(0.0f, 0.0f, -8.0f); Weapon = Tuning.weaponSlot Tuning.kar98k 4 }
        let mutable rng = Rng.create 155UL
        let mutable soldiers = [| ally; enemy |]
        let mutable shots = 0
        for _ in 1..300 do
            let _, next, events = AiBrain.step Tuning.TickDuration &rng level Map.empty player soldiers
            soldiers <- next
            shots <- shots + (events |> List.filter (function ShotFired(Some(EntityId 10), _, _, _) -> true | _ -> false) |> List.length)
        Assert.True(shots > 0)
        Assert.True(soldiers[1].Health < Units.health 100.0f)
