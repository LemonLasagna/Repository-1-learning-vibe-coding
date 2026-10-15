# 9. Functional Specification (FS)

| Doc ID | FS-PHOS-001 | Version | 0.1 DRAFT | Traces to | URS-PHOS-001 |
|---|---|---|---|---|---|

Each FS item states **what the system does**. The design specification (doc 10) states how. IDs are
`FS-<area>-nnn` and trace to the URS IDs shown in brackets.

## 9.1 Common record behaviour (applies to every GxP entity)

| ID | Function | URS |
|---|---|---|
| FS-REC-001 | Each record has a system-generated business number from its numbering scheme (pattern tokens: `{SITE}`, `{YYYY}`, `{SEQ:5}`, `{TYPE}`). Numbers are never reused. Cancelled records keep their number | DMS-003, SEC-015 |
| FS-REC-002 | Records move only through workflow transitions. A direct status edit is impossible through the UI or the API | WF-001 |
| FS-REC-003 | Saving a modified GxP record prompts for a reason when the entity's audit policy says `reason_required`. The reason is written to the audit trail | DI-004 |
| FS-REC-004 | A save with a stale `row_version` returns **409 Conflict** and shows the user a diff with the current version | DI-009 |
| FS-REC-005 | "Delete" on a GxP record is not offered. Users can Cancel, Void, or Retire, and each of these is a transition with reason and (if configured) signature | DI-003 |
| FS-REC-006 | The record header shows status, owner, due date, workflow progress, and all executed signatures with name, date/time (offset), and meaning | SEC-010 |
| FS-REC-007 | The audit-trail tab shows field-level history with field labels as they were at the time of change, plus user, timestamp, reason, and old/new values. It can be exported to PDF/A and CSV | DI-005 |
| FS-REC-008 | "Print record" produces a PDF/A rendering with the signature manifestation and an audit-trail appendix (optional). The output hash is recorded | DI-008 |
| FS-REC-009 | Attachments are stored with SHA-256, virus-scanned, and immutable once the record leaves Draft. A replacement creates a new attachment row; the old one is retained | DI-001 |
| FS-REC-010 | Related-records panel: typed links (caused-by, resolved-by, affects, references) are navigable in both directions | QMS-007 |

## 9.2 Configuration framework

