namespace Ironsight.Shell

#nowarn "9"

open System
open System.Diagnostics
open System.Numerics
open Microsoft.FSharp.NativeInterop
open Ironsight
open Ironsight.ProcGen
open Silk.NET.OpenGL

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

    let uploadGun name =
        let mesh = Guns.forWeapon name
        let vertices = GlUtil.flattenVertices mesh.Vertices
        uploadMesh gunVao gunVertexBuffer gunIndexBuffer vertices mesh.Indices BufferUsageARB.StaticDraw
        gunIndexCount <- uint32 mesh.Indices.Length
        loadedGun <- name

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
                ragdolls.Spawn(soldier.Id, Humanoid.worldSkeleton soldier, impulse)
            | _ -> ()
        let dt = float32 (Stopwatch.GetElapsedTime ragdollClock).TotalSeconds
        ragdollClock <- now
        ragdolls.Step(dt, world.Level)

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
                    ragdolls.TryGet soldier.Id |> Option.map (Humanoid.poseFromSkeleton soldier)
                else None)
            |> MeshGen.union
        let grenadeMesh =
            world.Grenades
            |> Array.map (fun grenade ->
                MeshGen.box (Vector3(0.14f, 0.18f, 0.14f)) Metal
                |> MeshGen.translate grenade.Position)
            |> MeshGen.union
        let ragdollOffset = uint32 soldierVertices.Length
        let grenadeOffset = ragdollOffset + uint32 ragdollMesh.Vertices.Length
        let meshVertices = Array.concat [ soldierVertices; ragdollMesh.Vertices; grenadeMesh.Vertices ]
        let meshIndices =
            Array.concat
                [ soldierIndices
                  ragdollMesh.Indices |> Array.map ((+) ragdollOffset)
                  grenadeMesh.Indices |> Array.map ((+) grenadeOffset) ]
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
            // Re-predicted every frame so the arc tracks the crosshair. Two
            // seconds of ticks covers any throw the player can make before the
            // grenade settles.
            particles.SetPreview(
                if hudInfo.GrenadeCooking && world.Player.IsAlive then
                    Grenades.predictPath world.Level (Tuning.TickRate * 2) world.Player
                else [||])
            particles.Render viewProjection
            let activeClass = world.Player.Slots[world.Player.Active].Class
            let weaponName = activeClass.Name
            if loadedGun <> weaponName then uploadGun weaponName
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
                let activeSlot = world.Player.Slots[world.Player.Active]
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
                // Sprinting lowers the weapon across the chest.
                sprintBlend <- sprintBlend + ((if world.Player.Sprinting then 1.0f else 0.0f) - sprintBlend) * 0.18f
                let view = Vector2(world.Player.Yaw, world.Player.Pitch)
                let viewDelta = view - lastView
                lastView <- view
                viewSway <- Vector2.Lerp(viewSway, Vector2(Math.Clamp(viewDelta.X * 2.5f, -0.06f, 0.06f), Math.Clamp(viewDelta.Y * 2.5f, -0.05f, 0.05f)), 0.22f)
                let position =
                    Vector3(
                        0.34f * (1.0f - ads) + MathF.Sin(phase) * 0.012f * bob - viewSway.X + sprintBlend * 0.10f,
                        // Aiming lifts the gun until its own sight line reaches
                        // eye level, rather than by a fixed amount that only
                        // ever suited the Kar98k.
                        -0.31f + ads * (0.31f - Guns.sightHeight activeSlot.Class.Name)
                        + MathF.Abs(MathF.Cos phase) * 0.012f * bob + viewSway.Y
                        - reloadPose * 0.20f - switchPose * 0.25f - boltPose * 0.045f - sprintBlend * 0.14f,
                        -0.68f - ads * 0.05f + recoil * 0.55f + boltPose * 0.07f)
                let model =
                    Matrix4x4.CreateRotationX(-recoil * 0.75f + sprintBlend * 0.42f)
                    * Matrix4x4.CreateRotationY(-sprintBlend * 0.38f)
                    * Matrix4x4.CreateRotationZ(-0.035f * (1.0f - ads) + reloadPose * 0.32f + boltPose * 0.22f)
                    * Matrix4x4.CreateTranslation position
                let projection = Matrix4x4.CreatePerspectiveFieldOfView(55.0f * MathF.PI / 180.0f, float32 width / float32 (max 1 height), 0.03f, 8.0f)
                let gunMatrix = model * projection
                gl.Clear ClearBufferMask.DepthBufferBit
                setMatrix program "uViewProjection" gunMatrix
                setMatrix program "uLightViewProjection" gunMatrix
                uniform3 program "uCamera" Vector3.Zero
                uniform1i program "uViewmodel" 1
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
                | Explosion(position, radius) ->
                    Some { Expires = expires; Position = position; Push = Vector3.Zero; Radius = radius }
                | _ -> None)
        killImpulses <- impulses @ killImpulses |> List.truncate 32
        let added =
            events
            |> List.choose (function
                | Impact(position, _, _) -> Some(Vector4(position, 0.16f))
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
