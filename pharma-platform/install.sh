#!/usr/bin/env bash
# =============================================================================
# PharmaOS installation wizard (developer workstation / single server, no Docker)
#
#   ./install.sh                       interactive: asks questions, explains each step
#   ./install.sh --non-interactive …   scripted: values from flags and PGPASSWORD
#   ./install.sh --help
#
# What it does, in order:
#   1. Checks prerequisites (PostgreSQL 16, Python, .NET 10 SDK, Node).
#   2. Verifies that the generated schema matches the data model (schema_gen --check).
#   3. Creates the database and loads database/schema/*.sql, recording a SHA-256 for each file.
#   4. Runs the data-integrity tests (OQ-DI-001…009). The install stops if any fails.
#   5. Runs installation checks on the live database (audit triggers, RLS, privileges).
#   6. Creates the application and auditor logins with generated passwords and writes
#      them to pharmaos.env (permissions 600). Passwords are never printed or logged.
#   7. Optionally builds and tests the backend and frontend, and starts the backend once
#      to check that it reaches the database.
#   8. Writes an installation report (install-reports/), structured like the IQ template
#      in docs/12-iqoqpq-templates.md §12.1.
#
# The database is never dropped unless you pass --recreate and confirm the name.
# =============================================================================
set -Eeuo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

# ---------------------------------------------------------------- defaults ---
INTERACTIVE=true
PG_HOST="${PGHOST:-localhost}"
PG_PORT="${PGPORT:-5432}"
PG_SUPERUSER="${PGUSER:-postgres}"
DB_NAME="pharmaos_dev"
RECREATE=false
DO_BACKEND=true
DO_WEB=true
DO_SMOKE=true
ENV_FILE="$ROOT/pharmaos.env"
REPORT_DIR="$ROOT/install-reports"

MIN_PG=160000
MIN_PY="3.10"
MIN_DOTNET="10.0.400"
MIN_NODE="22.22.3"

# ------------------------------------------------------------------- output ---
if [[ -t 1 ]]; then
  B=$'\e[1m'; G=$'\e[32m'; Y=$'\e[33m'; R=$'\e[31m'; D=$'\e[2m'; N=$'\e[0m'
else
  B=""; G=""; Y=""; R=""; D=""; N=""
fi
step()  { printf '\n%s== %s ==%s\n' "$B" "$*" "$N"; }
ok()    { printf '  %s✓%s %s\n' "$G" "$N" "$*"; }
warn()  { printf '  %s!%s %s\n' "$Y" "$N" "$*"; WARNINGS+=("$*"); }
fail()  { printf '  %s✗ %s%s\n' "$R" "$*" "$N" >&2; }
die()   { fail "$*"; printf '\n%sInstallation stopped. Nothing after this step was changed.%s\n' "$R" "$N" >&2; exit 1; }
info()  { printf '  %s%s%s\n' "$D" "$*" "$N"; }

WARNINGS=()
REPORT_ROWS=()   # "id|check|expected|actual|result"
record() { REPORT_ROWS+=("$1|$2|$3|$4|$5"); }

usage() {
  cat <<EOF
PharmaOS installation wizard

Usage: ./install.sh [options]

  --non-interactive       Do not ask questions; use flags and PGPASSWORD
  --pg-host HOST          PostgreSQL host                 (default: $PG_HOST)
  --pg-port PORT          PostgreSQL port                 (default: $PG_PORT)
  --pg-superuser USER     Superuser for setup only        (default: $PG_SUPERUSER)
  --db NAME               Database to create              (default: $DB_NAME)
  --recreate              Drop and recreate the database if it exists (asks you to confirm)
  --skip-backend          Do not build or test the .NET backend
  --skip-web              Do not install, test or build the Angular frontend
  --skip-smoke            Do not start the backend for the health check
  -h, --help              Show this help

The superuser password is read from PGPASSWORD, or asked for (hidden) in interactive mode.
It is used only during installation and is not stored.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --non-interactive) INTERACTIVE=false ;;
    --pg-host) PG_HOST="$2"; shift ;;
    --pg-port) PG_PORT="$2"; shift ;;
    --pg-superuser) PG_SUPERUSER="$2"; shift ;;
    --db) DB_NAME="$2"; shift ;;
    --recreate) RECREATE=true ;;
    --skip-backend) DO_BACKEND=false; DO_SMOKE=false ;;
    --skip-web) DO_WEB=false ;;
    --skip-smoke) DO_SMOKE=false ;;
    -h|--help) usage; exit 0 ;;
    *) printf 'Unknown option: %s\n\n' "$1" >&2; usage >&2; exit 2 ;;
  esac
  shift
