# 19. Kubernetes Deployment

Target: customer-owned RKE2 clusters (tier M/L, doc 03 §3.3). Tier S uses `deploy/compose/` instead.
Manifests: `deploy/k8s/` (Kustomize). They parse as valid YAML. They were **not** rendered with `kustomize build`
or applied to a cluster in this session, because no cluster or kubectl was available.

```
deploy/k8s/
├── base/                 namespaces (Pod Security labels), default-deny + explicit NetworkPolicies
├── platform/             CloudNativePG cluster + ScheduledBackup + PgBouncer Pooler, RabbitmqCluster
├── services/quality/     reference service: SA, Deployment, Service, HPA, PDB, ExternalSecret
└── overlays/{val,prod}/  digest-pinned images, env labels, prod HPA floor
```

## 19.1 Cluster prerequisites (installed by the platform team, versions pinned per release)

| Component | Purpose |
|---|---|
| Ingress controller (NGINX or Envoy Gateway) + cert-manager | TLS termination, certificates |
| CloudNativePG operator | PostgreSQL HA, backups, PITR, replica clusters for DR |
| RabbitMQ Cluster Operator | Quorum-queue clusters |
| OpenSearch operator | Search |
| Rook-Ceph (RGW) or an external Ceph cluster | S3 object storage with Object Lock |
| MetalLB, Longhorn | Bare-metal load balancing and persistent volumes |
| Harbor | Local registry; air-gapped image source |
| Keycloak operator | IdP |
| External Secrets Operator | Secrets from OpenBao |
| Argo CD | GitOps delivery |
| KEDA | Queue-driven autoscaling for workers |
| OpenTelemetry collector, Prometheus, Loki, Tempo | Observability |
| Kyverno or Gatekeeper | Admission policies (below) |
| Sigstore policy-controller | Image signature verification |

## 19.2 Admission policies (enforced)

- Images only from the approved registry, **referenced by digest**, with a cosign signature from the release key.
- `runAsNonRoot`, read-only root filesystem, dropped capabilities, and the `RuntimeDefault` seccomp profile
  (Pod Security "restricted" on service namespaces).
- Resource requests are mandatory. `latest` tags are forbidden.
- In namespaces labelled `pharmaos.io/environment: prod`, `kubectl exec` is denied except through the
  break-glass group (audited).

## 19.3 Workload patterns

| Workload | Kind | Scaling | Disruption |
|---|---|---|---|
| Domain/platform services | Deployment | HPA (CPU; RPS via custom metrics) | PDB `minAvailable: 2`; zone spread |
| Workers (rendering, indexer, MRP) | Deployment | KEDA on RabbitMQ queue depth | PDB `maxUnavailable: 1` |
| Audit sealer | CronJob (*/5 min) | `concurrencyPolicy: Forbid` | — |
| DB migrations | Job (Argo CD PreSync hook) | single | Blocks sync on failure |
| Monthly audit partition creation | CronJob | single | — |
| PostgreSQL | CNPG Cluster | vertical; read replicas | Supervised switchover |

## 19.4 Configuration and secrets

- Non-secret configuration goes in ConfigMaps generated per overlay. Secrets come only through
  ExternalSecret.
- The DB credentials per service map to **distinct PostgreSQL roles** with write grants on that service's
  schemas only (doc 02 §2.1).

## 19.5 Release and rollback

1. Argo CD sync wave 0: migrations Job (expand-only in the same release).
2. Wave 1: platform services. Wave 2: domain services. Wave 3: workers.
3. Rolling update with `maxUnavailable: 0`. Readiness gates on the DB-migration version.
4. Rollback = revert the overlay PR to the previous digests. Expand-only migrations keep the old code
   compatible. Contract migrations ship one release later.

## 19.6 Plant edge

The `edge-connector` runs per site on k3s or a hardened VM in the Level 3.5 DMZ. It is deployed through Argo CD
pull (the agent dials out) with the same digest/signature policy. It uses a local SQLite store-and-forward
buffer (≥ 72 h) and mTLS to the gateway with a per-site certificate.
