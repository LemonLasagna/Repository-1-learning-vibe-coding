# 7. User Stories

Format: **ID · As a … I want … so that …**, followed by acceptance criteria (Gherkin-style where a criterion is
GxP-critical). Each story traces to URS IDs (doc 08). Epics map to the roadmap releases (doc 20).

Each epic below lists representative stories that set the pattern and fix the hard rules. A full backlog
(about 600 stories) is decomposed from these during release planning.

---

## EPIC P — Platform & Configuration

**P-01 · As a business configurator I want to add a field to the Deviation form without code so that site-specific data is captured.**
- Given a DRAFT form definition in CONFIG, when I drag a Lookup field and bind it to list `SHIFT`, then the preview renders it and `meta.field_definition` gets a new version.
- The field cannot be published directly to PROD. It can only be added to a config package. *(URS-CFG-001, -006)*

**P-02 · As a configurator I want to define field visibility and requiredness rules so that forms adapt to classification.**
- Rule `classification = 'CRITICAL' → required(qa_owner_id)` is evaluated identically on client and server (same AST).
- A server-side violation returns 422 with field-level problem details. *(URS-CFG-003)*

**P-03 · As a configurator I want to model a workflow with parallel approval and escalation so that major deviations get QA and Production sign-off.**
- Parallel gateway with `ALL` completion; boundary timer at 30 days escalates to L1, and at 45 days to L2.
- Simulation mode runs a scenario without creating records and shows the path taken. *(URS-WF-001…005)*

**P-04 · As QA I want to approve a configuration package with an e-signature so that only validated configuration reaches PROD.**
- The package hash shown at approval equals the hash deployed in VAL and in PROD. A mismatch blocks deployment. *(URS-CFG-007)*

**P-05 · As an administrator I want to define a signature policy (meaning, re-auth method, second person) per action.** *(URS-SEC-012)*

**P-06 · As a user I want notifications in-app and by email with a daily digest option so that I am not flooded.** *(URS-NTF-001)*

## EPIC S — Security & Data Integrity

**S-01 · As a QA approver I want to sign electronically so that approval is legally binding and attributable.**
```gherkin
Scenario: First signature in a session requires both components
  Given I am logged in and have not signed in this session
  When I sign DEV-2026-00412 with meaning APPROVED
  Then I must enter user ID and password (and second factor if policy requires)
  And the signature shows my printed name, date/time with UTC offset, and meaning
  And the signature is bound to record id, version and SHA-256 of the canonical record
Scenario: Record changed after signature intent
  Given I opened the sign dialog for version 7
  And another user saved version 8
  When I submit the signature
  Then the signature is rejected with "record changed, review again"
```
*(URS-SEC-010…014)*

**S-02 · As a system I must reject GxP writes with no authenticated actor.** *(URS-DI-001; OQ-DI-001)*

**S-03 · As a QA auditor I want to review the audit trail of a record with field labels and reasons so that I can do a periodic audit-trail review.**
- The filter supports record, user, period, and operation. Export to PDF/A is signed and hashed. Each review is recorded in `audit.audit_trail_review` with an e-signature. *(URS-DI-005, -006)*

**S-04 · As a security officer I want role assignments to expire and be re-certified quarterly.** *(URS-SEC-006)*

**S-05 · As a system I must prevent a user from approving their own record when the policy requires a second person.** *(URS-SEC-013)*

## EPIC D — Document Management

**D-01 · As an author I want to check out an SOP, edit in Office, and check in a minor version.** *(URS-DMS-002)*

**D-02 · As a document owner I want parallel review then sequential approval, with comments resolved before approval.**
- Approval is blocked while any review comment is unresolved. *(URS-DMS-004)*

**D-03 · As QA I want an approved SOP to become effective only after 90% of affected users complete training, or on a set date, whichever the document type configures.** *(URS-DMS-006, URS-TRN-004)*

**D-04 · As an operator I want to print a controlled copy with a watermark, copy number, and print date so that paper copies are traceable.**
- Reconciliation of printed copies is tracked. Unreconciled copies appear on the owner's dashboard. *(URS-DMS-009)*

**D-05 · As a document owner I want periodic review reminders 60/30/7 days before due.** *(URS-DMS-007)*

## EPIC Q — Quality Processes

**Q-01 · As any employee I want to report a quality event in under 2 minutes from mobile.** *(URS-QMS-001)*

**Q-02 · As QA I want to triage an event into deviation, complaint, or no action, with a risk-based classification.** *(URS-QMS-002)*

