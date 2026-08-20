namespace Ironsight.Shell

#nowarn "9"

open System
open System.Diagnostics
open System.Numerics
open Microsoft.FSharp.NativeInterop
open Ironsight
open Ironsight.ProcGen
open Silk.NET.OpenGL

[<RequireQualifiedAccess>]
module ViewmodelAnimation =
    let private ease value =
        let value = MathEx.clamp01 value
        value * value * (3.0f - 2.0f * value)

    let private lerpScalar a b amount = a + (b - a) * amount

    let katanaYaw isKatana progress attack =
        match progress, attack with
        | Some value, Some KatanaSweep when value < 0.72f ->
            // Positive yaw places the -Z blade tip left on screen; negative
            // yaw places it right. This is one uninterrupted left-to-right cut.
            lerpScalar 1.35f -1.35f (ease (value / 0.72f))
        | Some value, Some KatanaSweep ->
            lerpScalar -1.35f 1.05f (ease ((value - 0.72f) / 0.28f))
        | Some value, Some KatanaOverhead when value < 0.72f -> 0.0f
        | Some value, Some KatanaOverhead ->
            lerpScalar 0.0f 1.05f (ease ((value - 0.72f) / 0.28f))
        | None, _ when isKatana -> 1.05f
        | _ -> 0.0f

    let katanaPitch progress attack =
        match progress, attack with
        | Some value, Some KatanaOverhead when value < 0.72f ->
            // Positive pitch raises the -Z blade tip; negative lowers it.
            // Starting raised avoids showing a backwards/upward attack phase.
            lerpScalar 1.20f -0.70f (ease (value / 0.72f))
        | Some value, Some KatanaOverhead ->
            lerpScalar -0.70f 0.0f (ease ((value - 0.72f) / 0.28f))
        | _ -> 0.0f

/// A recent lethal-looking event kept around briefly so a soldier who dies
/// this frame can be ragdolled away from what killed them. Radius > 0 marks a
/// radial (explosion) source; otherwise Push is the directional kick.
type private KillImpulse =
    { Expires: int64
      Position: Vector3
      Push: Vector3
      Radius: float32 }

