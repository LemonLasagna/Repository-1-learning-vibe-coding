# 8. User Requirement Specification (URS)

| Doc ID | URS-PHOS-001 | Version | 0.1 DRAFT |
|---|---|---|---|
| System | PharmaOS platform | GAMP 5 category | Configurable product (Cat. 4) with custom extensions (Cat. 5) where noted |

**Priority**: M = Mandatory (GxP or contractual), H = High, D = Desirable.
**GxP**: Y if the requirement affects product quality, patient safety, or data integrity.
**Risk**: H/M/L initial risk from the requirement-level assessment (doc 11 §11.6). It drives test rigour.

## 8.1 General (GEN)

| ID | Requirement | Pri | GxP | Risk |
|---|---|---|---|---|
| URS-GEN-001 | The system shall support the organisation hierarchy enterprise → region → country → legal entity → site → plant → building → area → room, plus business units and departments | M | N | L |
| URS-GEN-002 | The system shall support multiple languages for UI and configurable content | H | N | L |
| URS-GEN-003 | The system shall display date/time in the site time zone with the UTC offset, using a server-controlled time source | M | Y | H |
| URS-GEN-004 | The system shall support the six segments (pharma, biopharma, vaccine, device, cosmetics, nutraceutical) through configuration, without code branches | H | N | M |
| URS-GEN-006 | All data (database, documents, backups, keys) shall be stored on customer-controlled infrastructure; the system shall operate without internet access | M | Y | H |
| URS-GEN-007 | All runtime components shall be available under OSI-approved open-source licences | H | N | L |
| URS-GEN-005 | The system shall meet availability ≥ 99.9% monthly for production | H | Y | M |

## 8.2 Configuration (CFG)

| ID | Requirement | Pri | GxP | Risk |
|---|---|---|---|---|
| URS-CFG-001 | Administrators shall configure forms, fields, layouts, and per-state field access without coding | M | Y | M |
| URS-CFG-002 | Administrators shall configure lookup lists, numbering schemes, menus, list views, and email templates | M | N | L |
| URS-CFG-003 | Administrators shall configure business rules (validation, default, visibility, requiredness, derivation, routing) evaluated identically on client and server | M | Y | H |
| URS-CFG-004 | Administrators shall configure workflows, approval routes, escalations, and notifications | M | Y | H |
| URS-CFG-005 | Administrators shall configure dashboards and reports | H | N | L |
| URS-CFG-006 | All configuration artefacts shall be versioned with full history and comparison | M | Y | H |
| URS-CFG-007 | Configuration shall be promoted between environments only as approved, hash-verified packages; direct edits in production shall be prevented | M | Y | H |
| URS-CFG-008 | Administrators shall be able to define custom entities (no code) that inherit audit trail, workflow, and e-signature capabilities | H | Y | M |

## 8.3 Workflow (WF)

| ID | Requirement | Pri | GxP | Risk |
|---|---|---|---|---|
| URS-WF-001 | Support sequential, parallel (all/any/quorum), and conditional approvals | M | Y | H |
| URS-WF-002 | Support rejection loops back to any prior configured step, with loop counters | M | Y | M |
| URS-WF-003 | Support time-based escalation with multiple levels and business calendars | M | N | M |
| URS-WF-004 | Support delegation (planned out-of-office and per-task) with audit of on-behalf-of | M | Y | H |
| URS-WF-005 | Workflow definitions in use by running instances shall not change; instances complete on the version they started on | M | Y | H |
| URS-WF-006 | Provide a simulation mode for configured workflows | H | N | L |

## 8.4 Security (SEC)

| ID | Requirement | Pri | GxP | Risk |
|---|---|---|---|---|
| URS-SEC-001 | Authenticate via SSO (OIDC/SAML) federated to the corporate directory (on-prem Active Directory / LDAP; cloud IdPs optional) | M | Y | H |
| URS-SEC-002 | Enforce MFA for privileged users and configurable for all users | M | Y | H |
| URS-SEC-003 | Role-based access with site and org-node scope | M | Y | H |
| URS-SEC-004 | Attribute-based policies (site, department, confidentiality, record owner, record state) | M | Y | H |
| URS-SEC-005 | Segregation-of-duties rules that block conflicting role assignments or actions | M | Y | H |
| URS-SEC-006 | Time-bound role assignments and periodic access review campaigns | M | Y | M |
| URS-SEC-007 | Session inactivity timeout (configurable) and concurrent session control | M | Y | M |
| URS-SEC-008 | Account lockout after configurable failed attempts with security alerting | M | Y | H |
| URS-SEC-009 | Encryption in transit (TLS 1.2+, prefer 1.3) and at rest (AES-256), keys in a self-hosted key manager (optionally HSM-backed) | M | Y | H |
| URS-SEC-010 | Electronic signatures shall show signer's printed name, date/time, and meaning on the record and in human-readable output | M | Y | H |
| URS-SEC-011 | Electronic signatures shall be linked to their record so they cannot be excised, copied, or transferred | M | Y | H |
| URS-SEC-012 | Signature policy per action: meaning, re-authentication components, reason required, second-person requirement | M | Y | H |
| URS-SEC-013 | Prevent self-approval where policy requires independence | M | Y | H |
| URS-SEC-014 | Signatures shall require two distinct identification components; continuous-session rules shall be configurable | M | Y | H |
| URS-SEC-015 | User IDs shall be unique and never reused or reassigned | M | Y | H |

