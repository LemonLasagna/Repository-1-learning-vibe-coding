# 3. Infrastructure Architecture: self-hosted, open source

## 3.1 Principles

1. **All data stays on infrastructure the customer owns.** That covers the database, documents, search
   indices, backups, and keys, in the customer's own data centre or plant server room. Nothing requires a
   public-cloud account.
2. **Every runtime component is open source**, under an OSI-approved licence. No component needs a
   commercial licence to run in production.
3. **It must be able to run air-gapped.** Installs and upgrades come as signed offline bundles. No runtime call
   goes to the internet: no telemetry, no licence server, no CDN.
4. **Protocol-level abstraction.** Application code depends on S3, AMQP 0.9.1, the RESP (Redis) protocol,
   the OpenSearch REST API, OIDC, and PostgreSQL. Any compatible open-source implementation can be swapped in.

## 3.2 Open-source bill of materials

Licence names reflect the team's current understanding and are all **(unverified)**. Confirm each against the
licence text of the exact version you pin, and record it in the SBOM.

| Capability | Component (default) | Licence | Alternative | Replaces (previous design) |
|---|---|---|---|---|
| Relational database | **PostgreSQL 16** | PostgreSQL | — | — |
| PG HA and backup on Kubernetes | CloudNativePG | Apache 2.0 | Patroni (MIT) on VMs | — |
| PG backup (VM installs) | pgBackRest | MIT | Barman (GPL-3.0) | — |
| Connection pooling | PgBouncer | ISC | — | — |
| Search | **OpenSearch** | Apache 2.0 | PostgreSQL full-text only (small installs) | Elasticsearch |
| Cache / rate-limit / SignalR backplane | **Valkey** | BSD-3 | — | Redis |
| Messaging | RabbitMQ | MPL 2.0 | — | — |
| Object storage (multi-node) | **Ceph RGW** (S3 API, Object Lock) | LGPL 2.1/3 | Garage (AGPL-3.0) | MinIO |
| Object storage (single-server) | **SeaweedFS** (S3 API) | Apache 2.0 | Local filesystem adapter + app-level immutability | MinIO |
| Identity / SSO / MFA | Keycloak | Apache 2.0 | — | — |
| Secrets and key management | **OpenBao** (transit engine for envelope encryption and signing) | MPL 2.0 | Kubernetes secrets + SOPS (MPL 2.0) for small installs | Cloud KMS, HashiCorp Vault |
| Hardware key protection (optional) | Any PKCS#11 HSM via OpenBao; SoftHSM2 (BSD) for non-prod | — | — | Cloud HSM |
| Analytics / BI | **Apache Superset** on a PostgreSQL read replica | Apache 2.0 | Metabase (AGPL-3.0) | Power BI Embedded |
| Document rendering to PDF/A | **Gotenberg** (LibreOffice + Chromium) + veraPDF for PDF/A conformance checks | MIT / MPL 2.0 + GPL-3.0 | — | Commercial .NET PDF libraries |
| Online Office editing (optional) | Collabora Online (WOPI) | MPL 2.0 | ONLYOFFICE Docs (AGPL-3.0) | — |
| Container runtime / Kubernetes | **RKE2** (hardened, FIPS-capable) | Apache 2.0 | k3s (small/edge), upstream kubeadm | Managed Kubernetes |
| Single-server orchestration | Docker Engine / Podman + Compose | Apache 2.0 | — | — |
| Bare-metal load balancer | MetalLB | Apache 2.0 | kube-vip | Cloud load balancer |
| Ingress / TLS | ingress-nginx + cert-manager (internal CA via OpenBao PKI) | Apache 2.0 | Envoy Gateway | Cloud certificate services |
| Persistent volumes | Longhorn | Apache 2.0 | Rook-Ceph (Apache 2.0) | Cloud block storage |
| Container registry | **Harbor** (with Trivy scanning, cosign verification) | Apache 2.0 | — | Cloud registry |
| GitOps | Argo CD | Apache 2.0 | Flux (Apache 2.0) | — |
| Git hosting (customer side, optional) | Forgejo | GPL-3.0 (recent versions) | Gitea (MIT) | — |
| Metrics / logs / traces | Prometheus + OpenTelemetry Collector (Apache 2.0); Grafana, Loki, Tempo (AGPL-3.0) | mixed | OpenSearch for logs | Cloud monitoring |
| Backup to second site / offline media | restic (BSD-2) + LTO WORM tape or immutable backup appliance | — | Bacula (AGPL) | Cross-region object storage |
| Time | chrony against a local GPS/PTP stratum-1 or the domain controllers | GPL-2.0 | — | Cloud time service |
| Policy / image signing | Kyverno (Apache 2.0), Sigstore cosign (Apache 2.0) | Apache 2.0 | OPA Gatekeeper | — |

