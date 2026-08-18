namespace Ironsight.Shell

open System
open System.IO
open System.Numerics
open System.Text.Json
open System.Text.Json.Serialization

type BloodColor = Crimson | Blue | Green | Black | Yellow | Pink

[<CLIMutable>]
type GameSettings =
    { Fov: float32
      Contrast: float32
      MouseSensitivity: float32
      AdsToggle: bool
      CrouchToggle: bool
      BloodColor: BloodColor }

[<RequireQualifiedAccess>]
module Settings =
    let defaults =
        { Fov = 65.0f
          Contrast = 1.0f
          MouseSensitivity = 1.0f
          AdsToggle = false
          // Hold-to-crouch by default; toggle is opt-in like ADS toggle.
          CrouchToggle = false
          BloodColor = Crimson }

    /// Reject out-of-range values from hand-edited or old config files.
    let clamp (settings: GameSettings) =
        { settings with
            Fov = Math.Clamp(settings.Fov, 55.0f, 95.0f)
            Contrast = Math.Clamp(settings.Contrast, 0.8f, 1.3f)
            MouseSensitivity = Math.Clamp(settings.MouseSensitivity, 0.2f, 3.0f) }

    let bloodColors = [| Crimson; Blue; Green; Black; Yellow; Pink |]

    type BloodColorConverter() =
        inherit JsonConverter<BloodColor>()
        override _.Read(reader, _typeToConvert, _options) =
            let name = reader.GetString()
            bloodColors
            |> Array.tryFind (fun color -> String.Equals(string color, name, StringComparison.OrdinalIgnoreCase))
            |> Option.defaultValue Crimson
        override _.Write(writer, value, _options) = writer.WriteStringValue(string value)

    let bloodNames =
        bloodColors |> Array.map (fun color -> (string color).ToUpperInvariant())

    let bloodColorIndex color =
        bloodColors |> Array.tryFindIndex ((=) color) |> Option.defaultValue 0

    let bloodRgb = function
        | Crimson -> Vector3(0.58f, 0.015f, 0.018f)
        | Blue -> Vector3(0.06f, 0.12f, 0.62f)
        | Green -> Vector3(0.05f, 0.45f, 0.10f)
        | Black -> Vector3(0.05f, 0.03f, 0.03f)
        | Yellow -> Vector3(0.80f, 0.62f, 0.05f)
        | Pink -> Vector3(0.78f, 0.14f, 0.36f)

    let stepBloodColor (settings: GameSettings) direction =
        let count = bloodColors.Length
        let next = (bloodColorIndex settings.BloodColor + direction + count) % count
        { settings with BloodColor = bloodColors[next] }

    let private jsonOptions () =
        let options = JsonSerializerOptions(JsonSerializerDefaults.Web)
        options.WriteIndented <- true
        options.Converters.Add(BloodColorConverter())
        options

    let serialize (settings: GameSettings) =
        JsonSerializer.Serialize(settings, jsonOptions ())

    let deserialize (json: string) =
        JsonSerializer.Deserialize<GameSettings>(json, jsonOptions ()) |> clamp

    /// Root for everything the game persists (settings, downloaded maps).
    /// IRONSIGHT_HOME overrides it for portable installs and tests.
    let appDataDir () =
        match Environment.GetEnvironmentVariable "IRONSIGHT_HOME" with
        | value when not (String.IsNullOrWhiteSpace value) -> value
        | _ -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.ApplicationData, "ironsight")

    let private filePath () = Path.Combine(appDataDir (), "settings.json")

    let load () =
        try
            if File.Exists(filePath ()) then deserialize (File.ReadAllText(filePath ()))
            else defaults
        with _ -> defaults

    let save (settings: GameSettings) =
        try
            let path = filePath ()
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, serialize settings)
            true
        with _ -> false
