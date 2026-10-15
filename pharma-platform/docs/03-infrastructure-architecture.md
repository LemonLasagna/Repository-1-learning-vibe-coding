# 3. Infrastructure Architecture

## 3.1 Deployment models

| Model | Who | Topology |
|---|---|---|
| **Multi-tenant SaaS** (default) | Most customers | Shared Kubernetes clusters per region; shared PostgreSQL with RLS; per-tenant MinIO buckets and search indices |
| **Dedicated SaaS** | Large or regulated-sensitive tenants | Dedicated namespace and dedicated PostgreSQL cluster; the same images |
| **Customer-hosted** | Restricted environments | Helm/Kustomize bundle; customer operates the infrastructure; the vendor supplies the validation package |

All three run the same container images and configuration packages. Only infrastructure overlays differ.

## 3.2 Environments (GxP landscape)

| Env | Purpose | Data | Change path |
|---|---|---|---|
| DEV | Engineering | Synthetic | CI on merge |
| TEST | Automated regression | Synthetic | CI nightly |
| CONFIG | Customer configuration authoring (Config Studio) | Synthetic + masked | Config packages exported from here |
| VAL | Customer validation/UAT (qualified) | Masked or representative | Release + config package under change control |
| PROD | Production (qualified) | Real GxP | Same packages as VAL, promoted unchanged (hash-verified) |
| DR | Warm standby in paired region | Replica | Failover runbook (doc 16) |

## 3.3 Regional cloud topology (per region)

```
Region (primary)                                          Region (DR pair)
┌──────────────────────────────────────────────────┐     ┌───────────────────────────┐
│ Edge: CDN + WAF + DDoS                           │     │ WAF (standby)              │
│ ┌──────────────── VPC ─────────────────────────┐ │     │ ┌────── VPC ─────────────┐ │
│ │ Public subnets: Ingress LB (L7, TLS 1.3)     │ │     │ │ k8s (scaled to min)    │ │
│ │ Private app subnets (3 AZ):                  │ │     │ │ PG replica (CNPG       │ │
│ │   k8s cluster: system / platform / services  │ │     │ │   replica cluster)     │ │
│ │   node pools (taints) + GPU-free             │ │     │ │ MinIO site-replication │ │
│ │ Private data subnets (3 AZ):                 │ │◄───►│ │ ES snapshot restore    │ │
│ │   PostgreSQL (CNPG, 1 primary + 2 sync/async)│ │     │ │ RabbitMQ (cold)        │ │
│ │   MinIO (erasure-coded, WORM buckets)        │ │     │ └────────────────────────┘ │
│ │   Elasticsearch (3 master, n data)           │ │     └───────────────────────────┘
│ │   RabbitMQ (3-node quorum queues)            │ │
│ │   Redis/Valkey (sentinel or cluster)         │ │
│ │ Mgmt subnet: bastion (JIT), CI runners       │ │
│ └──────────────────────────────────────────────┘ │
│ KMS / HSM │ Secrets (Vault or cloud SM)           │
│ Log archive (immutable object lock)               │
└──────────────────────────────────────────────────┘
          ▲  mTLS over site-to-site VPN / private link
┌─────────┴──────────────── Customer plant ────────────────────────────────┐
│ Level 4: corporate IT ──── Level 3.5 DMZ: edge-connector (k3s or VM)     │
│ Level 3: MES clients, label printers, balances ── Level 2: SCADA/OPC UA  │
└──────────────────────────────────────────────────────────────────────────┘
```

The plant connectivity follows ISA-95/IEC 62443 zone-and-conduit thinking. The cloud never initiates
connections into Level 2/3. The edge-connector dials out.

## 3.4 Kubernetes layout

