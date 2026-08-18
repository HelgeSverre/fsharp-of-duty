namespace Ironsight.Shell

#nowarn "9"

open System
open System.Numerics
open Microsoft.FSharp.NativeInterop
open Ironsight
open Ironsight.ProcGen
open Silk.NET.OpenAL

type AudioSystem() =
    let context = new AudioContext()
    let al = AL.GetApi()
    let sources = Array.init 24 (fun _ -> al.GenSource())
    let mutable sourceIndex = 1

    let upload sound =
        let buffer = al.GenBuffer()
        al.BufferData(buffer, BufferFormat.Mono16, sound.Samples, sound.SampleRate)
        buffer

    let rifle = upload (AudioSynth.gunshot true)
    let smg = upload (AudioSynth.gunshot false)
    let blast = upload (AudioSynth.explosion ())
    let step = upload (AudioSynth.footstep ())
    let reload = upload (AudioSynth.reloadClick ())
    let heartbeat = upload (AudioSynth.heartbeat ())
    let radio = upload (AudioSynth.radio ())
    let wind = upload (AudioSynth.wind ())
    let buffers = [| rifle; smg; blast; step; reload; heartbeat; radio; wind |]

    let play (buffer: uint32) (position: Vector3) (gain: float32) (pitch: float32) =
        let source = sources[sourceIndex]
        sourceIndex <- 1 + (sourceIndex % (sources.Length - 1))
        al.SourceStop source
        al.SetSourceProperty(source, SourceInteger.Buffer, buffer)
        al.SetSourceProperty(source, SourceVector3.Position, position.X, position.Y, position.Z)
        al.SetSourceProperty(source, SourceFloat.Gain, gain)
        al.SetSourceProperty(source, SourceFloat.Pitch, pitch)
        al.SetSourceProperty(source, SourceFloat.ReferenceDistance, 3.0f)
        al.SetSourceProperty(source, SourceFloat.MaxDistance, 85.0f)
        al.SourcePlay source

    do
        al.DistanceModel DistanceModel.InverseDistanceClamped
        al.SetSourceProperty(sources[0], SourceInteger.Buffer, wind)
        al.SetSourceProperty(sources[0], SourceBoolean.Looping, true)
        al.SetSourceProperty(sources[0], SourceFloat.Gain, 0.10f)
        al.SourcePlay sources[0]

    member _.UpdateListener(player: Player) =
        let eye = player.Position + Vector3(0.0f, 1.55f, 0.0f)
        al.SetListenerProperty(ListenerVector3.Position, eye.X, eye.Y, eye.Z)
        let forward = Ballistics.directionFromAngles player.Yaw player.Pitch Vector2.Zero
        let orientation = [| forward.X; forward.Y; forward.Z; 0.0f; 1.0f; 0.0f |]
        use orientationPointer = fixed orientation
        al.SetListenerProperty(ListenerFloatArray.Orientation, orientationPointer)
        al.SetSourceProperty(sources[0], SourceVector3.Position, eye.X, eye.Y, eye.Z)

    member _.Handle(events: GameEvent list) =
        for event in events do
            match event with
            | ShotFired(_, position, _, weapon) ->
                let automatic = weapon = "Thompson" || weapon = "MG42"
                play (if automatic then smg else rifle) position 0.88f (0.97f + float32 sourceIndex * 0.004f)
            | Explosion(position, _) ->
                play blast position 1.0f 1.0f
                // A second, lower and quieter copy reads as the blast rolling off
                // the surroundings rather than one flat pop.
                play blast position 0.55f 0.62f
            | FootStep(position, _) -> play step position 0.38f 1.0f
            | Subtitle _ -> play radio Vector3.Zero 0.28f 1.0f
            | _ -> ()

    member _.PlayReload(position: Vector3) = play reload position 0.48f 1.0f
    member _.PlayHeartbeat(position: Vector3) = play heartbeat position 0.62f 1.0f
    member _.PlayDistantShot(position: Vector3) = play rifle position 0.20f 0.72f

    interface IDisposable with
        member _.Dispose() =
            for source in sources do al.DeleteSource source
            for buffer in buffers do al.DeleteBuffer buffer
            al.Dispose()
            context.Dispose()
