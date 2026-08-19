using Api.Infrastructure;
using Application.Registration;
using Domain.Sessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TestSupport;

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
/// <b>Both flags at once is a supported combination, and <see cref="RegisterAccountAsync" /> is what
/// asks for it.</b> Registering an account needs a test principal on the provider scheme — nothing else
/// authenticates <c>/api/registration/*</c>, whose policy names that scheme and nothing else — and the
/// session the finish leg hands back is a cookie, which only the application's own handler can read. The
/// two flags therefore had to stop being one decision: <see cref="TestAuthHandler" /> is <em>registered</em>
/// whenever either flag wants it, and named as the <c>DefaultAuthenticateScheme</c> only by
/// <paramref name="usesApplicationAuthentication" /> being off. A host with both on answers a
/// header-carrying client on the provider scheme and a cookie-carrying one on the cookie scheme, which is
/// exactly the pair one registration walks through.
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
            // Registering the scheme and naming it as the default are two decisions, split because the
            // two flags need them in different combinations. Registration alone is harmless — a scheme
            // nothing selects answers nothing — and it is naming it as the default that takes the
            // application's own handlers off the path. The repointing block below needs the type in the
            // container whether or not the default moves, because the handler a scheme map names is
            // resolved from services before ActivatorUtilities is reached.
            if (!usesApplicationAuthentication || repointsProviderSchemeToTestHandler)
            {
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            }

            // AddAuthentication(Action<AuthenticationOptions>) is AddAuthentication() followed by
            // services.Configure(...), so splitting the two lines above and below preserves both the
            // registrations and their order: this Configure still runs after the application's own, and
            // the last one to run decides the default.
            if (!usesApplicationAuthentication)
            {
                services.Configure<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                });
            }

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
    /// An account that has been signed in, and the client already presenting its handle.
    /// </summary>
    /// <remarks>
    /// The two ids ride along because nothing a signed-in request answers names either one, and the
    /// tests that need them today dig them back out of <c>credentials</c> with raw SQL. Handing them
    /// back from the call that wrote them removes that lookup — and with it the risk that a test which
    /// seeded one account reads another's ids because its own query matched two rows.
    /// </remarks>
    public sealed record SignedInClient(HttpClient Client, Guid UserId, Guid BudgetId);

    /// <summary>
    /// Seeds one whole sign-in and hands back a client that already presents its cookie.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The session is seeded rather than established through a route</b>, because the routes that
    /// establish one all require a passkey a synthetic authenticator has to sign for — three extra
    /// requests, a device, and a ceremony, for a test whose subject is somewhere else entirely.
    /// <see cref="RegisterAccountAsync" /> is the other end of that trade and exists for the tests that
    /// really are about the ceremony.
    /// </para>
    /// <para>
    /// <b>The handle rides on <c>DefaultRequestHeaders</c> rather than in a cookie container</b>, and the
    /// difference is not stylistic. The cookie this application issues is <c>Secure</c> and
    /// <c>__Host-</c> prefixed, and a <see cref="System.Net.CookieContainer" /> filled from a
    /// <c>Set-Cookie</c> would refuse to send it back over the <c>http://localhost</c> the test server
    /// answers on — silently, as a request carrying no cookie at all, which reads as a broken
    /// authentication path. Nothing in this assembly uses a cookie container today; a caller that
    /// reaches for one should expect exactly that failure and should keep sending the header instead.
    /// </para>
    /// </remarks>
    /// <param name="subject">
    /// The provider subject the seeded federated credential carries. It reaches no request here — the
    /// cookie decides who is asking — but a test that also looks the account up by subject wants to name
    /// it, and every test in the suite that does so today looks it up that way.
    /// </param>
    /// <param name="email">The address the account is created with; defaults as a bearer client's does.</param>
    /// <param name="kind">
    /// Which kind of session to open, which decides which credential opens it: a passkey for
    /// <see cref="SessionKind.Full" />, the account's federated credential for
    /// <see cref="SessionKind.Locked" />. The seeding throws if the domain derives the other one.
    /// </param>
    public async Task<SignedInClient> CreateSignedInClientAsync(
        string? subject = null,
        string? email = null,
        SessionKind kind = SessionKind.Full,
        CancellationToken cancellationToken = default)
    {
        RequireApplicationAuthentication(nameof(CreateSignedInClientAsync));

        string resolvedSubject = subject ?? defaultSubject ?? "test-subject";
        RepositoryTestHost.SignedInOwner owner = await RepositoryTestHost.SeedSignedInOwnerOnAsync(
            SeedingConnectionString,
            resolvedSubject,
            email ?? $"{resolvedSubject}@example.com",
            kind,
            cancellationToken);

        return new SignedInClient(
            CreateCookieClient(Base64UrlText.Encode(owner.SessionToken)),
            owner.UserId,
            owner.BudgetId);
    }

    /// <summary>
    /// Registers a whole account through both real legs of the ceremony and hands back the client the
    /// <c>Set-Cookie</c> left behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Needs both authentication flags on.</b> The two <c>/api/registration</c> routes declare a
    /// policy naming the provider scheme and nothing else, so the options leg has to be driven by a
    /// principal on that scheme — which is what <c>repointsProviderSchemeToTestHandler</c> arranges — and
    /// the session the finish leg hands back is a cookie only the application's own handler can read.
    /// </para>
    /// <para>
    /// <b>The account identifier is derived, never read back.</b> <see cref="RegistrationAccountId.For" />
    /// over the options leg's own challenge is what the two legs agree on, and reading <c>users</c>
    /// instead would hand back whatever identifier the finish leg chose for itself — which is the one
    /// failure the derivation exists to make impossible. The budget is read, because registration writes
    /// exactly one and nothing derives it.
    /// </para>
    /// </remarks>
    public async Task<SignedInClient> RegisterAccountAsync(
        string? subject = null,
        string? email = null,
        CancellationToken cancellationToken = default)
    {
        RequireApplicationAuthentication(nameof(RegisterAccountAsync));
        if (!repointsProviderSchemeToTestHandler)
        {
            throw new InvalidOperationException(
                $"{nameof(RegisterAccountAsync)} drives '{RegistrationCeremony.OptionsPath}', whose "
                + "policy names the identity provider's scheme and nothing else — so the real JwtBearer "
                + "handler answers, sees no Authorization header and refuses with 401. Build the factory "
                + "with repointsProviderSchemeToTestHandler: true.");
        }

        using HttpClient provider = CreateAuthenticatedClient(subject, email);
        RegistrationCeremonyResult registered = await RegistrationCeremony.RegisterAsync(
            provider,
            SyntheticAuthenticator.CreateEs256(PasskeyRelyingPartyId));
        await RegistrationCeremony.EnsureOkAsync(registered.Response);

        return new SignedInClient(
            CreateCookieClient(RegistrationCeremony.SessionCookieValueOf(registered.Response)),
            registered.AccountId,
            await SoleBudgetIdAsync(registered.AccountId, cancellationToken));
    }

    /// <summary>
    /// A client presenting <paramref name="cookieValue" /> as its session handle on every request.
    /// </summary>
    /// <remarks>
    /// The cookie's name comes from <see cref="SessionCookieAuthenticationTests.CookieName" /> rather
    /// than being typed again, for the reason <see cref="ConfigureClient" /> gives about the client
    /// header: one wire value with two spellings in one assembly is a disagreement waiting to happen.
    /// That constant is itself a literal rather than <c>SessionCookie.Name</c>, deliberately, and the
    /// argument for that is stated where it is declared.
    /// </remarks>
    private HttpClient CreateCookieClient(string cookieValue)
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", $"{SessionCookieAuthenticationTests.CookieName}={cookieValue}");

        return client;
    }

    /// <summary>
    /// Refuses a cookie-carrying client on a host that named <see cref="TestAuthHandler" /> as its
    /// default scheme.
    /// </summary>
    /// <remarks>
    /// Such a host never asks the cookie handler anything, so every request the returned client made
    /// would answer 401 with nothing in the response, the log or the assertion naming the cause — the
    /// exact failure mode the remarks on <c>usesApplicationAuthentication</c> describe. Naming the fix in
    /// the message is the whole point of throwing here rather than letting the requests fail.
    /// </remarks>
    private void RequireApplicationAuthentication(string member)
    {
        if (usesApplicationAuthentication)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{member} hands out a client that authenticates from the session cookie, and this factory "
            + $"names {TestAuthHandler.SchemeName} as the default authenticate scheme — so the cookie "
            + "handler is never asked and every request would answer 401 for a reason no assertion "
            + "names. Build the factory with usesApplicationAuthentication: true; through "
            + $"{nameof(PostgresTestHost)}, pass it to the host's constructor.");
    }

    /// <summary>
    /// The one budget <paramref name="userId" /> owns, read on the seeding connection.
    /// </summary>
    /// <remarks>
    /// Refuses anything but exactly one row. A registration writes one budget, so two means this account
    /// was reached twice and a caller taking "the first" would be scoped to whichever came back first;
    /// none means the registration wrote nothing and every later assertion is about an account that does
    /// not exist.
    /// </remarks>
    private async Task<Guid> SoleBudgetIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(SeedingConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(
            "select id from budgets where user_id = @userId", connection);
        command.Parameters.AddWithValue("userId", userId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException($"No budget is filed under account '{userId}'.");
        }

        Guid budgetId = reader.GetGuid(0);

        return await reader.ReadAsync(cancellationToken)
            ? throw new InvalidOperationException(
                $"Account '{userId}' owns more than one budget; this lookup assumes exactly one.")
            : budgetId;
    }

    /// <summary>
    /// The connection every seeding and read-back here goes over: the elevated account, as all the
    /// existing seeding uses.
    /// </summary>
    /// <remarks>
    /// The application role is least-privilege on purpose — it holds no <c>INSERT</c> on half these
    /// tables and is policed on the rest — and none of this is what a test is measuring. It falls back
    /// to the application's own string for the same reason the constructor's parameter does.
    /// </remarks>
    private string SeedingConnectionString => adminConnectionString ?? appConnectionString;

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
