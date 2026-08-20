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
      GamepadSensitivity: float32
      AdsToggle: bool
      CrouchToggle: bool
      /// Stored as the negative on purpose. A bool missing from an older
      /// settings.json deserialises to false, so a positively-named field
      /// defaulting to true would arrive switched off for everyone who already
      /// has a settings file. The menu presents it the right way round.
      ScrollWeaponSwitchOff: bool
      BloodColor: BloodColor
      Fullscreen: bool
      WindowWidth: int
      WindowHeight: int }

[<RequireQualifiedAccess>]
module Settings =
    let defaults =
        { Fov = 65.0f
          Contrast = 1.0f
          MouseSensitivity = 1.0f
          GamepadSensitivity = 1.0f
          AdsToggle = false
          // Hold-to-crouch by default; toggle is opt-in like ADS toggle.
          CrouchToggle = false
          ScrollWeaponSwitchOff = false
          BloodColor = Crimson
          Fullscreen = false
          WindowWidth = 1280
          WindowHeight = 720 }

    /// Reject out-of-range values from hand-edited or old config files.
    /// The same bounds drive the SettingsUi sliders — keep the two in sync.
    let clamp (settings: GameSettings) =
        { settings with
            Fov = Math.Clamp(settings.Fov, 55.0f, 95.0f)
            Contrast = Math.Clamp(settings.Contrast, 0.8f, 1.3f)
            MouseSensitivity = Math.Clamp(settings.MouseSensitivity, 0.2f, 3.0f)
            // 0 means the field was absent from an older settings.json.
            GamepadSensitivity =
                if settings.GamepadSensitivity = 0.0f then 1.0f
                else Math.Clamp(settings.GamepadSensitivity, 0.2f, 3.0f)
            // 0 means the fields were absent from a settings.json written
            // before the display options existed. Anything else is floored so
            // a hand-edited file cannot produce an unusable window.
            WindowWidth = if settings.WindowWidth <= 0 then 1280 else max 640 settings.WindowWidth
            WindowHeight = if settings.WindowHeight <= 0 then 720 else max 360 settings.WindowHeight }

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

    /// Sizes offered by the RESOLUTION row. Replaced at startup with the modes
    /// the active monitor actually reports; this fallback covers backends that
    /// enumerate nothing (some Wayland and headless setups) and keeps the
    /// tests deterministic.
    let mutable resolutions = [| 1280, 720; 1600, 900; 1920, 1080 |]

    /// Index of the entry closest to the stored size, so a size that is no
    /// longer offered (monitor swapped, config hand-edited) still steps from
    /// somewhere sensible instead of snapping to the start of the list.
    let resolutionIndex (settings: GameSettings) =
        if resolutions.Length = 0 then 0
        else
            resolutions
            |> Array.mapi (fun index (w, h) ->
                index, abs (w - settings.WindowWidth) + abs (h - settings.WindowHeight))
            |> Array.minBy snd
            |> fst

    let stepResolution (settings: GameSettings) direction =
        if resolutions.Length = 0 then settings
        else
            let count = resolutions.Length
            let w, h = resolutions[(resolutionIndex settings + direction + count) % count]
            { settings with WindowWidth = w; WindowHeight = h }

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
