# PharmaOS — Configurable GxP Digital Platform

Working name for an original, **self-hosted, open-source-stack**, metadata-driven platform that unifies QMS, DMS, LIMS, MES/EBR, WMS, pharma ERP,
training, validation, audit, and regulatory management on one data model. It is for pharmaceutical,
biopharmaceutical, vaccine, medical device, cosmetics, and nutraceutical manufacturers.

The design is original. It takes nothing from any vendor's proprietary code, schema, or documentation.
Capability comparisons with commercial products are about functional scope only.

## What is in this repository

| Path | What it is | Status |
|---|---|---|
| `docs/01…21` | The 20 requested deliverables plus the API architecture | Design-level |
| `database/model/catalog.dsl` | **Single source of truth** for the data model: 446 entities in 23 bounded contexts | Authoritative |
| `tools/schema_gen.py` | Generates DDL, ERD, and data dictionary from the catalog | Executable |
| `database/schema/*.sql` | PostgreSQL 16 DDL: 487 physical tables, 927 FKs, audit triggers, RLS | **Applies cleanly on PG 16** |
| `database/tests/integrity_tests.sql` | Executable data-integrity controls (OQ-DI-001…009) | **9/9 pass** |
| `database/data-dictionary.md` | Column-level dictionary (generated) | Generated |
| `api/openapi/quality-v1.yaml` | Reference OpenAPI 3.1 contract (Quality service) | Reference |
| `deploy/k8s/` | Kustomize base and overlays for tier M/L (on-prem RKE2) | Reference |
| `deploy/compose/` | Tier S single-server stack: PostgreSQL loads the schema at first start | Reference |

```bash
python3 tools/schema_gen.py            # regenerate DDL + ERD + dictionary
python3 tools/schema_gen.py --check    # CI: fail if generated files are stale
for f in database/schema/0*.sql; do psql -v ON_ERROR_STOP=1 -d pharmaos -f "$f"; done
psql -v ON_ERROR_STOP=1 -d pharmaos -f database/tests/integrity_tests.sql
```

## Deliverables index

| # | Deliverable | # | Deliverable |
|---|---|---|---|
| 1 | [Business Architecture](docs/01-business-architecture.md) | 11 | [Validation Master Plan](docs/11-validation-master-plan.md) |
| 2 | [Application Architecture](docs/02-application-architecture.md) | 12 | [IQ/OQ/PQ Templates](docs/12-iqoqpq-templates.md) |
| 3 | [Infrastructure Architecture](docs/03-infrastructure-architecture.md) | 13 | [SOP Templates](docs/13-sop-templates.md) |
| 4 | [Detailed ERD](docs/04-erd.md) (generated) | 14 | [Security Model](docs/14-security-model.md) |
| 5 | [Database Schema](docs/05-database-schema.md) | 15 | [Data Migration Strategy](docs/15-data-migration-strategy.md) |
| 6 | [UI Wireframes](docs/06-ui-wireframes.md) | 16 | [Disaster Recovery Plan](docs/16-disaster-recovery-plan.md) |
| 7 | [User Stories](docs/07-user-stories.md) | 17 | [Source Code Structure](docs/17-source-code-structure.md) |
| 8 | [User Requirement Specification](docs/08-urs.md) | 18 | [DevOps Structure](docs/18-devops.md) |
| 9 | [Functional Specification](docs/09-functional-specification.md) | 19 | [Kubernetes Deployment](docs/19-kubernetes-deployment.md) |
| 10 | [Design Specification](docs/10-design-specification.md) | 20 | [Product Roadmap](docs/20-product-roadmap.md) |
| — | [API Architecture](docs/21-api-architecture.md) | | |

## Design positions that depart from the brief

These are deliberate, and each is argued in the referenced document.

1. **Not a microservice per module.** The design uses about 20 deployables over **one PostgreSQL cluster with a
   schema per bounded context and cross-schema foreign keys**. In GxP, a CAPA that points at a deleted batch
   is a data-integrity finding. That risk outweighs independent database deployability. Each service gets
   write grants only to its own schemas. ([02](docs/02-application-architecture.md), [05](docs/05-database-schema.md))
2. **Configuration is a GxP change.** Every no-code artefact (form, workflow, rule, signature policy) is
   versioned. Artefacts are promoted DEV→VAL→PROD as signed **configuration packages** under change control,
   and are never edited in production. A no-code platform without this moves the validation burden onto
   each customer, every time they make a change. ([10](docs/10-design-specification.md), [11](docs/11-validation-master-plan.md))
3. **Analytics run on Apache Superset, not Power BI, and are for non-GxP use only.** Batch records, CoAs, and
   release reports are rendered in-platform: deterministic, versioned, hashed, PDF/A.
   ([02](docs/02-application-architecture.md))
4. **The workflow engine is a constrained BPMN subset built in-house.** It is not a general BPMN 2.0 engine.
   E-signature on transitions, deterministic replay, and validatability matter more than BPMN coverage.
   ([10](docs/10-design-specification.md))
5. **Self-hosted and open source.** All data, including the database, documents, backups, and keys, stays on
   customer-owned servers. Every runtime component is OSI-licensed, and the platform can run air-gapped. Three
   deployment tiers run the same images: single server (Docker Compose), site cluster (RKE2), and
   multi-site with a secondary data centre. Swaps from the original brief: Elasticsearch → OpenSearch,
   Redis → Valkey, MinIO → Ceph RGW / SeaweedFS, cloud KMS / Vault → OpenBao, Power BI → Superset.
   ([03](docs/03-infrastructure-architecture.md#32-open-source-bill-of-materials); licences tagged *(unverified)*)
6. **"Equivalent to or exceeding" eight mature products is a multi-year programme.** The
   [roadmap](docs/20-product-roadmap.md) sequences it so that a validatable QMS+DMS core ships first.
   MES/EBR, the deepest and highest-risk module, comes last.

## Regulatory reference convention

Clause numbers are cited only where the citation is certain. Examples are the 21 CFR Part 11 sections
(§11.10, §11.50, §11.70, §11.100, §11.200, §11.300). Everywhere else the requirement is described and the
source document is named, and the team must verify it against the current published text. Items marked
*(unverified)* need confirmation before they go into a validated deliverable.
