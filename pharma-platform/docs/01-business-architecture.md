# 1. Business Architecture

## 1.1 Vision

There is one regulated system of record for the product lifecycle, from supplier qualification to batch
release to post-market complaint. Configurable process models adapt it to six industry segments. No
segment needs code forks.

## 1.2 Target segments and their regulatory drivers

| Segment | Core GMP/QMS basis (verify current editions) | Distinguishing platform needs |
|---|---|---|
| Small-molecule pharma | US drug CGMP (21 CFR 210/211); EU GMP Part I and Annexes; ICH Q7 (APIs), Q9(R1), Q10 | EBR, QP certification, PQR/APR, OOS |
| Biopharmaceutical | Above, plus biologics regulations (21 CFR 600s); EU GMP Annex 2 | Cell-bank genealogy, cold chain, campaign mgmt, viral-clearance records |
| Vaccine | Biologics regulations plus lot-release by authority (e.g. official control authority batch release in EU) | Authority lot-release protocol, potency stability, EM-heavy aseptic (EU GMP Annex 1) |
| Medical device | 21 CFR 820 QMSR, which incorporates ISO 13485 *(unverified effective date: Feb 2026)*; EU MDR 2017/745; ISO 14971 | DHF/DMR/DHR equivalents, design controls, UDI, vigilance |
| Cosmetics | EU Cosmetics Regulation 1223/2009; US MoCRA; ISO 22716 | Safety assessment, product information file, adverse-event intake, lighter validation |
| Nutraceutical | US dietary supplement CGMP (21 CFR 111) | Identity testing per component, master manufacturing record, specification for each component |

**Segment packs** are delivered as configuration packages (doc 10 §10.4). A pack contains the document types,
workflows, forms, signature policies, metric definitions, and report templates for that segment. The core
code is identical across all segments.

## 1.3 Capability map (Level 1 → Level 2)

```
PharmaOS
├── Quality Management ─ Quality Events · Deviations · CAPA · Change Control · Complaints · OOS/OOT
│                        · Recalls · Management Review · Quality Metrics · PQR/APR · Lessons Learned
├── Quality Risk Mgmt ── Risk Registers · Risk Assessments (FMEA/HACCP/PHA/FTA) · Risk Controls · RCA (5-Why, Fishbone, Is/Is-Not)
├── Document Management ─ Policies · SOPs · Forms · Specifications · Protocols · Reports · Lifecycle · Periodic Review
│                        · Controlled Print · Distribution · Logbooks · Executed Forms
├── Supplier Quality ─── Qualification · ASL · Quality Agreements · SCAR · Scorecards · Change Notifications
├── Audit & Inspection ─ Audit Programme · Internal/Supplier/External Audits · Regulatory Inspection Front/Back Room
├── Training ─────────── Job Roles · Curricula · Read-and-Understand · ILT · OJT · Assessments · Training-gated Access
├── Validation ───────── System Inventory · VMP · URS/FS/DS · Risk-based Testing · IQ/OQ/PQ · RTM · Periodic Review
├── Manufacturing (MES) ─ Master Recipe (ISA-88) · Master Formula · EBR/EBMR · Dispensing/Weighing · Line Clearance
│                        · IPC · Equipment Logs · Yield & Label Reconciliation · Review-by-Exception · Batch Release
├── Laboratory (LIMS) ── Methods · Specifications · Samples · Results · Stability · EM & Micro · CoA · Instruments
├── Warehouse (WMS) ──── Receipt · Quarantine/Status · Lots/Containers · FEFO/FIFO Picking · Genealogy · Cycle Count
│                        · Reconciliation · Labels/Barcodes/GS1 · Serialization readiness · Temperature Excursions
├── Pharma ERP ───────── Procure-to-Pay (ASL-enforced) · MPS/MRP · Production Orders · Order-to-Delivery · Returns
│                        · Costing hand-off (finance GL remains in corporate ERP)
├── Regulatory (RIM) ─── Marketing Authorisations · Registered Details · Submissions · Variations · Commitments
│                        · Artwork · Labeling (CCDS→local) · Renewals · IDMP readiness
├── Equipment & Assets ─ Equipment Master · Status · Calibration · Maintenance · Cleaning · Utilities · Alarms
└── Platform ─────────── Organisation · Identity & Access · Configuration Studio · Workflow · E-Signature
                         · Audit Trail · Notifications · Reporting & Dashboards · Integration · Search
```

## 1.4 Value streams and module interplay

### VS-1 Procure → Receive → Release material
`erp.purchase_order` (ASL check, `sqm.approved_supplier_list`) → `wms.goods_receipt` (lot created **Quarantine**)
→ `lims.sample` (sampling plan) → `lims.test_result` against `lims.specification` → OOS? → `qms.oos_investigation`
→ QC disposition → `wms.lot_status_history` (**Released**, e-signed) → available for FEFO picking.

