using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using PharmaOS.Platform.Host.ESignature;

namespace PharmaOS.Platform.Host.Api;

/// <summary>Conventions shared by the record APIs (doc 21): ETag/If-Match, Problem Details with a stable code.</summary>
public static class ApiResults
{
    public static void SetETag(HttpContext http, int rowVersion) =>
        http.Response.Headers.ETag = $"\"{rowVersion.ToString(CultureInfo.InvariantCulture)}\"";

    public static bool TryParseIfMatch(HttpContext http, out int version) =>
        int.TryParse(http.Request.Headers.IfMatch.ToString().Trim().Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out version);

    public static IResult IfMatchRequired() =>
        Problem(StatusCodes.Status428PreconditionRequired, "IF_MATCH_REQUIRED", "If-Match required",
            "Send the ETag from your last read in the If-Match header.");

    public static IResult Stale(string what = "record") =>
        Problem(StatusCodes.Status412PreconditionFailed, "STALE_VERSION", "Stale version",
            $"The {what} was changed by someone else. Reload it and review the changes.");

    public static IResult MissingIdentity() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Missing identity",
            detail: "The session must carry 'sub' and 'tenant_id' claims.");

    public static IResult Problem(ActionRefusal r) => Problem(r.Status, r.Code, r.Title, r.Detail);

    public static IResult Problem(int status, string code, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });

    public static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>The Npgsql connection and transaction under the EF Core transaction (GxP context already set).</summary>
    public static (NpgsqlConnection Conn, NpgsqlTransaction Tx) Sql(PlatformDbContext db) =>
        ((NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());
}
