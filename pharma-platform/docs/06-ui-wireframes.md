# 6. UI Wireframes

Low-fidelity wireframes that fix layout, information hierarchy, and GxP-critical affordances. Visual design is
done in the design system (tokens, components) and is out of scope here.

## 6.1 Application shell

```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ ☰  PharmaOS   [Site: Basel-01 ▾]   🔍 Search records, docs, lots…    🔔 3  ✓ 12  👤 J.Doe ▾│
├────────────┬─────────────────────────────────────────────────────────────────────────┤
│ My Work    │  Breadcrumb: Quality › Deviations › DEV-2026-00412                      │
│ Quality  ▸ │                                                                         │
│ Documents ▸│                     ( page content )                                    │
│ Training  ▸│                                                                         │
│ Manufact. ▸│                                                                         │
│ Laboratory▸│                                                                         │
│ Warehouse ▸│                                                                         │
│ Regulatory▸│                                                                         │
│ Validation▸│                                                                         │
│ Reports   ▸│                                                                         │
│ ───────────│                                                                         │
│ Config  ⚙  │  (visible only with CONFIG_* permissions; disabled in PROD for edits)   │
└────────────┴─────────────────────────────────────────────────────────────────────────┘
 Footer: env badge [PROD] · server time 2026-10-15 14:02 CEST (UTC+02:00) · v4.2.0 · cfg pkg QMS-BASE 3.1
```

The menu is rendered from `meta.menu_item` and filtered by permission. The environment badge is a different
colour per environment, which prevents someone executing validation in PROD by mistake.

## 6.2 My Work (task inbox)

```
┌ My Work ─────────────────────────────────────────────────────────────────────────────┐
│ [Assigned to me 12] [My groups 31] [Delegated 2] [Watching 7]        Filter ▾  Sort ▾ │
├───┬──────────────────┬────────────────────────────┬───────────┬──────────┬───────────┤
│ ! │ Record           │ Task                        │ Due       │ SLA      │ From      │
├───┼──────────────────┼────────────────────────────┼───────────┼──────────┼───────────┤
│ 🔴│ DEV-2026-00412   │ QA Approve Investigation    │ Today     │ ██████▒ 92%│ M.Keller │
│ 🟠│ CC-2026-00088    │ Regulatory Impact Assessment│ 17 Oct    │ ████▒▒▒ 61%│ System   │
│   │ SOP-QA-0012 v4.0 │ Read & Understand (training)│ 22 Oct    │ ██▒▒▒▒▒ 30%│ System   │
│   │ BATCH 26B0147    │ QA Batch Review (3 exceptions)│ 18 Oct  │ ███▒▒▒▒ 45%│ MES      │
└───┴──────────────────┴────────────────────────────┴───────────┴──────────┴───────────┘
 Bulk actions: [Claim] [Delegate…]   (no bulk e-signature: each GxP signature is per record)
```

## 6.3 Generic GxP record page (metadata-rendered; Deviation example)

```
┌ DEV-2026-00412 · Temperature excursion in Granulation Room G-104 ──────────────────────┐
│ Status: ● QA Investigation Review   Classification: MAJOR   Owner: M.Keller   Due: 15 Oct │
│ Workflow: Draft ✓ → Triage ✓ → Investigation ✓ → [QA Review] → Approved → CAPA → Closed │
├──────────────────────────────────────────────────────────────┬───────────────────────────┤
│ [Details] [Impact] [Investigation] [CAPA] [Related] [Files]  │ ACTIONS                   │
│                                                              │ [Approve ✍] [Reject ✍]    │
│ Section: Event                                               │ [Request info] [Delegate] │
│  Title*          [Temperature excursion in G-104        ]    │ ─────────────────────────  │
│  Occurred at*    [2026-10-02 03:14 CEST]  (UTC+02:00)        │ SIGNATURES                │
│  Discovered at*  [2026-10-02 06:40 CEST]                     │ ✍ Authored  M.Keller      │
│  Area            [Granulation ▾]   Room [G-104 ▾]            │   02 Oct 07:12 CEST       │
│  Product         [Amlodipine 5mg ▾]                          │ ✍ Reviewed  S.Roth        │
│  Batches         [26B0141 ×] [26B0142 ×] [+ add]             │   09 Oct 10:03 CEST       │
│                                                              │ ─────────────────────────  │
│ Section: Assessment          (read-only in this state)       │ RISK                      │
│  Immediate action   …                                        │ Sev 4 × Occ 2 × Det 3 =24 │
│  Product impact     …                                        │ Level: MEDIUM             │
│                                                              │ ─────────────────────────  │
│ Section: Site-specific (configured fields from Field Builder)│ ACTIVITY  [Audit trail ▸] │
│  Shift            [Night ▾]   Line stop? [Yes]               │ 09 Oct S.Roth changed…    │
└──────────────────────────────────────────────────────────────┴───────────────────────────┘
```