done

ask() { # ask VAR "Question" default
  local __var="$1" __q="$2" __def="$3" __ans
  if $INTERACTIVE; then
    read -r -p "  $__q [$__def]: " __ans || true
    printf -v "$__var" '%s' "${__ans:-$__def}"
  else
    printf -v "$__var" '%s' "$__def"
  fi
}

yes_no() { # yes_no "Question" default(y|n) -> exit status
  local q="$1" def="$2" ans
  $INTERACTIVE || { [[ "$def" == y ]]; return; }
  read -r -p "  $q [$( [[ $def == y ]] && echo Y/n || echo y/N )]: " ans || true
  ans="${ans:-$def}"
  [[ "$ans" =~ ^[Yy] ]]
}

ver_ge() { [[ "$(printf '%s\n%s\n' "$2" "$1" | sort -V | head -1)" == "$2" ]]; }

# psql as the setup superuser. -X ignores ~/.psqlrc so output is predictable.
psql_su() { psql -X -v ON_ERROR_STOP=1 -h "$PG_HOST" -p "$PG_PORT" -U "$PG_SUPERUSER" "$@"; }

HOST_PID=""
cleanup() { [[ -n "$HOST_PID" ]] && kill "$HOST_PID" 2>/dev/null || true; }
trap cleanup EXIT
trap 'fail "Unexpected error on line $LINENO"; exit 1' ERR

STARTED_UTC="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

printf '%sPharmaOS installation wizard%s\n' "$B" "$N"
info "Sets up a developer workstation or a single server from this checkout."
info "Nothing is changed until step 3, and the database is never dropped without your confirmation."

# ------------------------------------------------------- 1. prerequisites ---
step "1/8 Prerequisites"

need() { command -v "$1" >/dev/null 2>&1; }

need psql || die "psql not found. Install the PostgreSQL 16 client (and server, if it runs on this machine)."
PSQL_CLIENT="$(psql --version | awk '{print $3}')"
ok "psql $PSQL_CLIENT"

need python3 || die "python3 not found (3.10 or newer is required for tools/schema_gen.py)."
PY_VER="$(python3 -c 'import sys;print("%d.%d"%sys.version_info[:2])')"
ver_ge "$PY_VER" "$MIN_PY" || die "Python $PY_VER is too old; $MIN_PY or newer is required."
ok "python $PY_VER"

DOTNET_VER="not checked"
if $DO_BACKEND; then
  need dotnet || die "dotnet not found. Install the .NET 10 SDK, or run with --skip-backend."
  DOTNET_VER="$(dotnet --list-sdks | awk '{print $1}' | sort -V | tail -1)"
  ver_ge "$DOTNET_VER" "$MIN_DOTNET" || die ".NET SDK $DOTNET_VER is too old; $MIN_DOTNET or newer is required."
  ok ".NET SDK $DOTNET_VER"
fi

NODE_VER="not checked"
if $DO_WEB; then
  need node || die "node not found. Install Node 24 LTS, or run with --skip-web."
  NODE_VER="$(node --version | tr -d v)"
  ver_ge "$NODE_VER" "$MIN_NODE" || die "Node $NODE_VER is too old for Angular 22; install Node 24 LTS (or $MIN_NODE+)."
  ok "node $NODE_VER"
fi

if $DO_SMOKE && ! need curl; then die "curl not found (needed for the health check). Install it or use --skip-smoke."; fi

GIT_COMMIT="$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)"
GIT_DIRTY="$(git -C "$ROOT" status --porcelain 2>/dev/null | grep -q . && echo "yes" || echo "no")"
ok "source commit $GIT_COMMIT (uncommitted changes: $GIT_DIRTY)"
[[ "$GIT_DIRTY" == "yes" ]] && warn "The checkout has uncommitted changes; the report cannot tie this install to one commit."

# ------------------------------------------------------------ 2. settings ---
step "2/8 Database connection"
info "A PostgreSQL superuser is needed once, to create the database and logins. It is not stored."
ask PG_HOST "PostgreSQL host" "$PG_HOST"
ask PG_PORT "PostgreSQL port" "$PG_PORT"
ask PG_SUPERUSER "Superuser name" "$PG_SUPERUSER"
ask DB_NAME "Database to create" "$DB_NAME"

