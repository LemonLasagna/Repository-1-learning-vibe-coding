# 14. Security Model

## 14.1 Principles

Zero trust between services · least privilege · defence in depth (gateway → service PEP → DB RLS → DB
triggers) · fail closed on GxP interlocks · every GxP action is attributable · no shared accounts for GxP
actions.

## 14.2 Identity and authentication

| Concern | Design |
|---|---|
| IdP broker | Keycloak (self-hosted), one realm per installation (or per tenant where an installation hosts several group companies) |
| Federation | LDAP/Kerberos user federation to on-prem Active Directory (default); OIDC/SAML to AD FS or an external IdP only if the customer chooses |
| MFA | TOTP, FIDO2/WebAuthn (preferred, phishing-resistant). Mandatory for admin, config, release (QP), and remote access |
| Shop floor | Badge (RFID) + PIN for identification on shared kiosks. **Badge + PIN alone is not accepted for e-signatures** unless the policy and the customer's risk assessment approve it as two distinct components *(customer decision; document the rationale)* |
| Tokens | Access token 5 min (JWT, audience per service), refresh token rotation, DPoP or mTLS-bound tokens for service clients |
| Sessions | Idle timeout configurable (default 15 min desktop, 5 min shop-floor), absolute timeout 10 h, concurrent session limit per policy |
| Service-to-service | mTLS (service mesh or cert-manager-issued certs) + client-credentials JWT with scoped audiences |
| Unique IDs | `iam.app_user.username` is unique per tenant and never reassigned. Deactivation only, no delete |

## 14.3 Authorization (RBAC + ABAC)

```
Effective permission(user, action, record) =
    RLS(tenant)
  ∧ ∃ role ∈ roles(user) : permission(action) ∈ role ∧ record.org_path ⊑ assignment.org_scope ∧ assignment.valid(now)
  ∧ ¬ ∃ ABAC deny policy matching (user attrs, record attrs, env attrs)
  ∧ ( no ABAC allow policies for resource ∨ ∃ ABAC allow matching )
  ∧ ¬ SoD conflict(user, action, record)
  ∧ (action ∉ gated ∨ trained(user, required curriculum @ effective version))
```

- **Permissions** are `resource:action`, for example `qms.deviation:read`, `qms.deviation:transition.approve`,
  `wms.lot:status.release`, `meta.form:edit`, and `audit.trail:export`.
- **Standard role set** (customisable): Viewer, Initiator, Owner, Reviewer, Approver (per module), QA Approver,
  Qualified Person, Operator, Supervisor, Analyst, Lab Reviewer, Warehouse Operator, RA Specialist, Validation
  Engineer, Trainer, Business Configurator, Security Administrator, System Administrator, Auditor (read-only + audit trail).
- **ABAC attributes**: user (site path, department, clearance, job roles, employment type); record (site path,
  confidentiality, owner, state, product); environment (env tier, network zone, time, MFA level `acr`).
- **SoD defaults** (non-removable for GxP roles):
  - System/Security Administrator ∩ any GxP approval permission
  - Business Configurator ∩ Config Package Approver
  - Author of record ∩ Approver on the same record (policy-driven)
  - Requestor of access ∩ approver of the same access
- **Admin without data access**: platform operators (vendor SRE) have no application roles. Database break-glass
  is JIT, dual-approved, session-recorded, and time-boxed. Every break-glass session is reviewed.

## 14.4 Data protection

| Layer | Control |
|---|---|
| In transit | TLS 1.3 preferred (1.2 minimum) at the edge; mTLS east-west; DB connections TLS with cert verification |
| At rest | LUKS2 full-disk encryption on all servers (TPM2/Tang unlock); object-store encryption with OpenBao-wrapped keys; restic-encrypted backups with a separate key hierarchy |
| Keys | OpenBao transit (optionally PKCS#11 HSM-backed); per-tenant data keys (envelope); Shamir-split unseal keys held by named custodians; rotation yearly or on demand; key-use audit |
| Field-level | Sensitive PII (complainant contact, employee health data) is encrypted at the application layer, with a blind index for search |
| Secrets | External Secrets Operator ← OpenBao (tier M/L); SOPS-encrypted env files (tier S); no plaintext secrets in images or Git |
| Object integrity | SHA-256 on every stored object, verified on read for GxP renditions; WORM object lock (compliance mode) on GxP buckets |
| Privacy | Data stays in the customer's own data centres (residency is the customer's choice of site); DPA; data subject requests are handled without breaking GxP retention (pseudonymise where law allows; document conflicts) |

## 14.5 21 CFR Part 11 control mapping

Sections cited are those of 21 CFR Part 11. The wording here is paraphrased: verify against the regulation text.

