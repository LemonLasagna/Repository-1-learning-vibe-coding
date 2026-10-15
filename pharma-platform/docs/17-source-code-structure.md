# 17. Source Code Structure

Monorepo: one repository, one version per release, and atomic changes across the schema, services, and UI.

```
pharmaos/
├── README.md
├── Directory.Build.props            # shared .NET settings: nullable, warnings-as-errors, analyzers
├── Directory.Packages.props         # central package versions
├── global.json                      # pinned .NET SDK (8.0.x LTS)
├── PharmaOS.sln
├── database/
│   ├── model/catalog.dsl            # source of truth (this repo)
│   ├── schema/                      # generated target-state DDL
│   ├── migrations/                  # ordered expand/contract migrations (V2026.10.001__xyz.sql)
│   ├── seed/                        # reference data (UoM, signature meanings, permissions)
│   └── tests/                       # SQL integrity tests (OQ-DI-*)
├── tools/
│   ├── schema_gen.py
│   └── release/                     # manifest builder, SBOM, signing
├── src/
│   ├── BuildingBlocks/
│   │   ├── PharmaOS.Gxp.Context/            # GxpContext, DbConnectionInterceptor (SET LOCAL app.*)
│   │   ├── PharmaOS.Gxp.Records/            # AggregateRoot, RowVersion, Ext, canonical JSON (RFC 8785), hashing
│   │   ├── PharmaOS.Outbox/                 # EF outbox, publisher, inbox dedupe
│   │   ├── PharmaOS.Messaging.RabbitMq/
│   │   ├── PharmaOS.Metadata.Client/        # runtime bundles, JSON Schema validation of ext
│   │   ├── PharmaOS.Rules/                  # AST evaluator (C#), decision tables
│   │   ├── PharmaOS.Workflow.Client/
│   │   ├── PharmaOS.ESig.Client/
│   │   ├── PharmaOS.Authorization/          # PEP, policy → SQL predicate compiler
│   │   ├── PharmaOS.Numbering/
│   │   ├── PharmaOS.Storage/                # IObjectStore (S3 API), hashing, WORM metadata
│   │   ├── PharmaOS.Search/                 # ISearchClient (ES/OpenSearch subset)
│   │   ├── PharmaOS.Reporting.Abstractions/ # IPdfRenderer, templates
│   │   ├── PharmaOS.Observability/
│   │   └── PharmaOS.Testing/                # Testcontainers fixtures, GxP assertions
│   ├── Gateway/PharmaOS.Gateway/            # YARP
│   ├── Services/
│   │   ├── IdentityAccess/
│   │   │   ├── IdentityAccess.Api/          # minimal APIs / controllers, OpenAPI
│   │   │   ├── IdentityAccess.Application/  # use cases (commands/queries), validators
│   │   │   ├── IdentityAccess.Domain/       # aggregates, domain events, invariants (no infrastructure refs)
│   │   │   ├── IdentityAccess.Infrastructure/ # EF Core DbContext (schemas iam, org), repositories, consumers
│   │   │   └── IdentityAccess.Tests/        # unit + integration (Testcontainers PG/RabbitMQ)
│   │   ├── ConfigStudio/   … same 5-project layout
│   │   ├── Workflow/
│   │   ├── ESignature/
│   │   ├── Audit/
│   │   ├── Notification/
│   │   ├── Document/
│   │   ├── Quality/        # Domain split by module folder: Deviations/, Capa/, ChangeControl/, Complaints/, Oos/, Risk/, Supplier/, Audits/
│   │   ├── Training/
│   │   ├── Validation/
│   │   ├── MasterData/
│   │   ├── Erp/
│   │   ├── Mes/            # Recipes/, Execution/, Dispensing/, Review/, Release/, Equipment/
│   │   ├── Warehouse/
│   │   ├── Lims/
│   │   ├── Regulatory/
│   │   ├── Reporting/
│   │   └── IntegrationHub/
│   ├── Workers/
│   │   ├── PharmaOS.RenderingWorker/
│   │   ├── PharmaOS.SearchIndexer/
│   │   └── PharmaOS.AuditSealer/
│   └── Edge/PharmaOS.EdgeConnector/         # OPC UA client, device drivers, SQLite store-and-forward
├── web/                                     # Nx workspace
│   ├── apps/
│   │   ├── portal/                          # main Angular SPA
│   │   ├── shopfloor/                       # PWA for MES/WMS tablets & scanners
│   │   └── config-studio/                   # builders (lazy-loaded into portal or standalone)
│   ├── libs/
│   │   ├── ui/                              # design system components + tokens
│   │   ├── metadata-renderer/               # dynamic forms/lists/record page from runtime bundles
│   │   ├── rules/                           # TS AST evaluator (parity with C#)
│   │   ├── esig/                            # signature dialog, step-up flow
│   │   ├── workflow-designer/               # BPMN-subset canvas
│   │   ├── dashboard-designer/
│   │   ├── data-access/                     # generated API clients from OpenAPI
│   │   └── feature-*/                       # feature libs per module (qms, dms, mes, …)
│   └── e2e/                                 # Playwright tests (tagged with URS/OQ IDs)
├── tests/
│   ├── rules-golden/                        # shared rule test vectors (C# + TS parity)
│   ├── contract/                            # consumer-driven contract tests between services
│   ├── performance/                         # k6 scripts (PQ-PERF-001)
│   └── validation/                          # automated OQ scripts emitting val.automated_test_evidence
├── api/openapi/                             # published contracts per service and version
├── config-packages/                         # vendor segment packs (pharma, biologics, device, …) as JSON
├── deploy/
│   ├── k8s/                                 # Kustomize base + overlays
│   └── helm/                                # optional chart for customer-hosted
└── .github/workflows/                       # CI/CD (doc 18)
```

