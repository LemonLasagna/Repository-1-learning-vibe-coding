# 18. DevOps Structure

## 18.1 Branching and environments

- Trunk-based: short-lived feature branches → PR → `main`. Release branches `release/YYYY.Q` are cut for
  quarterly releases. Hotfixes are cherry-picked.
- GitOps: environment state lives in an `environments` repository (Argo CD). Promotion is a PR that bumps image
  digests or config-package versions in the target overlay, and the PR requires the approvals listed in 18.4.

## 18.2 Pipeline stages

```
PR ─┬─ build (.NET, Angular) ─ unit tests ─ lint/format ─ architecture tests
    ├─ schema: schema_gen --check → apply DDL to ephemeral PG 16 → integrity_tests.sql  (implemented: .github/workflows/pharmaos-schema.yml)
    ├─ rules parity (C# vs TS golden vectors)
    ├─ SAST, SCA, secrets scan, licence policy check
    ├─ OpenAPI diff (breaking change → fail unless version bumped)
    └─ integration tests (Testcontainers: PG, RabbitMQ, SeaweedFS, Keycloak, OpenSearch, Valkey)
main ─ build images → SBOM (CycloneDX) → image scan → sign (cosign) + provenance attestation
     ─ deploy DEV (auto) → E2E smoke
nightly ─ deploy TEST → full E2E (Playwright) + automated OQ suite + k6 baseline + DAST
release ─ freeze → release candidate → TEST full regression → release validation package
        ─ vendor QA approval ✍ (in PharmaOS Validation module, dogfooding) → publish to customer VAL rings
```

## 18.3 Database migrations

- Expand/contract only: (1) add new structures, (2) deploy code that writes both, (3) backfill, (4) switch reads,
  (5) remove old structures in a later release.
- Migrations run as a Kubernetes `Job` (pre-sync hook). Each migration is idempotent and recorded in
  `schema_history` with a checksum.
- CI gate: a database migrated from the previous release plus this release's migrations must equal the
  generated DDL (schema diff = ∅).
- Migrations that touch GxP data set `app.user_id` to the migration service account and `app.change_reason` to
  the migration ID, so every backfilled change is audit-trailed.

## 18.4 Approvals for promotion

| To | Approvals |
|---|---|
| DEV, TEST | Automated |
| Vendor STAGING | Engineering lead |
| Signed release bundle published | Vendor release manager + vendor QA (release validation package approved) |
| Customer VAL (on-prem) | Customer imports the bundle into Harbor (cosign + Trivy verified) under customer change control |
| Customer PROD (on-prem) | Same digests as VAL; customer change-control reference in the GitOps PR, or in the compose installer log for tier S. The customer chooses the window |

## 18.5 Quality gates

| Gate | Threshold |
|---|---|
| Unit test coverage (Domain + Application) | ≥ 80% lines, 100% of rule-evaluator operators |
| Mutation score (Rules, Workflow, ESig, Authorization) | ≥ 70% |
| Critical/High vulnerabilities | 0 unwaived |
| OpenAPI breaking changes | 0 without a major version |
| Integrity tests | 100% pass |
| E2E High-risk scenarios | 100% pass |

## 18.6 Observability and operations

SLO dashboards per service · error budget policy (freeze features when burned) · on-call rota with runbooks
linked from alerts · post-incident reviews feed CAPA in the vendor QMS (the vendor uses PharmaOS itself).
