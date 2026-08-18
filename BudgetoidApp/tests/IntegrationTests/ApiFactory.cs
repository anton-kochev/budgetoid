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
/// <param name="usesApplicationAuthentication">
/// Leaves the application's own authentication defaults standing, instead of installing
/// <see cref="TestAuthHandler" /> as the default authenticate and challenge scheme. Declared last,
/// and optional, so every existing positional call site keeps compiling and keeps behaving exactly
/// as it does today.
/// </param>
/// <param name="repointsProviderSchemeToTestHandler">
/// Makes the identity provider's bearer scheme answer with <see cref="TestAuthHandler" /> inside this
/// host, so a route whose own policy <b>names</b> that scheme can be driven from a test. Declared last,
/// and optional, for the same reason as every parameter above it.
/// </param>
/// <remarks>
/// <para>
/// <b>Why <paramref name="usesApplicationAuthentication" /> exists.</b> The block below does not
/// <em>add</em> a scheme beside the application's — it names <see cref="TestAuthHandler" /> as the
/// <c>DefaultAuthenticateScheme</c> and the <c>DefaultChallengeScheme</c>, which is what makes a
/// header-carrying client authenticate at all. That default is also what a request presenting a
/// first-party session cookie would be authenticated by: the cookie handler would never be asked,
/// so the cookie could not be read on any request in this suite, however correct the production
/// code was. A factory that forces a test scheme as the default therefore makes cookie
/// authentication <em>structurally</em> unreachable, and a test that 401s for that reason proves
/// nothing about the feature it was written for — it reports the factory's own configuration back
/// to itself.
/// </para>
/// <para>
/// It is opt-in rather than the other way round because the whole existing suite authenticates
/// through the test scheme, and a flag that changed the default would move every one of those tests
/// onto a path they were never written against.
/// </para>
/// <para>
/// <b>Why <paramref name="repointsProviderSchemeToTestHandler" /> exists, and why naming the default
/// scheme is not enough.</b> Naming <see cref="TestAuthHandler" /> as the default decides who
/// authenticates a request that reaches <c>UseAuthentication()</c> — and nothing else.
/// <c>AuthorizationMiddleware</c> <b>re-authenticates</b> against the schemes a policy names and
/// replaces <c>HttpContext.User</c> with the result, so a route carrying
/// <c>AddAuthenticationSchemes(ProviderAuthentication.SchemeName)</c> is answered by the real Google
/// <c>JwtBearer</c> handler however the default is set. That handler sees a test subject header and no
/// <c>Authorization</c> header, returns <c>NoResult</c>, and the route answers 401 — with no header a
/// test can set changing it. Repointing the <b>scheme map's handler type</b> is what puts the test
/// handler on the far side of that second authentication.
/// </para>
/// <para>
/// It is indexed rather than looked up with <c>TryGetValue</c> on purpose: a scheme name this host does
/// not register is a fixture that has drifted from <c>Program.cs</c>, and a silent no-op there would
/// leave every test using the flag answering 401 for a reason no assertion names.
/// </para>
/// <para>
/// Measured before it was written, on a host of this shape outside the suite: with the flag off a
/// route whose policy names the provider scheme answers <c>401</c>, and with it on the same route
/// answers <c>200</c> carrying the test principal — so the post-configuration does land before
/// <c>AuthenticationSchemeProvider</c> reads the map.
/// </para>
/// </remarks>
public sealed class ApiFactory(
    string appConnectionString,
    string? defaultSubject = "test-subject",
    string environment = "Development",
    IReadOnlyDictionary<string, string?>? settings = null,
    Action<IServiceCollection>? configureServices = null,
    string? adminConnectionString = null,
    bool usesApplicationAuthentication = false,
    bool repointsProviderSchemeToTestHandler = false) : WebApplicationFactory<Program>
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
            // Skipped whole rather than registered-and-overridden: the scheme itself is harmless, and
            // it is naming it as the default that takes the application's own handlers off the path.
            // See the remarks on usesApplicationAuthentication.
            if (!usesApplicationAuthentication)
            {
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            }

            // Only meaningful beside the block above, which is what registers TestAuthHandler in the
            // container at all: the handler the scheme map names is resolved from services before
            // ActivatorUtilities is reached, and a type nothing registered would be constructed per
            // request instead. Both flags on at once is therefore a combination no caller should ask
            // for, and none does.
            if (repointsProviderSchemeToTestHandler)
            {
                services.Configure<AuthenticationOptions>(options =>
                    options.SchemeMap[ProviderAuthentication.SchemeName].HandlerType = typeof(TestAuthHandler));
            }

            // Runs last so a caller can replace anything the application registered, including the
            // test authentication above. Tests that need to fail a specific collaborator swap it here
            // rather than constructing a handler by hand, which would couple them to its constructor.
            configureServices?.Invoke(services);
        });
    }

    /// <summary>
    /// Makes every client this factory hands out a first-party one, by naming itself in the header the
    /// application's CSRF control requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it is here and not in the <c>CreateAuthenticatedClient*</c> helpers.</b> Most of the suite
    /// never calls one — it calls <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()" />
    /// directly, and a client without the header is answered <c>403</c> before it reaches the route it
    /// was written about. This is the one seam every client the factory produces passes through,
    /// whichever helper built it, so it is the only place that can carry the rule for all of them.
    /// <c>base.ConfigureClient</c> runs first because it is what sets <c>BaseAddress</c> and everything
    /// else the harness expects; adding the header is the whole of what is added on top.
    /// </para>
    /// <para>
    /// <b>The trap: this makes the control invisible to the entire suite.</b> Every test here now sends
    /// the header without ever mentioning it, so no test can fail because the control is missing — which
    /// is precisely why <see cref="FirstPartyRequestTests" /> removes it again on its own clients rather
    /// than trusting that the factory adds nothing. That removal is not defensive noise, and the two
    /// lines are a pair: delete this override and the whole suite starts answering <c>403</c>; delete the
    /// removal there and the three tests that exist to withhold the header start sending it and stay
    /// green while proving the opposite of what they claim.
    /// </para>
    /// <para>
    /// The name and value are taken from <see cref="FirstPartyRequestTests" /> rather than typed again.
    /// One wire value with two spellings in one assembly is a disagreement waiting to happen, and the
    /// disagreement would read as the control working.
    /// </para>
    /// </remarks>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add(
            FirstPartyRequestTests.ClientHeader,
            FirstPartyRequestTests.ClientHeaderValue);
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
    /// <param name="extraClaims">
    /// Further claims the provider asserts beside the three this product reads — the <c>name</c>,
    /// <c>picture</c> and <c>locale</c> a real token carries. Declared last, and optional, for the same
    /// reason as every parameter above it. See <see cref="TestAuthHandler.ExtraClaimHeader" /> for why a
    /// test asserting that a claim is <em>not</em> stored has to be able to send one.
    /// </param>
    public HttpClient CreateAuthenticatedClient(
        string? subject = null,
        string? email = null,
        string? emailVerified = null,
        IReadOnlyDictionary<string, string>? extraClaims = null)
    {
        HttpClient client = CreateSubjectClient(subject, out string resolvedSubject);
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, email ?? $"{resolvedSubject}@example.com");
        if (emailVerified is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.EmailVerifiedHeader, emailVerified);
        }

        if (extraClaims is not null)
        {
            foreach ((string type, string value) in extraClaims)
            {
                client.DefaultRequestHeaders.Add(TestAuthHandler.ExtraClaimHeader, $"{type}={value}");
            }
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
