# 10. Design Specification (DS)

| Doc ID | DS-PHOS-001 | Version | 0.1 DRAFT | Traces to | FS-PHOS-001 |
|---|---|---|---|---|---|

## 10.1 Metadata model

```
meta.module ─< meta.entity_definition ─< meta.field_definition ─> meta.lookup_list ─< meta.lookup_value
                     │                         ▲
                     ├─< meta.form_definition ─< form_section ─< form_field_placement
                     │            └─< form_state_override (state × field → access)
                     ├─< meta.business_rule ─< business_rule_action
                     ├─< meta.list_view_definition
                     ├─< meta.audit_policy
                     └─> wf.process_definition (default workflow)
meta.config_package ─< config_package_item (artifact_type, artifact_id, artifact_version, payload)
```

**Runtime resolution.** `GET /api/v1/meta/entities/{code}/runtime?state={s}` returns a compiled bundle. The
bundle contains a JSON Schema for `ext` and core fields, the UI schema (layout), rule ASTs, per-state access,
list views, and the available transitions. Bundles are cached in Valkey under the key
`meta:{tenant}:{entity}:{bundleHash}` and invalidated by a `ConfigDeployed` event. The client caches by ETag.

## 10.2 Rule language (JSON AST)

The rule language is safe and non-Turing-complete. It has no loops, no I/O, and bounded depth (≤ 32) and
size (≤ 2 000 nodes).

```json
{ "if": [
    { "in": [ { "var": "classification" }, ["MAJOR", "CRITICAL"] ] },
    { "require": ["qa_owner_id", "impact_assessment"] },
    { "hide": ["minor_justification"] }
]}
```

| Operator family | Operators |
|---|---|
| Logic | `and`, `or`, `not`, `if` |
| Compare | `==`, `!=`, `<`, `<=`, `>`, `>=`, `in`, `between`, `isEmpty` |
| Math | `+ - * /`, `round(x, n, mode)`, `min`, `max`, `abs` |
| Date | `now()` (server), `addBusinessDays(d, n, calendar)`, `diffDays` |
| Record | `var` (path), `count(children, filter)`, `lookup(list, code).attr` |
| Effects (rule actions) | `require`, `hide`, `readonly`, `setDefault`, `derive`, `error(msg)`, `warn(msg)`, `route(node)`, `notify(rule)` |

The evaluator is implemented twice, in C# (authoritative) and TypeScript (UX). Both run against **one shared
golden test suite** (`tests/rules-golden/*.json`) in CI, and a divergence fails the build. The server always
re-evaluates; the client result is advisory only.

## 10.3 Workflow engine design

- **Model**: the BPMN subset is stored as both `bpmn_xml` (interchange and diagram) and `model_json` (the
  executable form). `wf.process_node` and `wf.process_transition` are materialised for querying.
- **Execution**: token-based. Each service-boundary step is a DB transaction: advance tokens, create tasks,
  write `wf.process_history`, and emit outbox events.
- **Determinism**: given the same definition version, the same variables, and the same event sequence, the
  engine produces the same path. Timers run against the DB clock. Replay from `process_history` is used in
  OQ tests.
- **Timers**: `wf.timer_job` is polled with `SELECT … FOR UPDATE SKIP LOCKED` (no external scheduler needed).
  A misfire fires once on recovery.
- **Parallel joins**: a join node completes when the arrived token count reaches the expected count, evaluated
  under a row lock on `wf.process_instance`.
- **Business calendar**: `org.calendar` plus holidays. The SLA due date is computed when the task is created
  and stored in `wf.sla_tracking`.
- **Integration with records**: the domain service owns record state. The engine tells the service "transition
  X allowed/required". The service changes `workflow_state` in the same DB transaction, which is possible
  because both use the same database (doc 02 §2.1).

### Why build rather than adopt

| Option | Assessment |
|---|---|
| General BPMN engine (e.g. Camunda) | Full BPMN, but a Java runtime alongside a .NET stack. Licence terms for recent versions need checking for commercial production use *(unverified)*. E-sig-on-transition must be bolted on |
| Elsa Workflows (.NET, MIT) | A good fit technically. A **valid alternative** if the team prefers it. Risk: its generality enlarges the validation scope |
| **Constrained in-house engine (chosen)** | Small surface (≈10 element types), signature and SoD are first-class, deterministic replay, and test cases enumerate fully. Cost ≈ 2 engineers × 2 quarters to reach v1 |

## 10.4 Configuration packages and promotion

```
CONFIG env                    VAL env                          PROD env
──────────                    ───────                          ────────
artefacts (DRAFT) ─┐
                   ├─ build package (manifest, items, SHA-256)
                   ├─ QA approve ✍ ─────► import → verify hash → deploy (tx) → UAT ✍
                                                                       │
                                          same package bytes ──────────┴──► import → verify hash
                                                                               → deploy (tx) ✍ release
```

- The manifest lists artefact type, code, version, dependencies, and the target platform version range.
- The package is self-contained JSON, signed with the tenant's OpenBao transit key. The import verifies the signature and
  the hash.
