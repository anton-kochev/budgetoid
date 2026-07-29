using Api.Endpoints;
using Api.Infrastructure;
using Application;
using Application.Abstractions;
using Infrastructure;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Provisioning;
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
// IBudgetContext for its budget isolation query filters, and pooled contexts can't take scoped
// dependencies. Aspire's AddNpgsqlDbContext pools contexts; EnrichNpgsqlDbContext re-applies
// Aspire's retry/health/telemetry defaults here.
// The (serviceProvider, options) overload, not the plain one: BudgetSessionInterceptor is scoped
// because it reads the scoped IBudgetContext, and this overload's optionsLifetime defaults to
// Scoped, so it resolves from the request scope. The interceptor is what puts the ambient budget on
// each connection for the row-level security policies — without it the role's every policied query
// fails with 22P02.
builder.Services.AddDbContext<BudgetoidDbContext>((serviceProvider, options) =>
    options
        .UseNpgsql(BuildConnectionString(
            builder.Configuration.GetConnectionString("budgetoid"),
            builder.Environment.IsDevelopment()))
        .AddInterceptors(serviceProvider.GetRequiredService<BudgetSessionInterceptor>()));
builder.EnrichNpgsqlDbContext<BudgetoidDbContext>();
builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<IBudgetContext, HttpContextBudgetContext>();
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
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    // Lets a PATCH body tell "property absent" apart from "property explicitly null".
    options.SerializerOptions.Converters.Add(new OptionalJsonConverterFactory());
});
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
builder.Services.AddExceptionHandler<ConflictExceptionHandler>();
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

// Development is the only environment where the application shapes its own database. Production
// applies the migration and the grant matrix as deliberate admin steps at deploy time, so nothing
// here runs there — do not "helpfully" lift this block out of the Development check. The deployed
// container is handed exactly one connection string, the least-privilege one (see AppHost's publish
// branch), so lifting this code out would not quietly give a request-serving process DDL rights:
// it would fail at boot on the missing admin connection string a few lines below. The absence of
// the credential is what prevents the escalation; this check is what prevents the boot failure.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();

    // Fail fast on the elevated connection string, for the same reason as the Google client id
    // above: absent, it would surface much later as an opaque Npgsql error from a null connection.
    string adminConnectionString = app.Configuration.GetConnectionString("budgetoid-admin")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:budgetoid-admin is required in Development: startup migrates the "
            + "schema and provisions the application role, and neither can run on the "
            + "least-privilege connection the application serves requests with.");

    // The application role's password is read out of the application connection string rather than
    // from a configuration key of its own: provisioning sets the role's password to whatever the
    // application is already configured to connect with, so the two cannot drift apart. Do not add
    // a third setting for it.
    string appRolePassword =
        new NpgsqlConnectionStringBuilder(app.Configuration.GetConnectionString("budgetoid")).Password
        ?? throw new InvalidOperationException(
            "ConnectionStrings:budgetoid must carry a password in Development: it is the password "
            + "startup assigns to the application role.");

    // Migrate on a context built explicitly over the admin connection, not the scoped one from DI.
    // That one is configured with ConnectionStrings:budgetoid — the least-privilege role, which is
    // denied CREATE on the schema and so cannot run MigrateAsync even as a no-op. See the
    // __EFMigrationsHistory note in app-role-grants.sql.
    await using (BudgetoidDbContext db = new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(adminConnectionString)
            .Options))
    {
        await db.Database.MigrateAsync();
    }

    // Strictly after the migration: the grants name individual tables, so the schema has to exist
    // before they can be applied.
    await DatabaseProvisioning.ApplyGrantsAsync(adminConnectionString, appRolePassword);
}

app.MapDefaultEndpoints();
app.MapAccountEndpoints();
app.MapCurrencyEndpoints();
app.MapTransactionEndpoints();
app.MapPayeeEndpoints();
app.MapCategoryGroupEndpoints();
app.MapCategoryEndpoints();

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
//
// Two Npgsql options are now forbidden in any connection string this reaches, because budget
// isolation is enforced by a session setting (see BudgetSessionInterceptor). `No Reset On Close=true`
// would keep a returned connection's app.current_budget_id, making the pool reset — now a security
// control, not a hygiene one — stop clearing one tenant's budget before the next borrower.
// `Multiplexing=true` interleaves logical sessions over one physical connection, which no
// session-setting design can survive at all.
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
