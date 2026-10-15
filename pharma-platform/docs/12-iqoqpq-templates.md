# 12. IQ / OQ / PQ Templates

All protocols are executed online in the Validation module (`val.test_protocol` → `val.test_execution`) or on
paper from the same template. Every step records: actual result, Pass/Fail, executor, timestamp, evidence
reference, and (for High-risk steps) a witness/reviewer signature.

## 12.0 Common protocol header

| Field | Value |
|---|---|
| Protocol No. | `{IQ|OQ|PQ}-PHOS-{SITE}-{nnn}` |
| Title | |
| System / Version | PharmaOS {release}, config package(s) {code@version, hash} |
| Environment | VAL / PROD (IQ only in PROD) |
| Objective | |
| Scope / Out of scope | |
| Prerequisites | Approved VP; approved specifications; trained executors (training record refs) |
| References | URS / FS / DS / CS IDs, risk assessment |
| Acceptance criteria | |
| Approvals (pre-execution) | Author ✍ · Reviewer ✍ · QA ✍ |

**Step table format**

| Step | Requirement ref | Instruction | Expected result | Actual result | P/F | Evidence | Executed by / date | Reviewed by |
|---|---|---|---|---|---|---|---|---|

Post-execution: incidents list · deviations · conclusion · approvals (Executor ✍ · Reviewer ✍ · QA ✍).

---

## 12.1 IQ template — Installation Qualification (platform deployment)

**Objective.** To verify that the PharmaOS release is installed in the target environment as specified, and
that its configuration matches the approved baseline.

| Step | Ref | Instruction | Expected result |
|---|---|---|---|
| IQ-INF-001 | DS-INF | Record the Kubernetes cluster version, node pools, and node OS image | Match infrastructure spec |
| IQ-INF-002 | DS-INF | Verify every container image digest against the release manifest (`kubectl get pods -o jsonpath` → image@sha256) | All digests match the signed manifest; cosign verification passes |
| IQ-INF-003 | DS-INF | Verify PostgreSQL version, extensions (pgcrypto, pg_trgm, btree_gist), and parameters (`timezone=UTC`, `wal_level`, archiving on) | As specified |
| IQ-INF-004 | DS-DB | Run schema verification: compare the deployed schema with the release DDL (`pg_dump --schema-only` diff) | No differences |
| IQ-INF-005 | URS-DI-001 | Verify `trg_audit` exists on every GxP table (query `pg_trigger` against the catalog list of non-N tables) | Count = expected; zero missing |
| IQ-INF-006 | URS-DI-002 | Verify that the app role lacks UPDATE/DELETE on `audit.audit_trail` (`has_table_privilege`) | False for both |
| IQ-INF-007 | URS-GEN-003 | Verify NTP synchronisation on all nodes (offset < 1 s) and DB timezone = UTC | Pass |
| IQ-INF-008 | URS-SEC-009 | Verify TLS configuration at ingress (protocols ≥ 1.2, approved cipher suites); verify storage encryption enabled (PV class, MinIO SSE, DB volume) | Pass |
| IQ-INF-009 | URS-DI-011 | Verify the backup schedule and WAL archiving status (CNPG `ContinuousArchiving=True`) | Pass |
| IQ-INF-010 | URS-SEC-001 | Verify the Keycloak realm configuration export matches the baseline (hash of sanitised export) | Match |
| IQ-INF-011 | URS-CFG-007 | Verify installed config packages: codes, versions, and SHA-256 match the approved CS | Match |
| IQ-INF-012 | — | Record the SBOM reference for the release | Recorded |

## 12.2 OQ template — Operational Qualification

**Objective.** To verify that the configured functions operate as specified across normal, boundary, and
error conditions.

### 12.2.1 OQ-DI — Data integrity (executable; High risk)

| Step | Ref | Instruction | Expected result |
|---|---|---|---|
| OQ-DI-001 | URS-DI-001 | Attempt a GxP insert with no authenticated actor | Rejected (insufficient_privilege) |
| OQ-DI-002 | URS-DI-001 | Insert a record with actor and reason | Audit row with actor, reason, timestamp, new values, row hash |
| OQ-DI-003 | URS-DI-001/009 | Update one field | Audit row lists only the changed field with old/new; row_version + 1 |
| OQ-DI-004 | URS-DI-009 | Update with a stale row_version | Rejected (serialization_failure) |
| OQ-DI-005 | URS-DI-003 | Attempt a physical delete | Rejected |
| OQ-DI-006 | URS-SEC-003 | As tenant B, read and write tenant A data | 0 rows visible; write rejected |
| OQ-DI-007 | URS-DI-002 | Attempt audit-trail UPDATE/DELETE as the app role and as the owner | Both rejected |
| OQ-DI-008 | URS-SEC-011 | Attempt to modify an executed signature | Rejected |
| OQ-DI-009 | URS-DI-007 | Seal, verify, then tamper below the app (trigger disabled) and verify again | First verify OK; second detects tampering |

