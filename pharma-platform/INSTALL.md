# Installing PharmaOS

There are two kinds of installation. Pick yours first.

| You want to… | Section | Works today? |
|---|---|---|
| Run the code on a workstation, build it, run the tests, and look at the screens | [A. Developer workstation](#a-developer-workstation) | **Yes.** Every step below was run and verified |
| Install PharmaOS on a plant or company server for real use | [B. Server installation (tier S)](#b-server-installation-tier-s-single-server) | **Not yet.** The database and supporting services install; the application images are not built yet |

> **Sign-in** uses Keycloak with a server-side session. The browser holds only an HttpOnly cookie; tokens
> stay on the server. For development, a ready-made Keycloak realm with two test users is shipped (A5).
> It is for development only and must never be used for TEST, VAL or PROD.

---

## A. Developer workstation

### Quick path: the installation wizard

Once the prerequisites in A1 are installed, one command does A3 to A6 for you:

```bash
cd pharma-platform
./install.sh
```

The wizard:
1. Asks for the PostgreSQL connection and a database name.
2. Checks the tool versions.
3. Creates the database and runs the data-integrity tests.
4. Checks that every GxP table has its audit trigger and row-level security.
5. Creates two logins with generated passwords and saves them to `pharmaos.env` (readable only by you).
   If you agree (or pass `--demo-data`), it loads a demo organisation with two sites for the development users;
   deviations need a site. Never load demo data into TEST, VAL or PROD.
6. Builds and tests the backend and frontend.
7. Starts the backend once to confirm it reaches the database.

It finishes by writing `install-reports/install-report-<time>.md`. The report is laid out like the IQ
template, records the SHA-256 of every schema file, and has a checksum file next to it.

- It never drops an existing database unless you agree **and** type the database name.
- It never prints or logs a password.
- Scripted use: `PGPASSWORD=… ./install.sh --non-interactive --db pharmaos_dev --demo-data` (see `./install.sh --help`).
- Login names are `<database>_app` and `<database>_auditor`, so several installations can share one
  PostgreSQL server without overwriting each other's passwords.

The manual steps below do the same thing by hand.

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

For development, load the demo organisation (an enterprise, a legal entity and the sites BER and HAM for the
development tenant). Deviations need a site:

```bash
psql -q -v ON_ERROR_STOP=1 -d pharmaos_dev -f database/seed/demo-org.sql
```

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
not touch `pharmaos_dev`. Expected: 96 unit tests and 55 integration tests pass.

### A5. Start Keycloak and run the backend

**Keycloak (development realm).** With Docker:

```bash
docker compose -f deploy/keycloak/docker-compose.dev.yml up -d   # http://localhost:8080, admin / admin
```

Without Docker (needs Java 21), download Keycloak 26.8.1 from its GitHub releases page and run:

```bash
cp deploy/keycloak/dev-import/pharmaos-realm.json keycloak-26.8.1/data/import/
KC_BOOTSTRAP_ADMIN_USERNAME=admin KC_BOOTSTRAP_ADMIN_PASSWORD=admin \
  keycloak-26.8.1/bin/kc.sh start-dev --import-realm
```

The realm `pharmaos` (`deploy/keycloak/dev-import/pharmaos-realm.json`) contains:

| User | Password | Permissions |
|---|---|---|
| `qa.analyst` (Quinn Analyst) | `Dev-Only-Pass-2026` | units of measure: read, change; raise and work on deviations and CAPAs; audit trail; sign |
| `qa.approver` (Avery Approver) | `Dev-Only-Pass-2026` | units of measure: read; deviations: read, triage, approve; CAPA: read, approve; audit trail; sign |
| `read.only` (Riley Reader) | `Dev-Only-Pass-2026` | read units of measure, deviations and CAPAs |

Permissions are Keycloak client roles of `pharmaos-web`, sent as the `perm` claim. The tenant is the
user attribute `tenant_id`, which only an administrator can set. `esig.sign` allows electronic signatures;
in production, grant it only after the person's identity has been verified.

The realm is imported only when it does not exist yet. After editing the JSON, re-import it: with Docker,
`docker compose -f deploy/keycloak/docker-compose.dev.yml down` and `up -d` again; without Docker, stop
Keycloak, delete `keycloak-26.8.1/data/h2`, and start it again. Changes made in the admin console are not
written back to the JSON. A re-import gives the users new ids, while PharmaOS keeps a login tied to the id it
first saw (ids are never reassigned). Recreate the development database as well (`./install.sh --recreate
--demo-data`); otherwise the API answers 409 "Identity conflict".

**Backend.** Use the same passwords as in A3. `ASPNETCORE_ENVIRONMENT=Development` loads
`appsettings.Development.json`, which points at the local Keycloak over plain HTTP:

```bash
export ConnectionStrings__Db="Host=localhost;Database=pharmaos_dev;Username=pharmaos_app_login;Password=choose-app-password"
export ConnectionStrings__AuditDb="Host=localhost;Database=pharmaos_dev;Username=pharmaos_auditor_login;Password=choose-audit-password"
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Hosts/PharmaOS.Platform.Host --urls http://localhost:5080
```

In a second terminal:

```bash
curl http://localhost:5080/health/live     # {"status":"live"}
curl http://localhost:5080/health/ready    # {"status":"ready"}  (proves the database login works)
curl -i http://localhost:5080/api/mdm/v1/uoms | head -1   # HTTP/1.1 401 Unauthorized (expected: not signed in)
```

### A6. Build, test and run the frontend

```bash
cd web
npm ci                                   # exact versions from package-lock.json
npx ng test --watch=false                # expect: 91 passed
npx ng build                             # production build with strict template checks
npx ng serve                             # http://localhost:4200
```

`ng serve` forwards `/api`, `/health`, `/bff` and the sign-in callbacks to the backend on port 5080
(`web/proxy.conf.json`), so start A5 first. Open http://localhost:4200:

- Press **Sign in**. Keycloak asks for a user name and password; use `qa.analyst` from the table in A5.
- **Units of measure**: create a unit, open it, change the name with a reason for change. The audit trail
  shows "Quinn Analyst (qa.analyst)" as the actor.
- **Electronic signature**: as `qa.analyst`, press **Sign as Approved…** on the unit you just changed. It is
  refused: the last editor of a version cannot approve it. Sign out, sign in as `qa.approver`, open the same
  unit and sign as Approved. The sign-in page asks for the password again, even though you are signed in, and
  the signature appears with its manifest. Change the unit again and the signature is shown as applying to an
  older version.
- Sign in as `read.only` to see the server refuse changes (403).
- **Deviations** (needs the demo organisation): as `qa.analyst`, press **New deviation**, choose site BER, fill in
  the form and save the draft. Press **Submit ✍**: it is signed as Authored, with the password at Keycloak. As
  `qa.approver`, open it and press **Start investigation**. As `qa.analyst`, **Submit for QA approval** stays
  disabled until the root cause is entered; enter it with a reason for change, save, and submit. As
  `qa.approver`, **Approve and close ✍** (signed as Approved) or **Return to investigation** (with a reason).
  The workflow history, signatures and audit trail show each step. The form is generated from
  `web/public/metadata/deviation.bundle.json`; the server decides which fields are editable in each state.
- **My work** (the start page) lists what is waiting on the signed-in user, overdue first: as `qa.approver` you see
  triage, plan reviews, verifications and approvals; as `qa.analyst` your drafts, investigations and actions. The
  number next to the menu entry is red when something is overdue.
- **CAPA**: on a deviation in investigation, press **Raise CAPA** (it takes the deviation's site and text). Plan the
  effectiveness check and at least one action, then **Submit plan for review**. The deviation cannot be approved
  and closed until `qa.approver` has signed **Approve plan ✍**. Then complete each action with evidence, have QA
  sign **Verify implementation ✍**, record the effectiveness result, and have QA sign **Verify effectiveness and
  close ✍**.

**End-to-end tests.** With Keycloak, the backend and `ng serve` running, and the demo organisation loaded:

```bash
cd web && CHROMIUM_PATH=/path/to/chrome npm run e2e
```

It signs in through the Keycloak page, checks that no token is visible to JavaScript, creates and edits a
unit of measure, checks the audit actor, checks that the editor cannot approve, re-authenticates, signs out,
checks that `read.only` cannot write, and has `qa.approver` approve the unit with a Keycloak password prompt.
It then takes a deviation from draft to closed with both users, including the two signatures and the guard, and
finally runs a CAPA raised from a deviation through plan approval, the deviation's closure, implementation and the
effectiveness check, checking QA's "My work" list on the way.
The CI job `e2e` runs the same tests.

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
| API returns 401 | Not signed in, or the session expired (15 minutes idle) | Press **Sign in** |
| API returns 409 "Identity conflict" | The Keycloak realm was re-imported, so the same login now has a new id | Recreate the development database (`./install.sh --recreate --demo-data`) |
| "Unknown or inactive site" when raising a deviation | No demo organisation in the database | Load `database/seed/demo-org.sql` (A3) |
| API returns 403 "Missing CSRF header" | A request was made without `X-CSRF: 1` (e.g. by hand with the session cookie) | The web app adds it; add it to manual requests |
| Sign in fails with an error about obtaining the OpenID configuration | Keycloak not running, or the backend not started with `ASPNETCORE_ENVIRONMENT=Development` | Start Keycloak (A5); set the variable |
| Keycloak says "Invalid parameter: redirect_uri" | App opened on a host or port the dev realm does not list | Use http://localhost:4200 or http://localhost:5080 |
| Integration tests fail with "Set PHARMAOS_TEST_PG…" | Variable not set | Set it to a superuser connection string (A4) |
| `permission denied to create database` | Not connected as a superuser | Use the `postgres` account or one with CREATEDB and CREATEROLE |
