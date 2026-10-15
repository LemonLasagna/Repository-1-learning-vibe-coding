using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PharmaOS.Gxp;

namespace PharmaOS.Persistence;

/// <summary>
/// Copies the GxP context into transaction-local PostgreSQL settings (app.*) at the start of every
/// transaction. The database triggers and row-level security read them (doc 05 §5.5). Settings are
/// transaction-scoped, so a pooled connection cannot carry one request's identity into another.
/// Without a context the transaction is refused: fail closed.
/// </summary>
public sealed class GxpSessionInterceptor(IGxpContextAccessor accessor) : DbTransactionInterceptor
{
    private const string Sql =
        "SELECT set_config('app.tenant_id', @tenant, true), set_config('app.user_id', @user, true), " +
        "set_config('app.user_display', @display, true), set_config('app.on_behalf_of', @obo, true), " +
        "set_config('app.change_reason', @reason, true), set_config('app.correlation_id', @corr, true), " +
        "set_config('app.client', @client, true)";

    public override DbTransaction TransactionStarted(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        using var cmd = BuildCommand(connection, result);
        cmd.ExecuteNonQuery();
        return result;
    }

    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = BuildCommand(connection, result);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return result;
    }

    private DbCommand BuildCommand(DbConnection connection, DbTransaction transaction)
    {
        var ctx = accessor.Current
                  ?? throw new InvalidOperationException(
                      "No GxP context: a database transaction requires an authenticated actor and tenant.");
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = Sql;
        Add(cmd, "tenant", ctx.TenantId.ToString("D"));
        Add(cmd, "user", ctx.UserId.ToString("D"));
        Add(cmd, "display", ctx.UserDisplay);
        Add(cmd, "obo", ctx.OnBehalfOf?.ToString("D") ?? "");
        Add(cmd, "reason", ctx.ChangeReason ?? "");
        Add(cmd, "corr", ctx.CorrelationId.ToString("D"));
        Add(cmd, "client", ctx.ClientApp);
        return cmd;
    }

    private static void Add(DbCommand cmd, string name, string value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
