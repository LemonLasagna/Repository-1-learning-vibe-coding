# 21. API Architecture

Reference contract: `api/openapi/quality-v1.yaml` (OpenAPI 3.1, Deviation resource and the patterns every
service follows).

## 21.1 Conventions

| Topic | Rule |
|---|---|
| Base path | `/api/{service}/v{major}/…`, e.g. `/api/quality/v1/deviations` |
| Versioning | URL major version. Minor/additive changes are non-breaking. Breaking change → new major; the previous major is supported ≥ 12 months. `Deprecation` and `Sunset` headers on old versions. CI diffs OpenAPI and fails on unversioned breaking changes |
| Resources | Plural nouns; IDs are UUIDs; business number is also addressable: `GET /deviations/by-number/DEV-2026-00412` |
| State changes | **Never via PATCH of `status`.** `POST /{resource}/{id}/transitions` with `{action, signatureId?, reason?}` |
| Updates | `PATCH` with JSON Merge Patch; `If-Match: "{rowVersion}"` is required → 412 if stale, 428 if missing |
| Reason for change | `X-Change-Reason` header or `reason` field; required when the entity audit policy demands it (422 otherwise) |
| Idempotency | `Idempotency-Key` header on all POSTs; stored 24 h in `intg.idempotency_key` |
| Pagination | Cursor based: `?limit=50&cursor=…`; response `{items, nextCursor}` |
| Filtering | `?filter=` with a constrained expression (`status in ('OPEN','TRIAGE') and site.code eq 'BAS01'`) compiled to SQL with ABAC predicates appended; sorting `?sort=-dueDate` |
| Configured fields | Returned under `ext` with field codes; the runtime bundle describes their types |
| Errors | RFC 9457 Problem Details with stable `type` URIs |
| Time | RFC 3339 with offset; server stores UTC |
| Correlation | `traceparent` (W3C) accepted and propagated; `X-Correlation-Id` echoed |

## 21.2 Authentication and authorization

- OAuth 2.0 / OIDC bearer tokens from Keycloak. Audience per service. Scopes per resource family
  (`quality.read`, `quality.write`).
- Machine clients: client-credentials with mTLS-bound or DPoP tokens. Each `iam.api_client` has an owner
  user and is **never** allowed to execute e-signatures.
- Gateway: validates the JWT (signature, `aud`, `exp`), resolves the tenant, and strips client-supplied
  identity headers.
- Service: PEP evaluates permissions + ABAC + SoD (doc 14 §14.3). The DB enforces RLS.
- E-signature endpoints require a step-up token with `acr=esig` and a fresh `auth_time`.

## 21.3 Rate limiting

| Scope | Default limit | Algorithm |
|---|---|---|
| Per user (interactive) | 20 req/s, burst 60 | Token bucket in Redis |
| Per API client | Configurable per contract (default 50 req/s) | Token bucket |
| Per tenant | Fair-share ceiling (protects shared clusters) | Sliding window |
| Signature attempts | 5/min per user | Fixed window + lockout policy |
| Exports/reports | 10 concurrent per tenant | Queue |

Responses carry `RateLimit-Limit`, `RateLimit-Remaining`, and `RateLimit-Reset`. A rejected request gets 429
with `Retry-After`.

## 21.4 Documentation

- Swagger UI / Redoc served by the gateway at `/api/docs` (non-prod open; prod behind SSO).
- OpenAPI documents are generated from code (`Microsoft.AspNetCore.OpenApi`), post-processed to add the shared
  components, and published to `api/openapi/` on release. Angular clients are generated from them.
- A dynamic per-tenant OpenAPI extension describes the configured `ext` fields of each entity
  (`/api/meta/v1/openapi/{entity}`).

## 21.5 Events (async API)

- Exchange `pharmaos.{context}` (topic). Routing key `{entity}.{event}.v{n}`, e.g. `deviation.approved.v1`.
- CloudEvents 1.0 envelope (`id`, `source`, `type`, `time`, `tenantid` extension, `dataschema`).
- Delivery is at-least-once. Consumers deduplicate on `id` (inbox). External webhooks are signed with HMAC-SHA256
  and retried with exponential backoff.
- Event schemas are documented with AsyncAPI alongside the OpenAPI files.

## 21.6 Endpoint families per service (summary)

| Service | Families |
|---|---|
| identity-access | users, roles, permissions, assignments, groups, policies, sod-rules, access-reviews, org-nodes, sites… |
| config-studio | entities, fields, forms, rules, decision-tables, lookups, numbering, menus, templates, packages, deployments, runtime bundles |
| workflow-engine | definitions, instances, tasks (claim/complete/delegate), escalations, simulations |
| esignature | challenges, signatures, policies, meanings, acknowledgements |
| audit | trail (query/export), reviews, seals/verify |
| document | documents, versions, renditions, review-cycles, prints, distributions, logbooks |
| quality | events, deviations, investigations, capas, change-controls, complaints, oos, recalls, risk-assessments, suppliers-quality, audits, inspections, management-reviews, metrics, pqr |
| training | job-roles, courses, curricula, assignments, sessions, assessments, qualification-check |
| validation | systems, projects, requirements, protocols, executions, incidents, trace-matrix |
| mes | recipes, production-orders, batches, steps, dispensing, weighings, line-clearances, ipc, reconciliations, reviews, releases, equipment |
| warehouse | receipts, lots, containers, inventory, transactions, picks, transfers, counts, reconciliations, genealogy, labels |
| lims | methods, specifications, samples, tests, results, worksheets, stability, em, coas, standards, reagents |
| regulatory | authorizations, registered-details, submissions, variations, commitments, artworks, labels |
| erp | suppliers, customers, requisitions, purchase-orders, sales-orders, deliveries, mps, mrp-runs, production-orders, returns |
