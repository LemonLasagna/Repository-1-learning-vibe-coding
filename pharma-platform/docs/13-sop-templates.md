# 13. SOP Templates

These templates are delivered as `meta.document_template` entries (DOCX with merge fields) under document type
`SOP`. The section structure below is mandatory. Text in *italics* is guidance to the author and is removed on
approval.

## 13.0 Standard SOP structure

```
Header: {doc_number} v{major}.{minor} · {title} · Status: {status} · Effective: {effective_date} · Page x of y
1. Purpose            — why this procedure exists (1–3 sentences)
2. Scope              — sites, departments, systems, products in/out of scope
3. Responsibilities   — RACI table by role (not by person)
4. Definitions        — abbreviations and terms
5. Procedure          — numbered steps; each step: actor, action, system transaction, record produced
6. Records            — records generated, where stored (system + entity), retention class
7. References         — related SOPs, regulations (by document name; verify clause numbers before citing)
8. Training           — target job roles, training type (R&U / ILT / OJT), assessment required?
9. Revision history   — auto-generated from document_version (change summary, CC number)
Signature page        — auto-generated manifestation: Authored / Reviewed / Approved (name, date/time, meaning)
```

## 13.1 Platform-operation SOPs (customer needs these to operate a validated system)

| SOP ID | Title | Key content |
|---|---|---|
| SOP-CSV-001 | Computerised System Validation | Life cycle per VMP doc 11; risk method; deliverables per GAMP category |
| SOP-CSV-002 | Change Control for Computerised Systems | Platform releases, config packages, infrastructure, integrations; impact categories; regression scope |
| SOP-CSV-003 | Configuration Management (Config Packages) | Author in CONFIG → package → QA approval → VAL → UAT → PROD; hash verification; prohibited direct edits |
| SOP-SEC-001 | User Access Management | Request/approval, role scope, joiner/mover/leaver within 24 h, periodic access review (quarterly for GxP roles) |
| SOP-SEC-002 | Electronic Signatures | Meanings, acknowledgement statement, non-sharing of credentials, lockout handling, organisational certification to authorities where required |
| SOP-DI-001 | Audit Trail Review | Risk-based frequency per record class; reviewer qualifications; what to look for; documenting in `audit.audit_trail_review` |
| SOP-DI-002 | Data Integrity Governance | ALCOA+ expectations, true copies, hybrid records, metadata retention |
| SOP-IT-001 | Backup, Restore and Business Continuity | Doc 16; restore test frequency; evidence |
| SOP-IT-002 | Incident and Problem Management | Severity; GxP impact assessment; link to deviation where data or product impact |
| SOP-IT-003 | Periodic Review of Computerised Systems | Inputs, frequency, report |
| SOP-RET-001 | Record Retention and Archiving | Retention classes, legal hold, archival format (PDF/A + data export), disposition approval |

## 13.2 Process SOP templates (pre-filled examples)

### SOP-QA-010 Deviation Management — Procedure section (template)

| # | Actor | Action | System transaction | Record |
|---|---|---|---|---|
| 5.1 | Any employee | Report the event immediately on discovery, and no later than *{end of shift}* | Quality → Report Event | `qms.quality_event` |
| 5.2 | Supervisor | Take immediate action (stop line, quarantine material) and record it | Event → Immediate action | same |
| 5.3 | QA | Triage within *{1 business day}*: classify (minor/major/critical) using the classification decision table | Event → Triage ✍ | `qms.deviation` |
| 5.4 | Owner | Assess the impact on batches, materials, and equipment; place affected lots on hold | Deviation → Impact grid; WMS status change ✍ | `qms.deviation_batch`, `wms.lot_status_history` |
| 5.5 | Owner / team | Investigate using appropriate RCA tools; identify the root cause or document why it was not determinable | Investigation tab | `qms.investigation`, `qms.root_cause` |
| 5.6 | Owner | Propose CAPA where the root cause is identified or the risk warrants | Create CAPA | `qms.capa` |
| 5.7 | QA (+ Prod for major/critical) | Review and approve | QA Review ✍ | signatures |
| 5.8 | QA | Close within *{30 calendar days}* of discovery, or approve an extension with justification | Close ✍ / Extension ✍ | `qms.due_date_extension` |

### SOP-QA-020 CAPA · SOP-QA-030 Change Control · SOP-QA-040 Complaints · SOP-QC-010 OOS Investigation · SOP-QA-050 Batch Record Review and Release · SOP-DOC-001 Document Control · SOP-TRN-001 Training · SOP-WH-001 Receipt and Quarantine · SOP-WH-002 Dispensing · SOP-MFG-001 Line Clearance · SOP-QC-020 Stability Programme · SOP-QC-030 Environmental Monitoring · SOP-RA-001 Variation Management · SOP-SQ-001 Supplier Qualification · SOP-AUD-001 Internal Audit · SOP-AUD-002 Regulatory Inspection Management

Each follows the same table-driven procedure pattern. It maps every step to its system transaction and
resulting record, so an inspector can follow the SOP straight into the system.

## 13.3 SOP authoring rules (enforced by template and rules)

1. Each step names a role, not a person.
2. Each GxP decision step names its signature meaning.
3. Timelines are expressed as configuration-backed values (`{…}`). The SOP and the workflow SLA must agree.
   The document type rule warns when an SOP timeline differs from the linked workflow's `sla_definition`.
4. Regulatory references give the document name. A clause number may be added only after it is verified
   against the current text.
5. "Where-used" links (recipe phases, courses, specifications) must be updated in the same change control.
