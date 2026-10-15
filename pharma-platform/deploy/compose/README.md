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

Status: `docker compose config` validates this file. The `app` profile images are not built yet; they are
what the R0 release produces (doc 20). The infrastructure services use upstream open-source images.
