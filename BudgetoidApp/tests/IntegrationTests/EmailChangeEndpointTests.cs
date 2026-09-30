using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Application.Passkeys;
using Domain.Sessions;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// <c>POST /api/me/email-change</c>: a full session, a fresh passkey assertion, and a provider token
/// whose <c>sub</c>, <c>email</c> and <c>email_verified</c> claims are the only source of the new
/// identity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two principals, never merged.</b> The session cookie authenticates the request on the fallback
/// policy; the provider token is authenticated separately by a route filter. The cookie principal's
/// <c>sub</c> is the <em>account id</em>, so an endpoint that read the subject off
/// <c>HttpContext.User</c> would file a credential under the account's own id and still answer 200 —
/// the first test is the one that catches it.
/// </para>
/// <para>
/// <b>Two ways the provider token is presented, as the registration tests have them.</b> Most tests
/// repoint the provider scheme at <see cref="TestAuthHandler" /> and send its headers, which is how
/// <c>AccountRegistrationTests</c> drives the provider. The bearer-shaped refusals (forged, wrong
/// audience, expired, missing claims) need the real <c>JwtBearer</c> handler holding a test signing
/// key, which is how <c>LogRedactionTests</c> drives it; a header handler cannot be forged.
/// </para>
/// </remarks>
public sealed class EmailChangeEndpointTests
{
    private const string EmailChangePath = "/api/me/email-change";
    private const string MePath = "/api/me";
    private const string ReauthenticationOptionsPath = "/api/passkeys/reauthentication/options";
    private const string RegistrationOptionsPath = "/api/passkeys/registration/options";
    private const string RegistrationPath = "/api/passkeys/registration";

    private const string Subject = "google-changing";
    private const string Email = "changing@example.com";
    private const string NewSubject = "google-new-identity";
    private const string NewEmail = "moved@example.com";
    private const string StrangerSubject = "google-stranger";
    private const string StrangerEmail = "stranger@example.com";

    private const string RefusalMember = "refusal";
    private const string ConflictKindMember = "conflictKind";
    private const string SessionsEndedMember = "sessionsEnded";

    [Test]
    public async Task EmailChange_WithANewSubjectAndAddress_StoresBothFromTheProviderToken()
    {
        // Arrange
        await using PostgresTestHost host = await StartProviderHeaderHostAsync();
        Account account = await SignInWithAPasskeyAsync(host.Factory, Subject, Email);
        FederatedCredential original = (await FederatedCredentialsOfAsync(host, account.UserId)).Single();
        AssertionResult assertion = await ReauthenticateAsync(account);

        // Act
        HttpResponseMessage response = await PostEmailChangeAsync(
            account.Client, BodyOf(assertion), new Provider.Headers(NewSubject, NewEmail));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await SessionsEndedOfAsync(response)).IsEqualTo(0);
        await Assert.That(await EmailOfAsync(host, account.UserId)).IsEqualTo(NewEmail);

        IReadOnlyList<FederatedCredential> after = await FederatedCredentialsOfAsync(host, account.UserId);
        await Assert.That(after.Count).IsEqualTo(1);