## 8.5 Data Integrity (DI)

| ID | Requirement | Pri | GxP | Risk |
|---|---|---|---|---|
| URS-DI-001 | Every create, modify, and delete-attempt on GxP data shall be recorded in a secure, computer-generated, time-stamped audit trail with user, old value, new value, and reason | M | Y | H |
| URS-DI-002 | Audit trail entries shall not be modifiable or deletable by any user including administrators | M | Y | H |
| URS-DI-003 | GxP records shall not be physically deleted | M | Y | H |
| URS-DI-004 | Reason for change shall be configurable as mandatory per entity/field | M | Y | H |
| URS-DI-005 | Audit trails shall be reviewable and exportable in human-readable form | M | Y | H |
| URS-DI-006 | Periodic audit-trail review shall be supported with recorded evidence | M | Y | M |
| URS-DI-007 | Tampering below the application layer shall be detectable | H | Y | H |
| URS-DI-008 | Accurate and complete copies of records shall be producible in human-readable and electronic form for inspection | M | Y | H |
| URS-DI-009 | Concurrent edits shall not silently overwrite each other | M | Y | M |
| URS-DI-010 | Records shall be retained per configurable retention policies with legal hold | M | Y | H |
| URS-DI-011 | Data shall be backed up and restorable; restore shall be periodically verified | M | Y | H |

## 8.6 Document Management (DMS)

| ID | Requirement | Pri | GxP | Risk |
|---|---|---|---|---|
| URS-DMS-001 | Manage policies, SOPs, work instructions, forms, specifications, protocols, reports, and other configurable types | M | Y | M |
| URS-DMS-002 | Major/minor versioning, check-in/check-out, and lifecycle (Draft → In Review → Approved → Effective → Superseded → Obsolete) | M | Y | H |
| URS-DMS-003 | Document numbering per configurable scheme | M | N | L |
| URS-DMS-004 | Configurable review and approval routes with comment resolution | M | Y | H |
| URS-DMS-005 | PDF rendition with signature manifestation page and controlled-status watermark | M | Y | H |
| URS-DMS-006 | Effective date control, including training-gated effectiveness | M | Y | H |
| URS-DMS-007 | Periodic review scheduling and reminders | M | Y | M |
| URS-DMS-008 | Only the effective version shall be available for use in operations; superseded versions shall be marked | M | Y | H |
| URS-DMS-009 | Controlled printing with copy number, watermark, and reconciliation | H | Y | M |
| URS-DMS-010 | Electronic logbooks and executed forms with audit trail | H | Y | M |

## 8.7 Quality Management (QMS)

| ID | Requirement | Pri | GxP | Risk |
|---|---|---|---|---|
| URS-QMS-001 | Quality event intake by any user, including mobile | M | Y | M |
| URS-QMS-002 | Triage into deviation, complaint, OOS, or other process with risk-based classification | M | Y | H |
| URS-QMS-003 | Deviation management with impact assessment on batches, materials, and equipment | M | Y | H |
| URS-QMS-004 | Due dates and controlled extensions with justification and approval | M | Y | M |
| URS-QMS-005 | Root cause analysis tools: 5-Why, Fishbone (6M), Is/Is-Not | M | Y | M |
| URS-QMS-006 | CAPA with corrective and preventive actions, owners, and due dates | M | Y | H |
| URS-QMS-007 | CAPA linkage to source records (deviation, complaint, audit, OOS, management review) | M | Y | M |
| URS-QMS-008 | CAPA effectiveness checks | M | Y | H |
| URS-QMS-009 | Change control with classification and temporary changes | M | Y | H |
| URS-QMS-010 | Cross-functional impact assessment routing | M | Y | H |
| URS-QMS-011 | Change implementation tasks and coordinated effectiveness | M | Y | H |
| URS-QMS-012 | Customer complaint management with sample handling and communications | M | Y | H |
| URS-QMS-013 | Complaint reportability assessment and regulatory due-date tracking; hand-off to pharmacovigilance/vigilance system | M | Y | H |
| URS-QMS-014 | OOS investigations (Phase I lab, Phase II full-scale) | M | Y | H |
| URS-QMS-015 | Retest/resample control requiring documented justification | M | Y | H |
| URS-QMS-016 | OOT detection rules (statistical) and investigation | H | Y | M |
| URS-QMS-017 | Recall management with genealogy-based scoping | M | Y | H |
| URS-QMS-018 | Management review with inputs, metrics, and actions | M | Y | M |
| URS-QMS-019 | Quality metrics definition and calculation per site/period | H | N | L |
| URS-QMS-020 | Product quality review (PQR/APR) compilation | M | Y | M |