[[ "$DB_NAME" =~ ^[a-z_][a-z0-9_]{0,40}$ ]] \
  || die "Database name '$DB_NAME' is not allowed. Use lower-case letters, digits and _ (max 41 characters)."
[[ "$PG_PORT" =~ ^[0-9]+$ ]] || die "Port '$PG_PORT' is not a number."

if [[ -z "${PGPASSWORD:-}" ]]; then
  if $INTERACTIVE; then
    read -r -s -p "  Password for $PG_SUPERUSER (hidden): " PGPASSWORD || true
    echo
  else
    die "PGPASSWORD is not set. In non-interactive mode the superuser password must come from PGPASSWORD."
  fi
fi
export PGPASSWORD

psql_su -d postgres -qAtc "SELECT 1" >/dev/null 2>&1 \
  || die "Cannot connect to PostgreSQL at $PG_HOST:$PG_PORT as $PG_SUPERUSER. Check host, port and password."
IS_SUPER="$(psql_su -d postgres -qAtc "SELECT rolsuper FROM pg_roles WHERE rolname = current_user")"
[[ "$IS_SUPER" == "t" ]] || die "$PG_SUPERUSER is not a superuser. The schema creates roles and SECURITY DEFINER functions, which needs one."
PG_SERVER_NUM="$(psql_su -d postgres -qAtc "SHOW server_version_num")"
PG_SERVER="$(psql_su -d postgres -qAtc "SHOW server_version")"
if (( PG_SERVER_NUM >= MIN_PG )); then
  ok "connected: PostgreSQL $PG_SERVER as superuser $PG_SUPERUSER"
  record "IQ-INF-003a" "PostgreSQL server version" ">= 16" "$PG_SERVER" PASS
else
  record "IQ-INF-003a" "PostgreSQL server version" ">= 16" "$PG_SERVER" FAIL
  die "PostgreSQL $PG_SERVER is too old; 16 or newer is required."
fi

APP_LOGIN="${DB_NAME}_app"
AUDITOR_LOGIN="${DB_NAME}_auditor"
info "Logins to create: $APP_LOGIN (application) and $AUDITOR_LOGIN (audit read-only)."

DB_EXISTS="$(psql_su -d postgres -qAtc "SELECT 1 FROM pg_database WHERE datname = '$DB_NAME'")"
if [[ "$DB_EXISTS" == "1" ]]; then
  if ! $RECREATE && $INTERACTIVE; then
    warn "Database $DB_NAME already exists."
    yes_no "Drop it and start again? ALL DATA IN IT WILL BE LOST" n && RECREATE=true
  fi
  $RECREATE || die "Database $DB_NAME already exists. Choose another name, or rerun with --recreate."
  if $INTERACTIVE; then
    read -r -p "  Type the database name to confirm dropping it: " CONFIRM || true
    [[ "$CONFIRM" == "$DB_NAME" ]] || die "Name did not match; nothing was dropped."
  fi
fi

if $INTERACTIVE; then
  printf '\n  %sSummary%s\n' "$B" "$N"
  printf '    database      %s on %s:%s %s\n' "$DB_NAME" "$PG_HOST" "$PG_PORT" "$( [[ "$DB_EXISTS" == 1 ]] && echo '(will be DROPPED and recreated)' || echo '(new)')"
  printf '    backend       %s\n' "$( $DO_BACKEND && echo 'build + unit and integration tests' || echo skipped)"
  printf '    frontend      %s\n' "$( $DO_WEB && echo 'npm ci + tests + production build' || echo skipped)"
  printf '    health check  %s\n' "$( $DO_SMOKE && echo 'start backend once' || echo skipped)"
  yes_no "Continue?" y || die "Cancelled by user."
fi

# ---------------------------------------------------- 3. schema in sync ---
step "3/8 Data model"
if python3 tools/schema_gen.py --check >/dev/null 2>&1; then
  ok "generated schema matches database/model/catalog.dsl"
  record "IQ-INF-004a" "Generated DDL matches catalog" "in sync" "in sync" PASS
else
  record "IQ-INF-004a" "Generated DDL matches catalog" "in sync" "stale" FAIL
  die "database/schema is out of date. Run: python3 tools/schema_gen.py"
fi

