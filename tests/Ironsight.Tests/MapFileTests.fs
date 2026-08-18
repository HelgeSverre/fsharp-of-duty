namespace Ironsight.Tests

open System
open System.IO
open System.Net.Http
open Ironsight
open Ironsight.ProcGen
open Ironsight.Server
open Ironsight.Shell
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.Extensions.DependencyInjection
open Xunit

module MapFileTests =
    // Heightfields carry a height function, so decoded specs cannot be compared
    // structurally. Identity is proven the way the game relies on it instead:
    // re-encoding a decoded spec must reproduce the exact bytes, and compiling
    // it must produce the same geometry.
    [<Fact>]
    let ``every built-in map survives an encode decode roundtrip`` () =
        for spec in Levels.specs do
            let bytes = MapFile.encode spec
            match MapFile.decode bytes with
            | Error message -> Assert.Fail $"{spec.Name}: {message}"
            | Ok decoded ->
                Assert.Equal(spec.Name, decoded.Name)
                Assert.Equal(spec.Items.Length, decoded.Items.Length)
                Assert.Equal<byte array>(bytes, MapFile.encode decoded)
                let original = LevelCompile.compile spec
                let rebuilt = LevelCompile.compile decoded
                Assert.Equal(original.Vertices.Length, rebuilt.Vertices.Length)
                Assert.Equal(original.Indices.Length, rebuilt.Indices.Length)
                Assert.Equal(original.Nav.Length, rebuilt.Nav.Length)
                Assert.Equal(original.Spawns.Length, rebuilt.Spawns.Length)

    [<Fact>]
    let ``map hashes are stable and unique per map`` () =
        let hashes = Levels.specs |> Array.map (MapFile.encode >> MapFile.hash)
        Assert.Equal(hashes.Length, (Array.distinct hashes).Length)
        for spec in Levels.specs do
            Assert.Equal(MapFile.hash (MapFile.encode spec), MapFile.hash (MapFile.encode spec))

    [<Fact>]
    let ``decode rejects malformed data with errors not exceptions`` () =
        let good = MapFile.encode PaintballMap.spec
        let expectError (bytes: byte array) =
            match MapFile.decode bytes with
            | Error _ -> ()
            | Ok spec -> Assert.Fail $"expected rejection, decoded '{spec.Name}'"
        expectError [||]
        expectError "JUNKDATA"B
        expectError good[.. good.Length - 5] // truncated
        expectError (Array.append good [| 1uy; 2uy |]) // trailing bytes
        let wrongVersion = Array.copy good
        wrongVersion[4] <- 99uy
        expectError wrongVersion
        let unknownTag = Array.copy good
        // First item tag sits right after magic+version+name+count.
        let nameLength = int good[5] // BinaryWriter 7-bit length; map names are short
        unknownTag[4 + 1 + 1 + nameLength + 4] <- 250uy
        expectError unknownTag

    let private startServer () =
        let app = Program.build [||] 0
        app.StartAsync().GetAwaiter().GetResult()
        let address =
            app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses
            |> Seq.head
        let port = Uri(address).Port
        app, Uri $"ws://127.0.0.1:%d{port}/play"

    [<Fact>]
    [<Trait("Category", "Integration")>]
    let ``a client without the server's custom map downloads verifies and caches it`` () =
        let scratch = Path.Combine(Path.GetTempPath(), "ironsight-maptest-" + Guid.NewGuid().ToString "N")
        Directory.CreateDirectory scratch |> ignore
        // A custom map: a renamed built-in, so its hash matches no built-in.
        let custom = { PaintballMap.spec with Name = "Custom Range" }
        let customBytes = MapFile.encode custom
        let customHash = MapFile.hash customBytes
        let mapPath = Path.Combine(scratch, "custom" + MapFile.Extension)
        File.WriteAllBytes(mapPath, customBytes)
        let previousLevel = Environment.GetEnvironmentVariable "IRONSIGHT_LEVEL"
        let previousHome = Environment.GetEnvironmentVariable "IRONSIGHT_HOME"
        Environment.SetEnvironmentVariable("IRONSIGHT_LEVEL", mapPath)
        Environment.SetEnvironmentVariable("IRONSIGHT_HOME", Path.Combine(scratch, "home"))
        try
            let app, uri = startServer ()
            try
                // The welcome announces the custom map.
                use client = new OnlineClient(uri, "Downloader", TeamDeathmatch, "Thompson")
                client.ConnectAsync().GetAwaiter().GetResult()
                Assert.Equal("Custom Range", client.LevelName)
                Assert.Equal(customHash, client.MapHash)
                // Raw HTTP: correct hash serves the exact bytes; others 404.
                use http = new HttpClient()
                let httpBase = UriBuilder(uri, Scheme = "http", Path = "").Uri
                let served = http.GetByteArrayAsync(Uri(httpBase, $"/maps/{customHash}")).GetAwaiter().GetResult()
                Assert.Equal<byte array>(customBytes, served)
                let missing = http.GetAsync(Uri(httpBase, "/maps/deadbeef")).GetAwaiter().GetResult()
                Assert.Equal(404, int missing.StatusCode)
                // The store downloads, verifies and caches.
                match MapStore.resolve uri customHash with
                | Error message -> Assert.Fail message
                | Ok level -> Assert.Equal("Custom Range", level.Name)
                Assert.True(File.Exists(Path.Combine(MapStore.cacheDir (), customHash + MapFile.Extension)))
                client.CloseAsync().GetAwaiter().GetResult()
            finally
                app.StopAsync().GetAwaiter().GetResult()
                app.DisposeAsync().AsTask().GetAwaiter().GetResult()
            // With the server gone, the cache alone must satisfy the map.
            match MapStore.resolve (Uri "ws://127.0.0.1:1/play") customHash with
            | Error message -> Assert.Fail $"cache miss after download: {message}"
            | Ok level -> Assert.Equal("Custom Range", level.Name)
        finally
            Environment.SetEnvironmentVariable("IRONSIGHT_LEVEL", previousLevel)
            Environment.SetEnvironmentVariable("IRONSIGHT_HOME", previousHome)
            try Directory.Delete(scratch, true) with _ -> ()