type Renderer(gl: GL) =
    let hud = new Hud(gl)
    let particles = new Particles(gl)
    let mutable width = 1280
    let mutable height = 720
    let mutable logicalWidth = 1280
    let mutable logicalHeight = 720
    let mutable vao = 0u
    let mutable vertexBuffer = 0u
    let mutable indexBuffer = 0u
    let mutable program = 0u
    let mutable skyProgram = 0u
    let mutable shadowProgram = 0u
    let mutable shadowFramebuffer = 0u
    let mutable shadowTexture = 0u
    let mutable indexCount = 0u
    let mutable soldierVao = 0u
    let mutable soldierVertexBuffer = 0u
    let mutable soldierIndexBuffer = 0u
    let mutable soldierIndexCount = 0u
    let mutable gunVao = 0u
    let mutable gunVertexBuffer = 0u
    let mutable gunIndexBuffer = 0u
    let mutable gunIndexCount = 0u
    let mutable loadedGun = ""
    // Rotor state for guns whose barrels turn: the angle currently uploaded,
    // and the rate it is spinning at. Spin-up and spin-down are what sell a
    // minigun, so the rate eases toward its target rather than snapping.
    let mutable gunSpin = 0.0f
    let mutable gunSpinRate = 0.0f
    let mutable lastSpinStamp = 0L
    let mutable loadedLevel = ""
    let mutable loadedLevelRevision = -1
    let mutable settings = Settings.defaults
    let mutable decals: Vector4 list = []
    let mutable recoil = 0.0f
    let mutable recoilVelocity = 0.0f
    let mutable sprintBlend = 0.0f
    let mutable viewSway = Vector2.Zero
    let mutable lastView = Vector2.Zero
    let mutable deathWatching = false
    let mutable deathStarted = Stopwatch.GetTimestamp()
    let ragdolls = Ragdoll.System()
    let mutable killImpulses: KillImpulse list = []
    let mutable ragdollClock = Stopwatch.GetTimestamp()

    let createLinkedProgram = GlUtil.createProgram gl
    let setMatrix = GlUtil.setMatrix gl
    let uniform1f (program: uint32) (name: string) (value: float32) = gl.Uniform1(gl.GetUniformLocation(program, name), value)
    let uniform1i (program: uint32) (name: string) (value: int) = gl.Uniform1(gl.GetUniformLocation(program, name), value)
    let uniform3 (program: uint32) (name: string) (value: Vector3) = gl.Uniform3(gl.GetUniformLocation(program, name), value.X, value.Y, value.Z)

    /// Bind the VAO's vertex/index buffers and upload both arrays.
    let uploadMesh vao vb ib (vertices: float32[]) (indices: uint32[]) usage =
        gl.BindVertexArray vao
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vb)
        GlUtil.upload gl BufferTargetARB.ArrayBuffer vertices usage
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ib)
        GlUtil.upload gl BufferTargetARB.ElementArrayBuffer indices usage

    let createShadowMap () =
        shadowTexture <- gl.GenTexture()
        gl.BindTexture(TextureTarget.Texture2D, shadowTexture)
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, 2048u, 2048u, 0, PixelFormat.DepthComponent, PixelType.Float, NativePtr.nullPtr<byte> |> NativePtr.toVoidPtr)
        GlUtil.clampLinearTexture gl TextureTarget.Texture2D
        shadowFramebuffer <- gl.GenFramebuffer()
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, shadowFramebuffer)
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, shadowTexture, 0)
        gl.DrawBuffer DrawBufferMode.None
        gl.ReadBuffer ReadBufferMode.None
        let status = gl.CheckFramebufferStatus FramebufferTarget.Framebuffer
        if status <> GLEnum.FramebufferComplete then invalidOp $"Shadow framebuffer is incomplete: {status}"
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0u)

    let deleteMesh () =
        if vao <> 0u then gl.DeleteVertexArray vao
        if vertexBuffer <> 0u then gl.DeleteBuffer vertexBuffer
        if indexBuffer <> 0u then gl.DeleteBuffer indexBuffer
        vao <- 0u
        vertexBuffer <- 0u
        indexBuffer <- 0u

    let uploadLevel (level: Level) =
        deleteMesh ()
        let vertices = GlUtil.flattenVertices level.Vertices
        let struct (v, vb, ib) = GlUtil.createMeshBuffers gl
        vao <- v
        vertexBuffer <- vb
        indexBuffer <- ib
        uploadMesh vao vertexBuffer indexBuffer vertices level.Indices BufferUsageARB.StaticDraw
        gl.BindVertexArray 0u
        indexCount <- uint32 level.Indices.Length
        loadedLevel <- level.Name
        loadedLevelRevision <- level.Revision

    let localPlayerMarks (world: World) =
        world.PersistentMarks
        |> Array.filter (function
            | PaintSplat(_, _, _, Some id, _)
            | StuckDart(_, _, Some id, _)
            | WetPatch(_, _, Some id, _, _, _)
            | StuckNail(_, _, Some id, _)
            | StuckArrow(_, _, Some id, _) -> id = world.Player.Id
            | _ -> false)

    // Wet-patch lifetimes tick every frame, but the viewmodel only needs a new
    // mesh when a mark appears, disappears, changes colour, or becomes wetter.
    let localMarkKey marks =
        marks
        |> Array.map (function
            | PaintSplat(_, _, color, _, localOffset) -> hash (0, color, localOffset)
            | StuckDart(_, _, _, localOffset) -> hash (1, localOffset)
            | WetPatch(_, _, _, localOffset, _, saturation) -> hash (2, localOffset, saturation)
            | StuckNail(_, _, _, localOffset) -> hash (3, localOffset)
            | EmbeddedHarpoon(tip, direction, skewered) -> hash (4, tip, direction, skewered)
            | StuckArrow(_, direction, _, localOffset) -> hash (5, direction, localOffset))
        |> hash

    let harpoonLoadPose (slot: WeaponSlot) =
        if slot.Class.Mechanism <> Harpoon then 1.0f
        else
            match slot.State with
            | Reloading remaining ->
                1.0f - MathEx.clamp01 (Units.raw remaining / max 0.01f (Units.raw slot.Class.ReloadTime))
            | _ when slot.InMag > 0 -> 1.0f
            | _ -> 0.0f

    let weaponMeshKey name paintKey localMarks mechanismPose =
        let poseKey =
            if name = "Harpoon Gun" || name = "Bow" || name = "M134 Minigun" then
                int (MathF.Round(mechanismPose * 1000.0f))
            else -1
        $"{name}:{paintKey}:{localMarkKey localMarks}:{poseKey}"

    let uploadGun name (world: World) mechanismPose =
        let localMarks = localPlayerMarks world
        let sleevePositions =
            [| Vector3(0.31f, -0.48f, 0.19f); Vector3(-0.34f, -0.45f, 0.05f)
               Vector3(0.22f, -0.31f, -0.04f); Vector3(-0.23f, -0.29f, -0.25f) |]
        let markMesh =
            localMarks
            |> Array.mapi (fun index mark ->
                let position = sleevePositions[index % sleevePositions.Length]
                match mark with
                | PaintSplat(_, _, color, _, _) ->
                    Guns.paintballMesh color
                    |> MeshGen.scale (Vector3(1.3f, 0.45f, 1.3f))
                    |> MeshGen.translate position
                | StuckDart _ ->
                    Guns.dartMesh
                    |> MeshGen.rotateX (MathF.PI * 0.5f)
                    |> MeshGen.translate position
                | WetPatch(_, _, _, _, _, saturation) ->
                    let size = 0.5f + saturation
                    Guns.splatMesh WetDark
                    |> MeshGen.scale (Vector3(size, 0.35f, size))
                    |> MeshGen.translate position
                | StuckNail _ ->
                    Guns.nailMesh
                    |> MeshGen.rotateX (MathF.PI * 0.5f)
                    |> MeshGen.translate position
                | StuckArrow _ ->
                    Guns.arrowMesh
                    |> MeshGen.scale (Vector3(0.55f, 0.55f, 0.55f))
                    |> MeshGen.rotateX (MathF.PI * 0.5f)
                    |> MeshGen.translate position
                | EmbeddedHarpoon _ -> MeshGen.empty)
            |> MeshGen.union
        let baseMesh =
            let mesh = Guns.forWeaponPose name mechanismPose
            if name = "Paintball Marker" then
                { mesh with
                    Vertices =
                        mesh.Vertices
                        |> Array.map (fun vertex ->
                            if vertex.MaterialId = Materials.id PaintBlue then
                                { vertex with MaterialId = Materials.id world.PaintColor }
                            else vertex) }
            else mesh
        let mesh = MeshGen.union [| baseMesh; markMesh |]
        let vertices = GlUtil.flattenVertices mesh.Vertices
        uploadMesh gunVao gunVertexBuffer gunIndexBuffer vertices mesh.Indices BufferUsageARB.DynamicDraw
        gunIndexCount <- uint32 mesh.Indices.Length
        let paintKey = if name = "Paintball Marker" then string world.PaintColor else ""
        loadedGun <- weaponMeshKey name paintKey localMarks mechanismPose

    /// Spawn ragdolls for newly dead soldiers, seeded from whatever recent
    /// event plausibly killed them, then advance the simulation on wall-clock
    /// time (same convention as the first-person death fall).
    let updateRagdolls (world: World) =
        let now = Stopwatch.GetTimestamp()
        killImpulses <- killImpulses |> List.filter (fun impulse -> impulse.Expires > now)
        ragdolls.Prune world.Soldiers
        for soldier in world.Soldiers do
            match soldier.Behavior with
            | Dying _ | DyingHeadshot _ when not (ragdolls.Contains soldier.Id) ->
                let chest = soldier.Position + Vector3(0.0f, 1.3f, 0.0f)
                let candidate =
                    killImpulses
                    |> List.map (fun impulse ->
                        if impulse.Radius > 0.0f then
                            let reach = impulse.Radius + 0.8f
                            let distance = Vector3.Distance(chest, impulse.Position)
                            if distance < reach then
                                let away = chest - impulse.Position + Vector3(0.0f, 0.5f, 0.0f)
                                let direction = if away.LengthSquared() < 0.001f then Vector3.UnitY else Vector3.Normalize away
                                direction * (10.0f * (1.0f - distance / reach))
                            else Vector3.Zero
                        elif Vector3.Distance(chest, impulse.Position) < 1.6f then impulse.Push
                        else Vector3.Zero)
                    |> List.fold (fun (best: Vector3) (push: Vector3) -> if push.LengthSquared() > best.LengthSquared() then push else best) Vector3.Zero
                let impulse =
                    if candidate.LengthSquared() < 0.05f then MathEx.yawForward soldier.Facing * 1.5f
                    else
                        // A touch of lift makes the body get thrown rather than
                        // just shoved: knocked off its feet before it crumples.
                        candidate + Vector3.UnitY * (candidate.Length() * 0.25f)
                ragdolls.Spawn(soldier.Id, Humanoid.worldSkeleton soldier, impulse, ?cut = Map.tryFind soldier.Id world.Dismemberments)
            | _ -> ()
        let dt = float32 (Stopwatch.GetElapsedTime ragdollClock).TotalSeconds
        ragdollClock <- now
        let pinsAt tip direction skewered =
            let forward = MathEx.normalizedOrZero direction
            skewered
            |> List.map (fun attachment -> attachment.Victim, tip - forward * attachment.DistanceBehindTip)
        let flyingPins =
            world.SpecialProjectiles
            |> Array.collect (fun projectile ->
                match projectile.Kind with
                | HarpoonRound skewered -> pinsAt projectile.Position projectile.Velocity skewered |> List.toArray
                | _ -> [||])
        let embeddedPins =
            world.PersistentMarks
            |> Array.collect (function
                | EmbeddedHarpoon(tip, direction, skewered) -> pinsAt tip direction skewered |> List.toArray
                | _ -> [||])
        let harpoonPins = Array.append flyingPins embeddedPins |> Map.ofArray
        ragdolls.Step(dt, world.Level, harpoonPins)

    let uploadActors (world: World) =
        let visibleSoldiers =
            world.Soldiers
            |> Array.filter (fun soldier ->
                let distanceSquared = Vector3.DistanceSquared(soldier.Position, world.Player.Position)
                distanceSquared > 0.85f * 0.85f && distanceSquared < 85.0f * 85.0f)
        let hasRagdoll (soldier: Soldier) =
            match soldier.Behavior with
            | Dying _ | DyingHeadshot _ -> ragdolls.Contains soldier.Id
            | _ -> false
        let soldierVertices, soldierIndices =
            visibleSoldiers |> Array.filter (hasRagdoll >> not) |> Humanoid.mesh
        let ragdollMesh =
            visibleSoldiers
            |> Array.choose (fun soldier ->
                if hasRagdoll soldier then
                    ragdolls.TryGet soldier.Id
                    |> Option.map (fun skeleton ->
                        match ragdolls.TryGetCut soldier.Id with
                        | Some(descriptor, proximal, distal) -> Humanoid.poseFromSkeletonCut soldier skeleton descriptor proximal distal
                        | None -> Humanoid.poseFromSkeleton soldier skeleton)
                else None)
            |> MeshGen.union
        let grenadeMesh =
            world.Grenades
            |> Array.map (fun grenade ->
                MeshGen.box (Vector3(0.14f, 0.18f, 0.14f)) Metal
                |> MeshGen.translate grenade.Position)
            |> MeshGen.union
        let oriented direction mesh =
            mesh
            |> MeshGen.transform (Matrix4x4.CreateFromQuaternion(MathEx.rotationFromZ direction))
        let projectileMesh =
            world.SpecialProjectiles
            |> Array.map (fun projectile ->
                match projectile.Kind with
                | PaintBall color -> Guns.paintballMesh color |> MeshGen.translate projectile.Position
                | NerfDart ->
                    Guns.dartMesh
                    |> oriented -projectile.Velocity
                    |> MeshGen.translate projectile.Position
                | BazookaRocket ->
                    Guns.rocketMesh
                    |> oriented -projectile.Velocity
                    |> MeshGen.translate projectile.Position
                | WaterDroplet ->
                    Guns.waterDropletMesh
                    |> oriented -projectile.Velocity
                    |> MeshGen.translate projectile.Position
                | NailRound ->
                    Guns.nailMesh
                    |> oriented -projectile.Velocity
                    |> MeshGen.translate projectile.Position
                | HarpoonRound _ ->
                    Guns.harpoonMesh
                    |> oriented -projectile.Velocity
                    |> MeshGen.translate projectile.Position
                | ArrowRound _ ->
                    Guns.arrowMesh
                    |> oriented -projectile.Velocity
                    |> MeshGen.translate projectile.Position)
            |> MeshGen.union
        let skeletonPoints (skeleton: Skeleton) =
            [| skeleton.Pelvis; skeleton.Chest; skeleton.Neck; skeleton.Head
               skeleton.LeftHip; skeleton.RightHip; skeleton.LeftKnee; skeleton.RightKnee
               skeleton.LeftAnkle; skeleton.RightAnkle; skeleton.LeftShoulder; skeleton.RightShoulder
               skeleton.LeftElbow; skeleton.RightElbow; skeleton.LeftHand; skeleton.RightHand |]
        let resolveMarkPosition fallback target localOffset =
            match target with
            | Some id when id = world.Player.Id -> world.Player.Position + localOffset
            | Some id ->
                world.Soldiers
                |> Array.tryFind (fun soldier -> soldier.Id = id)
                |> Option.map (fun soldier ->
                    let anchor = soldier.Position + localOffset
                    match ragdolls.TryGet soldier.Id with
                    | Some ragdoll ->
                        let initial = Humanoid.worldSkeleton soldier |> skeletonPoints
                        let current = skeletonPoints ragdoll
                        let nearest = initial |> Array.mapi (fun index point -> index, Vector3.DistanceSquared(point, anchor)) |> Array.minBy snd |> fst
                        current[nearest] + (anchor - initial[nearest])
                    | None -> anchor)
                |> Option.defaultValue fallback
            | None -> fallback
        let markMesh =
            world.PersistentMarks
            |> Array.map (function
                | PaintSplat(position, normal, color, target, localOffset) ->
                    Guns.splatMesh color
                    |> oriented normal
                    |> MeshGen.translate (resolveMarkPosition position target localOffset)
                | StuckDart(position, normal, target, localOffset) ->
                    Guns.dartMesh
                    |> oriented normal
                    |> MeshGen.translate (resolveMarkPosition position target localOffset + normal * 0.07f)
                | WetPatch(position, normal, target, localOffset, remaining, saturation) ->
                    let fade = MathEx.clamp01 (Units.raw remaining / Units.raw SpecialProjectiles.WetDuration)
                    let size = 0.65f + saturation * 1.5f
                    Guns.splatMesh WetDark
                    |> MeshGen.scale (Vector3(size * fade, size * fade, 0.7f))
                    |> oriented normal
                    |> MeshGen.translate (resolveMarkPosition position target localOffset)
                | StuckNail(position, normal, target, localOffset) ->
                    Guns.nailMesh
                    |> oriented normal
                    |> MeshGen.translate (resolveMarkPosition position target localOffset + normal * 0.07f)
                | EmbeddedHarpoon(tip, direction, _) ->
                    Guns.harpoonMesh
                    |> oriented -direction
                    |> MeshGen.translate tip
                | StuckArrow(position, direction, target, localOffset) ->
                    Guns.arrowMesh
                    |> oriented -direction
                    |> MeshGen.translate (resolveMarkPosition position target localOffset))
            |> MeshGen.union
        let ragdollOffset = uint32 soldierVertices.Length
        let grenadeOffset = ragdollOffset + uint32 ragdollMesh.Vertices.Length
        let projectileOffset = grenadeOffset + uint32 grenadeMesh.Vertices.Length
        let markOffset = projectileOffset + uint32 projectileMesh.Vertices.Length
        let meshVertices = Array.concat [ soldierVertices; ragdollMesh.Vertices; grenadeMesh.Vertices; projectileMesh.Vertices; markMesh.Vertices ]
        let meshIndices =
            Array.concat
                [ soldierIndices
                  ragdollMesh.Indices |> Array.map ((+) ragdollOffset)
                  grenadeMesh.Indices |> Array.map ((+) grenadeOffset)
                  projectileMesh.Indices |> Array.map ((+) projectileOffset)
                  markMesh.Indices |> Array.map ((+) markOffset) ]
        soldierIndexCount <- uint32 meshIndices.Length
        if meshIndices.Length > 0 then
            let vertices = GlUtil.flattenVertices meshVertices
            uploadMesh soldierVao soldierVertexBuffer soldierIndexBuffer vertices meshIndices BufferUsageARB.DynamicDraw

    let cameraMatrices (player: Player) (deathFall: float32) =
        // Death fall: the camera tips forward, slides a little in the facing
        // direction, and drops to ground level over the fall duration.
        let eased = deathFall * deathFall * (3.0f - 2.0f * deathFall)
        let stanceEye = Ballistics.eyeHeight player.Stance
        let eyeHeight = stanceEye + (0.15f - stanceEye) * eased
        let pitch = player.Pitch + recoil * 0.18f - eased * 0.95f
        let eye =
            player.Position
            + Vector3.UnitY * eyeHeight
            + MathEx.yawForward player.Yaw * (0.35f * eased)
        let forward = Ballistics.directionFromAngles player.Yaw pitch Vector2.Zero
        let view = Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitY)
        let scoped = player.Slots[player.Active].Class.Kind = SniperRifle
        let adsFov = if scoped then 20.0f else 40.0f
        let fieldOfView = (settings.Fov + (adsFov - settings.Fov) * player.Ads) * MathF.PI / 180.0f
        let projection = Matrix4x4.CreatePerspectiveFieldOfView(fieldOfView, float32 width / float32 (max 1 height), 0.05f, 120.0f)
        // System.Numerics stores row-vector matrices in row-major memory. OpenGL
        // reads that same memory as column-major, which supplies the required
        // transpose automatically for GLSL's matrix * column-vector convention.
        eye, view * projection, fieldOfView, pitch

    let lightMatrix =
        let view = Matrix4x4.CreateLookAt(Vector3(-38.0f, 58.0f, 34.0f), Vector3.Zero, Vector3.UnitY)
        let projection = Matrix4x4.CreateOrthographic(92.0f, 92.0f, 1.0f, 150.0f)
        view * projection

    do
        gl.Enable EnableCap.DepthTest
        gl.Enable EnableCap.CullFace
        gl.CullFace TriangleFace.Back
        program <- createLinkedProgram Shaders.levelVertex Shaders.levelFragment
        skyProgram <- createLinkedProgram Shaders.skyVertex Shaders.skyFragment
        shadowProgram <- createLinkedProgram Shaders.shadowVertex Shaders.shadowFragment
        createShadowMap ()
        let struct (sv, svb, sib) = GlUtil.createMeshBuffers gl
        soldierVao <- sv
        soldierVertexBuffer <- svb
        soldierIndexBuffer <- sib
        let struct (gv, gvb, gib) = GlUtil.createMeshBuffers gl
        gunVao <- gv
        gunVertexBuffer <- gvb
        gunIndexBuffer <- gib

    member _.Render(world: World, hudInfo: HudInfo) =
        // Re-upload when the script rebuilds the level (OpenPath) or the map changes.
        if loadedLevel <> world.Level.Name || loadedLevelRevision <> world.Level.Revision then uploadLevel world.Level
        // Watch the player's health and time the first-person fall. Wall-clock
        // timing keeps the collapse consistent regardless of tick cadence.
        if world.Player.IsDead then
            if not deathWatching then
                deathWatching <- true
                deathStarted <- Stopwatch.GetTimestamp()
        else
            deathWatching <- false
        let deathFall =
            if deathWatching then
                Math.Clamp(float32 (Stopwatch.GetElapsedTime(deathStarted).TotalSeconds) / 0.7f, 0.0f, 1.0f)
            else 0.0f
        gl.ClearColor(0.54f, 0.61f, 0.64f, 1.0f)
        if indexCount > 0u then
            let eye, viewProjection, fieldOfView, cameraPitch = cameraMatrices world.Player deathFall
            updateRagdolls world
            uploadActors world
            let noOffset = NativePtr.nullPtr<byte> |> NativePtr.toVoidPtr
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, shadowFramebuffer)
            gl.Viewport(0, 0, 2048u, 2048u)
            gl.Clear ClearBufferMask.DepthBufferBit
            gl.CullFace TriangleFace.Front
            gl.UseProgram shadowProgram
            setMatrix shadowProgram "uLightViewProjection" lightMatrix
            gl.BindVertexArray vao
            gl.DrawElements(PrimitiveType.Triangles, indexCount, DrawElementsType.UnsignedInt, noOffset)
            if soldierIndexCount > 0u then
                gl.BindVertexArray soldierVao
                gl.DrawElements(PrimitiveType.Triangles, soldierIndexCount, DrawElementsType.UnsignedInt, noOffset)
            gl.CullFace TriangleFace.Back
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0u)
            gl.Viewport(0, 0, uint32 width, uint32 height)
            gl.Clear(ClearBufferMask.ColorBufferBit ||| ClearBufferMask.DepthBufferBit)
            gl.Disable EnableCap.DepthTest
            gl.Disable EnableCap.CullFace
            gl.UseProgram skyProgram
            uniform1f skyProgram "uYaw" world.Player.Yaw
            uniform1f skyProgram "uPitch" cameraPitch
            uniform1f skyProgram "uAspect" (float32 width / float32 (max 1 height))
            uniform1f skyProgram "uTanHalfFov" (MathF.Tan(fieldOfView * 0.5f))
            let sky = SkyPalette.forLevel world.Level.Name
            uniform3 skyProgram "uSkyLow" sky.Low
            uniform3 skyProgram "uSkyHigh" sky.High
            uniform3 skyProgram "uSkyCloud" sky.Cloud
            uniform3 skyProgram "uSkyRidge" sky.Ridge
            uniform1f skyProgram "uSkyCloudAmount" sky.CloudAmount
            uniform1f skyProgram "uSkyHaze" sky.Haze
            gl.BindVertexArray vao
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3u)
            gl.Enable EnableCap.CullFace
            gl.Enable EnableCap.DepthTest
            gl.UseProgram program
            uniform1i program "uViewmodel" 0
            uniform1f program "uContrast" settings.Contrast
            setMatrix program "uViewProjection" viewProjection
            setMatrix program "uLightViewProjection" lightMatrix
            uniform3 program "uCamera" eye
            gl.ActiveTexture TextureUnit.Texture0
            gl.BindTexture(TextureTarget.Texture2D, shadowTexture)
            uniform1i program "uShadowMap" 0
            // HandleEvents already caps decals at the 16 uImpacts slots.
            uniform1i program "uImpactCount" decals.Length
            decals
            |> List.iteri (fun index decal ->
                gl.Uniform4(gl.GetUniformLocation(program, $"uImpacts[{index}]"), decal.X, decal.Y, decal.Z, decal.W))
            gl.BindVertexArray vao
            gl.DrawElements(PrimitiveType.Triangles, indexCount, DrawElementsType.UnsignedInt, noOffset)
            if soldierIndexCount > 0u then
                gl.BindVertexArray soldierVao
                gl.DrawElements(PrimitiveType.Triangles, soldierIndexCount, DrawElementsType.UnsignedInt, noOffset)
            gl.BindVertexArray 0u
            if hudInfo.DebugView then
                // Wallhack overlay: soldiers redrawn as wireframes with depth
                // testing off (visible through walls), the level as wireframe
                // with depth on so it hugs the real surfaces.
                gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line)
                if soldierIndexCount > 0u then
                    gl.Disable EnableCap.DepthTest
                    gl.BindVertexArray soldierVao
                    gl.DrawElements(PrimitiveType.Triangles, soldierIndexCount, DrawElementsType.UnsignedInt, noOffset)
                    gl.Enable EnableCap.DepthTest
                gl.BindVertexArray vao
                gl.DrawElements(PrimitiveType.Triangles, indexCount, DrawElementsType.UnsignedInt, noOffset)
                gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill)
                gl.BindVertexArray 0u
                // Line-of-sight web: every living soldier's eye to the local
                // player's eye; green when the trace is clear, red when a wall
                // is in the way.
                let playerEye = Ballistics.playerEyeOrigin world.Player
                for soldier in world.Soldiers do
                    if soldier.IsAlive then
                        let soldierEye = soldier.Position + Vector3(0.0f, 1.55f, 0.0f)
                        let clear = Ballistics.lineOfSight soldierEye playerEye world.Level
                        let color =
                            if clear then Vector4(0.20f, 1.0f, 0.30f, 0.85f)
                            else Vector4(1.0f, 0.16f, 0.10f, 0.45f)
                        particles.SubmitDebugLine(soldierEye, playerEye, color)
                        // Aim ray: where the soldier is actually pointing,
                        // drawn out to the horizon so sightlines read at range.
                        let aim = Ballistics.directionFromAngles soldier.Facing 0.0f Vector2.Zero
                        particles.SubmitDebugLine(soldierEye, soldierEye + aim * 200.0f, Vector4(0.35f, 0.75f, 1.0f, 0.6f))
                        // Orientation gizmo at the feet: X red, Y green, Z blue
                        // in the soldier's local frame (Z = facing).
                        let feet = soldier.Position
                        particles.SubmitDebugLine(feet, feet + MathEx.yawRight soldier.Facing * 0.6f, Vector4(1.0f, 0.15f, 0.15f, 0.95f))
                        particles.SubmitDebugLine(feet, feet + Vector3.UnitY * 0.6f, Vector4(0.15f, 1.0f, 0.15f, 0.95f))
                        particles.SubmitDebugLine(feet, feet + MathEx.yawForward soldier.Facing * 0.6f, Vector4(0.25f, 0.4f, 1.0f, 0.95f))
                        // Pose-matched melee proxies and their eligible sever
                        // bands. F3 therefore exposes exactly what the server's
                        // shared resolver sees instead of an unrelated bbox.
                        let target =
                            { Id = soldier.Id
                              Position = soldier.Position
                              Yaw = soldier.Facing
                              Stance = Anatomy.effectiveStance soldier
                              AnimPhase = soldier.AnimPhase }
                        for segment in Melee.anatomy target do
                            let proxyColor =
                                match segment.Part with
                                | BodyHead -> Vector4(1.0f, 0.25f, 0.75f, 0.95f)
                                | BodyTorso -> Vector4(1.0f, 0.62f, 0.12f, 0.90f)
                                | _ -> Vector4(0.95f, 0.90f, 0.16f, 0.82f)
                            particles.SubmitDebugLine(segment.StartPoint, segment.EndPoint, proxyColor)
                            let bandStart = Vector3.Lerp(segment.StartPoint, segment.EndPoint, segment.MinSeverFraction)
                            let bandEnd = Vector3.Lerp(segment.StartPoint, segment.EndPoint, segment.MaxSeverFraction)
                            particles.SubmitDebugLine(bandStart, bandEnd, Vector4(1.0f, 0.08f, 0.04f, 1.0f))
                let activeSlot = world.Player.Slots[world.Player.Active]
                if activeSlot.Class.Mechanism = Katana then
                    let attack = defaultArg activeSlot.LastMelee KatanaSweep
                    let trajectory = Melee.bladeTrajectory attack world.Player.Position world.Player.Yaw world.Player.Pitch
                    for pose in trajectory do
                        particles.SubmitDebugLine(pose.Base, pose.Tip, Vector4(0.20f, 0.88f, 1.0f, 0.42f))
                    for index in 0..trajectory.Length - 2 do
                        let previous, current = trajectory[index], trajectory[index + 1]
                        for station in [ 0.35f; 0.68f; 1.0f ] do
                            particles.SubmitDebugLine(
                                Vector3.Lerp(previous.Base, previous.Tip, station),
                                Vector3.Lerp(current.Base, current.Tip, station),
                                Vector4(0.15f, 1.0f, 0.72f, 0.76f))
            // Re-predicted every frame so the arc tracks the crosshair. Two
            // seconds of ticks covers any throw the player can make before the
            // grenade settles.
            particles.SetPreview(
                if hudInfo.GrenadeCooking && world.Player.IsAlive then
                    Grenades.predictPath world.Level (Tuning.TickRate * 2) world.Player
                else [||])
            // One-frame motion streaks make the physical rounds readable at
            // their real simulation speeds without retaining cosmetic state.
            for projectile in world.SpecialProjectiles do
                let direction = MathEx.normalizedOrZero projectile.Velocity
                let length, color =
                    match projectile.Kind with
                    | PaintBall PaintRed -> 0.42f, Vector4(1.0f, 0.08f, 0.10f, 0.85f)
                    | PaintBall PaintBlue -> 0.42f, Vector4(0.08f, 0.35f, 1.0f, 0.85f)
                    | PaintBall PaintGreen -> 0.42f, Vector4(0.10f, 1.0f, 0.28f, 0.85f)
                    | PaintBall PaintYellow -> 0.42f, Vector4(1.0f, 0.85f, 0.08f, 0.85f)
                    | PaintBall PaintPurple -> 0.42f, Vector4(0.75f, 0.10f, 1.0f, 0.85f)
                    | PaintBall _ -> 0.42f, Vector4(1.0f, 0.34f, 0.05f, 0.85f)
                    | NerfDart -> 0.32f, Vector4(1.0f, 0.36f, 0.04f, 0.82f)
                    | BazookaRocket -> 1.05f, Vector4(0.70f, 0.68f, 0.62f, 0.62f)
                    | WaterDroplet -> 0.24f, Vector4(0.08f, 0.62f, 1.0f, 0.72f)
                    | NailRound -> 0.28f, Vector4(0.72f, 0.76f, 0.82f, 0.74f)
                    | HarpoonRound _ -> 1.8f, Vector4(0.95f, 0.58f, 0.12f, 0.82f)
                    | ArrowRound _ -> 0.95f, Vector4(0.68f, 0.45f, 0.20f, 0.82f)
                particles.SubmitDebugLine(projectile.Position, projectile.Position - direction * length, color)
                match projectile.Kind with
                | HarpoonRound _ when projectile.Owner = world.Player.Id ->
                    let spool = world.Player.Position + Vector3(0.24f, 1.20f, 0.0f)
                    particles.SubmitDebugLine(spool, projectile.Position, Vector4(0.08f, 0.065f, 0.05f, 0.92f))
                | _ -> ()
            particles.Render viewProjection
            let activeClass = world.Player.Slots[world.Player.Active].Class
            let activeSlot = world.Player.Slots[world.Player.Active]
            let weaponName = activeClass.Name
            let localMarks = localPlayerMarks world
            let paintKey = if weaponName = "Paintball Marker" then string world.PaintColor else ""
            let harpoonPose = harpoonLoadPose activeSlot
            // The minigun's rotor is a pose like any other. Between rounds the
            // weapon sits in Cooling, so on a gun that cycles this fast that
            // reads as "the trigger is down"; the rate eases so it spins up and
            // coasts down rather than snapping.
            if Guns.animated weaponName && activeClass.Kind = MachineGun then
                let now = Stopwatch.GetTimestamp()
                let elapsed =
                    if lastSpinStamp = 0L then 0.0f
                    else min 0.1f (float32 (float (now - lastSpinStamp) / float Stopwatch.Frequency))
                lastSpinStamp <- now
                let firing = match activeSlot.State with Cooling _ -> true | _ -> false
                gunSpinRate <- gunSpinRate + ((if firing then 46.0f else 0.0f) - gunSpinRate) * min 1.0f (elapsed * 4.0f)
                gunSpin <- (gunSpin + gunSpinRate * elapsed) % MathF.Tau
            else
                gunSpinRate <- 0.0f
                lastSpinStamp <- 0L
            let mechanismPose =
                match activeSlot.State, activeClass.Mechanism with
                | Drawing charge, Bow -> Tuning.drawPose charge
                | _ when weaponName = "M134 Minigun" -> gunSpin
                | _ -> harpoonPose
            let meshKey = weaponMeshKey weaponName paintKey localMarks mechanismPose
            if loadedGun <> meshKey then uploadGun weaponName world mechanismPose
            let lookingThroughScope = activeClass.Kind = SniperRifle && world.Player.Ads >= 0.72f
            if gunIndexCount > 0u && not lookingThroughScope && not deathWatching then
                // Particle rendering binds its own shader. Rebind the level/
                // viewmodel program before setting uniforms or the weapon draw
                // is interpreted with the particle vertex layout on shot frames.
                gl.UseProgram program
                // The viewmodel gets its own projection so ADS does not distort it.
                // Bob and pose are deterministic functions of simulation state.
                let speed = Vector2(world.Player.Velocity.X, world.Player.Velocity.Z).Length()
                let phase = float32 world.Tick * 0.11f
                let bob = min 1.0f (speed / 4.0f)
                let ads = world.Player.Ads
                let reloadPose =
                    match activeSlot.State with
                    | Reloading remaining ->
                        let progress = 1.0f - Units.raw remaining / max 0.01f (Units.raw activeSlot.Class.ReloadTime)
                        MathF.Sin(MathF.PI * MathEx.clamp01 progress)
                    | _ -> 0.0f
                let switchPose =
                    match activeSlot.State with
                    | Switching(_, remaining) -> 1.0f - MathEx.clamp01 (Units.raw remaining / Units.raw Tuning.WeaponSwitchTime)
                    | _ -> 0.0f
                // Bolt rifles and the pump shotgun visibly cycle during the
                // post-shot cooldown: the weapon cants right and pulls back.
                let boltPose =
                    match activeSlot.State, activeSlot.Class.Mode with
                    | Cooling remaining, BoltAction ->
                        let total = 60.0f / activeSlot.Class.RoundsPerMin
                        let progress = 1.0f - Units.raw remaining / max 0.01f total
                        MathF.Sin(MathF.PI * MathEx.clamp01 ((progress - 0.12f) / 0.62f))
                    | _ -> 0.0f
                let katanaProgress =
                    match activeSlot.State, activeClass.Mechanism with
                    | Cooling remaining, Katana ->
                        let total = 60.0f / activeClass.RoundsPerMin
                        Some(1.0f - Units.raw remaining / max 0.01f total |> MathEx.clamp01)
                    | _ -> None
                let katanaSweepYaw = ViewmodelAnimation.katanaYaw (activeClass.Mechanism = Katana) katanaProgress activeSlot.LastMelee
                let katanaOverheadPitch = ViewmodelAnimation.katanaPitch katanaProgress activeSlot.LastMelee
                // Sprinting lowers the weapon across the chest.
                sprintBlend <- sprintBlend + ((if world.Player.Sprinting then 1.0f else 0.0f) - sprintBlend) * 0.18f
                let view = Vector2(world.Player.Yaw, world.Player.Pitch)
                let viewDelta = view - lastView
                lastView <- view
                viewSway <- Vector2.Lerp(viewSway, Vector2(Math.Clamp(viewDelta.X * 2.5f, -0.06f, 0.06f), Math.Clamp(viewDelta.Y * 2.5f, -0.05f, 0.05f)), 0.22f)
                let position =
                    let bowAdsOffset = if activeClass.Mechanism = Bow then ads * 0.18f else 0.0f
                    Vector3(
                        0.34f * (1.0f - ads) + bowAdsOffset + MathF.Sin(phase) * 0.012f * bob - viewSway.X + sprintBlend * 0.10f,
                        // Aiming lifts the gun until its own sight line reaches
                        // eye level, rather than by a fixed amount that only
                        // ever suited the Kar98k.
                        -0.31f + ads * (0.31f - Guns.sightHeight activeSlot.Class.Name)
                        + MathF.Abs(MathF.Cos phase) * 0.012f * bob + viewSway.Y
                        - reloadPose * 0.20f - switchPose * 0.25f - boltPose * 0.045f - sprintBlend * 0.14f,
                        -0.68f - ads * 0.05f + recoil * 0.55f + boltPose * 0.07f)
                let model =
                    Matrix4x4.CreateRotationX(-recoil * 0.75f + sprintBlend * 0.42f + katanaOverheadPitch)
                    * Matrix4x4.CreateRotationY(-sprintBlend * 0.38f + katanaSweepYaw)
                    * Matrix4x4.CreateRotationZ(-0.035f * (1.0f - ads) + reloadPose * 0.32f + boltPose * 0.22f)
                    * Matrix4x4.CreateTranslation position
                let projection = Matrix4x4.CreatePerspectiveFieldOfView(55.0f * MathF.PI / 180.0f, float32 width / float32 (max 1 height), 0.03f, 8.0f)
                let gunMatrix = model * projection
                gl.Clear ClearBufferMask.DepthBufferBit
                setMatrix program "uViewProjection" gunMatrix
                setMatrix program "uLightViewProjection" gunMatrix
                uniform3 program "uCamera" Vector3.Zero
                uniform1i program "uViewmodel" 1
                // Barrels glow as the gun heats. Only the front end: the
                // receiver and the grips never get near it.
                uniform1f program "uHeatGlow" activeSlot.Heat
                uniform1i program "uImpactCount" 0
                gl.Disable EnableCap.CullFace
                gl.BindVertexArray gunVao
                gl.DrawElements(PrimitiveType.Triangles, gunIndexCount, DrawElementsType.UnsignedInt, noOffset)
                gl.BindVertexArray 0u
                gl.Enable EnableCap.CullFace
        hud.Render(logicalWidth, logicalHeight, world, hudInfo)

    member _.SetSettings(value: GameSettings) = settings <- value

    member _.HandleEvents(events: GameEvent list) =
        particles.Handle events (Settings.bloodRgb settings.BloodColor)
        // Remember anything that could have killed someone for a moment, so a
        // death observed on a later frame still ragdolls away from its cause.
        let expires = Stopwatch.GetTimestamp() + int64 (0.7 * float Stopwatch.Frequency)
        let impulses =
            events
            |> List.choose (function
                | BloodImpact(position, direction, headshot) ->
                    Some { Expires = expires; Position = position; Push = direction * (if headshot then 6.5f else 4.5f); Radius = 0.0f }
                | HeadGib(position, direction) ->
                    Some { Expires = expires; Position = position; Push = direction * 7.0f; Radius = 0.0f }
                | HarpoonSkewer(position, direction, _) ->
                    Some { Expires = expires; Position = position; Push = direction * 9.0f; Radius = 0.0f }
                | Explosion(position, radius) ->
                    Some { Expires = expires; Position = position; Push = Vector3.Zero; Radius = radius }
                | _ -> None)
        killImpulses <- impulses @ killImpulses |> List.truncate 32
        let added =
            events
            |> List.choose (function
                | Impact(position, _, _) -> Some(Vector4(position, 0.16f))
                | HarpoonEmbedded(position, _) -> Some(Vector4(position, 0.24f))
                | Explosion(position, _) -> Some(Vector4(position, 1.35f))
                | _ -> None)
        // Only the first 16 are ever uploaded (see uImpacts in Render), so
        // there is no point retaining more.
        decals <- (added @ decals) |> List.truncate 16
    member _.StepEffects(dt: float32) = particles.Step dt

    member _.KickWeapon() = recoilVelocity <- min 2.4f (recoilVelocity + 1.6f)

    member _.StepViewmodel(dt: float32) =
        recoilVelocity <- recoilVelocity + (-recoil * 68.0f - recoilVelocity * 15.0f) * dt
        recoil <- Math.Clamp(recoil + recoilVelocity * dt, 0.0f, 0.12f)

    member _.Resize(framebufferWidth, framebufferHeight, logicalSize: Silk.NET.Maths.Vector2D<int>) =
        width <- max 1 framebufferWidth
        height <- max 1 framebufferHeight
        logicalWidth <- max 1 logicalSize.X
        logicalHeight <- max 1 logicalSize.Y
        gl.Viewport(0, 0, uint32 width, uint32 height)

    interface IDisposable with
        member _.Dispose() =
            let del (delete: uint32 -> unit) handle = if handle <> 0u then delete handle
            deleteMesh ()
            del gl.DeleteVertexArray soldierVao
            del gl.DeleteBuffer soldierVertexBuffer
            del gl.DeleteBuffer soldierIndexBuffer
            del gl.DeleteVertexArray gunVao
            del gl.DeleteBuffer gunVertexBuffer
            del gl.DeleteBuffer gunIndexBuffer
            del gl.DeleteFramebuffer shadowFramebuffer
            del gl.DeleteTexture shadowTexture
            del gl.DeleteProgram shadowProgram
            del gl.DeleteProgram skyProgram
            del gl.DeleteProgram program
            (particles :> IDisposable).Dispose()
            (hud :> IDisposable).Dispose()