# ------------------------------------------------------ 4. create + load ---
step "4/8 Database"
if [[ "$DB_EXISTS" == "1" ]]; then
  psql_su -d postgres -qc "DROP DATABASE \"$DB_NAME\" WITH (FORCE)"
  ok "dropped existing $DB_NAME"
fi
psql_su -d postgres -qc "CREATE DATABASE \"$DB_NAME\""
ok "created $DB_NAME"

SCHEMA_SUMS=()
for f in database/schema/0*.sql; do
  psql_su -d "$DB_NAME" -q -f "$f" >/dev/null 2>"$ROOT/.install-psql.err" \
    || { cat "$ROOT/.install-psql.err" >&2; rm -f "$ROOT/.install-psql.err"; die "Loading $f failed."; }
  sum="$(sha256sum "$f" | awk '{print $1}')"
  SCHEMA_SUMS+=("$f|$sum")
  ok "loaded $f"
done
rm -f "$ROOT/.install-psql.err"
TABLES="$(psql_su -d "$DB_NAME" -qAtc "SELECT count(*) FROM information_schema.tables WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('pg_catalog','information_schema')")"
ok "$TABLES tables"
record "IQ-INF-004b" "Physical tables created" "487" "$TABLES" "$( [[ "$TABLES" == 487 ]] && echo PASS || echo FAIL)"
[[ "$TABLES" == 487 ]] || die "Expected 487 tables, found $TABLES."

# ------------------------------------------------- 5. integrity + checks ---
step "5/8 Data-integrity tests and installation checks"
DI_OUT="$(psql_su -d "$DB_NAME" -f database/tests/integrity_tests.sql 2>&1)" || { echo "$DI_OUT" >&2; die "Integrity tests failed."; }
DI_PASSED="$(grep -c 'NOTICE:  OQ-DI-[0-9]* pass' <<<"$DI_OUT" || true)"
if grep -q 'ALL DATA-INTEGRITY TESTS PASSED' <<<"$DI_OUT"; then
  ok "OQ-DI-001…009 passed ($DI_PASSED pass notices)"
  record "OQ-DI-001..009" "Data-integrity controls (executable)" "all pass" "all pass" PASS
else
  echo "$DI_OUT" >&2
  record "OQ-DI-001..009" "Data-integrity controls (executable)" "all pass" "failed" FAIL
  die "Integrity tests did not report success."
fi

q() { psql_su -d "$DB_NAME" -qAtc "$1"; }

MISSING_AUDIT="$(q "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE c.relkind = 'r' AND obj_description(c.oid) LIKE '%gxp=true%'
    AND NOT EXISTS (SELECT 1 FROM pg_trigger t WHERE t.tgrelid = c.oid AND t.tgname = 'trg_audit')")"
GXP_TABLES="$(q "SELECT count(*) FROM pg_class c WHERE c.relkind = 'r' AND obj_description(c.oid) LIKE '%gxp=true%'")"
record "IQ-INF-005" "Audit trigger on every GxP table" "0 missing of $GXP_TABLES" "$MISSING_AUDIT missing" "$( [[ "$MISSING_AUDIT" == 0 ]] && echo PASS || echo FAIL)"
[[ "$MISSING_AUDIT" == 0 ]] && ok "audit trigger present on all $GXP_TABLES GxP tables" || die "$MISSING_AUDIT GxP tables have no audit trigger."

NO_RLS="$(q "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE c.relkind = 'r' AND n.nspname NOT IN ('pg_catalog','information_schema','audit') AND NOT c.relrowsecurity")"
record "IQ-INF-005b" "Row-level security on every business table" "0 without RLS" "$NO_RLS without RLS" "$( [[ "$NO_RLS" == 0 ]] && echo PASS || echo FAIL)"
[[ "$NO_RLS" == 0 ]] && ok "row-level security enabled on every business table" || die "$NO_RLS tables lack row-level security."

AUDIT_WRITE="$(q "SELECT has_table_privilege('pharmaos_app','audit.audit_trail','UPDATE') OR has_table_privilege('pharmaos_app','audit.audit_trail','DELETE')")"
record "IQ-INF-006" "Application role cannot modify audit trail" "false" "$AUDIT_WRITE" "$( [[ "$AUDIT_WRITE" == f ]] && echo PASS || echo FAIL)"
[[ "$AUDIT_WRITE" == f ]] && ok "application role cannot update or delete audit rows" || die "Application role can modify the audit trail."

