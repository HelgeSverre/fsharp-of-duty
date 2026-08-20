client := "src/Ironsight/Ironsight.fsproj"
server_project := "src/Ironsight.Server/Ironsight.Server.fsproj"
tests := "tests/Ironsight.Tests/Ironsight.Tests.fsproj"
solution := "Ironsight.sln"
image := "ironsight-server:local"
dotnet := "PATH=\"$PWD/.dotnet:$PATH\" dotnet"

[private]
default:
    @just --list

# Ensure a compatible SDK is available (install into .dotnet/ if needed).
[private]
[unix]
_sdk:
    #!/usr/bin/env bash
    set -euo pipefail
    if PATH="$PWD/.dotnet:$PATH" dotnet --version >/dev/null 2>&1; then exit 0; fi
    echo "No SDK satisfying global.json found — installing into .dotnet/"
    installer="${TMPDIR:-/tmp}/ironsight-dotnet-install.sh"
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
    bash "$installer" --jsonfile global.json --install-dir "$PWD/.dotnet"

[private]
[windows]
_sdk:
    @dotnet --version >nul 2>&1 || echo "No SDK satisfying global.json. Install it with dotnet-install.ps1."

# Run default Killhouse (Pv4).
[group('run')]
run: _sdk
    {{ dotnet }} run --project {{ client }}

# Run client with hot reload.
[group('run')]
dev: _sdk
    {{ dotnet }} watch --project {{ client }} run

# Run training yard.
[group('run')]
training: _sdk
    {{ dotnet }} run --project {{ client }} -- --training

# Connect to TDM (default server).
[group('run')]
online name="Player" weapon="Thompson": _sdk
    {{ dotnet }} run --project {{ client }} -- --online --name "{{ name }}" --weapon "{{ weapon }}"

# Connect to FFA (default server).
[group('run')]
ffa name="Player" weapon="Thompson": _sdk
    {{ dotnet }} run --project {{ client }} -- --online --ffa --name "{{ name }}" --weapon "{{ weapon }}"

# Connect to TDM (local server).
[group('run')]
[unix]
online-local name="Player" weapon="Thompson": _sdk
    IRONSIGHT_SERVER="ws://127.0.0.1:8080/play" {{ dotnet }} run --project {{ client }} -- --online --name "{{ name }}" --weapon "{{ weapon }}"

# Run authoritative server.
[group('run')]
server: _sdk
    {{ dotnet }} run --project {{ server_project }}

# Run server with a map.
[group('run')]
[unix]
server-map level="paintball": _sdk
    IRONSIGHT_LEVEL="{{ level }}" {{ dotnet }} run --project {{ server_project }}

# Restore dependencies.
[group('build')]
restore: _sdk
    {{ dotnet }} restore {{ solution }}

# Build solution.
[group('build')]
build: _sdk
    {{ dotnet }} build {{ solution }}

# Build solution (Release).
[group('build')]
release-build: _sdk
    {{ dotnet }} build {{ solution }} -c Release

# Clean build output.
[group('build')]
clean: _sdk
    {{ dotnet }} clean {{ solution }}

# Build+install .app (macOS, dev).
[group('build')]
[macos]
install: _sdk
    #!/usr/bin/env bash
    set -euo pipefail
    rid=$([ "$(sysctl -n hw.optional.arm64 2>/dev/null)" = "1" ] && echo osx-arm64 || echo osx-x64)
    out="$PWD/.build/install"
    # Clean output dir: incremental publish deletes the loose native dylibs
    # (stale single-file FileWrites), shipping a client that dies at Window.Create.
    rm -rf "$out"
    PATH="$PWD/.dotnet:$PATH" dotnet publish {{ client }} -c Release -r "$rid" --self-contained -o "$out"
    app="/Applications/Ironsight.app"
    rm -rf "$app"
    mkdir -p "$app/Contents/Resources"
    # Single-arch dev install: the publish goes straight into MacOS/, no launcher shim.
    sed "s/@VERSION@/0.0.0-dev/g" packaging/Info.plist > "$app/Contents/Info.plist"
    cp packaging/icon.icns "$app/Contents/Resources/icon.icns"
    cp -R "$out" "$app/Contents/MacOS"
    codesign --force --deep -s - "$app"
    echo "Installed $app ($rid)"

# Remove installed .app.
[group('build')]
[macos]
uninstall:
    rm -rf /Applications/Ironsight.app
    @echo "Removed /Applications/Ironsight.app"

# Format code.
[group('format')]
format: _sdk
    {{ dotnet }} format {{ solution }} --no-restore

# Check formatting.
[group('format')]
lint: _sdk
    {{ dotnet }} format {{ solution }} --verify-no-changes --no-restore

# Run fast tests (no sockets).
[group('test')]
test: _sdk
    {{ dotnet }} test {{ tests }} --nologo --filter "Category!=Integration"

# Run WebSocket smoke tests.
[group('test')]
smoke: _sdk
    {{ dotnet }} test {{ tests }} --nologo --filter "Category=Integration"

# Smoke test a remote server.
[group('test')]
[unix]
smoke-remote server="wss://fsharp-of-duty.fly.dev/play": _sdk
    IRONSIGHT_SMOKE_SERVER="{{ server }}" {{ dotnet }} test {{ tests }} --nologo --filter "FullyQualifiedName~advancing simulation"

# Run tests (Release, no restore).
[group('test')]
test-release: _sdk
    {{ dotnet }} test {{ tests }} -c Release --no-restore --nologo

# Pre-commit gate.
[group('test')]
check: lint build test smoke

# Render a map SVG preview.
[group('tools')]
map-preview level="paintball": _sdk
    {{ dotnet }} build src/Ironsight.Core/Ironsight.Core.fsproj --nologo -v quiet
    {{ dotnet }} fsi tools/MapPreview.fsx "{{ level }}"

# Export built-in maps to .ironmap.
[group('tools')]
map-export dir="": _sdk
    {{ dotnet }} build src/Ironsight.Core/Ironsight.Core.fsproj --nologo -v quiet
    {{ dotnet }} fsi tools/MapExport.fsx {{ dir }}

# Render weapon view previews.
[group('tools')]
gun-preview weapon="all": _sdk
    {{ dotnet }} build src/Ironsight.Core/Ironsight.Core.fsproj --nologo -v quiet
    {{ dotnet }} fsi tools/GunPreview.fsx "{{ weapon }}"

# Regenerate arsenal JSON.
[group('tools')]
arsenal-sync: _sdk
    {{ dotnet }} run --project {{ server_project }} -- --sync-arsenal

# Regenerate the arsenal page's orbitable weapon models.
[group('tools')]
model-sync: _sdk
    {{ dotnet }} run --project {{ server_project }} -- --sync-models

# Build server image.
[group('container')]
docker-build:
    docker build -t {{ image }} .

# Run server image locally.
[group('container')]
docker-run port="8080":
    docker run --rm -p "{{ port }}:8080" {{ image }}

# Validate fly.toml.
[group('deploy')]
fly-validate:
    flyctl config validate

# Deploy server to Fly.io.
[group('deploy')]
fly-deploy:
    flyctl deploy