Evidence: the console output of `database/tests/integrity_tests.sql`, attached and hashed.

### 12.2.2 OQ-ESIG — Electronic signatures (High)

| Step | Ref | Instruction | Expected result |
|---|---|---|---|
| OQ-ESIG-001 | SEC-010 | Sign a deviation as APPROVED; view the record and its PDF | Name, date/time with offset, and meaning shown in both |
| OQ-ESIG-002 | SEC-014 | First signature in session | Requires user ID + password (+ factor) |
| OQ-ESIG-003 | SEC-014 | Second signature in the same session (policy: continuous) | Requires password only |
| OQ-ESIG-004 | SEC-011 | Modify the record after signature | Signature shown as "applies to vN"; hash mismatch flagged |
| OQ-ESIG-005 | SEC-013 | Author attempts an approval with a second-person policy | Rejected with forbidden-sod |
| OQ-ESIG-006 | SEC-008 | Enter a wrong password N times | Account locked; security event logged |
| OQ-ESIG-007 | SEC-011 | Submit a signature after another user changed the record (stale intent) | Rejected: "record changed" |
| OQ-ESIG-008 | SEC-010 | First-time signer | E-signature acknowledgement required first |

### 12.2.3 OQ-WF — Workflow (High/Medium)
Parallel ALL/ANY/QUORUM completion · conditional routing on classification · rejection loop to a target with
counter · escalation L1/L2 with a business calendar (time-travel test clock in VAL) · delegation with
on-behalf-of in the audit trail · instance pinned to its version after a new version is published.

### 12.2.4 OQ-CFG — Configuration (High)
Field add → appears on the form only after package deploy · per-state access enforced by the API (not just the
UI) · rule parity (client = server) on the golden set · package hash mismatch blocks import · PROD editors
read-only.

### 12.2.5 OQ-SEC — Access (High)
Role scope by site · ABAC deny overrides · SoD assignment block · access expiry · session timeout · MFA enforcement
for privileged roles.

### 12.2.6 OQ-MES — Manufacturing (High)
Quarantined lot blocked at dispense · weight out of tolerance refused · second-person verifier ≠ performer ·
equipment status DIRTY blocks the step · hold-time breach blocks · untrained operator blocked · CPP out of limit
creates an exception · release blocked with an open exception · registered-detail mismatch blocks release for that
market.

### 12.2.7 OQ-LIMS, OQ-WMS, OQ-QMS, OQ-DMS, OQ-TRN, OQ-RIM
Each module has its own OQ annex, generated from its FS items and High/Medium URS. The structure is the same as
above.

## 12.3 PQ template — Performance Qualification (business process / UAT)

**Objective.** To demonstrate that the configured system supports the customer's end-to-end business processes
with production-representative data, users, and volumes.

| Scenario | Process | Pass criteria |
|---|---|---|
| PQ-E2E-001 | Material receipt → sampling → testing → release (VS-1) | Lot reaches Released only after approved results; complete audit trail; CoA verified |
| PQ-E2E-002 | Production order → dispense → EBR → review → release (VS-2) | Executed batch record PDF matches the data; review-by-exception lists all exceptions; release signature recorded |
| PQ-E2E-003 | Deviation → investigation → CAPA → effectiveness (VS-3) | All signatures and links present; metrics updated |
| PQ-E2E-004 | Change control with document, recipe, and training impacts (VS-4) | Coordinated effectiveness; retraining assigned; variation created |
| PQ-E2E-005 | SOP revision → training-gated effectiveness → MES gate (VS-5) | Untrained user blocked after the effective date |
| PQ-E2E-006 | Recall scoping from a finished-goods lot | Forward/backward genealogy complete in ≤ 5 s |
| PQ-PERF-001 | Load: 1 500 concurrent users, mixed profile, 1 h | p95 targets in doc 02 §2.9 met; zero errors on GxP writes |
| PQ-BCP-001 | Restore PROD backup to an isolated env; verify seal chain and record counts | RTO ≤ 4 h; `audit.verify_seals()` all OK; counts reconcile |
| PQ-BCP-002 | Regional failover drill (doc 16) | RPO ≤ 5 min; GxP functions available |

## 12.4 Validation Summary Report template (outline)

1. Introduction, scope, and system description (release, config packages, hashes)
2. Summary of activities against the VP
3. Results by protocol (executed, passed, failed, incidents)
4. Deviations and test incidents with resolution
5. RTM summary (coverage by URS priority)
6. Residual risk statement
7. Outstanding items and conditions for use
8. Conclusion and release for use. Approvals: System Owner ✍, Process Owner ✍, QA ✍
