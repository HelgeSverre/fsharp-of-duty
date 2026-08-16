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

# Connect TDM to the configured server (Fly.io by default).
[group('run')]
online name="Player" weapon="Thompson": _sdk
    {{ dotnet }} run --project {{ client }} -- --online --name "{{ name }}" --weapon "{{ weapon }}"

# Connect FFA to the configured server (Fly.io by default).
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

# Format F# and project files.
[group('format')]
format: _sdk
    {{ dotnet }} format {{ solution }} --no-restore

# Check formatting without changing files.
[group('format')]
lint: _sdk
    {{ dotnet }} format {{ solution }} --verify-no-changes --no-restore

# Run the complete headless test suite.
[group('test')]
test: _sdk
    {{ dotnet }} test {{ tests }} --nologo

# Run tests in release mode without restoring.
[group('test')]
test-release: _sdk
    {{ dotnet }} test {{ tests }} -c Release --no-restore --nologo

# Local pre-commit gate.
[group('test')]
check: lint build test

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
