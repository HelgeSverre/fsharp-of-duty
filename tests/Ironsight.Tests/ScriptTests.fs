namespace Ironsight.Tests

open Ironsight
open Xunit

module ScriptTests =
    [<Fact>]
    let ``script delay emits subtitle once`` () =
        let original = Sim.createTrainingWorld 12UL
        let rule = { Condition = Delay(Units.seconds 1.0f); Action = Say("Test", "Advance"); Fired = false }
        let ready = { original with Script = { MissionTime = Units.seconds 1.0f; Ended = false; Rules = [| rule |] } }
        let fired, firstEvents = Script.step ready
        let _, secondEvents = Script.step fired
        Assert.Contains(Subtitle("Test", "Advance"), firstEvents)
        Assert.Empty secondEvents

    [<Fact>]
    let ``open path removes collision brush and rebuilds render and navigation sources`` () =
        let original = Sim.createTrainingWorld 13UL
        let beforeBrushes = original.Level.Brushes.Length
        let beforeVertices = original.Level.Vertices.Length
        let rule = { Condition = Delay(Units.seconds 0.0f); Action = OpenPath 1; Fired = false }
        let ready = { original with Script = { original.Script with Rules = [| rule |] } }
        let opened, _ = Script.step ready
        Assert.Equal(beforeBrushes - 1, opened.Level.Brushes.Length)
        Assert.Equal(beforeVertices - 24, opened.Level.Vertices.Length)
        Assert.NotEmpty opened.Level.Nav
        // Renderers cache geometry per level name; the revision must advance so
        // they re-upload instead of drawing a wall that no longer collides.
        Assert.Equal(original.Level.Revision + 1, opened.Level.Revision)

    [<Fact>]
    let ``completed objective ends mission`` () =
        let original = Sim.createTrainingWorld 14UL
        let objectives = [| { original.Objectives[0] with Done = true } |]
        let ready = { original with Objectives = objectives }
        let completed, events = Script.step ready
        Assert.True(completed.Objectives[0].Done)
        Assert.True(completed.Script.Ended)
        Assert.Contains(Subtitle("HQ", "Mission complete"), events)

    [<Fact>]
    let ``entering mission trigger spawns an enemy wave once`` () =
        let original = Sim.createTrainingWorld 15UL
        let entered = { original with Player = { original.Player with Position = System.Numerics.Vector3.Zero } }
        let spawned, _ = Script.step entered
        let repeated, _ = Script.step spawned
        Assert.Equal(original.Soldiers.Length + 4, spawned.Soldiers.Length)
        Assert.Equal(spawned.Soldiers.Length, repeated.Soldiers.Length)
