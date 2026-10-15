# Installing PharmaOS

There are two kinds of installation. Pick yours first.

| You want to… | Section | Works today? |
|---|---|---|
| Run the code on a workstation, build it, run the tests, and look at the screens | [A. Developer workstation](#a-developer-workstation) | **Yes.** Every step below was run and verified |
| Install PharmaOS on a plant or company server for real use | [B. Server installation (tier S)](#b-server-installation-tier-s-single-server) | **Not yet.** The database and supporting services install; the application images are not built yet |

> **Known limitation today:** sign-in is not wired up. The screens open, but every data request is
> answered with **401 Unauthorized** because the API only accepts tokens from Keycloak, and no Keycloak
> realm is shipped yet. The automated tests are not affected: they use a test-only sign-in.

---

## A. Developer workstation

### A1. Prerequisites

| Tool | Version | Why this version |
|---|---|---|
| Git | any recent | |
| PostgreSQL | **16** | The schema and tests target 16. You need a superuser login (usually `postgres`) |
| Python | 3.10 or newer | Runs `tools/schema_gen.py`. Standard library only, no packages |
| .NET SDK | **10.0.4xx** | Pinned in `global.json`. .NET 10 is LTS; .NET 8 support ends Nov 2026 |
| Node.js | **24 LTS** (or 22.22.3+) | Angular 22 refuses older Node versions |
| Docker (optional) | with Compose v2 | Only for the tier S stack in section B |

**Windows:** use WSL2 (Ubuntu) and run everything inside it. The commands below are bash.

Check what you have:

```bash
psql --version          # psql (PostgreSQL) 16.x
python3 --version       # 3.10+
dotnet --list-sdks      # a line starting with 10.0.4
node --version          # v24.x
```

If .NET or Node is missing, Microsoft's `dotnet-install.sh` (`--channel 10.0`) and the nodejs.org
binaries both install into your home directory without admin rights.

### A2. Get the code

```bash
git clone https://github.com/LemonLasagna/Repository-1-learning-vibe-coding.git
cd Repository-1-learning-vibe-coding
git checkout claude/pharma-platform-architecture-9g7isp   # until PR #1 is merged
cd pharma-platform
```

All commands below run from `pharma-platform/`.

### A3. Create the database

Set your superuser connection once per terminal (change the password to yours):

```bash
export PGHOST=localhost PGUSER=postgres PGPASSWORD=postgres
```

Create the database and load the schema. This creates 487 tables, triggers and row-level security:

```bash
psql -c "CREATE DATABASE pharmaos_dev"
for f in database/schema/0*.sql; do psql -q -v ON_ERROR_STOP=1 -d pharmaos_dev -f "$f" > /dev/null; done
```

On a fresh database you will see one harmless line:
`NOTICE: trigger "trg_protect_audit" ... does not exist, skipping`.

Prove the data-integrity controls work. The last line must read `ALL DATA-INTEGRITY TESTS PASSED`:

```bash
psql -v ON_ERROR_STOP=1 -d pharmaos_dev -f database/tests/integrity_tests.sql
```

Create the two login accounts the application uses. Choose your own passwords:

```bash
psql -v ON_ERROR_STOP=1 -d pharmaos_dev -v app_pw='choose-app-password' -v audit_pw='choose-audit-password' <<'SQL'
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharmaos_app_login') THEN
    CREATE ROLE pharmaos_app_login LOGIN IN ROLE pharmaos_app;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharmaos_auditor_login') THEN
    CREATE ROLE pharmaos_auditor_login LOGIN IN ROLE pharmaos_auditor;
  END IF;
END $$;
ALTER ROLE pharmaos_app_login PASSWORD :'app_pw';
ALTER ROLE pharmaos_auditor_login PASSWORD :'audit_pw';
SQL
```

- `pharmaos_app_login` is what the application connects as. Row-level security applies to it, and it
  cannot read or change the audit trail.
- `pharmaos_auditor_login` can only read the audit trail.

Never point the application at the `postgres` superuser: that would bypass row-level security.

### A4. Build and test the backend

```bash
dotnet build PharmaOS.slnx -c Release                 # expect: 0 Warning(s), 0 Error(s)
dotnet test tests/PharmaOS.UnitTests -c Release --no-build
PHARMAOS_TEST_PG="Host=localhost;Username=postgres;Password=postgres" \
  dotnet test tests/PharmaOS.IntegrationTests -c Release --no-build
```

The integration tests create and then drop their own throw-away database (`pharmaos_it_…`). They do
not touch `pharmaos_dev`. Expected: 85 unit tests and 12 integration tests pass.

### A5. Run the backend

Use the same passwords as in A3:

```bash
export ConnectionStrings__Db="Host=localhost;Database=pharmaos_dev;Username=pharmaos_app_login;Password=choose-app-password"
export ConnectionStrings__AuditDb="Host=localhost;Database=pharmaos_dev;Username=pharmaos_auditor_login;Password=choose-audit-password"
dotnet run --project src/Hosts/PharmaOS.Platform.Host --urls http://localhost:5080
```

In a second terminal:

```bash
curl http://localhost:5080/health/live     # {"status":"live"}
curl http://localhost:5080/health/ready    # {"status":"ready"}  (proves the database login works)
curl -i http://localhost:5080/api/mdm/v1/uoms | head -1   # HTTP/1.1 401 Unauthorized (expected, see limitation above)
```

### A6. Build, test and run the frontend

```bash
cd web
npm ci                                   # exact versions from package-lock.json
npx ng test --watch=false                # expect: 70 passed
npx ng build                             # production build with strict template checks
npx ng serve                             # http://localhost:4200
```

`ng serve` forwards `/api` and `/health` to the backend on port 5080 (`web/proxy.conf.json`), so start
A5 first. Open http://localhost:4200:

- **Units of measure** shows "Not signed in" and a 401 error. This is the known limitation.
- **Deviation (demo)** works fully. It is generated from `web/public/metadata/deviation.bundle.json`
  and needs no API. Try ticking "Planned deviation", changing the classification, and pressing
  "Submit for QA review" to see the e-signature dialog. Nothing is saved.

### A7. Changing the data model

Edit `database/model/catalog.dsl`, then regenerate the SQL, ERD and data dictionary together:

```bash
python3 tools/schema_gen.py           # writes database/schema/*, docs/04-erd.md, database/data-dictionary.md
python3 tools/schema_gen.py --check   # CI runs this; it fails if generated files are stale
```

For an existing `pharmaos_dev`, drop it and redo A3. Migrations for deployed databases are described
in doc 18 §18.3.

---

## B. Server installation (tier S, single server)

**Status: not ready for real use.** The supporting services and the database install. The PharmaOS
application images (the `app` profile) are produced by the first release and do not exist yet. Even
when they do, a GxP installation is a validated activity: it needs an approved installation
qualification (IQ). The IQ template is in `docs/12-iqoqpq-templates.md` §12.1.

What the target installation looks like (details in `deploy/compose/README.md` and doc 03):

1. **Hardware.** Two servers: an application server (16 cores, 64 GB) and a database server
   (8 cores, 64 GB, NVMe RAID-1), plus a small standby/backup box. Data disks are LUKS2-encrypted.
2. **Configure.** In `pharma-platform/deploy/compose`, copy `.env.example` to `.env` and set every
   password. Store the file encrypted (SOPS), never in plain text.
3. **Start the supporting services:** `docker compose up -d`. On first start, PostgreSQL loads the schema
   and runs the data-integrity tests. Check with
   `docker compose logs postgres | grep "ALL DATA-INTEGRITY TESTS PASSED"`.
4. **Initialise OpenBao** (`bao operator init`) and hand the unseal key shares to named custodians.
5. **Import the signed release bundle** into the local registry and start the application:
   `docker compose --profile app up -d`. *(Blocked until release images exist.)*
6. **Configure backups:** a PostgreSQL streaming standby, plus restic to WORM tape or an immutable
   appliance (doc 16).

What has been checked so far: `docker compose config` validates the file, and the first-start
database script was run against PostgreSQL 16 (schema loads, all integrity tests pass, app login
works). The containers themselves have not been started, because no Docker engine was available when
this was written.

Larger installations (site cluster, multi-site) use `deploy/k8s/` on RKE2. See doc 19.

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `The Angular CLI requires a minimum Node.js version of v22.22.3…` | Node too old | Install Node 24 LTS |
| `A compatible .NET SDK was not found` | No 10.0.4xx SDK | Install .NET 10 SDK (`dotnet-install.sh --channel 10.0`) |
| `/health/ready` returns 503 | Wrong connection string or password | Recheck A3 passwords and A5 variables |
| `GxP write … without authenticated actor` | Something wrote to the database outside the application (or with the context missing) | Intended: the database refuses anonymous changes. Write through the API |
| API returns 401 | No Keycloak sign-in yet | Known limitation |
| Integration tests fail with "Set PHARMAOS_TEST_PG…" | Variable not set | Set it to a superuser connection string (A4) |
| `permission denied to create database` | Not connected as a superuser | Use the `postgres` account or one with CREATEDB and CREATEROLE |
