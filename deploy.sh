#!/usr/bin/env bash
# Stand up an IRONSIGHT dedicated server on a fresh VPS.
#
#   curl -fsSL https://raw.githubusercontent.com/HelgeSverre/fsharp-of-duty/main/deploy.sh | sudo bash
#
# Pulls the self-contained linux-x64 server from the latest GitHub release,
# installs it under /opt, runs it as an unprivileged systemd service, and tells
# you the URL to put in servers.json. No .NET, no Docker, no cloned repo.
#
#   --domain example.com   also front it with Caddy for automatic HTTPS, which
#                          is what the in-game browser needs (it wants wss://)
#   --port 8080            listen port (default 8080)
#   --version v0.0.4       pin a release instead of taking the latest
set -euo pipefail

REPO="HelgeSverre/fsharp-of-duty"
INSTALL_DIR="/opt/ironsight-server"
SERVICE="ironsight-server"
SERVICE_USER="ironsight"
PORT=8080
DOMAIN=""
VERSION="latest"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --domain) DOMAIN="${2:?--domain needs a hostname}"; shift 2 ;;
        --port) PORT="${2:?--port needs a number}"; shift 2 ;;
        --version) VERSION="${2:?--version needs a tag}"; shift 2 ;;
        -h|--help) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done

die() { echo "error: $*" >&2; exit 1; }
step() { echo; echo "==> $*"; }

[[ $EUID -eq 0 ]] || die "run as root (sudo bash deploy.sh)"
command -v systemctl >/dev/null || die "no systemd; use the Dockerfile instead (see docs/MULTIPLAYER.md)"
[[ "$(uname -m)" == "x86_64" ]] || die "the published server build is linux-x64; this box is $(uname -m)"
command -v curl >/dev/null || die "curl is required"
command -v tar >/dev/null || die "tar is required"

if [[ "$VERSION" == "latest" ]]; then
    URL="https://github.com/$REPO/releases/latest/download/ironsight-server-linux-x64.tar.gz"
else
    URL="https://github.com/$REPO/releases/download/$VERSION/ironsight-server-linux-x64.tar.gz"
fi

step "Downloading the server ($VERSION)"
TEMP="$(mktemp -d)"
trap 'rm -rf "$TEMP"' EXIT
curl -fsSL "$URL" -o "$TEMP/server.tar.gz" \
    || die "could not fetch $URL — is there a published release yet?"

step "Installing to $INSTALL_DIR"
id -u "$SERVICE_USER" >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
# Stop before overwriting so a re-run upgrades in place rather than writing
# under a running process.
systemctl stop "$SERVICE" 2>/dev/null || true
mkdir -p "$INSTALL_DIR"
tar -xzf "$TEMP/server.tar.gz" -C "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/Ironsight.Server"
# The service owns its directory so it can write a chat log or ban list there.
chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR"

step "Writing the systemd unit"
cat > "/etc/systemd/system/$SERVICE.service" <<UNIT
[Unit]
Description=IRONSIGHT dedicated server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$SERVICE_USER
WorkingDirectory=$INSTALL_DIR
Environment=PORT=$PORT
# Uncomment to name the server, set a map, or enable op commands.
# A server.json beside the binary configures rooms; see docs/MULTIPLAYER.md.
#Environment=IRONSIGHT_LEVEL=omaha
#Environment=IRONSIGHT_OP_KEY=change-me
ExecStart=$INSTALL_DIR/Ironsight.Server
Restart=always
RestartSec=3
# It only needs to listen on a port and read its own directory.
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=$INSTALL_DIR

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable --now "$SERVICE" >/dev/null

step "Waiting for the health check"
for _ in $(seq 1 30); do
    if curl -fsS "http://127.0.0.1:$PORT/health/ready" >/dev/null 2>&1; then
        READY=1
        break
    fi
    sleep 1
done
[[ "${READY:-}" == "1" ]] || {
    journalctl -u "$SERVICE" -n 30 --no-pager || true
    die "the server did not become ready; logs above"
}
echo "the server is up on port $PORT"

if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "Status: active"; then
    step "Opening port $PORT in ufw"
    ufw allow "$PORT/tcp" >/dev/null
fi

PUBLIC_URL="ws://$(curl -fsS --max-time 5 https://api.ipify.org 2>/dev/null || hostname -I | awk '{print $1}'):$PORT/play"

if [[ -n "$DOMAIN" ]]; then
    step "Setting up HTTPS for $DOMAIN with Caddy"
    # The in-game browser connects over wss://, so a plain-http server cannot be
    # listed. Caddy gets a Let's Encrypt certificate on its own, which is the
    # shortest path from "it runs" to "people can actually join".
    if ! command -v caddy >/dev/null; then
        apt-get update -qq
        apt-get install -y -qq debian-keyring debian-archive-keyring apt-transport-https curl gnupg
        curl -fsSL https://dl.cloudsmith.io/public/caddy/stable/gpg.key \
            | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
        echo "deb [signed-by=/usr/share/keyrings/caddy-stable-archive-keyring.gpg] https://dl.cloudsmith.io/public/caddy/stable/deb/debian any-version main" \
            > /etc/apt/sources.list.d/caddy-stable.list
        apt-get update -qq
        apt-get install -y -qq caddy
    fi
    cat > /etc/caddy/Caddyfile <<CADDY
$DOMAIN {
	reverse_proxy 127.0.0.1:$PORT
}
CADDY
    systemctl reload caddy || systemctl restart caddy
    command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "Status: active" && ufw allow 80/tcp >/dev/null && ufw allow 443/tcp >/dev/null || true
    PUBLIC_URL="wss://$DOMAIN/play"
fi

cat <<DONE

==> Done.

  Server URL   $PUBLIC_URL
  Logs         journalctl -u $SERVICE -f
  Restart      systemctl restart $SERVICE
  Upgrade      re-run this script
  Config       $INSTALL_DIR/server.json (rooms, name, MOTD) — see docs/MULTIPLAYER.md

To list it in the in-game browser, open a PR adding this to servers.json:

  { "name": "My Server", "url": "$PUBLIC_URL" }
DONE

if [[ -z "$DOMAIN" ]]; then
    cat <<'WARN'

Note: the browser's community list only accepts wss:// URLs, and this server is
plain ws://. Re-run with --domain example.com (pointing an A record here first)
to have Caddy fetch a certificate, or put it behind your own TLS proxy.
WARN
fi