Rules shown: field editability per workflow state (`meta.form_state_override`), an e-signature icon on actions
that need a signature, and the full signature manifestation (name, date/time with offset, meaning) next to the
record.

## 6.4 Electronic signature dialog

```
┌ Electronic Signature ───────────────────────────────────────────┐
│ You are signing:  DEV-2026-00412  (version 7, hash 9f3a…c21e)    │
│ Meaning:          APPROVED — "I approve the investigation        │
│                   conclusion and product impact assessment."     │
│ Signer:           Jane Doe (jdoe)                                │
│                                                                  │
│ Reason / comment  [                                          ]   │
│ User ID           [ jdoe            ] (pre-filled, editable)     │
│ Password          [ ••••••••••      ]                            │
│ Second factor     [ FIDO2 key / TOTP ]  (if policy requires)     │
│                                                                  │
│ ⚠ This electronic signature is the legally binding equivalent   │
│   of your handwritten signature.                                 │
│                                   [Cancel]   [Sign]              │
└──────────────────────────────────────────────────────────────────┘
 3 failed attempts → account locked + security event (policy-configurable)
```

## 6.5 EBR execution (shop-floor PWA, tablet landscape)

```
┌ BATCH 26B0147 · Amlodipine 5mg Tabs · Recipe MR-0042 v6.0 ────── Room C-201 · Op: K.Bauer┐
│ UP: Granulation › OP: Wet Massing › PHASE 4 of 9: Add binder solution                    │
├──────────────────────────────┬────────────────────────────────────────────────────────────┤
│ STEPS                        │ INSTRUCTION                                                │
│ ✓ 1 Line clearance           │ Add binder solution over 3–5 min at impeller 150–180 rpm.  │
│ ✓ 2 Equipment check (HSG-02) │                                                            │
│ ✓ 3 Charge API (scan)        │ Impeller speed (rpm)   [ 165 ]  ✓ 150–180  ⟲ OPC UA live   │
│ ▶ 4 Add binder solution      │ Addition time (min)    [ 4.2 ]  ✓ 3–5                       │
│   5 Wet massing              │ Binder lot             [ scan ▣ ]  L-26-0399 ✓ Released    │
│   6 IPC: LOD                 │                                                            │
│   7 Discharge                │ ┌─ CPP ─────────────────────────────────────────────────┐ │
│   8 Yield                    │ │ Out-of-limit entry → mandatory comment + creates batch │ │
│   9 Verify & sign            │ │ exception; critical CPP → blocks until QA disposition  │ │
│                              │ └────────────────────────────────────────────────────────┘ │
│ Exceptions: 0                │   [Comment]  [Report issue]          [CONFIRM STEP ✍ ]     │
└──────────────────────────────┴────────────────────────────────────────────────────────────┘
 Equipment HSG-02: CLEAN ✓  CAL ✓ (due 2027-01) QUAL ✓  hold time 41h/72h   Training: ✓ SOP-MFG-0110 v3
```

## 6.6 Dispensing / weighing

```
┌ Dispense DSP-26-0901 · Batch 26B0147 · Booth D-03 · Balance BAL-17 (daily check ✓ 06:05)┐
│ Material            Required    Tolerance   Lot (FEFO)     Potency   Status            │
│ Amlodipine besylate 6.944 kg   ±0.5%       L-26-0211 ✓    99.1%     ▶ weighing        │
│ MCC PH102           42.000 kg  ±1.0%       L-26-0187 ✓    —         pending           │
├──────────────────────────────────────────────────────────────────────────────────────┤
│  TARE  0.412 kg     GROSS  7.358 kg     NET  6.946 kg   ███████████████████▌ 100.03%  │
│  [scan container ▣]  [Tare]  [Capture weight]   Verified by: [scan badge ▣] (2nd person)│
└──────────────────────────────────────────────────────────────────────────────────────┘
 Weight captured from balance interface only (manual entry disabled unless policy allows + reason)
```

## 6.7 Batch review by exception and release

