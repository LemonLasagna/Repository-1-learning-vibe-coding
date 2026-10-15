# 16. Disaster Recovery Plan

## 16.1 Objectives

| Tier | Services | RPO | RTO |
|---|---|---|---|
| T0 | Keycloak, gateway, esignature, mes, warehouse (shop-floor path) | 5 min | 2 h |
| T1 | All other GxP services, PostgreSQL, MinIO | 5 min | 4 h |
| T2 | Search, reporting, analytics (derived) | Rebuildable | 24 h |

**Plant continuity.** If the cloud is unavailable, the edge-connector buffers equipment data (≥ 72 h). Human GxP
data capture switches to the **approved paper fallback**: pre-printed contingency batch record forms generated
nightly per active batch. Paper data is reconciled into the system afterwards as late entries with exceptions.
SOP-IT-001 governs this.

## 16.2 Failure scenarios

| Scenario | Detection | Response |
|---|---|---|
| Pod/node failure | k8s health checks | Automatic reschedule; PDBs keep quorum |
| AZ failure | Cloud health + synthetic probes | Multi-AZ spread; CNPG promotes a sync replica (automatic) |
| PostgreSQL corruption / bad migration | Integrity checks, app errors, seal verification failure | PITR to the point before the event into a parallel cluster → verify → switch |
| Ransomware / malicious deletion | Anomaly alerts, object-lock violations | Restore from the isolated backup account (object lock); rotate credentials; forensic copy retained |
| Region failure | Multi-signal (cloud status + probes) | Declared by the incident commander → regional failover (16.3) |
| Keycloak loss | Login failures | Fail over to the DR realm (database replica); realm config from Git |
| Search loss | Search errors | Rebuild indices from DB/MinIO (T2) |

## 16.3 Regional failover runbook (summary)

1. **Declare.** The incident commander declares a disaster (criteria: primary region unavailable > 30 min or
   projected > RTO). Notify customer QA contacts (contractual).
2. **Freeze.** Set the gateway in the primary (if reachable) to read-only to prevent split-brain.
3. **Promote the database.** CNPG replica cluster in the DR region → promote (`kubectl cnpg promote`). Record
   the last applied LSN and timestamp; this is the actual data loss window.
4. **Object storage.** MinIO site replication is already active. Verify replication lag at promotion time.
5. **Scale up** DR node pools; deploy the same image digests (GitOps sync of the `dr` overlay).
6. **DNS/CDN** switch to the DR ingress.
7. **Verify** (scripted smoke + GxP checks): login, e-signature, record open/save, `audit.verify_seals()`,
   record counts vs last replicated metrics, MES step confirm.
8. **Communicate** the RPO actually achieved. Customers assess whether transactions in the loss window must be
   re-entered. Each re-entry is a late entry with reason referencing the incident.
9. **Raise a deviation** in the customer QMS if GxP data loss or unavailability affected product decisions.
10. **Failback** is planned as a change, not executed under emergency.

## 16.4 Backup specification

| Data | Method | Frequency | Retention | Location |
|---|---|---|---|---|
| PostgreSQL | WAL archive + base backup (Barman via CNPG) | Continuous + daily | PITR 35 d; monthly 7 y (or per retention policy) | Object storage, cross-region, object-locked |
| MinIO | Site replication + versioning + object lock | Continuous | Per retention class | DR region |
| Keycloak | DB backup + realm export (no secrets) | Daily | 90 d | Object storage + Git |
| Configuration packages | Stored in DB and in the artefact registry | On creation | Life of system | Registry |
| Secrets | Vault snapshots | Daily | 30 d | Separate account |
| Elasticsearch | Snapshots (convenience only) | Daily | 14 d | Object storage |

## 16.5 Testing

| Test | Frequency | Evidence |
|---|---|---|
| Restore PG to an isolated env + `verify_seals()` + count reconciliation | Monthly (automated) | Report in `val.automated_test_evidence` |
| File restore from MinIO (random sample, hash check) | Monthly | Same |
| AZ failover (chaos test) | Quarterly in TEST, semi-annually in PROD window | Drill report |
| Full regional failover | Annually | Drill report, RPO/RTO achieved, lessons learned (PQ-BCP-002) |
| Paper fallback drill at a pilot plant | Annually | Customer record |