| ID | Function | URS |
|---|---|---|
| FS-CFG-001 | **Field Builder**: creates fields with these types: text, long text, rich text (sanitised), integer, decimal + UoM, date, date-time, boolean, lookup (single/multi), user, group, reference (to any entity), table (repeating group), attachment, signature block, calculated (read-only expression) | CFG-001 |
| FS-CFG-002 | **Form Builder**: sections, columns (1–4), tabs, field placement, conditional visibility, conditional requiredness, per-workflow-state access (hidden/read/edit/required) | CFG-001 |
| FS-CFG-003 | **Rule Builder**: rules typed as validate / default / derive / visibility / required / route / notify, with triggers on-change / on-save / on-transition. Expressions are built visually and stored as a JSON AST. Decision tables support hit policies FIRST, UNIQUE, COLLECT | CFG-003 |
| FS-CFG-004 | **Workflow Builder**: see 9.3 | CFG-004 |
| FS-CFG-005 | **Notification Builder**: event + condition + recipient rule (role, field-user, owner's manager, group, dynamic expression) + template + channel + digest mode | NTF-001 |
| FS-CFG-006 | **Role Builder**: roles as permission bundles (`resource:action`). Scoped assignment to an org node. Permission preview ("what can this user do at site X?") | SEC-003 |
| FS-CFG-007 | **Organization Builder**: hierarchy editor with drag-to-reparent. Reparenting a node with active records is blocked, or requires a migration task | GEN-001 |
| FS-CFG-008 | **E-Signature Builder**: signature meanings and policies per entity/action: auth components (password, password+TOTP, FIDO2), reason required, second person, manifest text | SEC-012 |
| FS-CFG-009 | **Audit Trail Builder**: per entity/field capture mode (full, change-only, none for non-GxP configured fields), reason-required flag, review frequency for audit-trail review scheduling. *Platform-shipped GxP fields cannot be set to "none".* | DI-004, DI-006 |
| FS-CFG-010 | **Document Template Builder**: DOCX/HTML templates with merge fields, tables, conditional blocks, and a signature manifestation block. Renders to PDF/A | DMS-005 |
| FS-CFG-011 | **Dashboard Builder**: grid layout, widgets bound to registered data sources, filters bound to context (site, period), audience by role | RPT-001 |
| FS-CFG-012 | **Report Builder**: tabular, grouped, and pivot reports on registered data sources. GxP reports additionally need a template, validation status, and an approval signature before use | RPT-002 |
| FS-CFG-013 | **Menu Builder**: menu tree bound to permissions | CFG-002 |
| FS-CFG-014 | **Config packages**: select artefacts → package (manifest + SHA-256) → e-signed approval → export → import in the target environment → hash check → deploy. The deployment is a single transaction with automatic rollback on failure | CFG-007 |
| FS-CFG-015 | **Version compare**: side-by-side diff for any configuration artefact version (JSON-aware, rendered with labels) | CFG-006 |
| FS-CFG-016 | **Custom entities**: an admin defines a new entity (fields, form, workflow, numbering). It is stored in `meta.custom_entity_record` and inherits the audit, e-sig, attachment, and search capabilities | CFG-008 |
| FS-CFG-017 | In environments flagged `PROD`, configuration editors open **read-only** | CFG-007 |

## 9.3 Workflow engine

| ID | Function | URS |
|---|---|---|
| FS-WF-001 | Supported elements: start/end, user task, service task (platform action), script-free business rule task (rule/decision table), exclusive gateway, parallel gateway, inclusive gateway, timer (boundary and intermediate), message catch (external event), call activity (sub-workflow) | WF-001 |
| FS-WF-002 | Approval step modes: SEQUENTIAL (ordered list), PARALLEL_ALL, PARALLEL_ANY, QUORUM(n). Assignee rules: user, role@org-scope, group, field reference, manager-of, expression | WF-001 |
| FS-WF-003 | Conditional routing on record fields, computed risk level, or decision-table output | WF-001 |
| FS-WF-004 | Rejection returns to any configured target node. A loop counter is recorded. A configurable maximum loop count raises an escalation | WF-002 |
| FS-WF-005 | Escalation policies: up to 5 levels, each with duration (business calendar aware), recipients, and an action (notify, reassign, add approver) | WF-003 |
| FS-WF-006 | Delegation: (a) planned out-of-office with a delegate, (b) ad hoc per-task delegation with a reason. The delegate's actions record `on_behalf_of`. The delegate must hold the required role/training, or the delegation is refused | WF-004 |
| FS-WF-007 | Running instances are pinned to `definition_version`. Publishing a new version does not affect them. An admin "migrate instance" action is available with reason + signature | WF-005 |
| FS-WF-008 | Simulation: run a definition against a hypothetical record payload. Shows the path, assignees resolved, and SLA timings. No records are written | WF-006 |
| FS-WF-009 | Transitions can require an e-signature (policy reference) and guard conditions (required fields complete, child records closed, SoD) | SEC-012 |
| FS-WF-010 | Shipped reference workflows: Deviation, CAPA, Change Control, Complaint, OOS, Document Lifecycle, Training Assignment, Batch Record Review, Batch Release, Supplier Qualification, Validation Protocol, Config Package | WF-001 |

### 9.3.1 Reference workflow: Deviation

```
Draft ─submit✍(Authored)→ Triage ─[minor]→ Owner Investigation ─→ QA Approval✍(Approved) ─→ Closed
                            │                                                     ▲
                            └─[major|critical]→ Investigation ═╦═ QA Review✍     ║
                                                               ╚═ Prod Review✍ ═╝ (PARALLEL_ALL)
                            reject ↺ to Investigation (loop counter)
Exits: Cancelled✍ (reason) from Draft/Triage only.
Guards: Closed requires all linked CAPA in state ≥ "Plan Approved", batch impact recorded for each linked batch.
Timers: 30 business days → L1 escalation; 45 → L2; due-date extension sub-process (qms.due_date_extension).
```

**As implemented (R0).** `DeviationWorkflow` in `src/Hosts/PharmaOS.Platform.Host/Qms/`, code-defined as `DEVIATION_STD` v1
(its JSON is stored in `wf.process_definition`; each instance records the version it runs under). The minor path:

| From | Action | To | Who (permission) | Signature | Guard / reason |
|---|---|---|---|---|---|
| Draft | submit | Triage | `qms.deviation:write` | Authored | Planned deviation needs a justification |
| Draft | cancel | Cancelled | `qms.deviation:write` | Cancelled | Reason required |
| Triage | start_investigation | Investigation | `qms.deviation:triage` (becomes QA owner) | — | — |
| Triage | cancel | Cancelled | `qms.deviation:triage` | Cancelled | Reason required |
| Investigation | submit_for_approval | QA Approval | `qms.deviation:write` | — | Root cause; impact assessment for major/critical |
| QA Approval | approve | Closed | `qms.deviation:approve` | Approved (four eyes: not the last editor, not an author or reviewer) | Every linked CAPA has an approved plan |
| QA Approval | reject | Investigation | `qms.deviation:approve` | — | Reason required |

Signed transitions run through the e-signature service: the challenge carries the action, and the signature and
the state change commit in one transaction. Field editability is per state and enforced by the server; a reason
for change is required after Draft. History is written to `wf.process_history` with the signature id.
Not yet: the major/critical branch with parallel QA and production review, timers and escalation, the
batch-impact guard on closure, due-date extensions, and task inboxes. (`DEVIATION_STD` v2 added the CAPA guard;
a stored v1 definition is upgraded on first use, and running instances keep the version they started with.)

### 9.3.2 Reference workflow: CAPA
`Draft → Plan Review✍ → Plan Approved → Implementation (actions tracked) → Implementation Verified✍ →
Effectiveness Check Pending (timer = planned date) → Effectiveness Verified✍ → Closed | Ineffective → new CAPA`

**As implemented (R0).** `CapaWorkflow` (`CAPA_STD` v1) in `src/Hosts/PharmaOS.Platform.Host/Qms/`. The plan is the actions
plus one effectiveness check; the plan-approval signature covers them (the record hash includes child records).

| From | Action | To | Who (permission) | Signature | Guard / reason |
|---|---|---|---|---|---|
| Draft plan | submit_plan | Plan review | `qms.capa:write` | — | At least one action and the effectiveness check |
| Draft plan | cancel | Cancelled | `qms.capa:write` | Cancelled | Reason required |
| Plan review | approve_plan | Implementation | `qms.capa:approve` | Approved (not the last editor) | — |
| Plan review | return_plan | Draft plan | `qms.capa:approve` | — | Reason required |
| Plan review | cancel | Cancelled | `qms.capa:approve` | Cancelled | Reason required |
| Implementation | verify_implementation | Effectiveness check | `qms.capa:approve` | Verified (not the last editor) | Every action done |
| Effectiveness check | close | Closed | `qms.capa:approve` | Verified (not the last editor) | Result recorded |

Actions (correction, corrective, preventive) have an assignee and due date; they are planned and withdrawn in Draft
plan and completed with evidence during Implementation. The effectiveness check (criteria, method, planned date) is
planned in Draft plan; its result (effective or ineffective, with evidence) is recorded in Effectiveness check. Every
change to actions or the check advances the CAPA's version (ETag). A CAPA raised from a deviation takes its site.
Not yet: the effectiveness timer (the planned date is recorded, not enforced), a follow-up CAPA raised automatically
when the check is ineffective, action-level workflows, and change-control links.

### 9.3.3 Reference workflow: Change Control
`Draft → Initial Assessment (classify; risk) → Impact Assessment (parallel per impacted area) → Approval to
Implement✍ (QA + Owner; + RA if regulatory impact) → Implementation (tasks; variations submitted) →
Pre-effectiveness Verification✍ → Effective (coordinated date; triggers document effective dates, recipe
versions, retraining) → Post-implementation Review✍ → Closed`

### 9.3.4 Reference workflow: Training
`Assigned → In Progress → (Assessment) → Completed✍ (trainee) → [OJT: Trainer Verified✍] → Qualified`, with
`Overdue` by timer and `Waived✍` through QA.

### 9.3.5 Reference workflow: Batch Release
`EBR Complete → Production Review✍ → QA Review (by exception)✍ → Release Checks (automated) →
QP/QA Decision✍ (Release | Reject | Conditional) → Lot status updated (WMS)`

## 9.4 Electronic signatures

| ID | Function | URS |
|---|---|---|
| FS-ESIG-001 | The signature dialog displays record identity, version, record hash (short), meaning and its description, and the signer's name | SEC-010 |
| FS-ESIG-002 | The first signature in a session requires user ID + password (+ second factor per policy). Subsequent signatures within the same continuous session require at least the password. A policy can require both components for every signature | SEC-014 |
| FS-ESIG-003 | The signature binds `record_id`, `record_version`, and the SHA-256 of the canonical record snapshot. A subsequent change makes the signature show as "applies to version n" | SEC-011 |
| FS-ESIG-004 | Failed signature attempts are logged. N consecutive failures (policy) lock the account and raise a security alert | SEC-008 |
| FS-ESIG-005 | Signatures cannot be updated or deleted (DB-enforced) | SEC-011 |
| FS-ESIG-006 | Second-person policies reject a signature by any user who authored or previously signed the record in a conflicting meaning | SEC-013 |
| FS-ESIG-007 | Users must acknowledge the e-signature statement (versioned) before their first signature | SEC-010 |

## 9.5 Module functions (summary; detail per module in the FS appendices)

### Document Management
FS-DMS-001 lifecycle states and transitions per document type · FS-DMS-002 check-out lock with admin break-lock (audited) ·
FS-DMS-003 Office/online editing via WOPI-compatible editor (optional) or upload · FS-DMS-004 PDF rendition with
signature page, header/footer stamps and status watermark (`DRAFT`, `EFFECTIVE`, `SUPERSEDED`, `UNCONTROLLED COPY`) ·
FS-DMS-005 effective-date scheduler with training gate threshold · FS-DMS-006 periodic review tasks · FS-DMS-007 controlled print and
reconciliation · FS-DMS-008 document relations and "where-used" (recipes, courses, specifications) · FS-DMS-009
site applicability · FS-DMS-010 e-logbooks with correction-by-new-entry (`correction_of_id`).

### Quality
FS-QMS-001 quick intake form (mobile) · FS-QMS-002 triage with decision table for classification · FS-QMS-003
batch/material/equipment impact grid · FS-QMS-004 due-date extensions (max count, approval) · FS-QMS-005 RCA canvas
(5-Why tree, Fishbone with 6M default categories, Is/Is-Not grid), root cause marked → `qms.root_cause` ·
FS-QMS-006 CAPA plan with actions and effectiveness criteria · FS-QMS-007 change control impact matrix auto-routing by
affected item type · FS-QMS-008 temporary change expiry enforcement · FS-QMS-009 complaint reportability decision table
→ due date; PV hand-off message · FS-QMS-010 OOS: Phase I checklist, hypothesis tests, retest authorisation guard,
Phase II with batch impact · FS-QMS-011 OOT rules (e.g. regression-based prediction interval, run rules) on
stability and process data · FS-QMS-012 recall scoping from genealogy, customer notices, recovery tracking ·
FS-QMS-013 management review pack generation · FS-QMS-014 metric calculation jobs · FS-QMS-015 PQR compilation.

### Manufacturing
FS-MES-001 recipe designer (procedure → unit procedure → operation → phase) with phase library · FS-MES-002 parameter
limits (target, low, high; CPP flag) · FS-MES-003 recipe versioning and approval; production orders pin the version ·
FS-MES-004 batch scaling and potency adjustment at order creation · FS-MES-005 step execution engine (state per step;
prerequisites; parallel branches) · FS-MES-006 equipment status interlocks and hold times · FS-MES-007 training gate
per step · FS-MES-008 dispensing with FEFO proposal and balance integration · FS-MES-009 second-person verification
(different user, badge scan + password) · FS-MES-010 line clearance checklists · FS-MES-011 IPC capture and limit
evaluation · FS-MES-012 exception engine (out-of-limit, override, late entry, manual entry where automatic expected)
· FS-MES-013 review-by-exception view · FS-MES-014 yield and label reconciliation · FS-MES-015 executed batch record
PDF/A generation · FS-MES-016 release checklist engine with pluggable checks.

### Warehouse
FS-WMS-001 receipt with ASL check · FS-WMS-002 lot creation and container labelling (GS1) · FS-WMS-003 status
transitions with e-signature · FS-WMS-004 putaway rules by storage condition · FS-WMS-005 FEFO/FIFO picking with
minimum remaining shelf life · FS-WMS-006 genealogy graph (recursive query) · FS-WMS-007 cycle counts (ABC, random)
· FS-WMS-008 reconciliation · FS-WMS-009 temperature excursion workflow · FS-WMS-010 destruction with witness.

### Laboratory
FS-LIMS-001 specification versions per market · FS-LIMS-002 sample generation rules (receipt, IPC, stability pull,
EM schedule) · FS-LIMS-003 custody events · FS-LIMS-004 worksheets · FS-LIMS-005 result entry with calculation engine
(validated formulas, rounding per pharmacopoeial convention configured per test) · FS-LIMS-006 auto OOS/OOT flag →
investigation · FS-LIMS-007 second-person review · FS-LIMS-008 stability protocols and pull calendars · FS-LIMS-009
stability trending · FS-LIMS-010 EM limits and excursions · FS-LIMS-011 CoA generation · FS-LIMS-012 instrument
integration (file-drop/SDMS/CDS) with audit-trail-review flag.

### Regulatory, Training, Validation, Supplier, Audit, ERP
Each follows the same pattern of entity + workflow + rules, as defined by the URS items in doc 08 §8.9–8.16. Their
detailed FS appendices (FS-RIM, FS-TRN, FS-VAL, FS-SQM, FS-AUD, FS-ERP) are produced in the release in which the
module is built (doc 20).

## 9.6 Interfaces

| ID | Interface | Direction | Protocol |
|---|---|---|---|
| FS-INT-001 | Corporate directory (on-prem AD / LDAP; optional external IdP) | In | OIDC/SAML via Keycloak; SCIM or LDAP sync |
| FS-INT-002 | Corporate ERP / GL | Out (postings), In (master data, optional) | REST / file / iDoc adapter in integration-hub |
| FS-INT-003 | Equipment (OPC UA), balances, label printers | In/Out | edge-connector |
| FS-INT-004 | CDS / SDMS / instruments | In | file-drop, vendor API |
| FS-INT-005 | Pharmacovigilance / vigilance system | Out | REST / E2B-capable middleware |
| FS-INT-006 | Serialization system (L3/L4) | Out/In | REST / EPCIS |
| FS-INT-007 | eCTD publishing tool | In/Out | file/REST |
| FS-INT-008 | Email (SMTP), Teams/Slack | Out | SMTP, webhooks |
| FS-INT-009 | Apache Superset (self-hosted) | Out (read replica) | Guest token with row-level filter |
