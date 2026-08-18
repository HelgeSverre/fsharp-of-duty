client := "src/Ironsight/Ironsight.fsproj"
server_project := "src/Ironsight.Server/Ironsight.Server.fsproj"
tests := "tests/Ironsight.Tests/Ironsight.Tests.fsproj"
solution := "Ironsight.sln"
image := "ironsight-server:local"
dotnet := "PATH=\"$PWD/.dotnet:$PATH\" dotnet"

[private]
default:
    @just --list

# Ensure an SDK accepted by global.json is available. As in fedit, an existing
# compatible system SDK wins; otherwise the SDK is installed locally in .dotnet/.
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

# Run the default player-vs-four Paintball Killhouse.
[group('run')]
run: _sdk
    {{ dotnet }} run --project {{ client }}

# Watch source files and restart the default client.
[group('run')]
dev: _sdk
    {{ dotnet }} watch --project {{ client }} run

# Run the large generated Normandy battlefield.
[group('run')]
battlefield: _sdk
    {{ dotnet }} run --project {{ client }} -- --battlefield

# Run the training yard.
[group('run')]
training: _sdk
    {{ dotnet }} run --project {{ client }} -- --training

# Run the Stalingrad street mission.
[group('run')]
stalingrad: _sdk
    {{ dotnet }} run --project {{ client }} -- --stalingrad

# Connect TDM to the configured server (the official server by default).
[group('run')]
online name="Player" weapon="Thompson": _sdk
    {{ dotnet }} run --project {{ client }} -- --online --name "{{ name }}" --weapon "{{ weapon }}"

# Connect FFA to the configured server (the official server by default).
[group('run')]
ffa name="Player" weapon="Thompson": _sdk
    {{ dotnet }} run --project {{ client }} -- --online --ffa --name "{{ name }}" --weapon "{{ weapon }}"

# Connect TDM to a local server on port 8080.
[group('run')]
[unix]
online-local name="Player" weapon="Thompson": _sdk
    IRONSIGHT_SERVER="ws://127.0.0.1:8080/play" {{ dotnet }} run --project {{ client }} -- --online --name "{{ name }}" --weapon "{{ weapon }}"

# Run the authoritative server with the default Paintball Killhouse.
[group('run')]
server: _sdk
    {{ dotnet }} run --project {{ server_project }}

# Run the authoritative server with a selected generated map.
[group('run')]
[unix]
server-map level="paintball": _sdk
    IRONSIGHT_LEVEL="{{ level }}" {{ dotnet }} run --project {{ server_project }}

# Restore all dependencies.
[group('build')]
restore: _sdk
    {{ dotnet }} restore {{ solution }}

# Build the solution.
[group('build')]
build: _sdk
    {{ dotnet }} build {{ solution }}

# Build the release configuration.
[group('build')]
release-build: _sdk
    {{ dotnet }} build {{ solution }} -c Release

# Remove MSBuild output.
[group('build')]
clean: _sdk
    {{ dotnet }} clean {{ solution }}

# Build a release .app for this Mac and install it to /Applications (dev only).
[group('build')]
[macos]
install: _sdk
    #!/usr/bin/env bash
    set -euo pipefail
    rid=$([ "$(sysctl -n hw.optional.arm64 2>/dev/null)" = "1" ] && echo osx-arm64 || echo osx-x64)
    out="$PWD/.build/install"
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

# Remove the locally installed .app.
[group('build')]
[macos]
uninstall:
    rm -rf /Applications/Ironsight.app
    @echo "Removed /Applications/Ironsight.app"

# Format F# and project files.
[group('format')]
format: _sdk
    {{ dotnet }} format {{ solution }} --no-restore

# Check formatting without changing files.
[group('format')]
lint: _sdk
    {{ dotnet }} format {{ solution }} --verify-no-changes --no-restore

# Run the fast headless test suite (excludes the socket integration tests).
[group('test')]
test: _sdk
    {{ dotnet }} test {{ tests }} --nologo --filter "Category!=Integration"

# Drive scripted matches over real WebSockets against an in-process server.
[group('test')]
smoke: _sdk
    {{ dotnet }} test {{ tests }} --nologo --filter "Category=Integration"

# Smoke test a deployed server. Connects a live bot, so it joins the public room.
[group('test')]
[unix]
smoke-remote server="wss://fsharp-of-duty.fly.dev/play": _sdk
    IRONSIGHT_SMOKE_SERVER="{{ server }}" {{ dotnet }} test {{ tests }} --nologo --filter "FullyQualifiedName~advancing simulation"

# Run tests in release mode without restoring.
[group('test')]
test-release: _sdk
    {{ dotnet }} test {{ tests }} -c Release --no-restore --nologo

# Local pre-commit gate.
[group('test')]
check: lint build test smoke

# Draw a level's plan and elevation to map-preview.svg.
[group('tools')]
map-preview level="paintball": _sdk
    {{ dotnet }} build src/Ironsight.Core/Ironsight.Core.fsproj --nologo -v quiet
    {{ dotnet }} fsi tools/map-preview.fsx "{{ level }}"

# Build the Fly-compatible dedicated-server image.
[group('container')]
docker-build:
    docker build -t {{ image }} .

# Run the dedicated-server image locally.
[group('container')]
docker-run port="8080":
    docker run --rm -p "{{ port }}:8080" {{ image }}

# Validate fly.toml without deploying.
[group('deploy')]
fly-validate:
    flyctl config validate

# Deploy the authoritative server to Fly.io.
[group('deploy')]
fly-deploy:
    flyctl deploy
