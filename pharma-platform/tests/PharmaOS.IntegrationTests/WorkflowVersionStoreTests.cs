using Npgsql;
using PharmaOS.Gxp;
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

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.WriteVersionAsync(ctx, id, Definition(code, "Go, changed"), CancellationToken.None));
            Assert.Contains("already released with different content", refused.Message);

            Assert.True(await store.WriteVersionAsync(ctx, id, Definition(code, "Go, changed", version: 2), CancellationToken.None));
        }
    }
}