**Q-03 · As an investigator I want 5-Why and Fishbone tools that create root-cause records linked to CAPA.** *(URS-QMS-005)*

**Q-04 · As a CAPA owner I want an effectiveness check scheduled automatically when all actions close.** *(URS-QMS-008)*

**Q-05 · As a change owner I want impact assessments auto-assigned by affected item type (equipment → Validation, document → DMS owner, registered detail → RA).** *(URS-QMS-010)*

**Q-06 · As complaint handling I want a reportability decision table with a computed regulatory due date.** *(URS-QMS-013)*

**Q-07 · As a QC lead I want OOS Phase I/II investigations with retest only after a documented hypothesis.**
- Retest creation is blocked until Phase I is concluded with `assignable_cause = false` and the retest is justified. *(URS-QMS-015)*

**Q-08 · As QA I want PQR sections auto-populated (batches, deviations, OOS, changes, stability, complaints) for the review period.** *(URS-QMS-020)*

## EPIC T — Training

**T-01 · As a manager I want a new hire's curricula assigned automatically from their job roles.** *(URS-TRN-001)*

**T-02 · As an operator I want to complete a read-and-understand with an e-signature and an optional assessment.** *(URS-TRN-002)*

**T-03 · As MES I need to block a step if the operator is not trained on the effective SOP version.** *(URS-TRN-005, URS-MES-009)*

## EPIC M — Manufacturing

**M-01 · As a process engineer I want to author a master recipe in the ISA-88 hierarchy with CPP limits and IPC definitions, then route it for approval.** *(URS-MES-001)*

**M-02 · As an operator I want guided step execution with live parameter capture from equipment.** *(URS-MES-004)*

**M-03 · As a dispensing operator I want FEFO-proposed lots, balance-integrated weighing, and second-person verification.**
```gherkin
Scenario: Non-released lot is scanned
  When I scan a container from a lot in status QUARANTINE
  Then the system blocks the dispense
  And records a batch exception attempt in the audit trail
Scenario: Weight outside tolerance
  When the net weight is 101.2% with tolerance ±0.5%
  Then capture is refused and I must adjust before re-capturing
```
*(URS-MES-006, -007)*

**M-04 · As production I want line clearance checklists with second-person verification before the first step.** *(URS-MES-008)*

**M-05 · As QA I want review-by-exception so that I only review deviations from the recipe.** *(URS-MES-012)*

**M-06 · As a QP I want an automated release checklist including registered-detail conformance per market.** *(URS-MES-013, URS-RIM-004)*

## EPIC W — Warehouse

**W-01 · As a receiver I want to receive against a PO with ASL verification and generate quarantine labels.** *(URS-WMS-001, -002)*

**W-02 · As a picker I want FEFO-proposed picks with an override that needs a reason.** *(URS-WMS-005)*

**W-03 · As QA I want forward and backward genealogy for a lot in under 5 seconds for recall scoping.** *(URS-WMS-008)*

**W-04 · As a warehouse lead I want blind cycle counts with recount thresholds and approved adjustments.** *(URS-WMS-010)*

## EPIC L — Laboratory

**L-01 · As QC I want samples generated automatically on goods receipt using the material's sampling plan.** *(URS-LIMS-002)*

**L-02 · As an analyst I want results evaluated against the effective specification version, with OOS/OOT flagged automatically.** *(URS-LIMS-005, -006)*

**L-03 · As a stability coordinator I want pull schedules generated from protocol conditions and time points.** *(URS-LIMS-008)*

**L-04 · As micro QC I want EM excursions above the action limit to auto-create a deviation.** *(URS-LIMS-010)*

**L-05 · As QA I want CoA generation from approved results with an e-signature.** *(URS-LIMS-012)*

## EPIC R — Regulatory

**R-01 · As RA I want a registration matrix (product × country × status).** *(URS-RIM-001)*

**R-02 · As RA I want a change control to create variation records per affected MA with category and deadline.** *(URS-RIM-003)*

**R-03 · As labeling I want CCDS changes to cascade to local label tasks with implementation deadlines.** *(URS-RIM-006)*

## EPIC V — Validation

**V-01 · As a validation engineer I want URS→FS→test trace links with a live RTM and coverage gaps highlighted.** *(URS-VAL-003)*

**V-02 · As a tester I want to execute protocols online with screenshot evidence and an e-signature per step.** *(URS-VAL-004)*

**V-03 · As QA I want a platform release package with automated regression evidence so that vendor releases are assessed quickly.** *(URS-VAL-007)*