**The application itself** (.NET 8 services, Angular frontend) is MIT-licensed upstream tooling. The platform
code's own licence is a business decision recorded in doc 20.

## 3.3 Deployment tiers

The application images, schema, and config packages are the same for all three tiers. Only the installer
profile changes.

| Tier | Typical customer | Topology | Install method |
|---|---|---|---|
| **S: Single site, small** | ≤ 150 named users, 1 site (nutraceutical, cosmetics, small pharma, CMO) | 2 physical servers or VMs: **app server** (all services in containers) and **DB server** (PostgreSQL primary). The DB server streams to a **standby** on the app server's host or a third small box | `deploy/compose/` (Docker Compose / Podman) |
| **M: Site or campus** | ≤ 1 500 users, 1–3 sites on one LAN/MAN | 3-node RKE2 cluster (services), 3 PostgreSQL nodes (CNPG or Patroni on VMs), 3-node Ceph or Longhorn storage | `deploy/k8s/` overlays |
| **L: Multi-site enterprise** | Many plants, several countries | **Primary data centre** (tier M, scaled) + **secondary data centre** (warm standby) + per-plant **edge-connector**. Plants reach the DC over the corporate WAN/MPLS or site-to-site VPN | `deploy/k8s/` + DR overlay |

**One logical system per company.** Running a separate, unconnected installation per plant is possible but
not recommended. It splits CAPA trending, supplier qualification, document control, and recall genealogy across
databases, which defeats the purpose of a unified QMS.

**Multi-tenancy.** `tenant_id` and row-level security stay in the schema. On-premises this usually means one
tenant per installation. The same mechanism can separate group companies or a CMO's clients within one
installation, with a proven isolation test (OQ-DI-006).

## 3.4 Environments (GxP landscape, on-premises)

| Env | Hosting | Data | Notes |
|---|---|---|---|
| DEV / TEST | Vendor's own infrastructure | Synthetic | Vendor CI (GitHub Actions or self-hosted Forgejo runners) |
| CONFIG | Customer, small VM or a namespace on the VAL cluster | Synthetic + masked | Config packages are authored here |
| VAL | Customer, **same tier and topology as PROD** (it may be smaller) | Masked or representative | Release + config packages under change control |
| PROD | Customer data centre | Real GxP | Same signed bundles and packages as VAL (hash-verified) |
| DR | Secondary data centre or second server room | Replica | Doc 16 |

## 3.5 Reference topology: tier L

```
 Primary data centre (customer-owned)                    Secondary data centre / 2nd server room
┌───────────────────────────────────────────────────┐   ┌──────────────────────────────────────┐
│ Perimeter: corporate firewall + reverse proxy/WAF  │   │ Firewall + reverse proxy (standby)    │
│  (e.g. ModSecurity + OWASP CRS, Apache 2.0)        │   │                                       │
│ ┌──────── App VLAN ────────────────────────────┐  │   │ RKE2 cluster (scaled down)            │
│ │ RKE2 cluster: 3 control-plane + 4–8 workers   │  │   │ PostgreSQL standby (streaming,        │
│ │  MetalLB VIP │ ingress-nginx │ Argo CD        │  │   │   async) / CNPG replica cluster        │
│ │  pharmaos-platform / -services / -workers     │  │◄─►│ Ceph RGW multisite zone (async)       │
│ └───────────────────────────────────────────────┘  │   │ OpenSearch (restore from snapshot)    │
│ ┌──────── Data VLAN ────────────────────────────┐  │   │ Keycloak (DB replica)                 │
│ │ PostgreSQL: primary + sync standby (+ async)  │  │   └──────────────────────────────────────┘
│ │ Ceph cluster (RGW S3, Object Lock buckets)    │  │
│ │ OpenSearch 3 nodes │ RabbitMQ 3 nodes │ Valkey │  │   Offline backup
│ └───────────────────────────────────────────────┘  │   ┌──────────────────────────────────────┐
│ ┌──────── Mgmt VLAN ────────────────────────────┐  │   │ restic repository → LTO WORM tape,     │
│ │ Harbor │ OpenBao (3-node Raft) │ Prometheus/   │  │──►│ rotated off-site weekly (or an         │
│ │ Grafana/Loki │ bastion (MFA, session record)   │  │   │ immutable backup appliance)            │
│ └───────────────────────────────────────────────┘  │   └──────────────────────────────────────┘
│ Time: chrony ← GPS/PTP stratum-1 or domain controllers                                      │
└───────────────────────────────────────────────────┘
        ▲ mTLS over corporate WAN / site-to-site VPN (plant dials in; DC never dials into L2/L3)
┌───────┴──────────────────────────── Each plant ──────────────────────────────────────────────┐
│ L4 corporate LAN: browsers, shop-floor tablets (Wi-Fi), label printers, scanners              │
│ L3.5 DMZ: edge-connector (k3s or VM) with SQLite store-and-forward (≥ 72 h)                   │
│ L3/L2: SCADA, OPC UA servers, balances                                                        │
└───────────────────────────────────────────────────────────────────────────────────────────────┘
```

