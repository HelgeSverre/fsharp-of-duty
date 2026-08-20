namespace Ironsight.Tests

open System
open System.Numerics
open Ironsight
open Ironsight.ProcGen
open Xunit

module CoreTests =
    let input = TestKit.input

    [<Fact>]
    let ``paintball killhouse is a bounded player versus four fight`` () =
        let world = Sim.createPaintballWorld 43UL
        let allies = world.Soldiers |> Array.filter (fun soldier -> soldier.Team = Allies)
        let axis = world.Soldiers |> Array.filter (fun soldier -> soldier.Team = Axis)
        Assert.Equal("Paintball Killhouse", world.Level.Name)
        Assert.Empty(allies)
        Assert.Equal(4, axis.Length)
        Assert.Equal("Thompson", world.Player.Slots[world.Player.Active].Class.Name)
        Assert.Equal(8, world.Level.Spawns |> Array.filter (fun struct (team, _) -> team = Some Allies) |> Array.length)
        Assert.Equal(8, world.Level.Spawns |> Array.filter (fun struct (team, _) -> team = Some Axis) |> Array.length)
        Assert.All(world.Level.Spawns, fun struct (_, position) ->
            Assert.InRange(position.X, world.Level.Bounds.Min.X, world.Level.Bounds.Max.X)
            Assert.InRange(position.Z, world.Level.Bounds.Min.Z, world.Level.Bounds.Max.Z)
            let obstructed =
                LevelCompile.brushesNear position (Tuning.PlayerRadius + 0.1f) world.Level
                |> Array.filter (fun brush -> brush.Bounds.Max.Y > 0.1f)
                |> Array.exists (fun brush -> MathEx.capsuleIntersectsAabb Tuning.PlayerRadius Tuning.StandingHeight position brush.Bounds)
            Assert.False(obstructed, $"Spawn intersects generated cover at {position}"))
        Assert.True(world.Level.Brushes.Length < 250)

    [<Fact>]
    let ``paintball rounds score and automatically reset combatants`` () =
        let original = Sim.createPaintballWorld 143UL
        let defeated =
            { original with
                Soldiers =
                    original.Soldiers
                    |> Array.map (fun soldier -> { soldier with Health = Units.health 0.0f; Behavior = Dying(Units.seconds 0.0f) }) }
        let struct (scored, events) = Sim.step (input 1L InputButtons.None Vector2.Zero) defeated
        let scoredRound = scored.Round.Value
        Assert.Equal(1, scoredRound.PlayerScore)
        Assert.True(scoredRound.ResetIn.IsSome)
        Assert.Contains(events, fun event -> event = Subtitle("MARSHAL", "ROUND WON"))
        let reset = TestKit.advance 2L 100L InputButtons.None scored
        let nextRound = reset.Round.Value
        Assert.Equal(2, nextRound.Number)
        Assert.Equal(1, nextRound.PlayerScore)
        Assert.True(reset.Player.IsAlive)
        Assert.All(reset.Soldiers, fun soldier -> Assert.True(soldier.IsAlive))

    [<Fact>]
    let ``mounted gun crew remains at its generated emplacement`` () =
        let nest =
            LevelDsl.level "MG nest"
                [ LevelDsl.street 40.0f 16.0f Mud
                  LevelDsl.mg42 (Vector3(0.0f, 0.0f, -12.0f)) MathF.PI Axis
                  LevelDsl.spawnSquad Allies 1 (Vector3(0.0f, 0.0f, 14.0f)) ]
            |> LevelCompile.compile
        let initial = Sim.createWorld nest "Silence the gun" 144UL
        let gunner = initial.Soldiers |> Array.find (fun soldier -> soldier.Weapon.Class.Name = "MG42")
        let initialPosition = gunner.Position
        let world = TestKit.advance 1L 180L InputButtons.None initial
        let updated = world.Soldiers |> Array.find (fun soldier -> soldier.Id = gunner.Id)
        Assert.Equal(initialPosition, updated.Position)

    [<Fact>]
    let ``mass enemy contact produces sustained fire without instant player death`` () =
        let arena = LevelDsl.level "Aim pacing" [ LevelDsl.street 100.0f 60.0f Mud ] |> LevelCompile.compile
        let baseline = Sim.createTrainingWorld 45UL
        let player = { baseline.Player with Position = Vector3.Zero; Yaw = 0.0f }
        let enemies =
            Array.init 50 (fun index ->
                let x = (float32 (index % 10) - 4.5f) * 2.0f
                let z = -28.0f - float32 (index / 10) * 1.4f
                let weapon = { Tuning.weaponSlot Tuning.kar98k 4 with State = Cooling(Units.seconds (float32 (index % 60) / 60.0f)) }
                { Id = EntityId(100 + index); Team = Axis; Position = Vector3(x, 0.0f, z); Facing = MathF.PI; Stance = Standing
                  Health = Units.health 100.0f; Behavior = AdvancingTo(player.Position, []); Weapon = weapon; Squad = 2
                  Contacts = Map.add player.Id (struct (player.Position, Units.seconds 0.0f)) Map.empty; Suppression = 0.0f; AnimPhase = float32 index })
        let start = { baseline with Player = player; Soldiers = enemies; Level = arena; Objectives = [||]; Script = { baseline.Script with Rules = [||] } }
        let world, events = TestKit.stepAll 1L 180L InputButtons.None start
        let hostileShots = events |> List.sumBy (function ShotFired(Some(EntityId id), _, _, _) when id >= 100 -> 1 | _ -> 0)
        Assert.True(hostileShots > 0)
        Assert.True(world.Player.IsAlive)

    [<Fact>]
    let ``enemy fire uses a deliberately loose player facing accuracy model`` () =
        Assert.InRange(Tuning.EnemyMaxPlayerShooters, 1, 8)
        Assert.True(Tuning.EnemyAimSpreadMultiplier >= 2.0f)
        Assert.True(Tuning.EnemyDamageScale <= 0.25f)

    [<Fact>]
    let ``movement and sighting favor fast arcade response`` () =
        Assert.True(Tuning.WalkSpeed >= 5.0f)
        Assert.True(Tuning.WalkSpeed * Tuning.SprintMultiplier >= 8.0f)
        Assert.True(Tuning.kar98kSniper.AdsTime <= Units.seconds 0.20f)
        Assert.True(Tuning.thompson.AdsTime <= Units.seconds 0.14f)

    [<Fact>]
    let ``kar98k hip fire is the tightest among player weapons`` () =
        let tightest = Tuning.onlineWeapons |> Array.minBy (fun weapon -> weapon.HipSpread)
        Assert.Equal("Kar98k", tightest.Name)
        Assert.True(Tuning.kar98k.HipSpread < Tuning.thompson.HipSpread)
        Assert.True(Tuning.kar98kSniper.HipSpread < Tuning.thompson.HipSpread)

    [<Fact>]
    let ``simulation is deterministic for equal seed and inputs`` () =
        let inputs = [ for tick in 1L..180L -> input tick InputButtons.Sprint (Vector2(0.2f, 1.0f)) ]
        let run () =
            inputs
            |> List.fold (fun world frame -> let struct (next, _) = Sim.step frame world in next) (Sim.createTrainingWorld 42UL)
        Assert.Equal(run (), run ())

    [<Fact>]
    let ``weapon enforces cooldown and consumes one round`` () =
        let world = Sim.createTrainingWorld 1UL
        let struct (fired, events) = Sim.step (input 1L InputButtons.Fire Vector2.Zero) world
        let struct (cooled, secondEvents) = Sim.step (input 2L InputButtons.Fire Vector2.Zero) fired
        Assert.Equal(world.Player.Slots[0].InMag - 1, fired.Player.Slots[0].InMag)
        Assert.Single(events |> List.filter (function ShotFired(Some(EntityId 1), _, _, _) -> true | _ -> false)) |> ignore
        Assert.Empty(secondEvents |> List.filter (function ShotFired(Some(EntityId 1), _, _, _) -> true | _ -> false))
        Assert.Equal(fired.Player.Slots[0].InMag, cooled.Player.Slots[0].InMag)

    [<Fact>]
    let ``a reload tap completes without keeping reload held`` () =
        let initial = Sim.createTrainingWorld 11UL
        let slots = Array.copy initial.Player.Slots
        slots[0] <- { slots[0] with InMag = 1; Reserve = 12 }
        let reloading = TestKit.advance 1L 1L InputButtons.Reload { initial with Player = { initial.Player with Slots = slots } }
        Assert.True(match reloading.Player.Slots[0].State with Reloading _ -> true | _ -> false)
        let world = TestKit.advance 2L 180L InputButtons.None reloading
        let weapon = world.Player.Slots[0]
        Assert.Equal(weapon.Class.MagSize, weapon.InMag)
        Assert.Equal(Ready, weapon.State)
        Assert.True(Tuning.kar98k.ReloadTime < Units.seconds 2.6f)

    [<Fact>]
    let ``number key switches campaign weapon after transition`` () =
        let world =
            Sim.createTrainingWorld 17UL
            |> TestKit.advance 1L 1L InputButtons.Weapon2
            |> TestKit.advance 2L 24L InputButtons.None
        Assert.Equal(3, world.Player.Active)
        Assert.Equal("Thompson", world.Player.Slots[world.Player.Active].Class.Name)

    [<Fact>]
    let ``pressing the same weapon key cycles within its category`` () =
        // Category 1 holds Kar98k -> Garand -> Lee-Enfield -> wraps to Kar98k.
        let mutable world = Sim.createTrainingWorld 29UL
        Assert.Equal(0, world.Player.Active)
        let mutable sequence = 1L
        let press () =
            world <-
                world
                |> TestKit.advance sequence sequence InputButtons.Weapon1
                |> TestKit.advance (sequence + 1L) (sequence + 24L) InputButtons.None
            sequence <- sequence + 25L
        press ()
        Assert.Equal("M1 Garand", world.Player.Slots[world.Player.Active].Class.Name)
        press ()
        Assert.Equal("Lee-Enfield", world.Player.Slots[world.Player.Active].Class.Name)
        press ()
        Assert.Equal("Kar98k", world.Player.Slots[world.Player.Active].Class.Name)

    [<Fact>]
    let ``every online weapon resolves by name`` () =
        for weapon in Tuning.onlineWeapons do
            Assert.Equal(Some weapon, Tuning.weaponByName weapon.Name)

    [<Fact>]
    let ``fourth slot equips a scoped high damage sniper rifle`` () =
        let world =
            Sim.createTrainingWorld 18UL
            |> TestKit.advance 1L 1L InputButtons.Weapon4
            |> TestKit.advance 2L 24L InputButtons.None
        let sniper = world.Player.Slots[world.Player.Active].Class
        Assert.Equal(7, world.Player.Active)
        Assert.Equal("Kar98k Sniper", sniper.Name)
        Assert.True(sniper.Damage > Tuning.kar98k.Damage)

    [<Fact>]
    let ``fifth slot equips a pellet shotgun without replacing the sniper`` () =
        let world =
            Sim.createTrainingWorld 19UL
            |> TestKit.advance 1L 1L InputButtons.Weapon5
            |> TestKit.advance 2L 24L InputButtons.None
        let beforeAmmo = world.Player.Slots[world.Player.Active].InMag
        let struct (fired, events) = Sim.step (input 25L InputButtons.Fire Vector2.Zero) world
        let shots =
            events
            |> List.filter (function ShotFired(Some(EntityId 1), _, _, "M1897 Trench Gun") -> true | _ -> false)
        let mutable rng = Rng.create 91UL
        let struct (_, pellets) = Weapons.step Tuning.TickDuration 0.0f Standing true false 0.0f &rng (Tuning.weaponSlot Tuning.m1897 5)
        Assert.Equal(9, world.Player.Active)
        Assert.Equal("M1897 Trench Gun", world.Player.Slots[world.Player.Active].Class.Name)
        Assert.Equal(1, shots.Length)
        Assert.Equal(8, pellets.Length)
        Assert.Equal(beforeAmmo - 1, fired.Player.Slots[fired.Player.Active].InMag)

    [<Fact>]
    let ``bolt action requires trigger release before another shot`` () =
        let _, events = TestKit.stepAll 1L 120L InputButtons.Fire (Sim.createTrainingWorld 23UL)
        let shots = events |> List.sumBy (function ShotFired(Some(EntityId 1), _, _, _) -> 1 | _ -> 0)
        Assert.Equal(1, shots)

    [<Fact>]
    let ``sustained automatic fire grows bloom`` () =
        let mutable rng = Rng.create 13UL
        let mutable slot = Tuning.weaponSlot Tuning.thompson 4
        let mutable firstShotBloom = 0.0f
        let mutable maxBloom = 0.0f
        let mutable shots = 0
        for _ in 1..120 do
            let struct (next, requests) = Weapons.step Tuning.TickDuration 0.0f Standing true false 0.0f &rng slot
            if requests.Length > 0 then
                if shots = 0 then firstShotBloom <- next.Bloom
                maxBloom <- max maxBloom next.Bloom
                shots <- shots + 1
            slot <- next
        Assert.True(shots >= 8, $"expected sustained fire, fired {shots}")
        Assert.True(maxBloom > firstShotBloom)

    [<Fact>]
    let ``firing while moving widens the spread`` () =
        let maxSpread speed =
            let mutable rng = Rng.create 17UL
            let mutable slot = Tuning.weaponSlot Tuning.m1911 4
            let mutable worst = 0.0f
            for _ in 1..120 do
                let struct (next, requests) = Weapons.step Tuning.TickDuration speed Standing true false 0.0f &rng slot
                for request in requests do
                    worst <- max worst (request.DirectionOffset.Length())
                slot <- next
            worst
        Assert.True(maxSpread 0.0f < maxSpread (Tuning.WalkSpeed * Tuning.SprintMultiplier))

    [<Fact>]
    let ``crouching and going prone tighten hipfire spread`` () =
        let maxSpread stance =
            let mutable rng = Rng.create 17UL
            let mutable slot = Tuning.weaponSlot Tuning.m1911 4
            let mutable worst = 0.0f
            for _ in 1..120 do
                let struct (next, requests) = Weapons.step Tuning.TickDuration 0.0f stance true false 0.0f &rng slot
                for request in requests do
                    worst <- max worst (request.DirectionOffset.Length())
                slot <- next
            worst
        Assert.True(maxSpread Crouched < maxSpread Standing)
        Assert.True(maxSpread Prone < maxSpread Crouched)

    [<Fact>]
    let ``jump follows gravity and lands on generated ground`` () =
        let initial = Sim.createTrainingWorld 29UL
        let jumped = TestKit.advance 1L 1L InputButtons.Jump initial
        Assert.True(jumped.Player.Position.Y > initial.Player.Position.Y)
        let world = TestKit.advance 2L 120L InputButtons.None jumped
        Assert.InRange(world.Player.Position.Y, 0.0f, 0.002f)
        Assert.Equal(0.0f, world.Player.Velocity.Y)

    [<Fact>]
    let ``controller preserves generated terrain step height`` () =
        let level =
            LevelDsl.level "Terrain step"
                [ LevelDsl.street 20.0f 12.0f Mud
                  LevelDsl.block (Vector3(0.0f, 0.125f, -1.0f)) (Vector3(4.0f, 0.25f, 2.0f)) Mud
                  LevelDsl.spawnSquad Allies 1 (Vector3(0.0f, 0.0f, 0.45f)) ]
            |> LevelCompile.compile
        let mutable player = { (Sim.createTrainingWorld 51UL).Player with Position = Vector3(0.0f, 0.0f, 0.45f) }
        let mutable highest = 0.0f
        for tick in 1L..30L do
            player <- Movement.step Tuning.TickDuration (input tick InputButtons.None Vector2.UnitY) level player
            highest <- max highest player.Position.Y
        Assert.InRange(highest, 0.249f, 0.251f)

    [<Fact>]
    let ``crouch jumping tucks the legs to clear higher ledges`` () =
        let level = LevelDsl.level "Jump range" [ LevelDsl.street 30.0f 10.0f Mud ] |> LevelCompile.compile
        let start = { (Sim.createTrainingWorld 51UL).Player with Position = Vector3.Zero; Velocity = Vector3.Zero }
        let apexOf holdCrouch =
            let mutable player = Movement.step Tuning.TickDuration (input 1L InputButtons.Jump Vector2.Zero) level start
            let mutable apex = player.Position.Y
            for tick in 2L..60L do
                let buttons = if holdCrouch then InputButtons.Crouch else InputButtons.None
                player <- Movement.step Tuning.TickDuration (input tick buttons Vector2.Zero) level player
                apex <- max apex player.Position.Y
            apex, player
        let plainApex, _ = apexOf false
        let tuckedApex, landed = apexOf true
        // Source-style air crouch: the feet gain the stand/crouch height delta.
        Assert.InRange(plainApex, 0.8f, 1.3f)
        Assert.True(tuckedApex > plainApex + 0.4f, $"tucked {tuckedApex} vs plain {plainApex}")
        // Still lands cleanly on the floor while holding crouch.
        Assert.InRange(landed.Position.Y, -0.01f, 0.05f)
        Assert.Equal(Crouched, landed.Stance)

    [<Fact>]
    let ``walking emits a generated-surface footstep event`` () =
        let world = Sim.createTrainingWorld 31UL
        let struct (_, events) = Sim.step (input 1L InputButtons.None Vector2.UnitY) world
        Assert.Contains(events, fun event -> match event with FootStep(_, Mud) -> true | _ -> false)

    [<Fact>]
    let ``an offline kill emits the same Kill event the feed reads online`` () =
        // The offline feed was the last consumer with no source: kills happened
        // in the sim but never became events, so nothing could render them.
        let world = Sim.createTrainingWorld 7UL
        // Stand an Axis soldier five metres dead ahead (yaw 0 faces -Z) rather
        // than hunting for one across the map: this is a test of the emission,
        // not of whether the player can win a firefight.
        let targetIndex = world.Soldiers |> Array.findIndex (fun soldier -> soldier.Team = Axis)
        let targetId = world.Soldiers[targetIndex].Id
        let soldiers = Array.copy world.Soldiers
        soldiers[targetIndex] <-
            { soldiers[targetIndex] with Position = world.Player.Position + Vector3(0.0f, 0.0f, -5.0f) }
        let aimed =
            { world with
                Player = { world.Player with Yaw = 0.0f; Pitch = 0.0f }
                Soldiers = soldiers }
        let mutable current = aimed
        let mutable kills = []
        for sequence in 1L .. 60L do
            let struct (next, events) = Sim.step (input sequence InputButtons.Fire Vector2.Zero) current
            current <- next
            kills <- kills @ (events |> List.filter (function Kill _ -> true | _ -> false))
        Assert.NotEmpty kills
        // Both directions emit: the AI shoots back, so pick out the player's
        // own kill rather than whichever landed first.
        let playerKill =
            kills
            |> List.tryPick (function
                | Kill(Some killer, victim, weapon, _) when killer = world.Player.Id -> Some(victim, weapon)
                | _ -> None)
        match playerKill with
        | Some(victim, weapon) ->
            Assert.Equal(targetId, victim)
            Assert.False(String.IsNullOrEmpty weapon)
            Assert.True(current.Soldiers |> Array.exists (fun soldier -> soldier.Id = victim && soldier.IsDead))
        | None -> failwith $"no kill credited to the player; got {kills}"

    [<Fact>]
    let ``every weapon keeps the number key its category table gave it`` () =
        // The old table listed slot indices into the offline twelve-weapon
        // array, which cannot describe a two-weapon online kit. Deriving the
        // category from the weapon instead has to land every gun on the same
        // key it always had, or muscle memory breaks.
        let expected =
            [ Tuning.kar98k, 0; Tuning.m1Garand, 0; Tuning.leeEnfield, 0
              Tuning.thompson, 1; Tuning.mp40, 1
              // A rifle that fires like an SMG, and sits with them.
              Tuning.stg44, 1
              Tuning.m1911, 2; Tuning.luger, 2
              Tuning.kar98kSniper, 3
              // Scoped beats full-auto: the FG42 is both.
              Tuning.fg42, 3
              Tuning.m1897, 4
              // Heavy beats full-auto too.
              Tuning.bar, 4 ]
        for weapon, category in expected do
            Assert.Equal(category, Tuning.categoryOf weapon)
        // Every online weapon lands on a real key.
        for weapon in Tuning.onlineWeapons do
            Assert.InRange(Tuning.categoryOf weapon, 0, 4)

    [<Fact>]
    let ``a two-weapon kit switches between primary and sidearm`` () =
        // The offline sandbox carries twelve; an online kit carries two. The
        // same selection code has to serve both.
        let world = Sim.createTrainingWorld 3UL
        let kit =
            { world.Player with
                Slots = [| Tuning.weaponSlot Tuning.kar98k 4; Tuning.weaponSlot Tuning.m1911 3 |]
                Active = 0 }
        Assert.Equal<int array>([| 0 |], Tuning.categorySlots kit.Slots 0)
        Assert.Equal<int array>([| 1 |], Tuning.categorySlots kit.Slots 2)
        // Key 3 is the pistol: the switch starts, locks the weapon clock, and
        // only lands when the 0.35 s raise finishes.
        let pressing = input 1L InputButtons.Weapon3 Vector2.Zero
        let switching, locked = Sim.stepWeaponSwitch pressing kit
        Assert.True locked
        Assert.Equal(0, switching.Active)
        Assert.Equal(0.0f, switching.Ads)
        let mutable current = switching
        for _ in 1 .. int (0.35f * float32 Tuning.TickRate) + 1 do
            let stepped, _ = Sim.stepWeaponSwitch (input 2L InputButtons.None Vector2.Zero) current
            current <- stepped
        Assert.Equal(1, current.Active)
        Assert.Equal("M1911", current.Slots[current.Active].Class.Name)
        // A key holding nothing is a no-op rather than an index crash.
        let untouched, lockedByEmpty = Sim.stepWeaponSwitch (input 3L InputButtons.Weapon5 Vector2.Zero) current
        Assert.False lockedByEmpty
        Assert.Equal(1, untouched.Active)

    [<Fact>]
    let ``the scroll wheel walks every carried weapon in slot order`` () =
        // Half-Life/Counter-Strike invnext: it steps through the inventory
        // rather than jumping by category, so it always reaches every gun even
        // when two share a key.
        let world = Sim.createTrainingWorld 5UL
        let kit =
            { world.Player with
                Slots =
                    [| Tuning.weaponSlot Tuning.kar98k 4
                       Tuning.weaponSlot Tuning.m1911 3
                       Tuning.weaponSlot Tuning.m1897 3 |]
                Active = 0 }
        // Complete a switch: the request only starts one, and it lands 0.35s on.
        let settle player button =
            let started, _ = Sim.stepWeaponSwitch (input 1L button Vector2.Zero) player
            let mutable current = started
            for _ in 1 .. int (0.35f * float32 Tuning.TickRate) + 1 do
                let stepped, _ = Sim.stepWeaponSwitch (input 2L InputButtons.None Vector2.Zero) current
                current <- stepped
            current
        let next = settle kit InputButtons.WeaponNext
        Assert.Equal(1, next.Active)
        let wrapped = settle (settle next InputButtons.WeaponNext) InputButtons.WeaponNext
        // 0 -> 1 -> 2 -> 0: it wraps rather than stopping at the end.
        Assert.Equal(0, wrapped.Active)
        // And backwards from the first slot wraps to the last.
        Assert.Equal(2, (settle wrapped InputButtons.WeaponPrev).Active)
        // Nothing to cycle with a single weapon.
        let lone = { kit with Slots = [| Tuning.weaponSlot Tuning.kar98k 4 |]; Active = 0 }
        let _, locked = Sim.stepWeaponSwitch (input 1L InputButtons.WeaponNext Vector2.Zero) lone
        Assert.False locked

    let private makePlayer id team : NetworkPlayer =
        { Id = EntityId id; Name = string id; Team = team; Position = Vector3.Zero; Velocity = Vector3.Zero
          Yaw = 0.0f; Pitch = 0.0f; Stance = Standing; Sprinting = false; Ads = 0.0f
          Health = Units.health 100.0f; RegenIn = Units.seconds 0.0f
          Slots = [| Tuning.weaponSlot Tuning.kar98k 2; Tuning.weaponSlot (Tuning.sidearm team) 2 |]; Active = 0; RequestedWeapon = None
          Grenade = GrenadeIdle 3; Connected = true; Ready = true; Alive = true; RespawnIn = Units.seconds 0.0f; SpawnProtection = Units.seconds 0.0f
          Kills = 0; Deaths = 0; Streak = 0; BestStreak = 0; LastInputSequence = 0L }

    [<Fact>]
    let ``friendly kill does not score in team deathmatch`` () =
        let state =
            { Multiplayer.create TeamDeathmatch with
                Players = [ EntityId 1, makePlayer 1 Allies; EntityId 2, makePlayer 2 Allies ] |> Map.ofList }
        let result = Multiplayer.recordKill (EntityId 1) (EntityId 2) state
        Assert.Equal(0, result.AlliesScore)
        Assert.True(result.Players[EntityId 2].Alive)

    [<Fact>]
    let ``team deathmatch hostile kill scores and marks victim dead`` () =
        let state =
            { Multiplayer.create TeamDeathmatch with
                Players = [ EntityId 1, makePlayer 1 Allies; EntityId 2, makePlayer 2 Axis ] |> Map.ofList }
        let result = Multiplayer.recordKill (EntityId 1) (EntityId 2) state
        Assert.Equal(1, result.AlliesScore)
        Assert.Equal(1, result.Players[EntityId 1].Kills)
        Assert.False(result.Players[EntityId 2].Alive)

    [<Fact>]
    let ``consecutive kills build a streak that dying resets`` () =
        let state =
            { Multiplayer.create TeamDeathmatch with
                Players = [ EntityId 1, makePlayer 1 Allies; EntityId 2, makePlayer 2 Axis; EntityId 3, makePlayer 3 Axis ] |> Map.ofList }
        let onARoll =
            state
            |> Multiplayer.recordKill (EntityId 1) (EntityId 2)
            |> Multiplayer.recordKill (EntityId 1) (EntityId 3)
        Assert.Equal(2, onARoll.Players[EntityId 1].Streak)
        Assert.Equal(2, onARoll.Players[EntityId 1].BestStreak)
        // The reset lives in markDead, the one place a player is marked dead,
        // so a grenade suicide ends a streak just like being fragged does.
        let died = Multiplayer.markDead (EntityId 1) onARoll
        Assert.Equal(0, died.Players[EntityId 1].Streak)
        Assert.Equal(2, died.Players[EntityId 1].BestStreak)
        let fragged = Multiplayer.recordKill (EntityId 2) (EntityId 1) onARoll
        Assert.Equal(0, fragged.Players[EntityId 1].Streak)
        Assert.Equal(2, fragged.Players[EntityId 1].BestStreak)
        // A grenade thrown before the owner was shot detonates later in the
        // same tick: the kill still counts, but a corpse has no streak.
        let posthumous = Multiplayer.recordKill (EntityId 1) (EntityId 3) (Multiplayer.markDead (EntityId 1) onARoll)
        Assert.Equal(0, posthumous.Players[EntityId 1].Streak)
        Assert.Equal(2, posthumous.Players[EntityId 1].BestStreak)
        Assert.Equal(3, posthumous.Players[EntityId 1].Kills)

    [<Fact>]
    let ``dying cancels an interrupted reload`` () =
        // Nothing steps a dead player's weapon, so a live Reloading timer would
        // freeze the replicated HUD's reload bar for the whole respawn wait.
        let carried = makePlayer 1 Allies
        let reloading =
            { carried with Slots = [| { carried.Slots[0] with State = Reloading(Units.seconds 1.4f) }; carried.Slots[1] |] }
        let state = { Multiplayer.create TeamDeathmatch with Players = Map.ofList [ EntityId 1, reloading ] }
        let dead = (Multiplayer.markDead (EntityId 1) state).Players[EntityId 1]
        Assert.All(dead.Slots, fun slot -> Assert.Equal(Ready, slot.State))

    [<Fact>]
    let ``names are trimmed and limited to 24 unicode scalars`` () =
        let name = "  " + String.replicate 30 "⚙" + "  "
        let sanitized = Multiplayer.sanitizeName name
        Assert.Equal(24, sanitized.EnumerateRunes() |> Seq.length)

    [<Fact>]
    let ``sanitized text is trimmed truncated and stripped of control characters`` () =
        // Chat lands unescaped in every other player's HUD and the tab doubles
        // as the chat wire separator, so no control scalar may survive.
        Assert.Equal("fragout", Multiplayer.sanitizeText 120 "  frag\tout\n  ")
        Assert.Equal("", Multiplayer.sanitizeText 120 "\r\n ")
        Assert.Equal("", Multiplayer.sanitizeText 120 null)
        // Control characters are dropped before the cap, so they cannot eat the
        // budget a real message needs.
        Assert.Equal("abcde", Multiplayer.sanitizeText 5 (String.replicate 10 "\u0007" + "abcdefgh"))
        Assert.Equal(6, (Multiplayer.sanitizeText 6 (String.replicate 30 "⚙")).EnumerateRunes() |> Seq.length)

    [<Fact>]
    let ``every online weapon has sane firing tuning`` () =
        // Weapons.step divides by RoundsPerMin and the arsenal ships whatever
        // Tuning contains — a typo'd zero must fail here, not at the range.
        for weapon in Tuning.onlineWeapons do
            Assert.True(weapon.RoundsPerMin > 0.0f, weapon.Name)
            Assert.True(weapon.MagSize > 0, weapon.Name)
            Assert.True(weapon.Pellets >= 1, weapon.Name)
            Assert.True(weapon.ReloadTime > Units.seconds 0.0f, weapon.Name)
            Assert.True(Units.raw weapon.Damage > 0.0f, weapon.Name)
            Assert.True(weapon.AdsSpread <= weapon.HipSpread, weapon.Name)