### VS-2 Plan → Make → Release batch
`erp.master_production_schedule` → `erp.production_order` (pinned `mes.master_recipe` **version**) →
`mes.dispensing_order` (FEFO pick of **Released** lots only, balance check verified) → `mes.batch_step_execution`
(parameters, IPCs, equipment status interlocks, training-gate per operator) → exceptions → `qms.deviation`
→ `mes.yield_reconciliation` / `mes.label_reconciliation` → `mes.batch_review` (review-by-exception)
→ `mes.batch_release` (automated checklist: deviations closed, OOS closed, CoA, change controls effective,
registered-detail conformance for the market) → QP/QA e-signature → `wms.lot` **Released**.

### VS-3 Detect → Investigate → Correct → Prevent
Any module raises a `qms.quality_event` → triage → Deviation / Complaint / OOS / Audit finding →
`qms.investigation` (5-Why, Fishbone, Is/Is-Not) → `qms.root_cause` → `qms.capa` → `qms.capa_action`
(may spawn `qms.change_control`) → `qms.capa_effectiveness_check` → trend into `qms.quality_metric_value`
→ `qms.management_review`.

### VS-4 Change → Assess → Implement → Make effective
`qms.change_control` → impact assessments (Quality, Validation, Regulatory, Training, Supply, EHS)
→ `qrm.risk_assessment` → `qms.change_regulatory_assessment` per country → `rim.variation`
→ tasks across DMS (new document versions), MES (new recipe version), VAL (re-qualification),
TRN (retraining assignments) → coordinated effective date → closure.

### VS-5 Author → Approve → Train → Use document
`dms.document_version` draft → review cycle → approval (e-sig) → `trn.training_assignment` to affected job roles
→ **effective only once a configurable training-completion threshold is met** → the training gate in `iam.training_gate`
blocks untrained users from executing the procedure in MES/LIMS.

### VS-6 Register → Maintain → Comply
`rim.marketing_authorization` → `rim.registered_detail` (shelf life, sites, specs) → **conformance check at batch
release** (is this site and spec registered for the destination market?) → variations driven by change control
→ commitments tracked to closure.

## 1.5 Organisation model

The hierarchy is Enterprise → Region → Country → Legal Entity → Site → Plant → Building → Area → Room. It runs
in parallel with Business Unit and Department. The hierarchy is materialised in `org.org_node` (path + depth).
That table is the scope anchor for role assignments and ABAC (doc 14).

## 1.6 Business actors (RACI anchors)

| Actor | Primary modules | Signature meanings typically held |
|---|---|---|
| Operator / Technician | MES, WMS, Logbooks | Performed |
| Supervisor | MES, Training (OJT) | Verified, Reviewed |
| QC Analyst / Reviewer | LIMS | Performed, Reviewed |
| QA Specialist | QMS, DMS, Batch Review | Reviewed, Approved |
| Qualified Person / Head of QA | Batch Release | Certified / Released |
| Document Owner / Author | DMS | Authored |
| Validation Engineer | VAL | Authored, Executed |
| Regulatory Affairs | RIM | Approved |
| Supplier Quality | SQM | Approved |
| Auditor | AUD | Authored |
| Trainer | TRN | Verified |
| System Administrator (IT) | Config, IAM | Admin actions only (no GxP approvals: enforced by SoD) |
| Business Configurator | Config Studio | Authored (config packages) |

## 1.7 Business KPIs the platform must produce

- Deviation and CAPA cycle time, overdue rate, recurrence rate
- Right-first-time batch rate, review-by-exception ratio, release lead time
- OOS rate (lab error vs product), invalidated OOS ratio
- Complaint rate per million units, reportable on-time rate
- Training compliance per role and per site; "trained-before-effective" rate
- Supplier PPM, SCAR ageing, ASL coverage
- Validation on-time periodic review, open test incidents
- Regulatory commitments on-time, variation lead time
- Inventory accuracy (cycle-count), expired-stock value, FEFO adherence
- Data-integrity indicators: audit-trail review completion, failed signature attempts, privileged-account activity

## 1.8 Business rules that are non-negotiable (hard-coded, not configurable)

The following stay out of reach of configuration on purpose, because configuring them away breaks compliance:

1. Released lots only can be issued to production (status interlock).
2. A record cannot be approved by its author when the signature policy has `require_second_person`. SoD rules
   cannot be disabled for GxP roles.
3. Executed signatures, audit-trail rows, and version snapshots are immutable.
4. GxP records are never physically deleted.
5. An effective document version must have an approval signature.
6. Batch release requires that every mandatory release check passes, or a documented, e-signed override by a
   distinct release authority.
7. The clock source is the server (UTC with site-timezone rendering). Client clocks are never trusted.