## 8.8 Risk (QRM)

| ID | Requirement | Pri | GxP | Risk |
|---|---|---|---|---|
| URS-QRM-001 | Configurable risk matrices (severity, probability, detectability, acceptance bands) | M | Y | M |
| URS-QRM-002 | FMEA worksheets with RPN before and after mitigation | M | Y | M |
| URS-QRM-003 | Risk registers with periodic review | H | Y | M |
| URS-QRM-004 | Risk controls linkable to CAPA actions and verification | H | Y | M |

## 8.9 Supplier Quality (SQM), Audit (AUD)

| ID | Requirement | Pri | GxP | Risk |
|---|---|---|---|---|
| URS-SQM-001 | Supplier qualification with questionnaires, audits, and risk level | M | Y | H |
| URS-SQM-002 | Approved supplier list per material and manufacturer site, enforced in purchasing and receipt | M | Y | H |
| URS-SQM-003 | Quality agreements with expiry tracking | M | Y | M |
| URS-SQM-004 | SCAR and supplier change notifications | M | Y | M |
| URS-SQM-005 | Supplier scorecards | H | N | L |
| URS-AUD-001 | Audit programme planning (internal, supplier, external) | M | Y | M |
| URS-AUD-002 | Checklists, findings with classification, CAPA linkage | M | Y | M |
| URS-AUD-003 | Regulatory inspection management: request log, document provision, observations, commitments | H | Y | M |

## 8.10 Training (TRN)

| ID | Requirement | Pri | GxP | Risk |
|---|---|---|---|---|
| URS-TRN-001 | Job-role-based curricula with automatic assignment | M | Y | M |
| URS-TRN-002 | Read-and-understand, instructor-led, OJT, e-learning, and assessments | M | Y | M |
| URS-TRN-003 | Training records with e-signatures (trainee and trainer where applicable) | M | Y | H |
| URS-TRN-004 | Automatic retraining on new document versions | M | Y | H |
| URS-TRN-005 | Qualification check API used by other modules to block untrained users | M | Y | H |
| URS-TRN-006 | Training matrix and compliance reporting | M | N | L |

## 8.11 Validation (VAL)

| ID | Requirement | Pri | GxP | Risk |
|---|---|---|---|---|
| URS-VAL-001 | Computerised system inventory with GxP and data-criticality classification | M | Y | M |
| URS-VAL-002 | Requirement management (URS, FS, DS, CS) with risk | M | Y | M |
| URS-VAL-003 | Live traceability matrix with gap detection | M | Y | M |
| URS-VAL-004 | Online protocol execution (IQ/OQ/PQ/UAT) with evidence, incidents, and signatures | M | Y | H |
| URS-VAL-005 | Validation summary reports | M | Y | M |
| URS-VAL-006 | Periodic review of validated systems | M | Y | M |
| URS-VAL-007 | Platform release packages with automated test evidence | H | Y | M |

## 8.12 Manufacturing (MES)

| ID | Requirement | Pri | GxP | Risk |
|---|---|---|---|---|
| URS-MES-001 | Master recipe authoring following ISA-88 procedural model with versioning and approval | M | Y | H |
| URS-MES-002 | Master formula with scaling and potency adjustment | M | Y | H |
| URS-MES-003 | Production orders pin the approved recipe version | M | Y | H |
| URS-MES-004 | Guided EBR execution with parameter capture (manual or automated) and limit checks | M | Y | H |
| URS-MES-005 | Critical step second-person verification | M | Y | H |
| URS-MES-006 | Dispensing with FEFO lot proposal and status/expiry interlock | M | Y | H |
| URS-MES-007 | Weighing integrated with qualified balances including daily check verification | M | Y | H |
| URS-MES-008 | Line clearance with checklist and verification | M | Y | H |
| URS-MES-009 | Interlocks: equipment status (clean, calibrated, qualified, hold time), operator training | M | Y | H |
| URS-MES-010 | In-process controls with limits | M | Y | H |
| URS-MES-011 | Yield and label reconciliation with limits | M | Y | H |
| URS-MES-012 | Exception recording and review-by-exception | M | Y | H |
| URS-MES-013 | Batch release with automated checks and QP/QA e-signature | M | Y | H |
| URS-MES-014 | Equipment use logbooks generated from execution | M | Y | M |
| URS-MES-015 | Generation of human-readable executed batch record (PDF/A) | M | Y | H |

