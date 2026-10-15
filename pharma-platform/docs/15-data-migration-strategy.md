# 15. Data Migration Strategy

## 15.1 Principles

1. **Migration is a validated activity.** It has a plan, a risk assessment, scripts under version control,
   verification evidence, and a report. *(URS-INT-003)*
2. Migrate **what the business needs to operate and what regulators need to see**. Do not migrate everything.
3. Preserve meaning, not the source format. Legacy audit trails are migrated as **attached evidence**, not
   replayed as fake new audit rows. The new audit trail starts at migration with `actor = migration service
   account` and `reason = 'Migrated from {system} batch {n}'`.
4. Never re-sign. Legacy signatures become read-only **legacy signature records** (signer name, date, meaning,
   source system), clearly labelled as such.

## 15.2 Sources (typical)

| Source | Typical data | Approach |
|---|---|---|
| Legacy QMS (eQMS or paper + spreadsheets) | Open and closed deviations, CAPA, CC, complaints | Open: full migration into active workflow states. Closed: summary records + PDF of the legacy record attached |
| Legacy DMS / file shares | Effective, superseded, and obsolete documents with history | Effective + last superseded as full versions; older versions as archived renditions |
| LMS | Training history | Completions as historical `trn.training_assignment` (status COMPLETED, source LEGACY) + curricula rebuilt |
| ERP / WMS | Materials, suppliers, customers, open POs/SOs, on-hand inventory by lot/location/status | Master data first; on-hand as an opening-balance transaction at cut-over |
| LIMS | Specifications, methods, open samples, stability studies in progress | Specs/methods rebuilt and approved in the new system; in-progress studies migrated with history |
| Paper batch records | — | Not migrated. New batches start in EBR |
| RIM spreadsheets | MAs, registered details, commitments | Full migration with RA verification |

## 15.3 Process

```
Discover → Profile → Map → Cleanse (in source) → Build loaders → Dry-run 1..n → Verify → Mock cut-over → Cut-over → Hypercare
```

| Step | Output |
|---|---|
| Discover | Data inventory, owners, volumes, GxP criticality |
| Profile | Quality report (nulls, duplicates, invalid codes, orphans) |
| Map | Mapping specification per entity, version-controlled (`intg.data_mapping`) with transformation rules and default values. Business-owner approval ✍ |
| Cleanse | Corrections done **in the source** under source change control, where possible |
| Build | Loaders in `integration-hub` using the platform API (not direct SQL), so business rules, numbering, and audit apply. Bulk mode is allowed for opening balances with the same triggers active |
| Dry runs | ≥ 2 full dry runs into a VAL copy, with timings for the cut-over window |
| Verify | See 15.4 |
| Mock cut-over | Full rehearsal with timing, rollback test |
| Cut-over | Freeze source → final extract → load → verify → go/no-go ✍ |
| Hypercare | 4–6 weeks; daily reconciliation of open items |

## 15.4 Verification (evidence recorded in `intg.migration_batch`)

| Level | Method | Acceptance |
|---|---|---|
| Count | Source vs target counts per entity and status | 100% match or explained |
| Checksum | Hash of normalised key fields per record (`checksum_source` vs `checksum_target`) | 100% match for GxP-critical fields |
| Sample | Risk-based sample of records compared field-by-field by an independent verifier (e.g. √N + 1, minimum 30 per entity; 100% for High-criticality small sets such as effective SOPs and specifications) | 0 errors in critical fields |
| Functional | Execute a business transaction on migrated data (approve migrated CAPA, pick migrated lot) | Pass |
| Legacy evidence | Each closed legacy record has an attached rendition whose hash is recorded | 100% |

`intg.migration_record_map` stores the legacy ID → new ID mapping permanently, for inspection traceability.

## 15.5 Risks and mitigations

| Risk | Mitigation |
|---|---|
| Inventory mismatch at go-live | Physical count immediately before cut-over; opening balances approved by Finance and QA |
| Open workflows land in invalid states | State-mapping table approved per process; loader validates guards |
| Documents effective without training records | Training import before document effectiveness; the training gate is disabled for a grace period by a controlled, time-boxed config package |
| Lost legacy audit trails | Legacy audit exports attached as evidence; legacy system kept read-only for retention, or archived in a validated format |
| Cut-over overruns | Dry-run timing + 30% buffer; documented rollback (source unfrozen) |

## 15.6 Legacy system decommissioning

Decommissioning requires: a retention decision per data class, archival in a validated format (PDF/A + CSV/JSON
with a schema description), verified readability, retrieval tested, and QA approval of the decommissioning
report.
