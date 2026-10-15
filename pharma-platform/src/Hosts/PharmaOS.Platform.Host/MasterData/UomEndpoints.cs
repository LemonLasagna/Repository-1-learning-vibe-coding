using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PharmaOS.Gxp;

namespace PharmaOS.Platform.Host.MasterData;

public sealed class Uom
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string Dimension { get; set; }
    public string Ext { get; set; } = "{}";
    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid UpdatedBy { get; set; }
}

public sealed record CreateUomRequest(string? Code, string? Name, string? Dimension);

public sealed record UpdateUomRequest(string? Name, string? Dimension);

public sealed record UomResponse(Guid Id, string Code, string Name, string Dimension, int RowVersion, DateTimeOffset UpdatedAt)
{
    public static UomResponse From(Uom u) => new(u.Id, u.Code, u.Name, u.Dimension, u.RowVersion, u.UpdatedAt);
}

/// <summary>
/// Reference vertical slice (doc 21 conventions): POST/GET/PATCH, ETag + If-Match concurrency,
/// mandatory reason for change on update, no DELETE. Every request runs in one transaction so the
/// GxP context reaches the database for reads (RLS) as well as writes (audit triggers).
/// </summary>
public static class UomEndpoints
{
    private const string Base = "/api/mdm/v1/uoms";

    public static void MapUomEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Base);
        group.MapPost("", Create).RequireAuthorization(Permissions.UomWrite);
        group.MapGet("{id:guid}", Get).RequireAuthorization(Permissions.UomRead);
        group.MapPatch("{id:guid}", Update).RequireAuthorization(Permissions.UomWrite);
    }

    private static async Task<IResult> Create(
        CreateUomRequest body, PlatformDbContext db, IGxpContextAccessor gxp, HttpContext http, CancellationToken ct)
    {
        if (gxp.Current is not { } ctx) return MissingIdentity();
        var errors = new Dictionary<string, string[]>();
        Require(errors, "code", body.Code, 50);
        Require(errors, "name", body.Name, 255);
        Require(errors, "dimension", body.Dimension, 30);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var uom = new Uom
        {
            Id = Guid.CreateVersion7(),
            TenantId = ctx.TenantId,
            Code = body.Code!.Trim(),
            Name = body.Name!.Trim(),
            Dimension = body.Dimension!.Trim().ToUpperInvariant(),
            CreatedBy = ctx.UserId,
            UpdatedBy = ctx.UserId,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Uoms.Add(uom);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Duplicate code",
                detail: $"A unit of measure with code '{uom.Code}' already exists.");
        }
        await tx.CommitAsync(ct);

        SetETag(http, uom.RowVersion);
        return Results.Created($"{Base}/{uom.Id}", UomResponse.From(uom));
    }

    private static async Task<IResult> Get(Guid id, PlatformDbContext db, HttpContext http, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var uom = await db.Uoms.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct);
        await tx.CommitAsync(ct);
        if (uom is null) return Results.NotFound();
        SetETag(http, uom.RowVersion);
        return Results.Ok(UomResponse.From(uom));
    }

    private static async Task<IResult> Update(
        Guid id, UpdateUomRequest body, PlatformDbContext db, IGxpContextAccessor gxp, HttpContext http, CancellationToken ct)
    {
        if (gxp.Current is not { } ctx) return MissingIdentity();
        if (!TryParseIfMatch(http, out var expectedVersion))
            return Results.Problem(statusCode: StatusCodes.Status428PreconditionRequired, title: "If-Match required",
                detail: "Send the ETag from your last read in the If-Match header.");
        if (ctx.ChangeReason is null)
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["X-Change-Reason"] = ["A reason for change is required."] });

        var errors = new Dictionary<string, string[]>();
        if (body.Name is not null) Require(errors, "name", body.Name, 255);
        if (body.Dimension is not null) Require(errors, "dimension", body.Dimension, 30);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var uom = await db.Uoms.SingleOrDefaultAsync(u => u.Id == id, ct);
        if (uom is null) return Results.NotFound();

        db.Entry(uom).Property(u => u.RowVersion).OriginalValue = expectedVersion;
        if (body.Name is not null) uom.Name = body.Name.Trim();
        if (body.Dimension is not null) uom.Dimension = body.Dimension.Trim().ToUpperInvariant();

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed, title: "Stale version",
                detail: "The record was changed by someone else. Reload it and review the changes.");
        }
        await tx.CommitAsync(ct);

        SetETag(http, uom.RowVersion);
        return Results.Ok(UomResponse.From(uom));
    }

    private static IResult MissingIdentity() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Missing identity",
            detail: "The token must carry 'sub' and 'tenant_id' claims.");

    private static void Require(Dictionary<string, string[]> errors, string field, string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) errors[field] = ["Required."];
        else if (value.Trim().Length > maxLength) errors[field] = [$"Maximum length is {maxLength}."];
    }

    private static void SetETag(HttpContext http, int rowVersion) =>
        http.Response.Headers.ETag = $"\"{rowVersion.ToString(CultureInfo.InvariantCulture)}\"";

    private static bool TryParseIfMatch(HttpContext http, out int version)
    {
        var raw = http.Request.Headers.IfMatch.ToString().Trim().Trim('"');
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }
}