## 17.1 Layering rules (enforced by architecture tests, e.g. NetArchTest)

- `*.Domain` references nothing but `PharmaOS.Gxp.Records`.
- `*.Application` references `Domain` and abstractions. It does not reference EF Core.
- `*.Infrastructure` is the only place with EF Core, RabbitMQ, and S3.
- Services never reference other services' projects. They communicate through HTTP clients generated from
  OpenAPI, or through messages defined in `*.Contracts` packages.
- No service writes another service's schema. This is enforced by DB grants and by an architecture test that
  scans EF mappings.

## 17.2 Reference code: GxP context interceptor

```csharp
public sealed class GxpDbConnectionInterceptor(IGxpContextAccessor ctx) : DbTransactionInterceptor
{
    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData data, DbTransaction tx, CancellationToken ct = default)
    {
        var c = ctx.Current ?? throw new InvalidOperationException("GxP write outside an authenticated context");
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT set_config('app.tenant_id', @t, true), set_config('app.user_id', @u, true),
                   set_config('app.user_display', @d, true), set_config('app.on_behalf_of', @o, true),
                   set_config('app.change_reason', @r, true), set_config('app.correlation_id', @c, true),
                   set_config('app.client', @a, true)
            """;
        cmd.AddParam("t", c.TenantId).AddParam("u", c.UserId).AddParam("d", c.UserDisplay)
           .AddParam("o", c.OnBehalfOf).AddParam("r", c.ChangeReason).AddParam("c", c.CorrelationId)
           .AddParam("a", c.ClientApp);
        await cmd.ExecuteNonQueryAsync(ct);
        return tx;
    }
}
```

## 17.3 Reference code: signature binding check

```csharp
public async Task<Result> BindSignatureAsync(Deviation dev, Guid signatureId, string action, CancellationToken ct)
{
    var sig = await _esig.GetAsync(signatureId, ct);
    var policy = _policies.For("DEVIATION", action);
    if (sig.SignerId != _ctx.Current.UserId)                          return Problems.SignatureNotOwned;
    if (sig.RecordId != dev.Id || sig.RecordVersion != dev.RowVersion) return Problems.SignatureStale;
    if (sig.RecordHash != Canonical.Sha256(dev.SnapshotForSignature())) return Problems.RecordChanged;
    if (sig.MeaningCode != policy.MeaningCode)                         return Problems.WrongMeaning;
    if (policy.RequireSecondPerson && dev.HasSignatureBy(sig.SignerId, policy.ConflictingMeanings))
                                                                       return Problems.SegregationOfDuties;
    dev.ApplyTransition(action, signatureId);                          // raises domain event → outbox
    return Result.Ok();
}
```

## 17.4 Conventions

- C#: .NET 8 LTS, nullable enabled, `TreatWarningsAsErrors`, analyzers (Roslyn and security analyzers).
  Records for DTOs. `TimeProvider` is injected everywhere (no `DateTime.Now`), which makes timer/escalation tests
  deterministic.
- TS: strict mode, ESLint + Angular ESLint, no `any` in libs.
- Commits: Conventional Commits. A commit that touches a validated function references its requirement ID
  (`feat(qms): add deviation extension limit [URS-QMS-004]`). A CI check generates the RTM delta per release.
- Tests are tagged with requirement IDs (`[Trait("Req","URS-DI-001")]`, Playwright `@URS-ESIG-001`). The
  release pipeline converts results into `val.automated_test_evidence`.
