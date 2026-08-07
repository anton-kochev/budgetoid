using System.Text.Json.Serialization;
using Api.Endpoints;
using Api.Infrastructure;
using Application;
using Application.Abstractions;
using Application.Users.EnsureUser;
using Infrastructure;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Provisioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using ServiceDefaults;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Kestrel's 30 MB default is a file-upload default, and this API accepts no files: every endpoint
// takes a small JSON object, the largest being a passkey registration response whose attestation
// object is a few kilobytes. Until this line, the anonymous sign-in endpoint would read 30 MB into
// memory before the handler had looked at a single byte of it, and a scale-to-zero container's whole
// memory budget is a small multiple of that.
//
// Global rather than on the two anonymous endpoints, for two reasons. A per-endpoint limit has to be
// in force before the body is read, and a minimal-API endpoint filter runs after model binding — by
// the time one could refuse the request the body has already been read into the strings it would have
// judged. And a global ceiling is the shape that cannot be forgotten on whatever endpoint is added
// next. An endpoint that legitimately needs more can raise it on itself; none does.
//
// This bounds the body. PasskeyPayloadLimits bounds each decoded member, and neither replaces the
// other: this keeps 30 MB from being read at all, and those keep a body well inside this limit from
// being validated and decoded four times over before the first check that could refuse it.
const long maxRequestBodyBytes = 64 * 1024;
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxRequestBodyBytes);

// Registered non-pooled (AddDbContext, scoped) because BudgetoidDbContext depends on the scoped
// IBudgetContext for its budget isolation query filters, and pooled contexts can't take scoped
// dependencies. Aspire's AddNpgsqlDbContext pools contexts; the Enrich call re-applies Aspire's
// retry/health/telemetry defaults here.
// The (serviceProvider, options) overload, not the plain one: SessionContextInterceptor is scoped
// because it reads the scoped IBudgetContext and IUserContext, and this overload's optionsLifetime
// defaults to Scoped, so it resolves from the request scope. The interceptor is what puts the
// signed-in user and the ambient budget on each connection for the row-level security policies —
// without it the role's every policied query fails with 22P02.
//
// The Azure enrichment, not the plain EnrichNpgsqlDbContext: the deployed API holds no database
// password. EnrichAzureNpgsqlDbContext layers a password provider onto the data source that fetches
// a Microsoft Entra access token from the container's user-assigned managed identity (Aspire reads
// AZURE_CLIENT_ID / AZURE_TOKEN_CREDENTIALS, which its Container Apps publisher injects); to
// PostgreSQL that token is the password. It self-disables when the connection string already carries
// both a username and a password, which is exactly the local-dev and integration-test case — that is
// why there is no environment check here, one registration serves both and the connection string
// decides.
// The enrichment works through ConfigureDataSource, so the AddDbContext above must keep handing
// UseNpgsql a connection *string*. Passing a pre-built NpgsqlDataSource instead would conflict with
// it: EF cannot see inside an externally built data source and can hand back a cached one that never
// received the token provider. Do not "simplify" it that way.
// The enrichment must also stay after AddDbContext — it requires the context to be registered
// already, and on .NET 10 it appends an options-configuration action, which is what preserves the
// interceptor registered above.
builder.Services.AddDbContext<BudgetoidDbContext>((serviceProvider, options) =>
    options
        .UseNpgsql(BuildConnectionString(
            builder.Configuration.GetConnectionString("budgetoid"),
            builder.Environment.IsDevelopment()))
        .AddInterceptors(serviceProvider.GetRequiredService<SessionContextInterceptor>()));
builder.EnrichAzureNpgsqlDbContext<BudgetoidDbContext>();
builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<IBudgetContext, HttpContextBudgetContext>();
// Readers and writer are three registrations over the one scoped CurrentUser on purpose: everything
// that needs to know who is signed in takes IUserContext, everything that needs the tenant takes
// IBudgetContext, and only what may *change* either takes IUserContextWriter. So the capability to
// name the request's identity and its budget is declared in the constructors that use it rather than
// travelling with every read. The three adapters registered here are also the only types that take
// CurrentUser itself, and that is what the claim rests on: injecting the scoped state anywhere else —
// provisioning middleware included — gives that collaborator both fields with neither interface, and
// the clearing rule CurrentUserWriter.ResolveUser carries stops applying to whatever it publishes.
builder.Services.AddScoped<IUserContext, HttpContextUserContext>();
builder.Services.AddScoped<IUserContextWriter, CurrentUserWriter>();
// Singleton, unlike the two contexts above: the relying party and the origin allow-list are
// configuration rather than request state, and one instance per request would only add a way for the
// two legs of one sign-in to disagree about which site they are.
builder.Services.AddSingleton<IPasskeyCeremonyPolicy, ConfiguredPasskeyCeremonyPolicy>();
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
// Before the catch-all, which would otherwise turn a refused sign-in into a 500 and log it as a
// fault. Handlers run in registration order and the first to claim the exception wins.
builder.Services.AddExceptionHandler<PasskeyVerificationExceptionHandler>();
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

