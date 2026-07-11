using Api.Endpoints;
using Api.Infrastructure;
using Application;
using Application.Abstractions;
using Infrastructure;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using ServiceDefaults;
using System.Text.Json.Serialization;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Registered non-pooled (AddDbContext, scoped) because BudgetoidDbContext depends on the scoped
// IUserContext for its transaction query filter, and pooled contexts can't take scoped
// dependencies. Aspire's AddNpgsqlDbContext pools contexts; EnrichNpgsqlDbContext re-applies
// Aspire's retry/health/telemetry defaults here.
builder.Services.AddDbContext<BudgetoidDbContext>(options =>
    options.UseNpgsql(BuildConnectionString(
        builder.Configuration.GetConnectionString("budgetoid"),
        builder.Environment.IsDevelopment())));
builder.EnrichNpgsqlDbContext<BudgetoidDbContext>();
builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<IUserContext, HttpContextUserContext>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = "https://accounts.google.com";
        // Read lazily: this options factory runs post-Build, so it sees the fully-composed
        // configuration. The presence of the value is enforced at boot (see post-Build check).
        options.Audience = builder.Configuration["Authentication:Google:ClientId"];
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = ["https://accounts.google.com", "accounts.google.com"],
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
        };
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
// Allowed origins come from configuration so the deployed frontend origin can be supplied per
// environment (appsettings.Development.json locally, Cors__AllowedOrigins__0 env/secret in prod)
// instead of being hardcoded. Configured lazily via the options pipeline so the policy is built
// from the fully-composed configuration (the same reason auth/connection strings read post-Build).
builder.Services.AddCors();
builder.Services.AddOptions<CorsOptions>().Configure<IConfiguration>((options, configuration) =>
{
    string[] allowedOrigins = configuration
        .GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod());
});
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
builder.Services.AddExceptionHandler<NotFoundExceptionHandler>();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();

WebApplication app = builder.Build();

// Fail fast at boot if the required auth config is missing, instead of letting the JwtBearer
// options factory throw lazily on the first authenticated request (an opaque 500). Read here,
// post-Build, so the value resolves from the fully-composed configuration — including in-memory
// sources contributed by the test host, which are only merged as the host is built. Set via
// user-secrets, environment, or appsettings.
_ = app.Configuration["Authentication:Google:ClientId"]
    ?? throw new InvalidOperationException("Authentication:Google:ClientId is required.");

app.UseExceptionHandler();
app.UseStatusCodePages(async statusCodeContext =>
{
    HttpContext httpContext = statusCodeContext.HttpContext;
    if (!httpContext.Response.HasStarted &&
        (httpContext.Response.StatusCode == StatusCodes.Status401Unauthorized ||
         httpContext.Response.StatusCode == StatusCodes.Status403Forbidden))
    {
        await Results.Problem(statusCode: httpContext.Response.StatusCode)
            .ExecuteAsync(httpContext);
    }
});
app.UseCors();
app.UseAuthentication();
app.UseMiddleware<UserProvisioningMiddleware>();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();

    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    BudgetoidDbContext db = scope.ServiceProvider.GetRequiredService<BudgetoidDbContext>();
    await db.Database.MigrateAsync();
}

app.MapDefaultEndpoints();
app.MapAccountEndpoints();
app.MapCurrencyEndpoints();
app.MapTransactionEndpoints();
app.MapPayeeEndpoints();
app.MapGroupEndpoints();

await app.RunAsync();

// Force TLS on the PostgreSQL connection outside local development. Azure Database for PostgreSQL
// Flexible Server rejects unencrypted connections (28000: no pg_hba.conf entry ... no encryption)
// and enforces TLS server-side, but the connection string injected from the Key Vault secret via
// Aspire carries only host/user/password/database and omits SslMode — so Npgsql would otherwise
// attempt an unencrypted connection. Rebuild the string with SslMode=Require, which (Npgsql 8+)
// encrypts without validating the server certificate, so Azure's cert chain need not be in the
// chiseled container's trust store.
//
// Development is deliberately left untouched: the local Aspire and Testcontainers PostgreSQL images
// have no TLS configured, and SslMode=Require against them fails with "No SSL enabled connection
// from this host is configured." A null connection string is returned unchanged so the null case
// preserves the existing fail-later behavior.
static string? BuildConnectionString(string? connectionString, bool isDevelopment)
{
    if (connectionString is null || isDevelopment)
    {
        return connectionString;
    }

    NpgsqlConnectionStringBuilder connectionStringBuilder = new(connectionString)
    {
        SslMode = SslMode.Require,
    };

    return connectionStringBuilder.ConnectionString;
}
