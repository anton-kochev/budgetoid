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
/// <b>Every test in this file is green the day it is written, and that is the point.</b> The gates live
/// in <see cref="UserProvisioningMiddleware" /> today, above the resolve and above the arm that lets a
/// registration request through, so the two <c>/api/registration/*</c> routes are already subject to
/// them. What is not green on arrival is the commit after next: the middleware is deleted, and every
/// test here goes red unless whatever replaces it refuses the same three principals with the same two
/// sentences. Held at the registration route rather than at a budget route because that is where the
/// cost of losing a gate is paid — an account created under an address nobody verified, from a token
/// that says so.
/// </para>
/// <para>
/// <b>Moved in substance from <c>AuthenticationTests</c></b>, which drives the same three gates at
/// <c>/api/transactions</c> and is deleted whole in a later commit. Both files existing at once is
/// deliberate: this one has to be shown to pass before the other can go. Where a case there carried an
/// argument for why it exists, the argument moves with it rather than being paraphrased.
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
            .IsEqualTo(UserProvisioningMiddleware.MissingClaimsTitle);
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
            .IsEqualTo(UserProvisioningMiddleware.MissingClaimsTitle);
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
            .IsEqualTo(UserProvisioningMiddleware.UnverifiedEmailTitle);
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
            .IsEqualTo(UserProvisioningMiddleware.UnverifiedEmailTitle);
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
            .IsEqualTo(UserProvisioningMiddleware.UnverifiedEmailTitle);
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
            .IsEqualTo(UserProvisioningMiddleware.UnverifiedEmailTitle);

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
    /// above the conflict, and what holds it there today is only that it lives in a middleware the next
    /// commit deletes.
    /// </para>
    /// <para>
    /// <b>The finish leg, over a live challenge, and neither half is optional.</b> The options leg runs
    /// no conflict check at all, so a refusal measured there is 401 against a leg that could never have
    /// answered 409 — the ordering would be untested and the test would read as if it held it. The nonce
    /// is minted while the claim is still verified and answered by a real device, so the request clears
    /// the ladder's fourth rung: over an unissued challenge a gate-less path would answer 400 and the
    /// conflict would stay out of reach for a second reason.
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

        // Arrange — a live nonce and a real device answering it, so the second attempt is refused by a
        // rule about the principal rather than by the challenge.
        IssuedRegistrationOptions second = await RegistrationCeremony.BeginAsync(verified);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        AttestationResult attestation = device.Register(
            second.Challenge,
            ApiFactory.PasskeyOrigin,
            signCount: 0,
            prfEnabled: true,
            userHandle: second.UserHandle);

        // Act — the same subject, now carrying an assertion the provider does not make.
        HttpClient unverified = factory.CreateAuthenticatedClient(Subject, Email, emailVerified: "false");
        HttpResponseMessage refused = await RegistrationCeremony.PostAsync(
            unverified,
            attestation,
            WrappedKeyFixture.Mint(),
            RegistrationCeremony.CardOf(RegistrationCeremony.Verifiers()));

        // Assert — the gate's 401, and therefore not the conflict's 409.
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await TitleOfAsync(refused))
            .IsEqualTo(UserProvisioningMiddleware.UnverifiedEmailTitle);

        // Act, again — the identical attestation from a principal the provider does vouch for, and the
        // control this test cannot do without: it is what shows the refusal above overtook a conflict
        // that was standing there the whole time. Without it the test passes on an arrangement that could
        // never have reached the 409 it claims to have pre-empted. The nonce is still live because the
        // gate refuses above the rung that spends it, which is the same ordering said a second way.
        HttpResponseMessage conflicted = await RegistrationCeremony.PostAsync(
            verified,
            attestation,
            WrappedKeyFixture.Mint(),
            RegistrationCeremony.CardOf(RegistrationCeremony.Verifiers()));

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
    /// Deliberately <b>not</b> extended to <c>NoAccountTitle</c>, which a later commit deletes with the
    /// middleware. A test naming it would have to be edited in that commit, and an assertion edited in
    /// the same commit as the thing it constrains holds nothing.
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
        string missingClaims = UserProvisioningMiddleware.MissingClaimsTitle;
        string unverifiedEmail = UserProvisioningMiddleware.UnverifiedEmailTitle;
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