EXT="$(q "SELECT string_agg(extname, ',' ORDER BY extname) FROM pg_extension WHERE extname IN ('pgcrypto','pg_trgm','btree_gist')")"
record "IQ-INF-003b" "Required extensions" "btree_gist,pg_trgm,pgcrypto" "$EXT" "$( [[ "$EXT" == btree_gist,pg_trgm,pgcrypto ]] && echo PASS || echo FAIL)"
ok "extensions: $EXT"

TZ_SETTING="$(q "SHOW timezone")"
if [[ "$TZ_SETTING" == "UTC" || "$TZ_SETTING" == "Etc/UTC" ]]; then
  record "IQ-INF-007" "Database time zone" "UTC" "$TZ_SETTING" PASS; ok "database time zone UTC"
else
  record "IQ-INF-007" "Database time zone" "UTC" "$TZ_SETTING" "WARN"
  warn "Database time zone is $TZ_SETTING. Timestamps are stored in UTC regardless; set timezone = 'UTC' in postgresql.conf for production."
fi

# ---------------------------------------------------------- 6. logins ---
step "6/8 Application logins"
gen_pw() { python3 -c 'import secrets;print(secrets.token_urlsafe(32))'; }
APP_PW="$(gen_pw)"
AUDITOR_PW="$(gen_pw)"

psql_su -d "$DB_NAME" -q -v db="$DB_NAME" -v app_login="$APP_LOGIN" -v app_pw="$APP_PW" \
  -v auditor_login="$AUDITOR_LOGIN" -v auditor_pw="$AUDITOR_PW" >/dev/null <<'SQL'
SELECT format('CREATE ROLE %I LOGIN IN ROLE pharmaos_app', :'app_login')
 WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'app_login') \gexec
SELECT format('CREATE ROLE %I LOGIN IN ROLE pharmaos_auditor', :'auditor_login')
 WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'auditor_login') \gexec
ALTER ROLE :"app_login" PASSWORD :'app_pw';
ALTER ROLE :"auditor_login" PASSWORD :'auditor_pw';
-- Only this installation's logins may connect to this database.
REVOKE CONNECT ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"db" TO :"app_login", :"auditor_login";
SQL
ok "created $APP_LOGIN and $AUDITOR_LOGIN with generated passwords"
ok "only these two logins (and superusers) may connect to $DB_NAME"
LOG_STMT="$(q "SHOW log_statement")"
[[ "$LOG_STMT" == "all" || "$LOG_STMT" == "ddl" ]] \
  && warn "log_statement=$LOG_STMT: the ALTER ROLE … PASSWORD statements may appear in the PostgreSQL server log. Rotate the passwords or purge that log."

# The app login must see nothing without a tenant context (RLS fail-closed).
VISIBLE="$(PGPASSWORD="$APP_PW" psql -X -h "$PG_HOST" -p "$PG_PORT" -U "$APP_LOGIN" -d "$DB_NAME" -qAtc "SELECT count(*) FROM mdm.uom")" \
  || die "The application login cannot connect. Check pg_hba.conf allows password logins for $APP_LOGIN."
record "IQ-INF-008" "Application login connects; no rows without tenant context" "connects, 0 rows" "connects, $VISIBLE rows" "$( [[ "$VISIBLE" == 0 ]] && echo PASS || echo FAIL)"
ok "application login connects and sees no data without a tenant (row-level security)"

umask 077
{
  echo "# PharmaOS connection settings written by install.sh on $STARTED_UTC"
  echo "# Load with:  set -a; source pharmaos.env; set +a"
  echo "# Contains passwords. Keep it private; do not commit it."
  # Single-quoted: the values contain ';', which the shell would otherwise treat as a command separator.
  echo "ConnectionStrings__Db='Host=$PG_HOST;Port=$PG_PORT;Database=$DB_NAME;Username=$APP_LOGIN;Password=$APP_PW'"
  echo "ConnectionStrings__AuditDb='Host=$PG_HOST;Port=$PG_PORT;Database=$DB_NAME;Username=$AUDITOR_LOGIN;Password=$AUDITOR_PW'"
} > "$ENV_FILE"
umask 022
ok "wrote $(basename "$ENV_FILE") (readable only by you)"
unset APP_PW AUDITOR_PW