| Node pool | Workloads | Notes |
|---|---|---|
| `system` | ingress, cert-manager, external-secrets, CNPG/RabbitMQ/ECK operators, OTel collector | Tainted |
| `platform` | Keycloak, gateway, workflow-engine, esignature, audit, config-studio, notification | Spread across AZs |
| `services` | Domain services | HPA on CPU + RPS |
| `workers` | rendering-worker, search-indexer, reporting jobs, MRP | KEDA on queue depth |
| `data` (optional) | Stateful sets if not using managed services | Local NVMe and anti-affinity |

Namespaces: `pharmaos-system`, `pharmaos-platform`, `pharmaos-services`, `pharmaos-data`, `pharmaos-observability`,
with default-deny NetworkPolicies (doc 19).

## 3.5 Component sizing (reference large tenant, doc 02 §2.9)

| Component | Sizing | Notes |
|---|---|---|
| PostgreSQL | Primary 32 vCPU / 256 GB / 4 TB NVMe; 2 replicas; PgBouncer | Audit trail ~2 M rows/day ≈ 1.5–3 GB/day before compression; monthly partitions; tiered detach to archive |
| Elasticsearch | 3 master (4 GB), 3 data (16 vCPU/64 GB/2 TB) | ILM: hot 90 d, warm, snapshots daily |
| RabbitMQ | 3 × 4 vCPU/16 GB | Quorum queues; per-context topic exchanges; DLQ per consumer |
| Redis/Valkey | 3 × 4 GB | Metadata cache, rate limit counters, SignalR backplane. **No GxP state** |
| MinIO | 4+ nodes erasure set, object lock (compliance mode) for GxP buckets | Versioning on, lifecycle per retention policy |
| Keycloak | 3 replicas + own PG database | Infinispan clustered sessions |
| Services | 2–6 replicas each, 0.5–2 vCPU | HPA |

## 3.6 Observability

- OpenTelemetry SDK in every service → OTel collector → metrics (Prometheus), traces (Tempo/Jaeger), logs (Loki/ELK).
- **Logs are not audit trails.** GxP evidence lives in `audit.*`. Logs exclude record contents and PII by default.
- SLOs per service, with burn-rate alerts. Synthetic probes for login, e-signature, and MES step confirm.
- Security monitoring: Keycloak events plus `iam.login_attempt` and `esig.signature_failure` go to the SIEM.

## 3.7 Backup posture (detail in doc 16)

- PostgreSQL: continuous WAL archiving to object storage (CNPG Barman), daily base backup, PITR 35 days, monthly
  long-term copies in a separate account with object lock.
- MinIO: site replication to the DR region and object lock.
- Elasticsearch: snapshots. It can be fully rebuilt from PostgreSQL plus MinIO (it is derived data).
- Keycloak: database backup plus realm export in Git (sans secrets).

## Licence review

Commercial SaaS distribution changes licence obligations. Verify each item with counsel before the
architecture is frozen. **All entries are (unverified)** and should be checked against the current licence text.

| Component | Concern to verify | Mitigation option |
|---|---|---|
| Elasticsearch | Licence moved from Apache 2.0 to Elastic License/SSPL, later with an AGPL option added | OpenSearch (Apache 2.0) is API-compatible for the features used here |
| Redis | Licence moved from BSD to source-available, with later changes | Valkey (BSD) is a drop-in replacement |
| MinIO | AGPLv3. Reported reductions in community-edition features | Commercial MinIO licence, or another S3-compatible store (e.g. Ceph RGW); the code uses only the S3 API |
| Power BI Embedded | Capacity-based pricing; data leaves the platform boundary | Keep it to non-GxP analytics; optional per tenant |
| Keycloak, RabbitMQ, PostgreSQL, .NET, Angular | Permissive/OSS licences generally compatible | Track via SBOM |
| PDF and form libraries (.NET) | Several popular PDF libraries are dual-licensed with revenue thresholds | Budget commercial licences; isolate behind `IPdfRenderer` |

The abstraction rule is that **application code depends only on protocol-level interfaces**: S3, AMQP 0.9.1,
the Redis protocol, and the Elasticsearch/OpenSearch REST subset behind `ISearchClient`. Swapping a component is
then a deployment change, not a rewrite.
