# Tier S: single-server install (Docker Compose / Podman)

For one site with ≤ 150 named users (doc 03 §3.3). Everything runs on customer-owned hardware, and no
component needs internet access at runtime.

1. Prepare an encrypted data disk (LUKS2) and mount it at `DATA_ROOT`.
2. `cp .env.example .env`, set every value, and encrypt the file with SOPS for storage.
3. `docker compose up -d`. On first start, PostgreSQL loads `database/schema/*.sql` and runs the data-integrity
   test suite (OQ-DI-001…009). Check the result with `docker compose logs postgres | grep "ALL DATA-INTEGRITY"`.
4. Initialise and unseal OpenBao (`bao operator init`). Distribute the Shamir unseal keys to named custodians.
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