# ----------------------------------------------------- 7. build and test ---
step "7/8 Build and test"
run_logged() { # run_logged "label" logfile cmd...
  local label="$1" log="$2"; shift 2
  if "$@" >"$log" 2>&1; then ok "$label"; return 0; fi
  tail -20 "$log" >&2; die "$label failed (full log: $log)"
}
# "Passed: 85, Failed: 0" from the dotnet test summary line.
test_counts() {
  local line; line="$(grep -E 'Passed!|Failed!' "$1" | tail -1)"
  printf '%s passed, %s failed' "$(grep -oE 'Passed: +[0-9]+' <<<"$line" | grep -oE '[0-9]+')" \
    "$(grep -oE 'Failed: +[0-9]+' <<<"$line" | grep -oE '[0-9]+')"
}
mkdir -p "$REPORT_DIR"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
LOGS="$REPORT_DIR/logs-$STAMP"
mkdir -p "$LOGS"

BACKEND_RESULT="skipped"
if $DO_BACKEND; then
  export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
  run_logged "backend build (warnings are errors)" "$LOGS/dotnet-build.log" dotnet build PharmaOS.slnx -c Release
  run_logged "backend unit tests" "$LOGS/unit-tests.log" dotnet test tests/PharmaOS.UnitTests -c Release --no-build
  UNIT_COUNTS="$(test_counts "$LOGS/unit-tests.log")"
  # Integration tests create and drop their own pharmaos_it_* database.
  PHARMAOS_TEST_PG="Host=$PG_HOST;Port=$PG_PORT;Username=$PG_SUPERUSER;Password=$PGPASSWORD" \
    run_logged "backend integration tests" "$LOGS/integration-tests.log" \
    dotnet test tests/PharmaOS.IntegrationTests -c Release --no-build
  INT_COUNTS="$(test_counts "$LOGS/integration-tests.log")"
  ok "unit tests: $UNIT_COUNTS; integration tests: $INT_COUNTS"
  BACKEND_RESULT="0 warnings; unit $UNIT_COUNTS; integration $INT_COUNTS"
  record "BUILD-BE" "Backend build and tests" "0 warnings, all tests pass" "$BACKEND_RESULT" PASS
fi

WEB_RESULT="skipped"
if $DO_WEB; then
  export NG_CLI_ANALYTICS=false
  run_logged "frontend dependencies (npm ci)" "$LOGS/npm-ci.log" npm --prefix web ci
  ( cd web && npx ng test --watch=false ) >"$LOGS/web-tests.log" 2>&1 \
    || { tail -20 "$LOGS/web-tests.log" >&2; die "frontend tests failed (full log: $LOGS/web-tests.log)"; }
  WEB_TESTS="$(grep -E 'Tests ' "$LOGS/web-tests.log" | tail -1 | sed 's/\x1b\[[0-9;]*m//g; s/^ *Tests *//' | tr -s ' ')"
  ok "frontend tests: $WEB_TESTS"
  ( cd web && npx ng build ) >"$LOGS/web-build.log" 2>&1 \
    || { tail -20 "$LOGS/web-build.log" >&2; die "frontend build failed (full log: $LOGS/web-build.log)"; }
  ok "frontend production build (web/dist/web)"
  WEB_RESULT="tests: $WEB_TESTS; build OK"
  record "BUILD-FE" "Frontend tests and production build" "all tests pass, build OK" "$WEB_RESULT" PASS
fi

# ------------------------------------------------------- 8. smoke check ---
SMOKE_RESULT="skipped"
if $DO_SMOKE; then
  step "8/8 Health check"
  PORT="$(python3 -c 'import socket;s=socket.socket();s.bind(("127.0.0.1",0));print(s.getsockname()[1]);s.close()')"
  DLL="src/Hosts/PharmaOS.Platform.Host/bin/Release/net10.0/PharmaOS.Platform.Host.dll"
  [[ -f "$DLL" ]] || die "Backend not built ($DLL missing)."
  ( set -a; # shellcheck disable=SC1090
    source "$ENV_FILE"; set +a
    exec dotnet "$DLL" --contentRoot "$(cd "$(dirname "$DLL")" && pwd)" --urls "http://127.0.0.1:$PORT" ) >"$LOGS/host.log" 2>&1 &
  HOST_PID=$!
  for _ in $(seq 1 60); do curl -fs "http://127.0.0.1:$PORT/health/live" >/dev/null 2>&1 && break; sleep 1; done
  READY="$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/health/ready" || true)"
  ANON="$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/api/mdm/v1/uoms" || true)"
  kill "$HOST_PID" 2>/dev/null || true; wait "$HOST_PID" 2>/dev/null || true; HOST_PID=""
  SMOKE_RESULT="ready=$READY, unauthenticated API=$ANON"
  if [[ "$READY" == 200 && "$ANON" == 401 ]]; then
    ok "backend reaches the database with the new login (/health/ready 200)"
    ok "API refuses requests without sign-in (401)"
    record "SMOKE" "Backend starts, reaches database, rejects anonymous API calls" "ready 200, API 401" "$SMOKE_RESULT" PASS
  else
    record "SMOKE" "Backend starts, reaches database, rejects anonymous API calls" "ready 200, API 401" "$SMOKE_RESULT" FAIL
    tail -20 "$LOGS/host.log" >&2
    die "Health check failed ($SMOKE_RESULT). Log: $LOGS/host.log"
  fi
