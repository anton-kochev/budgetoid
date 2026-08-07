using Api.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IntegrationTests;

/// <summary>
/// Hosts the API over a test database under <b>two</b> identities, mirroring how the deployed
/// application is configured: <c>ConnectionStrings:budgetoid</c> is the least-privilege
/// application role that serves every request, and <c>ConnectionStrings:budgetoid-admin</c> is
/// the elevated account used only by the Development-startup block to migrate and to provision
/// the role's grants. Splitting them is what lets the API suite prove the grant matrix is
/// sufficient rather than merely correct.
/// </summary>
/// <param name="appConnectionString">
/// Connection string the application serves requests on — the least-privilege role.
/// </param>
/// <param name="adminConnectionString">
/// Elevated connection string for startup migration and provisioning. Declared last, and
/// optional, so existing positional call sites keep compiling and so a <c>string?</c> subject
/// can never slide into it by accident. When omitted it falls back to
/// <paramref name="appConnectionString" />, which suits the tests that run in Production
/// (no startup migration, so no database is touched at all).
/// </param>
public sealed class ApiFactory(
    string appConnectionString,
    string? defaultSubject = "test-subject",
    string environment = "Development",
    IReadOnlyDictionary<string, string?>? settings = null,
    Action<IServiceCollection>? configureServices = null,
    string? adminConnectionString = null) : WebApplicationFactory<Program>
{
    /// <summary>
    /// The relying party every host built here answers as. A real domain label rather than a made-up
    /// one, because it is hashed into every credential the synthetic authenticator produces: a test
    /// signing for one relying party against a host configured for another would fail on the hash and
    /// say nothing about the check it meant to exercise.
    /// </summary>
    public const string PasskeyRelyingPartyId = "localhost";

    /// <summary>
    /// The single entry of the origin allow-list. Exposed so a ceremony's client data and the host's
    /// configuration cannot drift apart — the origin is what the verifier compares, and a test that
    /// typed its own copy would pass while the two agreed by accident.
    /// </summary>
    public const string PasskeyOrigin = "https://localhost";

    /// <summary>
    /// Boots the host one caller at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Building this host runs the API's Development startup block, which runs
    /// <c>app-role-grants.sql</c>, whose first statement writes the <c>pg_authid</c> tuple for
    /// <c>budgetoid_app</c>. A role is a cluster-level object, so under the assembly's one shared
    /// PostgreSQL cluster that is a single tuple every test in the suite writes. Two concurrent
    /// writers of it do not queue; they fail with <c>XX000 tuple concurrently updated</c>, measured
    /// at 4-, 8- and 16-way concurrency to lose all but one caller every time.
    /// </para>
    /// <para>
    /// The gate is here, and not on either test host, because this is the only object every boot
    /// passes through. <c>PostgresTestHost</c> builds one; <c>PasskeyCeremonyTests</c> builds one
    /// straight over a <c>RepositoryTestHost</c>, through no host seam at all. A gate on the hosts
    /// leaves that class racing, and a gate on both the host and here deadlocks — the semaphore is
    /// not reentrant.
    /// </para>
    /// </remarks>
    protected override IHost CreateHost(IHostBuilder builder) =>
        SharedPostgresCluster.UnderRoleGate(() => base.CreateHost(builder));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration(configuration =>
        {
            Dictionary<string, string?> values = new()
            {
                ["ConnectionStrings:budgetoid"] = appConnectionString,
                ["ConnectionStrings:budgetoid-admin"] = adminConnectionString ?? appConnectionString,
                ["Authentication:Google:ClientId"] = "test-client-id",

                // Supplied for every environment, not only the ones that run a ceremony. The relying
                // party id and the origin allow-list are values the application refuses to invent, so a
                // host that came up without them is a host a boot-time check may legitimately refuse —
                // and a factory that only supplied them in Development would make that check look like
                // a Production-only regression the day it moves into Program.cs. A caller overrides
                // either through the settings dictionary below, including to the empty string, which is
                // how the refusals themselves stay reachable from a test.
                [ConfiguredPasskeyCeremonyPolicy.RelyingPartyIdKey] = PasskeyRelyingPartyId,
            };

            // The allow-list default is dropped whole the moment a caller names any key beneath it,
            // rather than being overridden entry by entry like everything else here. An array cannot
            // be emptied by overriding an element: a "…:0" entry bound to null is still one element,
            // so the section still binds to a one-item array and still reads as configured. The only
            // shape that produces the empty list the boot guard exists to refuse is no element keys at
            // all, which means the default cannot be added in the first place.
            bool callerSuppliesAllowedOrigins = settings?.Keys.Any(key =>
                key.StartsWith(ConfiguredPasskeyCeremonyPolicy.AllowedOriginsKey, StringComparison.Ordinal)) is true;
            if (!callerSuppliesAllowedOrigins)
            {
                values[$"{ConfiguredPasskeyCeremonyPolicy.AllowedOriginsKey}:0"] = PasskeyOrigin;
            }

            // Applied after the defaults so a caller can still override either connection string
            // key — including pointing the application back at the admin account to isolate a
            // failure to privileges rather than to the change under test.
            if (settings is not null)
            {
                foreach ((string key, string? value) in settings)
                {
                    values[key] = value;
                }
            }

            configuration.AddInMemoryCollection(values);
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Runs last so a caller can replace anything the application registered, including the
            // test authentication above. Tests that need to fail a specific collaborator swap it here
            // rather than constructing a handler by hand, which would couple them to its constructor.
            configureServices?.Invoke(services);
        });
    }

    /// <summary>
    /// One route that is allowed to bring an account into existence. Named so that a test needing an
    /// account and a test asserting which routes may mint one cannot drift apart.
    /// </summary>
    public const string AccountProvisioningPath = "/api/accounts";

    /// <summary>
    /// Makes the one request that mints an account, so a test whose subject has never been seen can go
    /// on to call a route that no longer mints one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately <b>not</b> folded into <see cref="CreateAuthenticatedClient" />, and it must not be:
    /// provisioning is opt-in per route group now, and a client that quietly provisioned itself on
    /// construction would hide that rule from every test in the suite — including the tests whose whole
    /// subject is that <c>/api/me/*</c> and <c>/api/passkeys/*</c> mint nothing. Every caller writes
    /// this line, and writing it is what keeps the rule in sight.
    /// </para>
    /// <para>
    /// Fails loudly on any non-success status. A silent no-op here would leave the caller's real test
    /// measuring an account that was never created, which for a refusal test reads as a pass.
    /// </para>
    /// </remarks>
    public static async Task EstablishAccountAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.GetAsync(AccountProvisioningPath);
        response.EnsureSuccessStatusCode();
    }

    /// <param name="emailVerified">
    /// Raw value for the <c>email_verified</c> claim. Declared last, and optional, so existing
    /// positional call sites keep compiling. When omitted the handler emits its own default.
    /// </param>
    public HttpClient CreateAuthenticatedClient(string? subject = null, string? email = null, string? emailVerified = null)
    {
        HttpClient client = CreateSubjectClient(subject, out string resolvedSubject);
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, email ?? $"{resolvedSubject}@example.com");
        if (emailVerified is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.EmailVerifiedHeader, emailVerified);
        }

        return client;
    }

    public HttpClient CreateAuthenticatedClientWithoutEmail(string? subject = null)
    {
        HttpClient client = CreateSubjectClient(subject, out _);
        client.DefaultRequestHeaders.Add(TestAuthHandler.OmitEmailHeader, "true");
        return client;
    }

    public HttpClient CreateAuthenticatedClientWithUnverifiedEmail(string? subject = null) =>
        CreateAuthenticatedClient(subject, emailVerified: "false");

    public HttpClient CreateAuthenticatedClientWithoutEmailVerifiedClaim(string? subject = null)
    {
        HttpClient client = CreateSubjectClient(subject, out string resolvedSubject);
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, $"{resolvedSubject}@example.com");
        client.DefaultRequestHeaders.Add(TestAuthHandler.OmitEmailVerifiedHeader, "true");
        return client;
    }

    private HttpClient CreateSubjectClient(string? subject, out string resolvedSubject)
    {
        resolvedSubject = subject ?? defaultSubject ?? "test-subject";
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, resolvedSubject);
        return client;
    }
}
