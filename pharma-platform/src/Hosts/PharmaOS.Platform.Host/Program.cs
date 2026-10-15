using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PharmaOS.Gxp;
using PharmaOS.Persistence;
using PharmaOS.Platform.Host;
using PharmaOS.Platform.Host.Audit;
using PharmaOS.Platform.Host.MasterData;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IGxpContextAccessor, HttpGxpContextAccessor>();
builder.Services.AddScoped<GxpSessionInterceptor>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        // Keycloak realm on the customer's own network (doc 03). Claims are used as issued.
        o.Authority = builder.Configuration["Oidc:Authority"];
        o.Audience = builder.Configuration["Oidc:Audience"];
        o.MapInboundClaims = false;
        o.RequireHttpsMetadata = builder.Configuration.GetValue("Oidc:RequireHttpsMetadata", true);
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPermissionPolicy(Permissions.UomRead)
    .AddPermissionPolicy(Permissions.UomWrite)
    .AddPermissionPolicy(Permissions.AuditTrailRead);

// Connection strings are resolved lazily so test hosts and environments can override them.
builder.Services.AddDbContext<PlatformDbContext>((sp, o) =>
    o.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("Db"))
     .AddInterceptors(sp.GetRequiredService<GxpSessionInterceptor>()));
builder.Services.AddSingleton(sp =>
    NpgsqlDataSource.Create(sp.GetRequiredService<IConfiguration>().GetConnectionString("AuditDb")
                            ?? throw new InvalidOperationException("ConnectionStrings:AuditDb is not configured")));
builder.Services.AddSingleton<AuditTrailReader>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", async (PlatformDbContext db, CancellationToken ct) =>
        await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503))
    .AllowAnonymous();

app.MapUomEndpoints();
app.MapAuditEndpoints();

app.Run();

public partial class Program;
