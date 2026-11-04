#!/usr/bin/env bash
# Prepares an initialised, unsealed OpenBao for PharmaOS package signing. Safe to run again: it creates only what is missing.
#
#   BAO_ADDR=http://127.0.0.1:8200 BAO_TOKEN=<admin token> ./openbao-bootstrap.sh [--new-secret-id]
#
# It sets up, in OpenBao:
#   - the transit engine and one signing key, pharmaos-packages (ECDSA P-256, NOT exportable: the private key never leaves OpenBao)
#   - a policy that lets its holder read that key's public part, sign and verify with it, and nothing else
#   - an AppRole, pharmaos-host, with that policy; the platform host logs in with it and gets short-lived tokens
# It prints the role id, and with --new-secret-id a new secret id, for pharmaos.env (Keys__AppRoleId, Keys__AppRoleSecretId).
# The admin token is used only here and is not stored. Use a short-lived one (or the root token from `bao operator init`, then revoke it).
set -euo pipefail

: "${BAO_ADDR:?set BAO_ADDR, e.g. http://127.0.0.1:8200}"
: "${BAO_TOKEN:?set BAO_TOKEN to an admin token}"
KEY="${PACKAGE_KEY:-pharmaos-packages}"
POLICY="pharmaos-signer"
ROLE="pharmaos-host"
NEW_SECRET=0
[ "${1:-}" = "--new-secret-id" ] && NEW_SECRET=1

call() { # method path [json]  -> prints body; fails on HTTP errors with OpenBao's own message
  local method="$1" path="$2" body="${3:-}" out code
  out="$(mktemp)"
  code="$(curl -sS -o "$out" -w '%{http_code}' -X "$method" -H "X-Vault-Token: $BAO_TOKEN" -H 'Content-Type: application/json' \
          ${body:+--data "$body"} "$BAO_ADDR/v1/$path")" || { rm -f "$out"; echo "cannot reach OpenBao at $BAO_ADDR" >&2; exit 1; }
  if [ "$code" -ge 400 ]; then
    echo "OpenBao answered $code for $method $path: $(cat "$out")" >&2; rm -f "$out"; return 22
  fi
  cat "$out"; rm -f "$out"
}
exists() { call GET "$1" >/dev/null 2>&1; }
# Reads a JSON answer from stdin: json <dotted.path> prints the value; json-has <key> exits 0 if the key is present in .data or at top level.
json() { python3 -I -c 'import json,sys;d=json.load(sys.stdin)
for k in sys.argv[1].split("."): d=d.get(k) if isinstance(d,dict) else None
print("" if d is None else d)' "$1"; }
json_has() { python3 -I -c 'import json,sys;d=json.load(sys.stdin);sys.exit(0 if (d.get("data") or {}).get(sys.argv[1]) is not None or d.get(sys.argv[1]) is not None else 1)' "$1"; }

call GET sys/health >/dev/null 2>&1 || true
if ! call GET sys/mounts >/dev/null; then echo "The admin token was refused, or OpenBao is sealed." >&2; exit 1; fi

if exists sys/mounts/transit/tune; then echo "transit engine: already enabled"
else call POST sys/mounts/transit '{"type":"transit"}' >/dev/null; echo "transit engine: enabled"; fi

if exists "transit/keys/$KEY"; then
  [ "$(call GET "transit/keys/$KEY" | json data.type)" = "ecdsa-p256" ] || { echo "key $KEY exists but is not ecdsa-p256" >&2; exit 1; }
  echo "signing key $KEY: already exists"
else
  call POST "transit/keys/$KEY" '{"type":"ecdsa-p256","exportable":false,"allow_plaintext_backup":false}' >/dev/null
  echo "signing key $KEY: created (not exportable)"
fi

HCL="path \\\"transit/keys/$KEY\\\" {\\n  capabilities = [\\\"read\\\"]\\n}\\npath \\\"transit/sign/$KEY\\\" {\\n  capabilities = [\\\"update\\\"]\\n}\\npath \\\"transit/sign/$KEY/*\\\" {\\n  capabilities = [\\\"update\\\"]\\n}\\npath \\\"transit/verify/$KEY\\\" {\\n  capabilities = [\\\"update\\\"]\\n}\\npath \\\"transit/verify/$KEY/*\\\" {\\n  capabilities = [\\\"update\\\"]\\n}\\n"
call PUT "sys/policies/acl/$POLICY" "{\"policy\":\"$HCL\"}" >/dev/null
echo "policy $POLICY: written"

if call GET sys/auth | json_has "approle/"; then echo "approle auth: already enabled"
else call POST sys/auth/approle '{"type":"approle"}' >/dev/null; echo "approle auth: enabled"; fi

call POST "auth/approle/role/$ROLE" "{\"token_policies\":[\"$POLICY\"],\"token_ttl\":\"1h\",\"token_max_ttl\":\"4h\",\"secret_id_ttl\":\"0\",\"secret_id_num_uses\":0,\"token_num_uses\":0}" >/dev/null
echo "role $ROLE: written (tokens last 1 hour; the host logs in again on its own)"

echo
echo "Keys__AppRoleId=$(call GET "auth/approle/role/$ROLE/role-id" | json data.role_id)"
if [ "$NEW_SECRET" = 1 ]; then
  echo "Keys__AppRoleSecretId=$(call POST "auth/approle/role/$ROLE/secret-id" | json data.secret_id)"
  echo "(Shown once. Put both in pharmaos.env. Older secret ids stay valid until you destroy them in OpenBao.)"
else
  echo "Run again with --new-secret-id to create the secret id."
fi
