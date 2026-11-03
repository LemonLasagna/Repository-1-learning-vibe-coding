using Microsoft.Extensions.Logging;
using Npgsql;
using PharmaOS.Gxp;
using PharmaOS.Platform.Host.ESignature;
using PharmaOS.Platform.Host.Qms;
using PharmaOS.Platform.Host.Workflow;
using PharmaOS.Workflow;

namespace PharmaOS.IntegrationTests;

/// <summary>
/// A released workflow version never changes (URS-WF-005): the same content stored again is a no-op, different content under the same
/// version number is refused. A code change to a shipped workflow therefore needs a new version number.
/// </summary>
[Collection(PostgresFixture.Collection)]
[Trait("Req", "URS-WF-005")]
public sealed class WorkflowVersionStoreTests : IClassFixture<PostgresFixture>
{
    private static readonly Guid Tenant = Guid.Parse("0000000a-0000-0000-0000-000000000000");
    private static readonly Guid User = Guid.Parse("b9999999-0000-0000-0000-000000000001");

    private readonly PostgresFixture _db;

    public WorkflowVersionStoreTests(PostgresFixture db) => _db = db;

    private static WorkflowDefinition Definition(string code, string label, int version = 1) => new(code, version, "DRAFT",
        [new TransitionDefinition("go", "DRAFT", "DONE", Permission: "qms.capa:write", Label: label)]);

    /// <summary>A store on its own connection and transaction (rolled back), with a workflow definition row to attach versions to.</summary>
    private async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx, WorkflowStore Store, Guid DefinitionId, GxpContext Ctx)> OpenAsync(string code)
    {
        var conn = new NpgsqlConnection(_db.AdminConnectionString);
        await conn.OpenAsync();
        var tx = await conn.BeginTransactionAsync();
        await using (var session = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true), set_config('app.tenant_id', @tenant, true)", conn, tx))
        {
            session.Parameters.AddWithValue("user", User.ToString());
            session.Parameters.AddWithValue("tenant", Tenant.ToString());
            await session.ExecuteNonQueryAsync();
        }
        Guid id;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO wf.process_definition (tenant_id, code, name, bpmn_xml, model_json, status, is_gxp, created_by, updated_by)
            VALUES (@tenant, @code, 'Version store test', '', '{}'::jsonb, 'ACTIVE', true, @user, @user)
            RETURNING id
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("tenant", Tenant);
            insert.Parameters.AddWithValue("code", code);
            insert.Parameters.AddWithValue("user", User);
            id = (Guid)(await insert.ExecuteScalarAsync())!;
        }
        return (conn, tx, new WorkflowStore(conn, tx), id, new GxpContext(Tenant, User, "Test", null, null, Guid.NewGuid(), "tests"));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>An activated draft of (code, version), as it is after an authored workflow has been put into use.</summary>
    private static async Task InsertActiveDraftAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string code, int version)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO wf.workflow_draft (tenant_id, code, version_no, initial_state, transitions, status, created_by, updated_by)
            VALUES (@tenant, @code, @version, 'DRAFT', '[]'::jsonb, 'ACTIVE', @user, @user)
            """, conn, tx);
        cmd.Parameters.AddWithValue("tenant", Tenant);
        cmd.Parameters.AddWithValue("code", code);
        cmd.Parameters.AddWithValue("version", version);
        cmd.Parameters.AddWithValue("user", User);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_release_that_reuses_an_activated_versions_number_is_not_applied_and_records_keep_working()
    {
        var code = "STORE" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var (conn, tx, store, id, ctx) = await OpenAsync(code);
        var log = new CapturingLogger();
        WorkflowLog.Logger = log;
        try
        {
            await using (conn)
            await using (tx)
            {
                var entityDefinition = await new EsigStore(conn, tx).EnsureEntityDefinitionAsync(ctx, new CapaSignable(), CancellationToken.None);
                // Version 2 was activated from a draft: stored, and recorded as an active draft.
                Assert.True(await store.WriteVersionAsync(ctx, id, Definition(code, "authored", version: 2), CancellationToken.None));
                await InsertActiveDraftAsync(conn, tx, code, 2);

                // A later release from code defines version 2 differently. It is not applied, and nothing throws.
                var release = Definition(code, "from the release", version: 2);
                Assert.Equal(id, await store.EnsureDefinitionAsync(ctx, release, "Test workflow", entityDefinition, CancellationToken.None));

                Assert.True(await store.ReleaseNotAppliedAsync(id, release, CancellationToken.None));
                var current = await store.CurrentAsync(id, release, CancellationToken.None);
                Assert.Equal(2, current.Version);
                Assert.Equal("authored", current.Transitions[0].Label);
                var entry = Assert.Single(log.Entries);
                Assert.Equal(LogLevel.Error, entry.Level);
                Assert.Contains($"Workflow {code}: this release defines version 2", entry.Message);

                // The same content is no conflict at all.
                Assert.False(await store.ReleaseNotAppliedAsync(id, Definition(code, "authored", version: 2), CancellationToken.None));
            }
        }
        finally
        {
            WorkflowLog.Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }
    }

    [Fact]
    public async Task Two_releases_from_code_that_disagree_about_a_version_number_still_fail()
    {
        var code = "STORE" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var (conn, tx, store, id, ctx) = await OpenAsync(code);
        await using (conn)
        await using (tx)
        {
            var entityDefinition = await new EsigStore(conn, tx).EnsureEntityDefinitionAsync(ctx, new CapaSignable(), CancellationToken.None);
            await store.EnsureDefinitionAsync(ctx, Definition(code, "first release"), "Test workflow", entityDefinition, CancellationToken.None);

            // No activated draft holds version 1, so this is a release that changed without a new number: a mistake to stop.
            var refused = await Assert.ThrowsAsync<WorkflowVersionConflictException>(() =>
                store.EnsureDefinitionAsync(ctx, Definition(code, "edited without a new version number"), "Test workflow", entityDefinition, CancellationToken.None));
            Assert.Equal((code, 1), (refused.Code, refused.Version));
            Assert.False(await store.ReleaseNotAppliedAsync(id, Definition(code, "edited without a new version number"), CancellationToken.None));
        }
    }

    [Fact]
    public async Task Storing_a_version_again_with_the_same_content_does_nothing()
    {
        var code = "STORE" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var (conn, tx, store, id, ctx) = await OpenAsync(code);
        await using (conn)
        await using (tx)
        {
            Assert.True(await store.WriteVersionAsync(ctx, id, Definition(code, "Go"), CancellationToken.None));
            Assert.False(await store.WriteVersionAsync(ctx, id, Definition(code, "Go"), CancellationToken.None));
        }
    }

    [Fact]
    public async Task Different_content_under_a_released_version_number_is_refused_and_a_new_number_is_accepted()
    {
        var code = "STORE" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var (conn, tx, store, id, ctx) = await OpenAsync(code);
        await using (conn)
        await using (tx)
        {
            Assert.True(await store.WriteVersionAsync(ctx, id, Definition(code, "Go"), CancellationToken.None));

            var refused = await Assert.ThrowsAsync<WorkflowVersionConflictException>(
                () => store.WriteVersionAsync(ctx, id, Definition(code, "Go, changed"), CancellationToken.None));
            Assert.Contains("already released with different content", refused.Message);

            Assert.True(await store.WriteVersionAsync(ctx, id, Definition(code, "Go, changed", version: 2), CancellationToken.None));
        }
    }
}
