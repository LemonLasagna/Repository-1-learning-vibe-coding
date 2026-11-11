# Tier S: single-server install (Docker Compose / Podman)

For one site with ≤ 150 named users (doc 03 §3.3). Everything runs on customer-owned hardware, and no
component needs internet access at runtime.

1. Prepare an encrypted data disk (LUKS2) and mount it at `DATA_ROOT`.
2. `cp .env.example .env`, set every value, and encrypt the file with SOPS for storage.
3. `docker compose up -d`. On first start, PostgreSQL loads `database/schema/*.sql` and runs the data-integrity
   test suite (OQ-DI-001…009). Check the result with `docker compose logs postgres | grep "ALL DATA-INTEGRITY"`.
4. Initialise and unseal OpenBao (`bao operator init`). Distribute the Shamir unseal keys to named custodians. Then run
   `BAO_ADDR=… BAO_TOKEN=<admin token> ./openbao-bootstrap.sh --new-secret-id` and put the two values it prints in `.env`
   (`OPENBAO_ROLE_ID`, `OPENBAO_SECRET_ID`); see "Signing keys" below.
5. Import the release bundle into the local registry, then `docker compose --profile app up -d`.
6. Configure backups: a PostgreSQL streaming standby on a second box, plus restic to LTO WORM tape or an
   immutable appliance (doc 16 §16.4).

## Images

Two application images exist, built from this checkout (`docker compose --profile app build platform web`):

| Image | What it is |
|---|---|
| `platform-host` | The API and the browser session (BFF), from `src/Hosts/PharmaOS.Platform.Host`. Listens on 8080. |
| `web` | The Angular app as static files behind nginx, which also hands `/api`, `/bff`, `/health` and the sign-in callbacks to the platform host. Listens on 8080. |

Both run as an unprivileged user, with a read-only root file system, no added privileges and a health check. The
Dockerfiles are in `../images`. The `images` workflow builds them and starts them from this file against a real database
on every change that affects them.

What is **not** here yet: the `gateway` (TLS in front of `web`), `domain` and `workers` images in the file have no code
behind them, so they cannot be built. Base images are named by tag; production pins them by digest and signs the result
when the release bundle is built (IQ-INF-002). Sign-in sessions are kept in the host's memory, so they end when the
container restarts: users sign in again.

The infrastructure services use upstream open-source images.

## Signing keys (OpenBao)

Configuration packages are signed with a key that lives only in OpenBao. `openbao-bootstrap.sh` creates, and is safe to run
again:

- the transit engine and one key, `pharmaos-packages` (ECDSA P-256, **not exportable**, so the private key cannot be read out,
  even by the platform host);
- a policy, `pharmaos-signer`, that allows reading the key's public part, signing and verifying with it, and nothing else (no
  export, rotate, delete, other keys or system paths);
- an AppRole, `pharmaos-host`, with that policy. The host logs in with the role id and secret id and gets 1-hour tokens, logging
  in again on its own, so no token needs renewing.

Things to know:

- The secret id does not expire. Anyone holding it plus network access to OpenBao can sign packages. Keep `.env` encrypted
  (SOPS) and restrict who can reach port 8200 (it is on the internal `data` network only).
- If OpenBao is sealed or down, the platform runs as before but cannot sign. After a restart OpenBao must be unsealed by its key
  custodians before signing works again.
- Rotating the key (`bao write -f transit/keys/pharmaos-packages/rotate`) adds a version; older signatures stay verifiable
  because the host keeps every version's public key.
- The CI job `openbao` runs the script and the signer tests against a real OpenBao in dev mode. It does not exercise
  initialisation, unsealing or Raft storage; those are checked by hand on a real installation.

## Configuration packages

Workflow definitions move between installations (CONFIG, VAL, PROD) as signed packages, not by editing each one:

1. **Build** (`cfg.package:author`): `POST /api/config/v1/packages` with the released workflow versions to include. The package gets a
   manifest and a SHA-256 over its content.
2. **Approve** (`cfg.package:approve`): an electronic signature; OpenBao signs the hash in the same step.
3. **Export** the approved package as a file and carry it to the other installation.
4. **Import** (`cfg.package:author`): the target recomputes the hash, checks the signature against the keys it trusts and dry-runs every
   item. Nothing is stored unless all of it passes. Importing deploys nothing.
5. **Deploy** (`cfg.package:deploy`): an electronic signature (RELEASED) that releases the package's workflows in one transaction.
   Everything is checked again at that moment; if anything fails, nothing is released and no signature is kept. Each deployment is
   recorded.

A package that should not go further is **discarded** with a reason: one that was built and never approved (`cfg.package:author`), or
one that was imported and never deployed (`cfg.package:deploy`). Nothing is deleted. The package, its items, its import record and the
reason stay, and it can no longer be approved, exported or deployed. Its code is free to use again, so a corrected package can be built
or imported under the same code. (The database enforces this with a unique index on the code that ignores discarded packages, so an
existing installation needs its schema recreated: `install.sh --recreate`.)

All five steps have screens under **Packages** in the menu (shown to users with `cfg.package:author`). Approve and deploy open the same
e-signature dialog as other signed steps and need `esig.sign` as well. In a `PROD` environment the workflow screens show a banner and
become read-only.

Settings (environment variables use `__`, e.g. `ConfigPackages__EnvironmentName`):

| Setting | Meaning |
|---|---|
| `ConfigPackages:EnvironmentName` | `CONFIG`, `VAL`, `PROD` or your own name. Written into packages built here. **`PROD` makes workflow authoring read-only**: drafts cannot be created, edited, submitted, approved, activated or rejected there, so workflows change only by deploying a package. |
| `ConfigPackages:TrustedKeys` | The public keys (name, key version, PEM) of the installations packages may come from. This installation's own key is always trusted. |
| `ConfigPackages:RequireSecondPerson` | When true, the person who built a package cannot approve it. Off by default. |

Whoever can change `TrustedKeys` decides what the installation accepts, and the app does not record that change; keep the file under
change control. Only workflow definitions can be packaged in this version.