| Part 11 | Requirement (paraphrased) | Platform control | Evidence |
|---|---|---|---|
| §11.10(a) | Validation for accuracy, reliability, consistent intended performance, ability to discern invalid/altered records | VMP doc 11; seals + verify_seals | OQ-DI-009, VSR |
| §11.10(b) | Accurate and complete copies, human-readable and electronic | PDF/A record print + JSON/CSV export with audit trail | OQ-RPT |
| §11.10(c) | Protection of records for retention period | WORM storage, retention policies, backups, no-delete triggers | OQ-DI-005, PQ-BCP |
| §11.10(d) | Limiting system access to authorised individuals | SSO, MFA, RBAC/ABAC, RLS | OQ-SEC, OQ-DI-006 |
| §11.10(e) | Secure, computer-generated, time-stamped audit trails; changes don't obscure previous; retained; available | `audit.audit_trail` triggers, append-only, retained with record | OQ-DI-001…008 |
| §11.10(f) | Operational system checks to enforce sequencing | Workflow guards, MES step state machine | OQ-WF, OQ-MES |
| §11.10(g) | Authority checks | PEP/PDP, SoD, signature policies | OQ-SEC, OQ-ESIG-005 |
| §11.10(h) | Device checks (validity of input source) | Balance/equipment identity via edge-connector certificates; source tagging (MANUAL/OPCUA/SCALE) | OQ-MES |
| §11.10(i) | Persons have education, training, experience | Training module; training gate | OQ-TRN |
| §11.10(j) | Written policies holding individuals accountable for e-signature actions | SOP-SEC-002; acknowledgement record | OQ-ESIG-008 |
| §11.10(k) | Controls over systems documentation | DMS control of system documentation; change control | SOP-CSV-002 |
| §11.50 | Signature manifestation: printed name, date/time, meaning | Signature display and manifest | OQ-ESIG-001 |
| §11.70 | Signature/record linking | record_hash binding, immutable signatures | OQ-ESIG-004, OQ-DI-008 |
| §11.100 | Uniqueness; identity verification; certification to FDA | Unique user IDs; org certification record `esig.org_certification` | SOP-SEC-002 |
| §11.200 | Two distinct identification components; continuous-session rules; use only by genuine owner | Step-up re-auth, policy-driven components | OQ-ESIG-002/003 |
| §11.300 | ID/password controls: uniqueness, periodic checks, loss management, safeguards, testing of devices | IdP password policy, lockout, token revocation, FIDO2 lifecycle | OQ-SEC |

**EU GMP Annex 11 and PIC/S data-integrity expectations** (described by topic, without clause numbers) are
covered as follows: risk management throughout the life cycle → doc 11 §11.6; supplier assessment →
doc 11 §11.7; data accuracy checks for critical data entry → second-person verification and interface source
tagging; data storage, backup, and periodic restore checks → doc 16; printouts → §11.10(b) row; audit trails
with reason for change → URS-DI-004; change and configuration management → config packages; periodic
evaluation → periodic review; security → this document; incident management → SOP-IT-002; e-signatures →
§11.50–11.300 rows; batch release by an authorised person with e-signature → MES release; business continuity →
doc 16; archiving → SOP-RET-001.

## 14.6 ALCOA+ mapping

| Principle | Control |
|---|---|
| Attributable | Actor required at DB level; on-behalf-of for delegation; no shared accounts |
| Legible | Human-readable audit-trail rendering with field labels; PDF/A |
| Contemporaneous | Server timestamps; late-entry exceptions in MES; edge source timestamps preserved |
| Original | First capture retained (instrument raw data referenced by hash); corrections as new entries |
| Accurate | Interface capture, limit checks, second-person verification, validated calculations |
| Complete | No delete; audit trail of every change; invalidated results retained with reason |
| Consistent | Sequenced workflows; server clock; deterministic rendering |
| Enduring | WORM storage; retention policies; format migration plan (PDF/A, open data formats) |
| Available | Search, audit-trail export, inspection request handling within agreed times |

## 14.7 Threat model highlights (STRIDE)

| Threat | Example | Mitigation |
|---|---|---|
| Spoofing | Credential sharing on shop floor | Short idle timeouts, badge + PIN identity, e-sig re-auth, anomaly alerts (same user on two terminals) |
| Tampering | DBA edits a record directly | Triggers + RLS + no superuser in app path; seals detect trigger-disabled changes; break-glass recording |
| Repudiation | "I didn't approve that" | Step-up re-auth evidence (`acr`, `auth_time`), signature hash, IP/device in session log |
| Information disclosure | Cross-tenant leakage | RLS (tested), per-tenant search indices, per-tenant OpenBao keys |
| Denial of service | API flood | Gateway rate limits, WAF, per-tenant quotas, KEDA scaling |
| Elevation | Configurator grants self approval role | SoD on role assignment; access requests need an independent approver |
| Supply chain | Compromised dependency | SBOM, dependency pinning, signature verification (cosign), SCA scanning, provenance (SLSA level target 3) |
| Config injection | Malicious rule expression | Non-Turing-complete AST; size/depth limits; no script execution |

## 14.8 Security testing

SAST and SCA on every PR · DAST weekly against TEST · container image scanning (block on critical) · annual
third-party penetration test plus a test before each major release · tenant-isolation test suite on every
build (OQ-DI-006 extended to all tables) · secrets scanning pre-commit and in CI.