Plant connectivity follows ISA-95 levels and IEC 62443 zone-and-conduit thinking: the edge-connector dials out
to the data centre, and nothing inbound reaches L2/L3.

## 3.6 Reference hardware sizing

These are indicative. Validate them with a load test (PQ-PERF-001) on the customer's actual hardware.

| Tier | Role | Qty | CPU / RAM / Disk (each) |
|---|---|---|---|
| S | App server (all containers) | 1 | 16 cores / 64 GB / 1 TB NVMe |
| S | DB server (PostgreSQL primary + SeaweedFS volume) | 1 | 8 cores / 64 GB / 2 TB NVMe RAID-1 + data disks for documents |
| S | Standby / backup box (PG standby, restic repo) | 1 | 4 cores / 32 GB / 4 TB |
| M | RKE2 control plane | 3 | 4 cores / 16 GB / 200 GB |
| M | RKE2 workers | 3–4 | 16 cores / 64 GB / 500 GB |
| M | PostgreSQL | 3 | 16 cores / 128 GB / 2 TB NVMe |
| M | Ceph OSD/RGW (or Longhorn on workers) | 3 | 8 cores / 64 GB / 4 × 4 TB |
| L | Workers | 6–10 | 24–32 cores / 128 GB |
| L | PostgreSQL | 3 (+1 DR) | 32 cores / 256 GB / 4 TB NVMe |
| L | OpenSearch | 3 | 16 cores / 64 GB / 2 TB |
| L | Ceph | 4–6 | 16 cores / 64 GB / 8 × 8 TB |

Audit-trail growth for the reference large tenant (2 M rows/day) is about 1.5–3 GB/day before compression.
Monthly partitions are detached to the archive tier (doc 05 §5.7).

## 3.7 Encryption and keys without a cloud KMS

- **Disk**: LUKS2 full-disk encryption on all data servers. Keys are unsealed at boot by TPM2 (clevis) or a
  network-bound Tang server (clevis/tang, GPL). Both are open source.
- **Application envelope encryption**: OpenBao transit engine. Each data-encryption key per tenant is
  wrapped by an OpenBao key. Rotation is yearly or on demand.
- **Signing**: config packages and audit seals are signed with OpenBao transit keys (`kms_signature` column).
  Release bundles are signed by the vendor with cosign.
- **Optional HSM**: OpenBao seal and keys can be backed by a PKCS#11 HSM where the customer requires
  hardware-protected keys *(confirm PKCS#11 support in the OpenBao version pinned)*.
- **TLS**: an internal CA in OpenBao PKI issues certs through cert-manager. Public trust is not required on an
  internal network.

## 3.8 Observability (all on-premises)

OpenTelemetry SDK → OTel Collector → Prometheus (metrics), Tempo (traces), Loki (logs) → Grafana. Alertmanager
routes to email or the customer's on-call tool. **Logs are not audit trails**: GxP evidence lives only in
`audit.*`. Nothing is exported off-site.

## 3.9 Upgrades and air-gapped delivery

1. The vendor publishes a **release bundle**. It contains container images (OCI archive), Helm/Kustomize
   manifests, DB migrations, the SBOM (CycloneDX), release notes with validation impact, and the release
   validation package. The bundle is signed with cosign.
2. The customer transfers the bundle by approved media or an internal artefact proxy. It is verified with
   `cosign verify-blob` and imported into **Harbor**, where Trivy rescans it.
3. Argo CD (tier M/L) or the compose installer (tier S) deploys it into VAL under change control, then into
   PROD with the same digests.
4. Vulnerability database updates for Trivy are imported the same way (offline DB bundle).

## 3.10 What the customer now owns (operational responsibilities)

Moving off the cloud moves these duties to the customer's IT or to a managed-service partner. Name an owner
for each in the system's operating SOPs (doc 13):

| Duty | Notes |
|---|---|
| Hardware lifecycle, firmware, spares | Including disk failure response for PostgreSQL and Ceph |
| OS patching (monthly) | RKE2 and host OS. Patching is a change under SOP-CSV-002 |
| Physical security and environmental controls of server rooms | Part of the data-integrity controls (record protection) |
| Backup media handling and off-site rotation | Doc 16 |
| Certificate and key custody | OpenBao unseal keys split (Shamir) among named custodians |
| Capacity monitoring | Disk growth, especially the audit trail and documents |
| 24×7 coverage for the shop-floor path | Or the agreed paper fallback (doc 16 §16.1) |
