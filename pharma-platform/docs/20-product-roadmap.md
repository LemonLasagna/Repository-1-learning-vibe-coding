# 20. Product Roadmap

## 20.1 Sequencing logic

1. **The platform core first.** Config, workflow, e-sig, audit, and IAM are needed by every module, and they are
   the hardest to change later.
2. **QMS + DMS + Training next.** They give the shortest path to a validatable, sellable product, they are the
   most commoditised market, and they become the vendor's own QMS (dogfooding).
3. **LIMS and WMS** before MES, because MES interlocks depend on lot status, specifications, and equipment.
4. **MES/EBR last.** It has the deepest domain, the highest validation risk, and needs plant integration.
   Commercial MES/EBR products embody many years of shop-floor refinement, so plan for pilots with design
   partners before general availability.

## 20.2 Releases

| Release | Window (indicative) | Scope | Exit criteria |
|---|---|---|---|
| **R0 Foundation** | Q1–Q2 | Org, IAM (Keycloak, RBAC/ABAC, SoD), audit trail + seals, e-signature, metadata runtime, form/field/rule builders, workflow engine v1, notifications, config packages, document storage, search, gateway, CI/CD. **Self-hosted delivery:** tier S compose installer, tier M RKE2 overlays, signed offline release bundles, OpenBao integration, backup/restore tooling | OQ-DI, OQ-ESIG, OQ-WF, OQ-CFG all automated and passing; penetration test |
| **R1 QMS Core** | Q3–Q4 | DMS (lifecycle, periodic review, controlled print), Training (curricula, R&U, assessments, training gate), Deviations, CAPA, Change Control, RCA tools, dashboards v1, GxP report rendering | First design-partner VAL go-live; vendor runs its own QMS on it |
| **R2 QMS Complete** | Y2 Q1–Q2 | Complaints (+ PV hand-off), OOS/OOT, Risk/FMEA, Supplier Quality, Audits & Inspections, Management Review, Quality Metrics, PQR, Validation module (CSV lifecycle, online execution, RTM) | 3 customers in PROD; release validation package process proven |
| **R3 Lab & Warehouse** | Y2 Q3–Q4 | Master data, LIMS (specs, samples, results, stability, EM, CoA, instrument file integration), WMS (receipt, status, FEFO, genealogy, labels, counts, reconciliation), Equipment (calibration, maintenance, cleaning), ERP P2P | Material release value stream (VS-1) live at a design partner |
| **R4 MES/EBR** | Y3 Q1–Q3 | Recipe designer, production orders, dispensing/weighing, EBR execution, line clearance, IPC, reconciliation, review-by-exception, batch release, edge-connector (OPC UA, balances, printers) | Pilot on a solid-dose line at a design partner; parallel run with paper before going paperless |
| **R5 Regulatory & ERP+** | Y3 Q3–Q4 | RIM (MA, registered details, submissions, variations, commitments, artwork, labeling), registered-detail release check, MPS/MRP, order-to-delivery, returns | Release conformance check live |
| **R6 Segment packs & scale** | Y4 | Biologics and vaccine packs (cell banks, campaigns, authority lot release), device pack (design controls, DHR, UDI), cosmetics and nutraceutical packs; serialization integration; IDMP; advanced analytics | Each pack validated against its segment URS |

## 20.3 Team shape (indicative, to be confirmed in planning)

| Stream | People (steady state) |
|---|---|
| Platform (config, workflow, e-sig, audit, IAM) | 8–10 |
| QMS/DMS/Training | 8 |
| LIMS | 6 |
| WMS/ERP | 6 |
| MES/EBR + edge | 8–10 |
| RIM | 4 |
| Frontend design system & renderer | 5 |
| QA/automation & validation | 6 |
| SRE/DevOps/Security | 5 |
| Domain SMEs (QA, QC, Mfg, RA), product management | 6 |
| **Total** | **≈ 65–70** |

## 20.4 Top programme risks

| Risk | Mitigation |
|---|---|
| Configurability turns into an unvalidatable "anything goes" | Config packages, segment packs as reference, guardrails (hard rules in doc 01 §1.8) |
| MES complexity is underestimated | Design partners from R2; ISA-88 discipline; a narrow first scope (solid dose, one site) |
| Shared-DB coupling slows teams | Strict schema ownership, expand/contract discipline, cross-schema FK inventory kept current |
| Customer IT lacks capacity to run a self-hosted GxP stack (patching, backups, DR drills) | Tier S kept to a minimal footprint; installer health checks; optional managed-service partner programme; operating SOPs shipped with the product |
| Licence changes in infrastructure components | Protocol-level abstractions; licence review gate per release (doc 03) |
| Regulatory guidance revisions (Annex 11 revision, CSA) | Regulatory intelligence feed → requirement updates → release planning |
| Customer validation cost blocks adoption | Vendor validation package, automated customer regression subset, risk-based leverage |

## 20.5 Open-source and self-hosting decisions

| Decision | Options | Note |
|---|---|---|
| Licence of the PharmaOS code itself | **Decided: AGPL-3.0** (recorded [date]). Apache 2.0 and source-available were considered | Consequences: a modified version offered to users over a network must offer those users its source (the UI needs a source link); dependency licences must be compatible with AGPL-3.0 (SBOM check, IQ-INF-014); a commercial licence for organisations that cannot accept AGPL is a later option, not yet decided |
| Paid offering if the code is open | **Decided: validation package and support/SLA** (segment packs and upgrades with regression evidence later). The validation evidence is usually what regulated customers pay for | A vendor-sold validation package needs independent review to be credible; see the customer statement |
| Single-tenant only vs keep multi-tenant capability | Keep it (as now) | Costs little and serves group companies and CMOs |
