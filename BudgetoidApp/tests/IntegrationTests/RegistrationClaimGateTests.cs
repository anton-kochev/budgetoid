using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// The three claim gates a provider token has to pass before an account can be created, driven at the
/// route that creates one.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file was written green against a middleware and stayed green when the middleware went, and
/// that transition is what it was for.</b> The gates lived in a provisioning middleware, above the
/// resolve and above the arm that let a registration request through; they now live in
/// <see cref="RegistrationClaimGate" />, an endpoint filter on the <c>/api/registration</c> group.
/// Every test here was carried across untouched but for the two constants it pins, which is the
/// evidence that the replacement refuses the same three principals with the same two sentences.
/// Held at the registration route rather than at a budget route because that is where the cost of
/// losing a gate is paid — an account created under an address nobody verified, from a token that says
/// so — and that choice is now the only choice there is: a provider token reaches no other route.
/// </para>
/// <para>
/// <b>Moved in substance from a deleted <c>AuthenticationTests</c></b>, which drove the same three
/// gates at <c>/api/transactions</c> back when a bearer token reached that route at all. Where a case
/// there carried an argument for why it exists, the argument moved with it rather than being
/// paraphrased.
/// </para>
/// <para>
/// <b>The refusals are read by title, not by status.</b> Every gate on this path answers 401, and so
/// does the route's own policy when the fixture's scheme repointing stops working — a status-only
/// assertion would stay green while measuring the fixture rather than the gate. The routes are
/// authenticated by the identity provider's scheme and by nothing else, which is why
/// <see cref="CreateApiFactory" /> asks for <c>repointsProviderSchemeToTestHandler</c>; see the remarks
/// on <see cref="ApiFactory" /> for why naming the default scheme cannot stand in for it.
/// </para>
/// </remarks>
public sealed class RegistrationClaimGateTests
{
    /// <summary>The Google subject these tests are refused as.</summary>
    private const string Subject = "claim-gate-registering";

    private const string Email = "claim-gate-registering@budgetoid.test";

    /// <summary>
    /// A second principal, used only as the control in <see cref="RegistrationOptions_ReadsTheEmailVerifiedClaimWithBoolTryParse" />.
    /// </summary>
    /// <remarks>
    /// Carried over from <c>AccountRegistrationTests</c>: the subject the control would otherwise reuse
    /// has already been refused on this host, and reusing it would leave the control measuring whether a
    /// refused subject can start a ceremony rather than whether a verified claim is what admits one.
    /// </remarks>
    private const string VerifiedSubject = "claim-gate-registering-verified";

    private const string VerifiedEmail = "claim-gate-registering-verified@budgetoid.test";

    /// <summary>
    /// Address used by the test that asserts nothing was written. Held as a constant so the value sent on
    /// the request and the value counted in the database cannot drift apart.
    /// </summary>
    private const string UnverifiedEmail = "claim-gate-unverified@budgetoid.test";

    private const string OptionsPath = RegistrationCeremony.OptionsPath;

    private const string ProblemMediaType = "application/problem+json";

    /// <summary>
    /// A principal the provider names but reports no address for cannot start a registration.
    /// </summary>
    /// <remarks>
    /// The account being created is reached by its address and by nothing else, so a registration
    /// admitted without one produces a row nobody can be matched to later. Refused by the same sentence
    /// as a missing subject, deliberately: both say the token is unusable, and neither tells the caller
    /// which half of it the server could not read.
    /// </remarks>
    [Test]
    public async Task RegistrationOptions_WithNoEmailClaim_IsRefusedAsMissingClaims()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClientWithoutEmail(Subject);

