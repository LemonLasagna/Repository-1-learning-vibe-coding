# 16. Disaster Recovery Plan (self-hosted)

## 16.1 Objectives

| Tier | Services | RPO | RTO | Notes |
|---|---|---|---|---|
| T0 | Keycloak, gateway, esignature, mes, warehouse (shop-floor path) | ≤ 5 min (0 within the primary DC with a synchronous standby) | 2 h | |
| T1 | All other GxP services, PostgreSQL, object storage | ≤ 5 min | 4 h | Tier S installs: RPO ≤ 5 min, RTO 8 h (manual standby promotion) |
| T2 | OpenSearch, Superset, observability | Rebuildable | 24 h | Derived data |

**Plant continuity.** If the data centre or the WAN link is unavailable, the edge-connector buffers equipment
data for at least 72 h. Human GxP data capture switches to the **approved paper fallback**: contingency batch
record forms, regenerated nightly for each active batch and printed at the plant. Paper data is reconciled into
the system afterwards as late entries with exceptions, governed by SOP-IT-001.

## 16.2 Failure scenarios

| Scenario | Detection | Response |
|---|---|---|
| Container or node failure | Kubernetes health checks / compose restart policy | Automatic restart or reschedule; PDBs keep quorum |
| Disk failure (DB server) | SMART + RAID alerts, Prometheus | Hot-swap; PostgreSQL keeps running on RAID-1. If the array fails, promote the standby |
| PostgreSQL primary server loss | CNPG/Patroni health checks | Automatic failover to the **synchronous standby in the same DC** (no data loss) |
| Logical corruption / bad migration / operator error | Integrity checks, app errors, `audit.verify_seals()` failure | PITR to just before the event into a parallel instance → verify → switch |
| Ransomware | Anomaly alerts, mass-encryption indicators, Ceph Object Lock violations | Isolate the network; restore from the **offline tape or immutable copy** that ransomware could not reach; rebuild hosts from known-good images; rotate all credentials; keep a forensic copy |
| Primary data centre loss (fire, flood, power) | Multiple independent signals | Site failover to the secondary DC (16.3) |
| WAN loss to one plant | Edge-connector and probe alerts | Plant runs the paper fallback; edge buffers machine data; no DC failover needed |
| Keycloak loss | Login failures | Restart; restore from the DB replica; realm config is held in Git |
| OpenSearch loss | Search errors | Rebuild indices from PostgreSQL + object storage (T2) |
| OpenBao loss | Encryption/sign failures | Restore from a Raft snapshot; unseal with the Shamir key custodians |

## 16.3 Site failover runbook (tier L; tier S/M use the same steps where they apply)

1. **Declare.** The incident commander declares a disaster. Criteria: primary DC unavailable for more than
   30 min, or projected to exceed the RTO. Notify QA and the system owner (as the SOP requires).
2. **Fence the primary.** Power off, or network-isolate, the primary DB servers if they are reachable, so two
   primaries can never accept writes (split-brain).
3. **Promote the database** in the secondary DC (`kubectl cnpg promote` or `patronictl failover`). Record the
   last applied WAL position and its timestamp. This is the actual data-loss window.
4. **Object storage.** Ceph RGW multisite: promote the secondary zone to master and check the sync status. For
   tier S, restore the SeaweedFS volume from the latest restic snapshot.
5. **Start services** in the secondary cluster with the same image digests from the local Harbor replica
   (Argo CD sync of the `dr` overlay).
6. **Switch the DNS records** in internal DNS (low TTL) to the secondary VIP. Plants' edge-connectors fail over to the
   secondary endpoint in their configuration.
7. **Verify** with scripted smoke tests and GxP checks: login, e-signature, record open/save,
   `audit.verify_seals()`, record counts against the last replicated metrics, and an MES step confirm.
8. **Communicate** the RPO actually achieved. Process owners decide whether transactions in the loss window must
   be re-entered. Each re-entry is a late entry with a reason referencing the incident.
9. **Raise a deviation** if GxP data loss or unavailability affected product decisions.
10. **Failback** to the rebuilt primary DC is planned and executed as a change, never under emergency.

## 16.4 Backup specification (3-2-1-1-0 rule: 3 copies, 2 media, 1 off-site, 1 immutable/offline, 0 verify errors)

| Data | Method | Frequency | Retention | Location |
|---|---|---|---|---|
| PostgreSQL | WAL archive + base backup (CNPG Barman to Ceph RGW, or pgBackRest on VMs) | Continuous + daily | PITR 35 d | Local object store (copy 1) |
| PostgreSQL | Streaming replica | Continuous | — | Secondary DC (copy 2) |
| PostgreSQL + documents | restic snapshot of the backup repository | Daily | Monthly snapshots for 7 y (or per retention policy) | LTO **WORM** tape, rotated off-site weekly, or an immutable backup appliance (copy 3, offline) |
| Documents (object storage) | Ceph multisite replication + versioning + Object Lock | Continuous | Per retention class | Secondary DC |
| Keycloak | DB backup + realm export (no secrets) to Git | Daily | 90 d | Local + tape |
| OpenBao | Raft snapshot (encrypted) | Daily | 30 d | Local + tape. Unseal keys held separately by custodians |
| Config packages, release bundles | Stored in DB + Harbor | On creation | Life of system | Harbor replicated to the secondary DC |
| OpenSearch | Snapshot to object storage | Daily | 14 d | Convenience only |

Backups are encrypted with restic's repository encryption. The repository password is held in OpenBao, with a
sealed paper copy in a safe. Tapes are labelled and logged in an equipment logbook (`dms.logbook`).

## 16.5 Testing

| Test | Frequency | Evidence |
|---|---|---|
| Restore PostgreSQL to an isolated environment + `verify_seals()` + count reconciliation | Monthly (automated) | Report in `val.automated_test_evidence` |
| Document restore (random sample, SHA-256 check against `file.stored_object.sha256`) | Monthly | Same |
| Tape restore (full restic restore from an off-site tape) | Quarterly | Restore report, signed |
| In-DC DB failover (switch to the synchronous standby) | Quarterly in VAL, semi-annually in a PROD maintenance window | Drill report |
| Full site failover | Annually | Drill report, RPO/RTO achieved, lessons learned (PQ-BCP-002) |
| Plant paper-fallback drill | Annually per plant | Customer record |
