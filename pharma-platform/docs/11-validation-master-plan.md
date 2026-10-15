# 11. Validation Master Plan (VMP)

| Doc ID | VMP-PHOS-001 | Version | 0.1 DRAFT |
|---|---|---|---|

## 11.1 Purpose and scope

This plan defines how PharmaOS is validated across two layers:

1. **Vendor (supplier) validation.** The platform product, every release. It produces a reusable **validation
   package** that customers can leverage.
2. **Customer (regulated user) validation.** The customer's intended use: configuration, integrations, data
   migration, and procedures. Customers own their validation. This VMP gives them a template.

The approach follows the GAMP 5 (Second Edition) risk-based life cycle and the critical-thinking / assurance
principles in the FDA guidance on Computer Software Assurance *(unverified: confirm the current status and
title of the final guidance)*. It applies the electronic records/signatures requirements of 21 CFR Part 11 and
EU GMP Annex 11 *(a revised Annex 11 was released for consultation; confirm the applicable version)*. It also
follows the PIC/S guidance on data management and integrity.

## 11.2 System classification

| Component | GAMP category | Rationale |
|---|---|---|
| Platform core (OS, DB, runtime, infrastructure services) | 1 | Infrastructure, qualified, not validated per function |
| Platform application, out-of-the-box | 4 | Configurable product |
| Customer configuration (forms, rules, workflows, reports) | 4 (configured) | Tested at intended-use level |
| Customer-specific code extensions (integration mappings with transforms, custom report code) | 5 | Full life cycle |

## 11.3 Roles and responsibilities

| Role | Responsibility |
|---|---|
| Vendor Quality Unit | Approves the vendor validation package, release decisions, SDLC procedures |
| Vendor Engineering | SDLC execution, automated testing, traceability |
| Customer System Owner | Accountable for the system in operation (availability, access, support) |
| Customer Process Owner | Accountable for intended use and data |
| Customer QA | Approves the validation plan, protocols, and report; release for use |
| Customer Validation Lead | Plans and executes customer validation |
| SMEs | Requirements, risk assessment, test review |

## 11.4 Life cycle and deliverables

| Phase | Vendor deliverables | Customer deliverables |
|---|---|---|
| Planning | Quality plan, SDLC SOPs | Validation plan (VP) referencing this VMP; supplier assessment of the vendor |
| Specification | Product URS (doc 08), FS (doc 09), DS (doc 10) | Customer URS (intended use, delta to product URS), configuration specification (CS = config package manifest) |
| Risk assessment | Function-level risk assessment | Requirement-level risk assessment (§11.6) |
| Build | Code reviews, static analysis, SBOM, unit tests | Configuration in CONFIG env |
| Verification | Automated unit/integration/E2E regression; IQ/OQ in vendor reference env; DI test suite (OQ-DI-*) | IQ (customer env, if hosted by customer); OQ of configured functions (risk-scaled); PQ/UAT of business processes |
| Traceability | Product RTM generated from `val.trace_link` | Customer RTM (URS → CS/FS → test) |
| Reporting | Release validation summary, test evidence package | Validation summary report (VSR) and release for use |
| Operation | Release notes with impact categorisation, platform change control | Periodic review, change control, audit-trail review, backup/restore verification |

## 11.5 Release model and continuous validation

- **Release cadence**: quarterly feature releases and monthly patch releases. Hotfixes come under emergency
  change.
- Each release has a `val.platform_release_package`: release notes with an impact category per change
  (**none / low / medium / high** to validated functions), the automated regression result linked to commit
  SHAs (`val.automated_test_evidence`), and the vendor QA sign-off.
- Customers receive a release in VAL ≥ 4 weeks before PROD. A customer **regression subset** (the customer's
  high-risk OQ/PQ scripts) runs automatically against their config packages in VAL.
- High-impact changes are opt-in behind feature flags until the customer enables them under their own change
  control.

## 11.6 Risk assessment method

For each requirement (URS level):

| Factor | Scale |
|---|---|
| GxP impact (patient safety, product quality, data integrity) | High / Medium / Low / None |
| Probability of failure (novel, complex, custom vs standard, proven) | High / Medium / Low |
| Detectability (likelihood that a failure is detected before harm by downstream controls) | High / Medium / Low |

Risk priority → test strategy:

| Risk | Test approach |
|---|---|
| High | Scripted testing with positive, negative, and boundary cases; independent review; objective evidence for every step |
| Medium | Scripted or structured unscripted testing, with evidence of outcome |
| Low | Unscripted/exploratory or leveraged vendor testing, with summary record |
| None (non-GxP) | Vendor testing only |

The URS risk column (doc 08) is the initial assessment. It is refined per customer.

## 11.7 Leveraging vendor evidence

Customers may leverage vendor testing for unconfigured standard functions. They must:
1. Assess the vendor (audit or postal questionnaire plus a review of the SDLC evidence).
2. Confirm that the leveraged functions are used unconfigured, or that the configuration doesn't affect them.
3. Reference the vendor's test evidence in the customer RTM.

## 11.8 Data integrity controls in validation scope

Data-integrity controls are **always tested at High risk** in vendor OQ. They are re-executed in the customer IQ
for customer-hosted deployments:
- Audit trail capture, immutability, and tamper detection: OQ-DI-001…009 (executable: `database/tests/integrity_tests.sql`)
- E-signature controls: OQ-ESIG-*
- Access control, SoD, and tenant isolation: OQ-SEC-*
- Time synchronisation: IQ-INF-*
- Backup/restore: PQ-BCP-* (doc 16)

## 11.9 Acceptance criteria

- All High-risk test steps pass, or the deviation is resolved with documented rationale and QA approval.
- No open critical/major test incidents. Minor incidents have an approved resolution plan.
- RTM shows 100% coverage of Mandatory URS items.
- The VSR is approved by QA before release for use.

## 11.10 Deviation and incident handling during validation

Test incidents (`val.test_incident`) are categorised as: script error, environment, configuration defect,
product defect, or requirement ambiguity. Product defects are raised to the vendor. Retest scope is decided by
the validation lead and approved by QA.

## 11.11 Maintaining the validated state

- Change control for every configuration package, release upgrade, infrastructure change, and integration
  change.
- Periodic review (`val.system_periodic_review`) annually for High-criticality systems. It covers incidents,
  changes, audit-trail reviews, access reviews, backup verifications, and open CAPA.
- Annual data integrity assessment (`val.data_integrity_assessment`).