// The passkey ceremony's configuration is refused at boot for the same reason as the Google client
// id above: a deployment that comes up healthy and only breaks when somebody attempts a ceremony is a
// deployment whose defect surfaces to a user instead of to the pipeline. Boot is the last moment the
// pipeline is still watching.
//
// Both values are refused, and the second is the dangerous one. A relying party id is hashed into
// every credential an authenticator stores, so a host that came up with the wrong one registers
// passkeys nobody can ever use and no migration repairs them — but at least it is wrong loudly. An
// empty origin allow-list is not a permissive default: it refuses every ceremony, and refuses it with
// the one deliberately uninformative 401 that explains nothing, so the misconfiguration reads as a
// working deployment that users simply cannot sign in to. The key names come from the reader's own
// constants so the guard and the reader cannot drift apart.
//
// Presence is necessary and not sufficient. A value that passes a presence check and then refuses
// every ceremony is a defect that surfaces to a user rather than to the pipeline, which is the whole
// reason this guard is at boot rather than in a lazily-resolved singleton — so the form of each value
// is checked here too, and the two values are checked against each other. The origin the browser puts
// in client data is a serialized origin: scheme, host, and a non-default port, and nothing else. A
// frontend origin written as "https://example.com/" or as a bare "example.com" is a plausible thing to
// paste into a deployment variable and matches no client data that will ever arrive.
string? relyingPartyId = app.Configuration[ConfiguredPasskeyCeremonyPolicy.RelyingPartyIdKey];
if (string.IsNullOrWhiteSpace(relyingPartyId))
{
    throw new InvalidOperationException(
        $"{ConfiguredPasskeyCeremonyPolicy.RelyingPartyIdKey} is required: it is the domain every "
        + "registered passkey is permanently bound to.");
}

// A relying party id is a bare domain — never a URL, and never an address literal. Checking it here
// rather than only through the origins below is what keeps a relying party id pasted as
// "https://example.com" from being reported as every origin being wrong.
if (Uri.CheckHostName(relyingPartyId) is not UriHostNameType.Dns)
{
    throw new InvalidOperationException(
        $"{ConfiguredPasskeyCeremonyPolicy.RelyingPartyIdKey} is '{relyingPartyId}', which is not a "
        + "bare domain name: a relying party id carries no scheme, port or path.");
}

if (app.Configuration.GetSection(ConfiguredPasskeyCeremonyPolicy.AllowedOriginsKey).Get<string[]>()
    is not { Length: > 0 } allowedOrigins)
{
    throw new InvalidOperationException(
        $"{ConfiguredPasskeyCeremonyPolicy.AllowedOriginsKey} must list at least one origin: no "
        + "ceremony can be accepted from an empty allow-list.");
}

foreach (string allowedOrigin in allowedOrigins)
{
    RequireCeremonyOrigin(allowedOrigin, relyingPartyId);
}

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
// applies the migration and the grant matrix at deploy time, from the deploy pipeline's
// Tools/DbProvision tool on an admin connection, so nothing here runs there — do not "helpfully"
// lift this block out of the Development check. The deployed container is handed exactly one
// connection string, the least-privilege one (see AppHost's publish branch), so lifting this code
// out would not quietly give a request-serving process DDL rights: it would fail at boot on the
// missing admin connection string a few lines below. The absence of the credential is what prevents
// the escalation; this check is what prevents the boot failure.
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
    // from a configuration key of its own: startup sets the role's password to whatever the
    // application is already configured to connect with, so the two cannot drift apart. Do not add
    // a third setting for it. Password auth is the local and test path only — production binds the
    // role to the API's managed identity instead (AttachAppRoleIdentityAsync).
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
    // before they can be applied. The grants script carries no credential — it leaves the role
    // loginable and credential-free — so the second call is what makes the role reachable with the
    // password the application connection string already holds. Without it, every request would fail
    // to connect with 28P01.
    await DatabaseProvisioning.ApplyGrantsAsync(adminConnectionString);
    await DatabaseProvisioning.AttachAppRolePasswordAsync(adminConnectionString, appRolePassword);
}

app.MapDefaultEndpoints();
app.MapAccountEndpoints();
app.MapCurrencyEndpoints();
app.MapTransactionEndpoints();
app.MapPayeeEndpoints();
app.MapCategoryGroupEndpoints();
app.MapCategoryEndpoints();
app.MapPasskeyEndpoints();
app.MapAccountErasureEndpoints();

