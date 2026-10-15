#!/usr/bin/env bash
# Starts PostgreSQL, Keycloak, the backend and the Angular dev server in the background, each logging to
# ~/pharmaos-logs. Runs on every container start; run it again by hand to restart everything.
# DEVELOPMENT ONLY.
set -Eeuo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP="$(cd "$HERE/../pharma-platform" && pwd)"
KC_HOME="${KC_HOME:-$HOME/keycloak-26.8.1}"
LOGS="$HOME/pharmaos-logs"
mkdir -p "$LOGS"

# The addresses the browser uses. In Codespaces every forwarded port has its own https host name; the backend
# and Keycloak only see localhost, so they are told the public addresses explicitly.
if [[ -n "${CODESPACE_NAME:-}" ]]; then
  domain="${GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN:-app.github.dev}"
  APP_URL="https://$CODESPACE_NAME-4200.$domain"
  KC_URL="https://$CODESPACE_NAME-8080.$domain"
fi
APP_URL="${PHARMAOS_APP_URL:-${APP_URL:-http://localhost:4200}}"
KC_URL="${PHARMAOS_KC_URL:-${KC_URL:-http://localhost:8080}}"

DLL="$APP/src/Hosts/PharmaOS.Platform.Host/bin/Release/net10.0/PharmaOS.Platform.Host.dll"
if [[ ! -f "$DLL" || ! -f "$APP/pharmaos.env" || ! -x "$KC_HOME/bin/kc.sh" ]]; then
  echo "PharmaOS is not installed yet (or the installer failed). Run: bash .devcontainer/setup.sh" >&2
  exit 1
fi

stop() {
  if [[ -f "$LOGS/$1.pid" ]]; then kill -- -"$(cat "$LOGS/$1.pid")" 2>/dev/null || true; rm -f "$LOGS/$1.pid"; fi
}
launch() { # launch NAME CMD... : own process group, so stop() also ends its children
  local name="$1"; shift
  stop "$name"
  setsid nohup "$@" >"$LOGS/$name.log" 2>&1 </dev/null &
  echo $! >"$LOGS/$name.pid"
}
wait_for() { # wait_for LABEL URL SECONDS LOGNAME
  for _ in $(seq "$3"); do
    if curl -fs -o /dev/null "$2"; then echo "  ✓ $1"; return 0; fi
    sleep 1
  done
  echo "  ✗ $1 did not start in $3 s; see $LOGS/$4.log" >&2
  return 1
}

echo "== Starting PharmaOS (logs in $LOGS) =="
sudo service postgresql start >/dev/null

# Keycloak imports the realm only once, so the redirect addresses of this codespace are added before the first start.
mkdir -p "$KC_HOME/data/import"
python3 - "$APP/deploy/keycloak/dev-import/pharmaos-realm.json" "$KC_HOME/data/import/pharmaos-realm.json" "$APP_URL" <<'EOF'
import json, sys
src, dst, origin = sys.argv[1:]
realm = json.load(open(src))
for client in realm["clients"]:
    if client["clientId"] == "pharmaos-web":
        if origin + "/signin-oidc" not in client["redirectUris"]:
            client["redirectUris"].append(origin + "/signin-oidc")
            client["attributes"]["post.logout.redirect.uris"] += "##" + origin + "/signout-callback-oidc"
json.dump(realm, open(dst, "w"), indent=2)
EOF
# Browsers use the public address; the backend reaches Keycloak on localhost (backchannel).
launch keycloak env KC_BOOTSTRAP_ADMIN_USERNAME=admin KC_BOOTSTRAP_ADMIN_PASSWORD=admin \
  "$KC_HOME/bin/kc.sh" start-dev --import-realm --http-port 8080 --hostname "$KC_URL" --hostname-backchannel-dynamic true

# shellcheck disable=SC2016 # $1 and $2 expand in the inner shell
launch backend env ASPNETCORE_ENVIRONMENT=Development \
  Oidc__Authority="$KC_URL/realms/pharmaos" \
  Oidc__MetadataAddress="http://localhost:8080/realms/pharmaos/.well-known/openid-configuration" \
  Oidc__PublicOrigin="$APP_URL" \
  bash -c 'set -a; source "$1"; set +a; exec dotnet "$2" --contentRoot "$(dirname "$2")" --urls http://localhost:5080' \
  _ "$APP/pharmaos.env" "$DLL"

cd "$APP/web"
[[ -d node_modules ]] || npm ci --no-audit --no-fund >"$LOGS/npm-ci.log" 2>&1
launch web env NG_CLI_ANALYTICS=false npx ng serve --configuration codespaces --port 4200

wait_for "Keycloak" "http://localhost:8080/realms/pharmaos" 120 keycloak
wait_for "backend and database" "http://localhost:5080/health/ready" 60 backend
wait_for "screens" "http://localhost:4200/" 240 web

cat <<EOF

PharmaOS is running: $APP_URL
  Sign in as qa.analyst, qa.approver or read.only, password Dev-Only-Pass-2026
  Keycloak admin console: $KC_URL (admin / admin)
  Restart everything: bash .devcontainer/start.sh
EOF