## 8.13 Warehouse (WMS)

| ID | Requirement | Pri | GxP | Risk |
|---|---|---|---|---|
| URS-WMS-001 | Goods receipt with ASL verification, container count, and condition checks | M | Y | H |
| URS-WMS-002 | Lot status management (Quarantine, Released, Rejected, Restricted, Expired, On Hold) with e-signed status change | M | Y | H |
| URS-WMS-003 | Inventory for raw materials, packaging, intermediates, and finished goods by location and container | M | Y | M |
| URS-WMS-004 | Barcode (GS1-128, DataMatrix) and QR labels | M | Y | M |
| URS-WMS-005 | FEFO/FIFO picking with justified override | M | Y | H |
| URS-WMS-006 | Expiry and retest date control | M | Y | H |
| URS-WMS-007 | Lot and container tracking | M | Y | H |
| URS-WMS-008 | Full forward/backward genealogy | M | Y | H |
| URS-WMS-009 | Temperature-zone storage and excursion management | M | Y | H |
| URS-WMS-010 | Cycle counting (blind) and approved adjustments | M | Y | M |
| URS-WMS-011 | Periodic stock reconciliation | M | Y | M |
| URS-WMS-012 | Serialization data readiness (serial numbers, aggregation) for integration with serialization systems | H | Y | M |

## 8.14 Laboratory (LIMS)

| ID | Requirement | Pri | GxP | Risk |
|---|---|---|---|---|
| URS-LIMS-001 | Specifications versioned per material/product/market with limits | M | Y | H |
| URS-LIMS-002 | Sample creation from receipt, production, stability, and EM schedules | M | Y | M |
| URS-LIMS-003 | Sample chain of custody | M | Y | M |
| URS-LIMS-004 | Test assignment and worksheets | M | Y | M |
| URS-LIMS-005 | Result entry (manual or instrument) with calculations and limit evaluation | M | Y | H |
| URS-LIMS-006 | OOS/OOT flagging and investigation link | M | Y | H |
| URS-LIMS-007 | Reagents and reference standards with expiry | M | Y | M |
| URS-LIMS-008 | Stability studies, conditions, time points, pulls, and trending | M | Y | H |
| URS-LIMS-009 | Environmental monitoring sample points, schedules, and limits | M | Y | H |
| URS-LIMS-010 | EM excursion handling linked to deviations | M | Y | H |
| URS-LIMS-011 | Microbial identification records | H | Y | M |
| URS-LIMS-012 | Certificate of Analysis generation with e-signature | M | Y | H |
| URS-LIMS-013 | Instrument integration with raw-data reference and audit-trail review flag | H | Y | H |

## 8.15 Regulatory (RIM)

| ID | Requirement | Pri | GxP | Risk |
|---|---|---|---|---|
| URS-RIM-001 | Product registration per country with status and registered details | M | Y | M |
| URS-RIM-002 | Submission tracking including health-authority questions | M | N | M |
| URS-RIM-003 | Variation tracking linked to change control | M | Y | H |
| URS-RIM-004 | Registered-detail conformance checks at batch release | M | Y | H |
| URS-RIM-005 | Regulatory commitments tracking | M | Y | M |
| URS-RIM-006 | Artwork and label control with proof review and approval | M | Y | H |
| URS-RIM-007 | Renewal tracking | H | N | M |

## 8.16 ERP

| ID | Requirement | Pri | GxP | Risk |
|---|---|---|---|---|
| URS-ERP-001 | Purchase requisitions and orders with ASL enforcement | M | Y | H |
| URS-ERP-002 | MPS and MRP producing planned orders | H | N | L |
| URS-ERP-003 | Production orders with component reservation | M | Y | M |
| URS-ERP-004 | Sales orders, deliveries with lot allocation, returns (RMA) | H | Y | M |
| URS-ERP-005 | Finance postings handed off to corporate ERP/GL via integration | H | N | L |

## 8.17 Reporting, Notifications, Integration

| ID | Requirement | Pri | GxP | Risk |
|---|---|---|---|---|
| URS-RPT-001 | Role-based dashboards (executive, quality, manufacturing, warehouse, regulatory, training, supplier, risk) | M | N | L |
| URS-RPT-002 | GxP reports reproducible: same inputs + template version → same output; output hash recorded | M | Y | H |
| URS-NTF-001 | Configurable notifications via email and in-app with digests | M | N | L |
| URS-INT-001 | REST APIs (OpenAPI) for all entities with versioning and rate limits | M | N | M |
| URS-INT-002 | Event publication for integration (webhooks/messaging) | H | N | M |
| URS-INT-003 | Data migration tooling with reconciliation evidence | M | Y | H |