        // Act
        HttpResponseMessage response = await client.PostAsync(OptionsPath, content: null);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo(ProblemMediaType);
        await Assert.That(await TitleOfAsync(response))
            .IsEqualTo(RegistrationClaimGate.MissingClaimsTitle);
    }

    /// <summary>
    /// A principal carrying no subject cannot start a registration either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half of one gate, and the half no test in <c>AuthenticationTests</c> covered: the
    /// condition there is <c>sub</c> <b>or</b> <c>email</c>, so a rewrite that dropped the subject check
    /// left every test in that file green. The subject is what a later sign-in is matched on, and a
    /// registration admitted without one writes a federated credential keyed on nothing.
    /// </para>
    /// <para>
    /// <b>The claim is taken off the principal rather than off the request</b>, and no header can do it.
    /// <see cref="TestAuthHandler" /> answers a request carrying no subject header with <c>NoResult</c> —
    /// an unauthenticated caller, refused by the route's policy with no title at all, which is a
    /// different refusal and would leave this test measuring authorization instead of the gate. An empty
    /// header value does not reach the handler either: <c>HttpHeaders</c> drops a header whose only value
    /// is empty, so the request arrives with none, and it was measured doing exactly that. What is left
    /// is a claims transformation, which <c>AuthenticationService</c> applies to the ticket every time a
    /// scheme authenticates — including the second authentication <c>AuthorizationMiddleware</c> runs
    /// against the policy's own scheme — producing an authenticated principal with a verified address and
    /// no subject, which is the one state this gate is about.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RegistrationOptions_WithNoSubjectClaim_IsRefusedAsMissingClaims()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(
            host,
            services => services.AddSingleton<IClaimsTransformation, SubjectStrippingTransformation>());
        HttpClient client = factory.CreateAuthenticatedClient(Subject, Email);

        // Act
        HttpResponseMessage response = await client.PostAsync(OptionsPath, content: null);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo(ProblemMediaType);
        await Assert.That(await TitleOfAsync(response))
            .IsEqualTo(RegistrationClaimGate.MissingClaimsTitle);
    }

    /// <summary>
    /// An address the provider explicitly declines to vouch for cannot be registered.
    /// </summary>
    /// <remarks>
    /// A distinct title, not the shared missing-claims one: the two rejections are the same status but
    /// different corrective actions for the caller. Pinned through the middleware's own constant, so the
    /// sentence cannot be reworded in one place and left behind in the other.
    /// </remarks>
    [Test]
    public async Task RegistrationOptions_WithAnEmailNotAssertedVerified_IsRefusedAsUnverified()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClientWithUnverifiedEmail(Subject);

        // Act
        HttpResponseMessage response = await client.PostAsync(OptionsPath, content: null);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo(ProblemMediaType);
        await Assert.That(await TitleOfAsync(response))
            .IsEqualTo(RegistrationClaimGate.UnverifiedEmailTitle);
    }

    /// <summary>
    /// Silence is not consent: a token carrying no <c>email_verified</c> claim at all is refused too.
    /// </summary>
    /// <remarks>
    /// The gate has to fail closed on an absent claim, and absent is the case a truthiness check written
    /// as "not false" admits. It is refused with the unverified sentence rather than the missing-claims
    /// one because the token is readable and the address is simply unvouched for — the claim is read and
    /// never stored, so there is nothing missing that a caller could go and supply.
    /// </remarks>
    [Test]
    public async Task RegistrationOptions_WithNoEmailVerifiedClaim_IsRefusedAsUnverified()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClientWithoutEmailVerifiedClaim(Subject);

        // Act
        HttpResponseMessage response = await client.PostAsync(OptionsPath, content: null);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo(ProblemMediaType);
        await Assert.That(await TitleOfAsync(response))
            .IsEqualTo(RegistrationClaimGate.UnverifiedEmailTitle);
    }

    /// <summary>
    /// The rule is <c>bool.TryParse</c>, and it takes both values to say so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two halves are a pair and neither is optional: <c>"1"</c> is truthy to a provider yet not a
    /// boolean, so it kills a gate that merely checks the value is not <c>"false"</c>; <c>"True"</c>
    /// kills a gate that compares ordinally against <c>"true"</c>. Only <c>bool.TryParse</c> satisfies
    /// both at once. Kept in one method rather than split, because the pairing <em>is</em> the claim —
    /// two separate tests can be half-deleted, and the half left standing reads as a complete rule.
    /// </para>
    /// <para>
    /// The admitted half runs second, on a subject of its own, and asserts 200 rather than a title: an
    /// options leg that answers is the only evidence that the refusal above came from the claim and not
    /// from the route being unreachable in this fixture.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RegistrationOptions_ReadsTheEmailVerifiedClaimWithBoolTryParse()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient truthy = factory.CreateAuthenticatedClient(Subject, Email, emailVerified: "1");
        HttpClient mixedCase = factory.CreateAuthenticatedClient(
            VerifiedSubject,
            VerifiedEmail,
            emailVerified: "True");

        // Act
        HttpResponseMessage refused = await truthy.PostAsync(OptionsPath, content: null);
        HttpResponseMessage admitted = await mixedCase.PostAsync(OptionsPath, content: null);

        // Assert
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await TitleOfAsync(refused))
            .IsEqualTo(RegistrationClaimGate.UnverifiedEmailTitle);
        await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    /// A registration refused at the claim gate leaves no <c>users</c> row behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A real database, because the claim this test makes is about what was written, not about what came
    /// back. A 401 with an account already provisioned behind it would pass every status and title
    /// assertion above and still have leaked the address into storage.
    /// </para>
    /// <para>
    /// The <b>finish</b> leg is the one driven, not the options leg, and that is what makes the count
    /// evidence rather than description: the options leg writes no <c>users</c> row even when it
    /// succeeds, so a refusal measured there is zero against a route that never writes one. This request
    /// is the genuine article in every other respect — a real device, real envelopes, a full card — over
    /// a challenge this server never issued, which it has to be, because the options leg that would have
    /// issued one is refused by the same gate.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_RefusedAtTheClaimGate_WritesNoUserRow()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClient(
            Subject,
            UnverifiedEmail,
            emailVerified: "false");
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act
        HttpResponseMessage finish = await RegistrationCeremony.RegisterOverAsync(
            client,
            device,
            UnissuedChallenge());

        // Assert
        await Assert.That(finish.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await TitleOfAsync(finish))
            .IsEqualTo(RegistrationClaimGate.UnverifiedEmailTitle);

        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await Assert.That(await CountUsersByEmailAsync(connection, UnverifiedEmail)).IsEqualTo(0L);
    }

    /// <summary>
    /// A subject the system already knows is gated before it is told that it is known.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The claim is an ordering, and both answers are plausible.</b> This request would be refused
    /// either way — 401 at the claim gate, or 409 at the conflict — and the 409 is the one that leaks:
    /// it says "this Google account is already registered" to somebody whose address nobody vouched for,
    /// which confirms that an account exists on the strength of a claim the server has just declined to
    /// accept as evidence, one refusal before anything at all has been verified. The gate has to sit
    /// above the conflict, and what holds it there is that
    /// <see cref="RegistrationClaimGate" /> is an endpoint filter on the <c>/api/registration</c> group:
    /// a filter runs after model binding and before the delegate, so it precedes every handler on either
    /// leg by construction.
    /// </para>
    /// <para>
    /// <b>The options leg, and it moved there because that leg grew the conflict this test is about.</b>
    /// It used to be arranged on the finish leg over a live challenge, for a reason that has since
    /// expired: the options leg ran no conflict check at all, so a 401 measured there was a refusal
    /// against a leg that could never have answered 409, and the ordering would have been untested while
    /// reading as if it were held. That leg now asks <c>credentials</c> about the subject <em>before</em>
    /// it mints a nonce, so it answers the very 409 this gate has to overtake — and the same principal
    /// meeting the same route is a sharper statement of the ordering than a ceremony was, with no
    /// challenge, no device and no attestation standing between the claim and the answer. It is also
    /// where the leak now costs most: on the options leg the 409 is the <em>first</em> thing an
    /// unverified caller could be told, before any authenticator has been asked for anything.
    /// </para>
    /// <para>
    /// <b>The finish leg's copy of this ordering is no longer arranged anywhere, and that is stated
    /// rather than hidden.</b> One filter on one group serves both legs, so the two orderings are one
    /// fact with one mechanism, and moving the gate below either handler reddens this test. What is not
    /// covered any more is a change that left the gate above the options handler and put something below
    /// it on the finish leg alone. The finish leg still has its own gate test —
    /// <see cref="Registration_RefusedAtTheClaimGate_WritesNoUserRow" />, which refuses an unverified
    /// claim above the challenge rung and writes no row — but that arrangement holds no account, so
    /// there is no conflict there for the gate to overtake.
    /// </para>
    /// <para>
    /// The account is created through the route rather than seeded, because the conflict this test is
    /// contrasted with is raised by the credential the route writes. A seeded row would be this test's
    /// own idea of what registration leaves behind, and the ordering would then be measured against a
    /// fixture rather than against the product.
    /// </para>
    /// <para>
    /// Asserted by title, as everywhere here: the status alone separates 401 from 409, but a fixture
    /// whose scheme repointing lapsed answers 401 too, and the title is what tells those apart.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_ForAKnownSubject_WithAnEmailNotAssertedVerified_IsRefusedAsUnverified()
    {
        // Arrange — an account for this subject, created by the route that creates accounts.
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient verified = factory.CreateAuthenticatedClient(Subject, Email);
        RegistrationCeremonyResult account = await RegistrationCeremony.RegisterAsync(
            verified,
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));
        await Assert.That(account.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        // Act — the same subject asking to open a second ceremony, now carrying an assertion the
        // provider does not make. No challenge and no device: this leg reads the principal and then asks
        // `credentials` about the subject, and the whole claim is about the order of those two.
        HttpClient unverified = factory.CreateAuthenticatedClient(Subject, Email, emailVerified: "false");
        HttpResponseMessage refused = await unverified.PostAsync(OptionsPath, content: null);

        // Assert — the gate's 401, and therefore not the conflict's 409.
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await TitleOfAsync(refused))
            .IsEqualTo(RegistrationClaimGate.UnverifiedEmailTitle);

        // Act, again — the identical request from a principal the provider does vouch for, and the
        // control this test cannot do without: it is what shows the refusal above overtook a conflict
        // that was standing there the whole time. Without it the test passes on an arrangement that could
        // never have reached the 409 it claims to have pre-empted — which is exactly what this test was
        // before the options leg learned to answer one.
        HttpResponseMessage conflicted = await verified.PostAsync(OptionsPath, content: null);

        await Assert.That(conflicted.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// The two titles this gate writes and the one a refused passkey writes are three sentences, not two.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each constant is public "so a test can pin it", and until now no test compared any of them with
    /// any other — every caller in the suite asserts one title against one response, which stays green if
    /// two of the three collapse into the same sentence. Collapsed, a caller cannot act on the
    /// distinction: "your token is unusable", "your provider does not vouch for this address" and "that
    /// passkey did not verify" are three different things to go and do, answered by one status code.
    /// </para>
    /// <para>
    /// <b>Three is now the whole population, and it used to be four.</b> A fourth titled 401 —
    /// <c>NoAccountTitle</c>, written by the provisioning middleware when an authenticated principal
    /// named no account — was deliberately left out of this comparison while it existed, on the grounds
    /// that it was about to be deleted and an assertion edited in the same commit as the thing it
    /// constrains holds nothing. That commit has happened. There is no such refusal to be distinct from:
    /// an authenticated request can no longer name an account that does not exist, because the only
    /// credential that authenticates one is a cookie issued over a session row, and a session row is
    /// only ever written beside the account it names. A principal with no account now reaches the
    /// cookie handler, is answered <c>NoResult</c>, and leaves indistinguishable from an anonymous
    /// caller. So the set below is complete rather than pruned, and a fourth titled refusal added to
    /// this path belongs in it.
    /// </para>
    /// <para>
    /// The emptiness check is not redundant with the distinctness one: two empty titles collide and are
    /// caught, but a single empty title is distinct from both its neighbours while telling a caller
    /// nothing at all.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EveryTitleThisGateWrites_IsDistinctFromEveryOtherTitledRefusal()
    {
        // Arrange
        string missingClaims = RegistrationClaimGate.MissingClaimsTitle;
        string unverifiedEmail = RegistrationClaimGate.UnverifiedEmailTitle;
        string passkeyRefused = PasskeyVerificationExceptionHandler.Title;

        // Act
        string[] titles = [missingClaims, unverifiedEmail, passkeyRefused];

        // Assert — pairwise first, so a failure names the two sentences that agreed.
        await Assert.That(missingClaims).IsNotEqualTo(unverifiedEmail);
        await Assert.That(missingClaims).IsNotEqualTo(passkeyRefused);
        await Assert.That(unverifiedEmail).IsNotEqualTo(passkeyRefused);
        await Assert.That(titles.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(titles.Length);
        await Assert.That(titles.Any(string.IsNullOrWhiteSpace)).IsFalse();
    }

    /// <summary>Bytes no options leg on this host ever issued.</summary>
    private static byte[] UnissuedChallenge() => RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// The <c>title</c> of a problem response, or a sentence saying what arrived instead.
    /// </summary>
    /// <remarks>
    /// Returned as text rather than thrown on, so an assertion comparing titles fails carrying the body
    /// that arrived — which on this path is how one 401 is told from another.
    /// </remarks>
    private static async Task<string> TitleOfAsync(HttpResponseMessage response)
    {
        string payload = await response.Content.ReadAsStringAsync();

        return JsonNode.Parse(payload) is JsonObject body && body["title"] is { } title
            ? title.GetValue<string>()
            : $"<no title; body was: {payload}>";
    }

    /// <summary>
    /// Counts <c>users</c> rows for an address with raw Npgsql. EF's escape hatches (<c>Find</c>,
    /// <c>FromSql*</c>) are banned symbols in this solution, and the count has to be taken on the
    /// container's superuser connection anyway so that row-level security cannot make an existing row
    /// look absent.
    /// </summary>
    private static async Task<long> CountUsersByEmailAsync(NpgsqlConnection connection, string email)
    {
        await using NpgsqlCommand command = new("select count(*) from users where email = @email", connection);
        command.Parameters.AddWithValue("email", email);

        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from 'users', got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// Hosts the API over this host's database with the provider scheme answered by the test handler.
    /// </summary>
    /// <remarks>
    /// Built here rather than through <see cref="PostgresTestHost.CreateFactory" />, which exposes no way
    /// to ask for the repointing — the two routes under <c>/api/registration</c> declare a policy naming
    /// the provider scheme and nothing else, and <c>AuthorizationMiddleware</c> re-authenticates against
    /// it whatever the default scheme is.
    /// </remarks>
    /// <param name="configureServices">
    /// Registrations applied after everything the application and the factory made, or null to leave both
    /// standing. Declared last, and optional, for the reason every optional parameter on
    /// <see cref="ApiFactory" /> itself is.
    /// </param>
    private static ApiFactory CreateApiFactory(
        PostgresTestHost host,
        Action<IServiceCollection>? configureServices = null) =>
        new(
            host.AppConnectionString,
            adminConnectionString: host.ConnectionString,
            repointsProviderSchemeToTestHandler: true,
            configureServices: configureServices);

    /// <summary>
    /// Takes the <c>sub</c> claim off an otherwise ordinary provider principal, leaving everything else
    /// it carries — the address, the verified assertion and the authentication type that makes it
    /// authenticated at all — untouched.
    /// </summary>
    /// <remarks>
    /// A new principal rather than a mutation of the one handed in, because a transformation is run more
    /// than once per request and is documented to leave its input alone. Rebuilt from the surviving
    /// claims and the same authentication type, so the result is authenticated: a principal whose
    /// identity has none is anonymous, and an anonymous caller is refused by the policy before this gate
    /// is reached.
    /// </remarks>
    private sealed class SubjectStrippingTransformation : IClaimsTransformation
    {
        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            ArgumentNullException.ThrowIfNull(principal);

            ClaimsIdentity stripped = new(
                principal.Claims.Where(claim => !string.Equals(claim.Type, "sub", StringComparison.Ordinal)),
                principal.Identity?.AuthenticationType ?? TestAuthHandler.SchemeName);

            return Task.FromResult(new ClaimsPrincipal(stripped));
        }
    }

    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