        // The trap first: the cookie principal's sub is the account id.
        await Assert.That(after[0].Subject).IsNotEqualTo(account.UserId.ToString());
        await Assert.That(after[0].Subject).IsEqualTo(NewSubject);
        await Assert.That(after[0].Id).IsNotEqualTo(original.Id);
        await Assert.That(await CredentialExistsAsync(host, original.Id)).IsFalse();
    }

    [Test]
    public async Task EmailChange_WithAFullSessionAndNoProviderToken_IsRefused401ProviderToken_AndChangesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartProviderHeaderHostAsync();
        Account account = await SignInWithAPasskeyAsync(host.Factory, Subject, Email);
        AccountState before = await StateOfAsync(host, account.UserId);
        AssertionResult assertion = await ReauthenticateAsync(account);

        // Act — a valid session and a valid assertion, so the missing token is the only fault.
        HttpResponseMessage response = await PostEmailChangeAsync(
            account.Client, BodyOf(assertion), new Provider.Absent());

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await RefusalOfAsync(response)).IsEqualTo("provider_token");
        await Assert.That(await StateOfAsync(host, account.UserId)).IsEqualTo(before);
    }

    [Test]
    [Arguments(BearerFault.ForgedSignature)]
    [Arguments(BearerFault.WrongAudience)]
    [Arguments(BearerFault.Expired)]
    [Arguments(BearerFault.WrongIssuer)]
    public async Task EmailChange_WithAProviderTokenTheBearerHandlerRejects_IsRefused401ProviderToken_AndChangesNothing(
        BearerFault fault)
    {
        // Arrange
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "email-change" };
        await using PostgresTestHost host = await StartRealBearerHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        Account account = await SignInWithAPasskeyAsync(factory, Subject, Email);
        AccountState before = await StateOfAsync(host, account.UserId);
        AssertionResult assertion = await ReauthenticateAsync(account);
        string token = fault switch
        {
            BearerFault.ForgedSignature => WithRandomSignature(ProviderToken(signingKey, Claims(NewSubject, NewEmail))),
            BearerFault.WrongAudience => ProviderToken(
                signingKey, Claims(NewSubject, NewEmail), audience: "another-client-id"),
            BearerFault.Expired => ProviderToken(
                signingKey, Claims(NewSubject, NewEmail), issuedAtUtc: DateTime.UtcNow.AddHours(-3)),
            BearerFault.WrongIssuer => ProviderToken(
                signingKey, Claims(NewSubject, NewEmail), issuer: "https://issuer.attacker.example"),
            _ => throw new ArgumentOutOfRangeException(nameof(fault), fault, null),
        };

        // Act
        HttpResponseMessage response = await PostEmailChangeAsync(
            account.Client, BodyOf(assertion), new Provider.Bearer(token));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await RefusalOfAsync(response)).IsEqualTo("provider_token");
        await Assert.That(await StateOfAsync(host, account.UserId)).IsEqualTo(before);
    }

    [Test]
    public async Task EmailChange_WithAProviderTokenTheBearerHandlerValidates_StoresItsSubjectAndAddress()
    {
        // Arrange — the control for the bearer-shaped refusals: the same real handler, the same key,
        // a token faultless in every respect. Without it, a filter that refused every bearer passes them.
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "email-change" };
        await using PostgresTestHost host = await StartRealBearerHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        Account account = await SignInWithAPasskeyAsync(factory, Subject, Email);
        AssertionResult assertion = await ReauthenticateAsync(account);
        string token = ProviderToken(signingKey, Claims(NewSubject, NewEmail));

        // Act
        HttpResponseMessage response = await PostEmailChangeAsync(
            account.Client, BodyOf(assertion), new Provider.Bearer(token));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await EmailOfAsync(host, account.UserId)).IsEqualTo(NewEmail);
        IReadOnlyList<FederatedCredential> after = await FederatedCredentialsOfAsync(host, account.UserId);
        await Assert.That(after.Count).IsEqualTo(1);
        await Assert.That(after[0].Subject).IsEqualTo(NewSubject);
    }

    [Test]
    public async Task EmailChange_RefusedForItsProviderToken_LeavesThePasskeyChallengeUnspent()
    {
        // Arrange — one assertion, over one live single-use challenge.
        await using PostgresTestHost host = await StartProviderHeaderHostAsync();
        Account account = await SignInWithAPasskeyAsync(host.Factory, Subject, Email);
        AccountState before = await StateOfAsync(host, account.UserId);
        AssertionResult assertion = await ReauthenticateAsync(account);

        // Act — refused for the missing token, then the SAME assertion with a valid token. The
        // provider filter runs before the passkey gate, so the first request never reached the nonce.
        // Were the gate first, the first request would spend it and the second would answer
        // 401 "assertion".
        HttpResponseMessage refused = await PostEmailChangeAsync(
            account.Client, BodyOf(assertion), new Provider.Absent());
        AccountState afterRefusal = await StateOfAsync(host, account.UserId);
        HttpResponseMessage retried = await PostEmailChangeAsync(
            account.Client, BodyOf(assertion), new Provider.Headers(NewSubject, NewEmail));

        // Assert
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await RefusalOfAsync(refused)).IsEqualTo("provider_token");
        await Assert.That(afterRefusal).IsEqualTo(before);

        await Assert.That(await RefusalOfAsync(retried)).IsNotEqualTo("assertion");
        await Assert.That(retried.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await EmailOfAsync(host, account.UserId)).IsEqualTo(NewEmail);
    }

    [Test]
    public async Task EmailChange_WithAnotherAccountsPasskey_IsRefused401Assertion_AndChangesNeitherAccount()
    {
        // Arrange — the challenge is Alice's own; only the credential answering it is Bob's.
        await using PostgresTestHost host = await StartProviderHeaderHostAsync();
        Account alice = await SignInWithAPasskeyAsync(host.Factory, Subject, Email);
        Account bob = await SignInWithAPasskeyAsync(host.Factory, StrangerSubject, StrangerEmail);
        AccountState aliceBefore = await StateOfAsync(host, alice.UserId);
        AccountState bobBefore = await StateOfAsync(host, bob.UserId);
        byte[] challenge = await BeginCeremonyAsync(alice.Client, ReauthenticationOptionsPath);
        AssertionResult assertion = bob.Device.Authenticate(challenge, ApiFactory.PasskeyOrigin, userHandle: null);

        // Act
        HttpResponseMessage response = await PostEmailChangeAsync(
            alice.Client, BodyOf(assertion), new Provider.Headers(NewSubject, NewEmail));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await RefusalOfAsync(response)).IsEqualTo("assertion");
        await Assert.That(await StateOfAsync(host, alice.UserId)).IsEqualTo(aliceBefore);
        await Assert.That(await StateOfAsync(host, bob.UserId)).IsEqualTo(bobBefore);
    }

    [Test]
    public async Task EmailChange_WithAProviderTokenAndNoSession_IsRefused401_AndChangesNothing()
    {
        // Arrange — the assertion is Alice's own, over a live challenge, so only the cookie is missing.
        await using PostgresTestHost host = await StartProviderHeaderHostAsync();
        Account account = await SignInWithAPasskeyAsync(host.Factory, Subject, Email);
        AccountState before = await StateOfAsync(host, account.UserId);
        AssertionResult assertion = await ReauthenticateAsync(account);
        HttpClient anonymous = host.Factory.CreateClient();

        // Act
        HttpResponseMessage response = await PostEmailChangeAsync(
            anonymous, BodyOf(assertion), new Provider.Headers(Subject, NewEmail));

        // Assert — the fallback's own 401, which carries no refusal word: the provider principal
        // authenticated, and it must not stand in for a session.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await RefusalOfAsync(response)).IsNull();
        await Assert.That(await StateOfAsync(host, account.UserId)).IsEqualTo(before);
    }

    [Test]
    public async Task EmailChange_ForALockedSession_IsRefused403_WhileAFullSessionOnTheSameAccountSucceeds()
    {
        // Arrange — two live sessions on ONE account: the locked one opened by its federated
        // credential, the full one by a passkey. One account, so only the session kind differs.
        await using PostgresTestHost host = await StartProviderHeaderHostAsync();
        Account account = await SignInWithAPasskeyAsync(host.Factory, Subject, Email);
        HttpClient locked = await OpenLockedSessionAsync(host, account.UserId, fill: 0x11);
        AccountState before = await StateOfAsync(host, account.UserId);

        // A locked session cannot mint a re-authentication challenge, so the full one mints it; the
        // assertion is valid, and only the session sending it differs.
        AssertionResult lockedAssertion = await ReauthenticateAsync(account);

        // Act
        HttpResponseMessage lockedResponse = await PostEmailChangeAsync(
            locked, BodyOf(lockedAssertion), new Provider.Headers(NewSubject, NewEmail));
        AccountState afterLocked = await StateOfAsync(host, account.UserId);

        AssertionResult fullAssertion = await ReauthenticateAsync(account);
        HttpResponseMessage fullResponse = await PostEmailChangeAsync(
            account.Client, BodyOf(fullAssertion), new Provider.Headers(NewSubject, NewEmail));

        // Assert — the control first, so a route that refused everybody cannot pass.
        await Assert.That(fullResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        await Assert.That(lockedResponse.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await TitleOfAsync(lockedResponse)).IsNotEqualTo(FirstPartyRequestMiddleware.Title);
        await Assert.That(afterLocked).IsEqualTo(before);
    }

    [Test]
    [Arguments("false")]
    [Arguments(null)]
    public async Task EmailChange_WhenTheProviderDoesNotVerifyTheAddress_IsRefused401EmailUnverified_AndChangesNothing(
        string? emailVerified)
    {
        // Arrange — "false", and the claim absent altogether.
        await using PostgresTestHost host = await StartProviderHeaderHostAsync();
        Account account = await SignInWithAPasskeyAsync(host.Factory, Subject, Email);
        AccountState before = await StateOfAsync(host, account.UserId);
        AssertionResult assertion = await ReauthenticateAsync(account);

        // Act
        HttpResponseMessage response = await PostEmailChangeAsync(
            account.Client, BodyOf(assertion), new Provider.Headers(NewSubject, NewEmail, emailVerified));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await RefusalOfAsync(response)).IsEqualTo("email_unverified");
        await Assert.That(await StateOfAsync(host, account.UserId)).IsEqualTo(before);
    }

    [Test]
    [Arguments("sub")]
    [Arguments("email")]
    public async Task EmailChange_WithoutSubOrEmailClaims_IsRefused401ProviderToken(string missingClaim)
    {
        // Arrange — a real, validly signed token that lacks one of the two claims.
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "email-change" };
        await using PostgresTestHost host = await StartRealBearerHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        Account account = await SignInWithAPasskeyAsync(factory, Subject, Email);
        AccountState before = await StateOfAsync(host, account.UserId);
        AssertionResult assertion = await ReauthenticateAsync(account);
        Dictionary<string, object> claims = Claims(NewSubject, NewEmail);
        claims.Remove(missingClaim);

        // Act
        HttpResponseMessage response = await PostEmailChangeAsync(
            account.Client, BodyOf(assertion), new Provider.Bearer(ProviderToken(signingKey, claims)));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await RefusalOfAsync(response)).IsEqualTo("provider_token");
        await Assert.That(await StateOfAsync(host, account.UserId)).IsEqualTo(before);
    }

    [Test]
    public async Task EmailChange_WithAnInvalidPasskeyAssertion_IsRefused401Assertion_AndChangesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartProviderHeaderHostAsync();
        Account account = await SignInWithAPasskeyAsync(host.Factory, Subject, Email);
        AccountState before = await StateOfAsync(host, account.UserId);
        AssertionResult assertion = WithFlippedSignature(await ReauthenticateAsync(account));

        // Act — a valid session and a valid provider token; one bit of the signature is wrong.
        HttpResponseMessage response = await PostEmailChangeAsync(
            account.Client, BodyOf(assertion), new Provider.Headers(NewSubject, NewEmail));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await RefusalOfAsync(response)).IsEqualTo("assertion");
        await Assert.That(await StateOfAsync(host, account.UserId)).IsEqualTo(before);
    }

    [Test]
    [Arguments(StrangerEmail, Subject)]
    [Arguments("STRANGER@Example.COM", NewSubject)]
    public async Task EmailChange_ToAnAddressAnotherAccountHolds_IsRefused409EmailAlreadyLinked_AndChangesNeitherAccount(
        string requestedEmail,
        string requestedSubject)
    {
        // Arrange — the exact address under the account's own subject, and the address in another
        // case under a new subject, which also has to roll back a credential retired and one filed.
        await using PostgresTestHost host = await StartProviderHeaderHostAsync();
        ApiFactory.SignedInClient stranger = await host.Factory.CreateSignedInClientAsync(StrangerSubject, StrangerEmail);
        Account account = await SignInWithAPasskeyAsync(host.Factory, Subject, Email);
        AccountState before = await StateOfAsync(host, account.UserId);
        AccountState strangerBefore = await StateOfAsync(host, stranger.UserId);
        AssertionResult assertion = await ReauthenticateAsync(account);

        // Act
        HttpResponseMessage response = await PostEmailChangeAsync(
            account.Client, BodyOf(assertion), new Provider.Headers(requestedSubject, requestedEmail));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await ConflictKindOfAsync(response)).IsEqualTo("email_already_linked");
        await AssertBodyNamesNeitherAsync(response, requestedEmail, requestedSubject);
        await Assert.That(await StateOfAsync(host, account.UserId)).IsEqualTo(before);
        await Assert.That(await StateOfAsync(host, stranger.UserId)).IsEqualTo(strangerBefore);
    }

    [Test]
    public async Task EmailChange_RefusedAfterTheSweepWouldRun_LeavesTheRetiredCredentialsSessionsLive()
    {
        // Arrange — a live session opened by the federated credential a new subject would retire, and an
        // address the stranger holds, so the refusal comes from the save AFTER the sweep ran. A sweep
        // outside the unit of work commits on its own and signs that browser out behind a 409.
        await using PostgresTestHost host = await StartProviderHeaderHostAsync();
        _ = await host.Factory.CreateSignedInClientAsync(StrangerSubject, StrangerEmail);
        Account account = await SignInWithAPasskeyAsync(host.Factory, Subject, Email);
        FederatedCredential original = (await FederatedCredentialsOfAsync(host, account.UserId)).Single();
        _ = await OpenLockedSessionAsync(host, account.UserId, fill: 0x44);
        await Assert.That(await SessionsOpenedByAsync(host, original.Id)).IsEqualTo(1L);
        AssertionResult assertion = await ReauthenticateAsync(account);

        // Act
        HttpResponseMessage response = await PostEmailChangeAsync(
            account.Client, BodyOf(assertion), new Provider.Headers(NewSubject, StrangerEmail));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await ConflictKindOfAsync(response)).IsEqualTo("email_already_linked");
        await Assert.That(await SessionsOpenedByAsync(host, original.Id)).IsEqualTo(1L);
        await Assert.That(await CredentialExistsAsync(host, original.Id)).IsTrue();
    }

    [Test]
    [Arguments("sub")]
    [Arguments("email")]
    public async Task EmailChange_WithABlankSubOrEmailClaim_IsRefused401ProviderToken(string blankClaim)
    {
        // Arrange — a real, validly signed token whose claim is present and holds only whitespace.
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "email-change" };
        await using PostgresTestHost host = await StartRealBearerHostAsync();
        await using ApiFactory factory = CreateRealBearerFactory(host, signingKey);
        Account account = await SignInWithAPasskeyAsync(factory, Subject, Email);
        AccountState before = await StateOfAsync(host, account.UserId);
        AssertionResult assertion = await ReauthenticateAsync(account);
        Dictionary<string, object> claims = Claims(NewSubject, NewEmail);
        claims[blankClaim] = "   ";

        // Act
        HttpResponseMessage response = await PostEmailChangeAsync(
            account.Client, BodyOf(assertion), new Provider.Bearer(ProviderToken(signingKey, claims)));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await RefusalOfAsync(response)).IsEqualTo("provider_token");
        await Assert.That(await StateOfAsync(host, account.UserId)).IsEqualTo(before);
    }

    [Test]
    public async Task EmailChange_WithASubjectAnotherAccountIsFiledUnder_IsRefused409ProviderIdentityInUse_AndChangesNeitherAccount()
    {
        // Arrange
        await using PostgresTestHost host = await StartProviderHeaderHostAsync();
        ApiFactory.SignedInClient stranger = await host.Factory.CreateSignedInClientAsync(StrangerSubject, StrangerEmail);
        Account account = await SignInWithAPasskeyAsync(host.Factory, Subject, Email);
        AccountState before = await StateOfAsync(host, account.UserId);
        AccountState strangerBefore = await StateOfAsync(host, stranger.UserId);
        AssertionResult assertion = await ReauthenticateAsync(account);

        // Act — the stranger's Google identity, with an address nobody holds.
        HttpResponseMessage response = await PostEmailChangeAsync(
            account.Client, BodyOf(assertion), new Provider.Headers(StrangerSubject, NewEmail));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await ConflictKindOfAsync(response)).IsEqualTo("provider_identity_in_use");
        await AssertBodyNamesNeitherAsync(response, NewEmail, StrangerSubject);
        await Assert.That(await StateOfAsync(host, account.UserId)).IsEqualTo(before);
        await Assert.That(await StateOfAsync(host, stranger.UserId)).IsEqualTo(strangerBefore);
    }

    [Test]
    public async Task EmailChange_ForTheSameSubjectWithANewAddress_KeepsTheCredentialAndUpdatesTheAddress()
    {
        // Arrange
        await using PostgresTestHost host = await StartProviderHeaderHostAsync();
        Account account = await SignInWithAPasskeyAsync(host.Factory, Subject, Email);
        FederatedCredential original = (await FederatedCredentialsOfAsync(host, account.UserId)).Single();
        AssertionResult assertion = await ReauthenticateAsync(account);

        // Act
        HttpResponseMessage response = await PostEmailChangeAsync(
            account.Client, BodyOf(assertion), new Provider.Headers(Subject, NewEmail));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await SessionsEndedOfAsync(response)).IsEqualTo(0);
        await Assert.That(await EmailOfAsync(host, account.UserId)).IsEqualTo(NewEmail);
        IReadOnlyList<FederatedCredential> after = await FederatedCredentialsOfAsync(host, account.UserId);
        await Assert.That(after.Count).IsEqualTo(1);
        await Assert.That(after[0]).IsEqualTo(original);
    }

    [Test]
    public async Task EmailChange_ToTheAddressAlreadyStoredUnderTheSameSubject_Answers200AndWritesNothing()
    {
        // Arrange — every statement the API's own DbContext sends is recorded.
        await using PostgresTestHost host = await StartProviderHeaderHostAsync();
        StatementRecorder recorder = new();
        await using ApiFactory factory = host.CreateFactory(configureServices: services =>
            services.ConfigureDbContext<BudgetoidDbContext>(options => options.AddInterceptors(recorder)));
        Account account = await SignInWithAPasskeyAsync(factory, Subject, Email);
        AccountState before = await StateOfAsync(host, account.UserId);
        AssertionResult assertion = await ReauthenticateAsync(account);
        int mark = recorder.Statements.Count;

        // Act
        HttpResponseMessage response = await PostEmailChangeAsync(
            account.Client, BodyOf(assertion), new Provider.Headers(Subject, Email));
        string[] window = [.. recorder.Statements.Skip(mark)];

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await SessionsEndedOfAsync(response)).IsEqualTo(0);
        await Assert.That(await StateOfAsync(host, account.UserId)).IsEqualTo(before);

        // Non-vacuity: the recorder saw the handler read the credential it compared against.
        await Assert.That(window.Any(statement => statement.Contains("credentials", StringComparison.Ordinal)))
            .IsTrue();
        await Assert.That(WritesTo(window, "users")).IsEmpty();
        await Assert.That(WritesTo(window, "credentials")).IsEmpty();
    }

    [Test]
    public async Task EmailChange_ReplacingTheCredential_EndsTheSessionsTheRetiredCredentialOpened_AndReportsThem()
    {
        // Arrange — a locked session opened by the federated credential about to be retired, beside
        // the full session (opened by a passkey) that makes the request.
        await using PostgresTestHost host = await StartProviderHeaderHostAsync();
        Account account = await SignInWithAPasskeyAsync(host.Factory, Subject, Email);
        FederatedCredential original = (await FederatedCredentialsOfAsync(host, account.UserId)).Single();
        _ = await OpenLockedSessionAsync(host, account.UserId, fill: 0x33);
        await Assert.That(await SessionsOpenedByAsync(host, original.Id)).IsEqualTo(1L);
        AssertionResult assertion = await ReauthenticateAsync(account);

        // Act
        HttpResponseMessage response = await PostEmailChangeAsync(
            account.Client, BodyOf(assertion), new Provider.Headers(NewSubject, NewEmail));
        HttpResponseMessage me = await account.Client.GetAsync(MePath);

        // Assert — the retired credential's session is reported and gone; the requesting one is not.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await SessionsEndedOfAsync(response)).IsEqualTo(1);
        await Assert.That(await SessionsOpenedByAsync(host, original.Id)).IsEqualTo(0L);
        await Assert.That(me.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task EmailChange_ForOneAccount_LeavesAnotherAccountsFederatedCredentialWhereItWas()
    {
        // Arrange — the stranger first, so the account that changes is not simply the first row.
        await using PostgresTestHost host = await StartProviderHeaderHostAsync();
        ApiFactory.SignedInClient stranger = await host.Factory.CreateSignedInClientAsync(StrangerSubject, StrangerEmail);
        Account account = await SignInWithAPasskeyAsync(host.Factory, Subject, Email);
        AccountState strangerBefore = await StateOfAsync(host, stranger.UserId);
        AssertionResult assertion = await ReauthenticateAsync(account);

        // Act
        HttpResponseMessage response = await PostEmailChangeAsync(
            account.Client, BodyOf(assertion), new Provider.Headers(NewSubject, NewEmail));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await StateOfAsync(host, stranger.UserId)).IsEqualTo(strangerBefore);
        IReadOnlyList<FederatedCredential> strangers = await FederatedCredentialsOfAsync(host, stranger.UserId);
        await Assert.That(strangers.Count).IsEqualTo(1);
        await Assert.That(strangers[0].Subject).IsEqualTo(StrangerSubject);
    }

    [Test]
    public async Task EmailChange_WithoutTheClientHeader_IsRefused403()
    {
        // Arrange
        await using PostgresTestHost host = await StartProviderHeaderHostAsync();
        Account account = await SignInWithAPasskeyAsync(host.Factory, Subject, Email);
        AccountState before = await StateOfAsync(host, account.UserId);
        AssertionResult assertion = await ReauthenticateAsync(account);
        account.Client.DefaultRequestHeaders.Remove(FirstPartyRequestTests.ClientHeader);

        // Act
        HttpResponseMessage response = await PostEmailChangeAsync(
            account.Client, BodyOf(assertion), new Provider.Headers(NewSubject, NewEmail));

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await TitleOfAsync(response)).IsEqualTo(FirstPartyRequestMiddleware.Title);
        await Assert.That(await StateOfAsync(host, account.UserId)).IsEqualTo(before);
    }

    [Test]
    public async Task EmailChange_TheRequestBodyCannotCarryAnAddressOrSubject()
    {
        // Arrange
        await using PostgresTestHost host = await StartProviderHeaderHostAsync();
        Account account = await SignInWithAPasskeyAsync(host.Factory, Subject, Email);
        AssertionResult assertion = await ReauthenticateAsync(account);
        JsonObject body = BodyOf(assertion);
        body["email"] = "from-the-body@example.com";
        body["sub"] = "google-from-the-body";

        // Act
        HttpResponseMessage response = await PostEmailChangeAsync(
            account.Client, body, new Provider.Headers(NewSubject, NewEmail));

        // Assert — whatever the status, the body's two members reached nothing.
        await Assert.That(await EmailOfAsync(host, account.UserId)).IsNotEqualTo("from-the-body@example.com");
        IReadOnlyList<FederatedCredential> after = await FederatedCredentialsOfAsync(host, account.UserId);
        await Assert.That(after.Select(credential => credential.Subject)).DoesNotContain("google-from-the-body");

        // And the token's are what landed.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await EmailOfAsync(host, account.UserId)).IsEqualTo(NewEmail);
        await Assert.That(after.Single().Subject).IsEqualTo(NewSubject);
    }

    public enum BearerFault
    {
        ForgedSignature,
        WrongAudience,
        Expired,
        WrongIssuer,
    }

    /// <summary>How a request presents the provider's identity.</summary>
    private abstract record Provider
    {
        /// <summary><see cref="TestAuthHandler" /> headers, on a host whose provider scheme is repointed.</summary>
        public sealed record Headers(string Subject, string Email, string? EmailVerified = "true") : Provider;

        /// <summary>An <c>Authorization: Bearer</c> token, for the real <c>JwtBearer</c> handler.</summary>
        public sealed record Bearer(string Token) : Provider;

        /// <summary>Nothing at all.</summary>
        public sealed record Absent : Provider;
    }

    private sealed record Account(HttpClient Client, Guid UserId, SyntheticAuthenticator Device);

    private readonly record struct FederatedCredential(Guid Id, string Subject);

    /// <summary>What a refused change must leave as it was, compared by value.</summary>
    /// <remarks>
    /// <see cref="Sessions" /> counts LIVE sessions only. A sweep that committed ahead of a refusal stamps
    /// <c>revoked_at_utc</c> and deletes nothing, so a count of every row reads the same before and after.
    /// </remarks>
    private sealed record AccountState(string Email, string FederatedCredentials, long Sessions);

    private static async Task<PostgresTestHost> StartProviderHeaderHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true, repointsProviderSchemeToTestHandler: true);
        await host.StartAsync();
        return host;
    }

    private static async Task<PostgresTestHost> StartRealBearerHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// A factory whose provider scheme is the real bearer handler holding <paramref name="signingKey" />
    /// and no metadata address, the shape <c>LogRedactionTests</c> builds.
    /// </summary>
    private static ApiFactory CreateRealBearerFactory(PostgresTestHost host, SecurityKey signingKey) =>
        host.CreateFactory(configureServices: services =>
            services.PostConfigure<JwtBearerOptions>(ProviderAuthentication.SchemeName, options =>
            {
                OpenIdConnectConfiguration configuration = new() { Issuer = LogCensusTraffic.ProviderIssuer };
                configuration.SigningKeys.Add(signingKey);
                options.Configuration = configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            }));

    private static Dictionary<string, object> Claims(string subject, string email) => new(StringComparer.Ordinal)
    {
        ["sub"] = subject,
        ["email"] = email,
        ["email_verified"] = true,
    };

    private static string ProviderToken(
        SecurityKey key,
        IDictionary<string, object> claims,
        string audience = LogCensusTraffic.ProviderAudience,
        DateTime? issuedAtUtc = null,
        string issuer = LogCensusTraffic.ProviderIssuer)
    {
        DateTime issued = issuedAtUtc ?? DateTime.UtcNow;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = issued,
            NotBefore = issued,
            Expires = issued.AddHours(1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>The same header and payload, and random bytes where the signature was.</summary>
    private static string WithRandomSignature(string token) =>
        $"{token[..token.LastIndexOf('.')]}.{Base64UrlText.Encode(RandomNumberGenerator.GetBytes(256))}";

    private static async Task<Account> SignInWithAPasskeyAsync(ApiFactory factory, string subject, string email)
    {
        (HttpClient client, Guid userId, _) = await factory.CreateSignedInClientAsync(subject, email);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);

        return new Account(client, userId, device);
    }

    private static async Task RegisterPasskeyAsync(HttpClient client, SyntheticAuthenticator device)
    {
        byte[] challenge = await BeginCeremonyAsync(client, RegistrationOptionsPath);
        AttestationResult attestation = device.Register(challenge, ApiFactory.PasskeyOrigin, 0, prfEnabled: true);
        WrappedKeyFixture keys = WrappedKeyFixture.Mint();
        int rotationEpoch = await FactorGeneration.NextAsync(client);

        HttpResponseMessage response = await client.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = attestation.AttestationObjectBase64Url,
            clientExtensionResults = new { prf = new { enabled = true } },
            factorId = keys.FactorId,
            wrappedPrivateKey = keys.WrappedPrivateKey,
            encapsulatedAccountKeys = keys.EncapsulatedAccountKeys,
            manifest = ManifestFixture.Mint().Text,
            rotationEpoch,
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<AssertionResult> ReauthenticateAsync(Account account)
    {
        byte[] challenge = await BeginCeremonyAsync(account.Client, ReauthenticationOptionsPath);

        return account.Device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(account.UserId));
    }

    private static async Task<byte[]> BeginCeremonyAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.PostAsync(path, content: null);
        response.EnsureSuccessStatusCode();
        JsonNode options = await ReadJsonAsync(response)
            ?? throw new InvalidOperationException($"'{path}' answered with no body.");

        return Base64UrlText.Decode(options["challenge"]!.GetValue<string>());
    }

    private static AssertionResult WithFlippedSignature(AssertionResult result)
    {
        byte[] signature = [.. result.Signature];
        signature[^1] ^= 0xFF;
        return result with { Signature = signature };
    }

    /// <summary>A locked session opened by the account's federated credential, and a client presenting it.</summary>
    private static async Task<HttpClient> OpenLockedSessionAsync(PostgresTestHost host, Guid userId, byte fill)
    {
        Guid federatedId = await RepositoryTestHost.FederatedCredentialIdOnAsync(host.ConnectionString, userId);
        byte[] token = RepositoryTestHost.SessionTokenBytes(fill);
        DateTime now = DateTime.UtcNow;
        await RepositoryTestHost.SeedSessionOnAsync(
            host.ConnectionString, federatedId, token, SessionKind.Locked, now.AddMinutes(-1), now.AddHours(1));

        HttpClient client = host.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", $"{SessionCookieAuthenticationTests.CookieName}={Base64UrlText.Encode(token)}");

        return client;
    }

    private static JsonObject BodyOf(AssertionResult result) => new()
    {
        ["credentialId"] = result.CredentialIdBase64Url,
        ["clientDataJson"] = result.ClientDataJsonBase64Url,
        ["authenticatorData"] = result.AuthenticatorDataBase64Url,
        ["signature"] = result.SignatureBase64Url,
        ["userHandle"] = result.UserHandleBase64Url,
    };

    private static async Task<HttpResponseMessage> PostEmailChangeAsync(
        HttpClient client,
        JsonObject body,
        Provider provider)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, EmailChangePath)
        {
            Content = JsonContent.Create(body),
        };

        switch (provider)
        {
            case Provider.Headers headers:
                request.Headers.Add(TestAuthHandler.SubjectHeader, headers.Subject);
                request.Headers.Add(TestAuthHandler.EmailHeader, headers.Email);
                if (headers.EmailVerified is null)
                {
                    request.Headers.Add(TestAuthHandler.OmitEmailVerifiedHeader, "true");
                }
                else
                {
                    request.Headers.Add(TestAuthHandler.EmailVerifiedHeader, headers.EmailVerified);
                }

                break;

            case Provider.Bearer bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer.Token);
                break;

            case Provider.Absent:
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
        }

        HttpResponseMessage response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();

        return response;
    }

    private static IReadOnlyList<string> WritesTo(IEnumerable<string> statements, string table) =>
    [
        .. statements.Where(statement =>
        {
            string trimmed = statement.TrimStart();
            return (trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
                && statement.Contains(table, StringComparison.Ordinal);
        }),
    ];

    private static async Task<AccountState> StateOfAsync(PostgresTestHost host, Guid userId)
    {
        IReadOnlyList<FederatedCredential> federated = await FederatedCredentialsOfAsync(host, userId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await using NpgsqlCommand sessions = new(
            "select count(*) from sessions where user_id = @id and revoked_at_utc is null", admin);
        sessions.Parameters.AddWithValue("id", userId);

        return new AccountState(
            await EmailOfAsync(host, userId),
            string.Join(", ", federated.Select(credential => $"{credential.Id}={credential.Subject}")),
            CountOf(await sessions.ExecuteScalarAsync()));
    }

    private static async Task<string> EmailOfAsync(PostgresTestHost host, Guid userId)
    {
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await using NpgsqlCommand command = new("select email from users where id = @id", admin);
        command.Parameters.AddWithValue("id", userId);

        return await command.ExecuteScalarAsync() switch
        {
            string email => email,
            var unexpected => throw new InvalidOperationException(
                $"No user row for '{userId}', got '{unexpected ?? "null"}'."),
        };
    }

    private static async Task<IReadOnlyList<FederatedCredential>> FederatedCredentialsOfAsync(
        PostgresTestHost host,
        Guid userId)
    {
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await using NpgsqlCommand command = new(
            "select id, subject from credentials where user_id = @id and type = 'federated' order by id",
            admin);
        command.Parameters.AddWithValue("id", userId);

        List<FederatedCredential> credentials = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            credentials.Add(new FederatedCredential(reader.GetGuid(0), reader.GetString(1)));
        }

        return credentials;
    }

    private static async Task<bool> CredentialExistsAsync(PostgresTestHost host, Guid credentialId)
    {
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await using NpgsqlCommand command = new("select count(*) from credentials where id = @id", admin);
        command.Parameters.AddWithValue("id", credentialId);

        return CountOf(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<long> SessionsOpenedByAsync(PostgresTestHost host, Guid credentialId)
    {
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await using NpgsqlCommand command = new(
            "select count(*) from sessions where credential_id = @id and revoked_at_utc is null", admin);
        command.Parameters.AddWithValue("id", credentialId);

        return CountOf(await command.ExecuteScalarAsync());
    }

    private static long CountOf(object? scalar) => scalar switch
    {
        long count => count,
        var unexpected => throw new InvalidOperationException($"Expected a count, got '{unexpected ?? "null"}'."),
    };

    /// <summary>
    /// A refusal body names neither the address nor the subject the caller tried, in any case: the caller
    /// sent both, and a body repeating them is a copy for every log and proxy on the way back.
    /// </summary>
    private static async Task AssertBodyNamesNeitherAsync(HttpResponseMessage response, string email, string subject)
    {
        string raw = await response.Content.ReadAsStringAsync();
        await Assert.That(raw.Contains(email, StringComparison.OrdinalIgnoreCase)).IsFalse();
        await Assert.That(raw.Contains(subject, StringComparison.Ordinal)).IsFalse();
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response)
    {
        string raw = await response.Content.ReadAsStringAsync();

        return string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw);
    }

    private static async Task<int> SessionsEndedOfAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response))?[SessionsEndedMember]?.GetValue<int>()
        ?? throw new InvalidOperationException(
            $"A {(int)response.StatusCode} answer carried no '{SessionsEndedMember}' member.");

    /// <summary>The <c>refusal</c> word, or null when the body is empty, not JSON, or has none.</summary>
    private static async Task<string?> RefusalOfAsync(HttpResponseMessage response) =>
        await MemberOfAsync(response, RefusalMember);

    private static async Task<string?> ConflictKindOfAsync(HttpResponseMessage response) =>
        await MemberOfAsync(response, ConflictKindMember);

    private static async Task<string> TitleOfAsync(HttpResponseMessage response) =>
        await MemberOfAsync(response, "title") ?? string.Empty;

    private static async Task<string?> MemberOfAsync(HttpResponseMessage response, string member)
    {
        string raw = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(raw) is JsonObject body && body[member] is JsonValue value
                ? value.GetValue<string>()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