```
┌ Batch 26B0147 · Review ─────────────────────────────────────────────────────────────────┐
│ Release checks                         Result   Evidence                                 │
│ ✓ All EBR steps complete               PASS     214/214                                   │
│ ⚠ Batch exceptions dispositioned       2 OPEN   EXC-3, EXC-7  [open]                      │
│ ✓ Deviations closed or impact-assessed PASS     DEV-2026-00412 (closed, no impact)        │
│ ✓ LIMS results approved, no open OOS   PASS     COA draft                                 │
│ ✓ Yield within limits                  PASS     98.7% (97–101)                            │
│ ✓ Label reconciliation                 PASS     variance 0                                │
│ ✓ Registered detail conformance (CH)   PASS     MA 65xxx: site, spec, shelf life          │
│ ✓ Change controls effective            PASS     CC-2026-00071                             │
│ ✓ Performers trained at execution time PASS     23 users                                  │
├─────────────────────────────────────────────────────────────────────────────────────────┤
│ Market: [Switzerland ▾]  Decision: (•) Release ( ) Reject ( ) Conditional (requires CC)  │
│                                                       [Certify & Release ✍ ] (disabled) │
└─────────────────────────────────────────────────────────────────────────────────────────┘
```

## 6.8 Builders (Config Studio)

### Form builder
```
┌ Form Builder · Deviation · DEV_DEFAULT v3 (DRAFT) ──────────────────── [Preview] [Validate]┐
│ FIELDS (palette)    │ CANVAS                                         │ PROPERTIES           │
│ ▸ Text              │ ┌ Section: Event ───────────────────────────┐   │ Field: shift         │
│ ▸ Number + UoM      │ │ [Title *        ] [Occurred at *  ]        │   │ Label: Shift         │
│ ▸ Date/time         │ │ [Area ▾] [Room ▾] [Shift ▾]  ← selected    │   │ Type: Lookup(SHIFT)  │
│ ▸ Lookup            │ └────────────────────────────────────────────┘   │ Required: rule ▾     │
│ ▸ Reference         │ ┌ Section: Assessment (visible if state≥TRIAGE)┐ │ Visible: always      │
│ ▸ Table (grid)      │ │ …                                           │ │ GxP critical: ☑      │
│ ▸ Signature block   │ └─────────────────────────────────────────────┘ │ Per-state access ▸   │
└─────────────────────┴───────────────────────────────────────────────┴──────────────────────┘
 [Save draft] [Add to config package ▾]      (Publish only through config package promotion)
```

### Workflow builder
```
┌ Workflow Builder · DEVIATION_STD v5 (DRAFT) ───────────────────────────── [Simulate ▶] ┐
│  (Start)→[Draft]→<Triage?>─minor→[Owner Investigation]→[QA Approve ✍]→(Closed)        │
│                      └─major/critical→[Investigation]═╦═[QA Review ✍]                  │
│                                                       ╚═[Prod Review ✍] (parallel, ALL) │
│                      ⏱ boundary timer 30d → escalate L1 (dept head) → L2 (site QA head) │
│  Selected: transition "QA Review → Approved"   requires e-sig: APPROVED, SoD: ≠ author  │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

### Dashboard builder
```
┌ Dashboard Builder · Quality Overview (site) ─────────────────────────────────────────────┐
│ Widgets: KPI tile · Bar · Line · Pareto · Heatmap · Table · Gauge · Control chart        │
│ ┌KPI: Open deviations┐┌KPI: Overdue CAPA┐┌Pareto: deviation root cause (90d)──────────┐  │
│ │  42  ▲6           ││  7   ▼2         ││ ██████ Human error ████ Equipment ██ Mat.   │  │
│ └───────────────────┘└─────────────────┘└─────────────────────────────────────────────┘  │
│ Data source: [qms.deviation_kpi_v ▾]  Filters bound to: site, period   Audience: QA Mgmt │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

## 6.9 Standard dashboards (shipped definitions)

| Dashboard | Primary widgets |
|---|---|
| Executive | Quality health index by site, right-first-time, release lead time, open critical events, inspection readiness, regulatory commitments on-time |
| Quality | Open/overdue deviations and CAPA, ageing, recurrence, root-cause Pareto, CAPA effectiveness pass rate, complaints per million units |
| Manufacturing | Batches in progress per line, review-by-exception ratio, exceptions per batch, yield control chart, equipment status board |
| Warehouse | Quarantine ageing, expiring stock (FEFO horizon), cycle-count accuracy, temperature excursions, label reconciliation variance |
| Regulatory | MA status by country, submissions pipeline, variations by category, commitments due, renewals calendar |
| Training | Compliance per role/site, overdue assignments, trained-before-effective %, assessment pass rate |
| Supplier | ASL coverage, scorecard tiers, SCAR ageing, certificate expiry, PPM by supplier |
| Risk | Risk heatmap (residual), top RPN items, overdue risk reviews, controls without verification |