await app.RunAsync();

// Refuse one configured passkey origin whose form or whose host cannot produce an accepted ceremony.
//
// Three things are checked, and each of them fails silently at runtime if it is not checked here.
//
// Form: an origin is scheme, host and a non-default port — no path, query, fragment or userinfo — and
// the verifier compares it to the string a browser puts in client data, character for character. The
// canonical serialization is compared rather than the parsed parts because Uri normalizes a trailing
// slash away, so a "https://example.com/" that is wrong for our purposes parses into something
// indistinguishable from the value that is right. Comparing against GetLeftPart also catches an
// explicit default port and an uppercased scheme or host, neither of which a browser ever sends.
//
// Scheme: WebAuthn treats an origin as usable only if it is a potentially trustworthy one, which means
// https everywhere except localhost, where plaintext http is allowed — the development configuration
// relies on exactly that exception, so it stays.
//
// Agreement with the relying party id: the browser refuses a ceremony client-side when the calling
// origin's host is neither the relying party id nor a subdomain of it. That refusal never reaches this
// process, so a deployment whose two values drift apart — they arrive from two independent deployment
// parameters — produces empty logs and a sign-in that simply never works.
static void RequireCeremonyOrigin(string allowedOrigin, string relyingPartyId)
{
    const string key = ConfiguredPasskeyCeremonyPolicy.AllowedOriginsKey;

    if (!Uri.TryCreate(allowedOrigin, UriKind.Absolute, out Uri? origin)
        // Uri lowercases the scheme it parsed, so these two literals cover every spelling of it.
        || origin.Scheme is not ("http" or "https"))
    {
        throw new InvalidOperationException(
            $"{key} contains '{allowedOrigin}', which is not an absolute http or https URI: a "
            + "ceremony origin is written in full, as 'https://example.com'.");
    }

    if (origin.UserInfo.Length > 0
        || !string.Equals(allowedOrigin, origin.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"{key} contains '{allowedOrigin}', which is not a serialized origin: an origin is a "
            + "scheme, a host and a non-default port and nothing else — no trailing slash, path, "
            + $"query or fragment. Expected '{origin.GetLeftPart(UriPartial.Authority)}'.");
    }

    if (origin.Scheme is "http"
        && !string.Equals(origin.Host, "localhost", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"{key} contains '{allowedOrigin}', which is plaintext http on a host other than "
            + "localhost: a browser will not run a ceremony from an origin that is not potentially "
            + "trustworthy.");
    }

    if (!string.Equals(origin.Host, relyingPartyId, StringComparison.OrdinalIgnoreCase)
        && !origin.Host.EndsWith($".{relyingPartyId}", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"{key} contains '{allowedOrigin}', whose host is neither "
            + $"'{relyingPartyId}' nor a subdomain of it. "
            + $"{ConfiguredPasskeyCeremonyPolicy.RelyingPartyIdKey} and {key} must describe the same "
            + "site, or the browser refuses every ceremony before the request is made.");
    }
}

// Force TLS on the PostgreSQL connection outside local development. Azure Database for PostgreSQL
// Flexible Server rejects unencrypted connections (28000: no pg_hba.conf entry ... no encryption)
// and enforces TLS server-side, but the connection string the deployed app is handed carries only
// the endpoint details — host, database, and the user, plus a password only where password auth is
// used at all (in production the credential is an Entra token supplied by the Azure enrichment
// above, not a password in the string). SslMode is absent either way, so Npgsql would otherwise
// attempt an unencrypted connection. Rebuild the string with SslMode=Require, which (Npgsql 8+)
// encrypts without validating the server certificate, so Azure's cert chain need not be in the
// chiseled container's trust store.
//
// Development is deliberately left untouched: the local Aspire and Testcontainers PostgreSQL images
// have no TLS configured, and SslMode=Require against them fails with "No SSL enabled connection
// from this host is configured." A null connection string is returned unchanged so the null case
// preserves the existing fail-later behavior.
//
// Two Npgsql options are now forbidden in any connection string this reaches, because both budget
// isolation and user isolation are enforced by session settings (see SessionContextInterceptor).
// `No Reset On Close=true` would keep a returned connection's app.current_budget_id and
// app.current_user_id, making the pool reset — now a security control, not a hygiene one — stop
// clearing one tenant's budget and one person's identity before the next borrower.
// `Multiplexing=true` interleaves logical sessions over one physical connection, which no
// session-setting design can survive at all. Two settings now ride on this, so flipping either
// option leaks tenancy and identity rather than tenancy alone.
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