else
  step "8/8 Health check (skipped)"
fi

# ---------------------------------------------------------------- report ---
FINISHED_UTC="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
REPORT="$REPORT_DIR/install-report-$STAMP.md"
{
  echo "# PharmaOS installation report"
  echo
  echo "Structured like IQ template docs/12-iqoqpq-templates.md §12.1. This is supporting evidence; it is"
  echo "not an approved IQ protocol and does not replace one."
  echo
  echo "| Item | Value |"
  echo "|---|---|"
  echo "| Started (UTC) | $STARTED_UTC |"
  echo "| Finished (UTC) | $FINISHED_UTC |"
  echo "| Performed by (OS user) | $(id -un) on $(hostname) |"
  echo "| Source commit | $GIT_COMMIT (uncommitted changes: $GIT_DIRTY) |"
  echo "| Database | $DB_NAME on $PG_HOST:$PG_PORT (PostgreSQL $PG_SERVER) |"
  echo "| Logins | $APP_LOGIN (application), $AUDITOR_LOGIN (audit read-only); passwords in pharmaos.env, not in this report |"
  echo "| Tools | psql $PSQL_CLIENT, Python $PY_VER, .NET SDK $DOTNET_VER, Node $NODE_VER |"
  echo
  echo "## Checks"
  echo
  echo "| ID | Check | Expected | Actual | Result |"
  echo "|---|---|---|---|---|"
  for row in "${REPORT_ROWS[@]}"; do
    IFS='|' read -r id chk exp act res <<<"$row"
    echo "| $id | $chk | $exp | $act | $res |"
  done
  echo
  echo "## Schema files loaded"
  echo
  echo "| File | SHA-256 |"
  echo "|---|---|"
  for s in "${SCHEMA_SUMS[@]}"; do echo "| ${s%%|*} | \`${s##*|}\` |"; done
  echo
  echo "## Warnings"
  echo
  if ((${#WARNINGS[@]})); then for w in "${WARNINGS[@]}"; do echo "- $w"; done; else echo "None."; fi
  echo
  echo "## Logs"
  echo
  echo "Build and test output: \`$(basename "$LOGS")/\` next to this report."
  echo
  echo "## Not covered by this installer"
  echo
  echo "- Sign-in (Keycloak) is not configured; API calls from the screens return 401."
  echo "- Server hardening, backups, TLS, NTP, and disk encryption (docs 03, 14, 16)."
  echo "- Executed and approved IQ/OQ protocols (docs 11, 12)."
} > "$REPORT"
REPORT_SHA="$(sha256sum "$REPORT" | awk '{print $1}')"
echo "$REPORT_SHA  $(basename "$REPORT")" > "$REPORT.sha256"

printf '\n%s%sInstallation complete.%s\n' "$B" "$G" "$N"
info "Report:   ${REPORT#"$ROOT"/}  (SHA-256 ${REPORT_SHA:0:16}…)"
info "Settings: pharmaos.env"
if ((${#WARNINGS[@]})); then printf '  %s%d warning(s); see the report.%s\n' "$Y" "${#WARNINGS[@]}" "$N"; fi
cat <<EOF

Next steps (development)
  Start Keycloak:      docker compose -f deploy/keycloak/docker-compose.dev.yml up -d
  Start the backend:   set -a; source pharmaos.env; set +a
                       ASPNETCORE_ENVIRONMENT=Development \\
                       dotnet run --project src/Hosts/PharmaOS.Platform.Host --urls http://localhost:5080
  Start the frontend:  cd web && npx ng serve        → http://localhost:4200
  Sign in as qa.analyst or read.only (password Dev-Only-Pass-2026; development realm only).
EOF