- The deployment validates referential integrity, runs rule-AST validation and workflow model validation, and
  dry-runs migrations of in-flight records (for example a newly required field). It then commits atomically.
- **Segment packs and accelerators** (doc 01 §1.2) are vendor-published config packages.

## 10.5 E-signature design

```
canonical(record) = JCS-style JSON (RFC 8785 key ordering and whitespace; numbers as normalised exact decimals, not ECMAScript doubles — see PharmaOS.Gxp.CanonicalJson) of: entity code, id, row_version, all non-volatile fields,
                    ext, child collection hashes (ordered)
record_hash       = SHA-256(canonical(record))
signature_hash    = SHA-256(signer_id | meaning_code | signed_at(UTC ISO-8601) | entity | record_id |
                            record_version | record_hash | reason | policy_id)
```

- The challenge (`esig.signature_challenge`) holds a single-use nonce, a 120 s TTL, and the bound `record_hash`.
- Re-authentication uses a Keycloak step-up: an OIDC authorization request with `acr_values=esig` and
  `max_age=0`. A FIDO2/WebAuthn authenticator or password + TOTP is configured per policy. The returned ID token
  must have `acr=esig`, `auth_time` ≥ challenge issue time, and `sub` = signer.
- The signature manifest text is rendered from the policy template and stored in `esig.signature_manifest`
  for human-readable output.
- The display rule is that a record view always shows signatures with the version they apply to. A record
  re-opened after a change shows "Signed v7 – current v8" until it is re-signed.

## 10.6 Audit trail design

Implemented in the DB (doc 05 §5.4). The design rationale:

| Choice | Rationale |
|---|---|
| Trigger-based capture | It cannot be bypassed by any code path, including bulk jobs and migrations. Application logs cannot satisfy this |
| Actor required in session context | Makes "who" mandatory at the lowest layer (OQ-DI-001) |
| Changed-fields-only on UPDATE | Storage efficiency without losing old/new values |
| Hash per row + async seals | Tamper evidence without serialising writes |
| Business-event audit in addition | Domain events such as "Signed", "Printed", "Viewed confidential", "Exported" are written by the app to `audit.audit_trail` with `operation = 'EVENT'`, because they are not row changes |

## 10.7 Authorization design

- PEP in every service (`PharmaOS.Authorization`) and PDP in `identity-access`. Decisions are cached for 60 s
  per (user, resource-type, scope).
- Policy evaluation order: (1) tenant isolation (RLS), (2) permission present in effective roles at the record's
  org scope, (3) ABAC policies (deny overrides), (4) SoD, (5) training gate (for executable actions).
- For lists, ABAC policies compile to SQL predicates (site path prefix, confidentiality ≤ clearance, owner =
  me) that are appended to queries. Policies that cannot compile to SQL are rejected for list-level use at
  configuration time.

## 10.8 MES execution design

- The recipe (approved version) is compiled at production-order creation into an **execution plan**: an
  immutable JSON stored with the batch record. It is scaled, potency-adjusted, and pinned to the specification
  versions.
- Step state machine: `Pending → Ready (prereqs met) → InProgress → Completed | Exception → Verified`.
- Equipment interlocks are evaluated server-side on `Ready → InProgress` and again on completion (to cover hold
  time).
- Parameter capture source is tagged as MANUAL, OPCUA, SCALE, or CALC. If a step is configured as automatic,
  a manual entry creates an exception automatically.
- The edge-connector buffers OPC UA values with source timestamps. Values arriving late are accepted with the
  original timestamp and flagged `late_arrival`.
- SignalR pushes step state to all tablets viewing the batch, and optimistic locking prevents double
  execution.

## 10.9 Genealogy design

`wms.genealogy_link` holds edges (parent_lot → child_lot, batch, relation). A recursive CTE gives forward and
backward traversal, with cycle protection and depth ≤ 20. A materialised closure table refreshed on
`BatchReleased` and on lot split/merge events serves recall queries in under 5 s on 10 M edges (target, to be
confirmed in PQ load test).

## 10.10 Reporting / rendering design

- GxP report = (template version, data source version, parameters) → render → PDF/A-2b → SHA-256 →
  `rpt.report_execution`.
- Rendering is deterministic. Generation time is a parameter (not `now()` inside templates), fonts are
  embedded, and locale is fixed per execution.
- Templates are tested with golden-file tests: rendered text extraction is compared with the expected output.

## 10.11 Numbering design

`meta.numbering_sequence(scheme, scope_key)` is incremented with `UPDATE … RETURNING` inside the record's
transaction, so it is gap-free unless the transaction rolls back. A gap from a rollback is acceptable and
documented, because the number was never shown or used.

## 10.12 Error model

RFC 9457 Problem Details everywhere. Each response carries a `type` URI per error class (validation,
conflict, signature-required, interlock-failed, forbidden-sod, training-required) and `errors[]` with field
paths. Clients act on `type`, never on message text.
