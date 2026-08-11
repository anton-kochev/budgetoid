using System.Security.Cryptography;
using Application.Abstractions;
using Application.Passkeys;
using Application.Passkeys.CompleteAssertion;
using Application.Passkeys.Reauthentication;
using Application.RecoveryCodes.GenerateRecoveryCodes;
using Application.RecoveryCodes.RedeemRecoveryCode;
using Application.Sessions.RevokeSessionsForCredential;
using Domain.Sessions;
using Domain.Users;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// How long a session lasts, on every path in the application that opens one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three handlers open a session — a passkey assertion, a recovery-code redemption and a
/// regeneration that swept the sessions it replaced — and each holds the interval as a constant of its
/// own.</b> Each of the three files that tests them pins its own handler's expiry, and none of them can
/// say the other two agree. This is the file where the three are put beside each other, because the
/// handlers' own remarks say the equality is a rule rather than a coincidence: all three open a
/// <see cref="SessionKind.Full" /> session, and a shorter one on the recovery paths would quietly tell
/// somebody who has just lost their authenticator that the way back in they were issued is worth less
/// than the one they lost.
/// </para>
/// <para>
/// <b>The value is asserted as an observable expiry against a fixed clock, and never read off the
/// production constant.</b> A test taking its expectation from the type under test agrees with whatever
/// that type later decides — <c>TimeSpan.FromDays(14000)</c> included, which is how an intercepted code
/// buys a session that never practically expires. Nothing here names a handler's field, so consolidating
/// the three constants into one shared value leaves this file untouched and still meaning what it means;
/// changing what any one of them <em>is</em> reddens it.
/// </para>
/// <para>
/// <b>Two claims, deliberately not one.</b>
/// <see cref="EveryPathThatOpensASession_ExpiresItFourteenDaysAfterTheHandlersInstant" /> pins the
/// number, and <see cref="EveryPathThatOpensASession_AgreesWithTheOthers" /> pins the equality without
/// naming it: the day product policy moves the interval, the first test is edited on purpose and the
/// second is what refuses an edit that moved only one of the three.
/// </para>
/// <para>
/// <b>Every path is driven through its real handler over the ordinary fakes</b> — a real device holding
/// a real key pair, a real signature over the real nonce, and a real set of codes filed by the domain
/// factories. A shortcut that constructed <see cref="Session.Establish" /> here would pin the domain,
/// which already has its own tests, and would say nothing about the three call sites that choose the
/// expiry.
/// </para>
/// </remarks>
public sealed class EstablishedSessionLifetimeTests
{
    /// <summary>
    /// How long a session lasts, restated rather than read off any handler. See the class remarks.
    /// </summary>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

    /// <summary>The instant every handler here reads from its clock.</summary>
    private static readonly DateTime UtcNow = new(2026, 8, 11, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>When the material these paths sign in against was filed.</summary>
    private static readonly DateTime IssuedEarlier = UtcNow.AddDays(-30);

    private const string RelyingPartyId = "localhost";
    private const string Origin = "https://localhost:4200";
    private const int ChallengeBytes = 32;
    private const int VerifierLength = 32;
    private const int CodesPerSet = 10;

    /// <summary>
    /// Every path that opens a session expires it exactly <see cref="SessionLifetime" /> after the
    /// instant its handler read.
    /// </summary>
    /// <remarks>
    /// An equality against a fixed clock, never "later than now": a lifetime of forty years is later
    /// than now, and so is every value a mistyped constant can produce.
    /// </remarks>
    [Test]
    public async Task EveryPathThatOpensASession_ExpiresItFourteenDaysAfterTheHandlersInstant()
    {
        // Arrange & Act — one sign-in on each of the three paths, all reading the same fixed instant.
        Expiries expiries = await OpenOneSessionOnEveryPathAsync();

        // Assert
        await Assert.That(expiries.PasskeyAssertion).IsEqualTo(UtcNow + SessionLifetime);
        await Assert.That(expiries.RecoveryCodeRedemption).IsEqualTo(UtcNow + SessionLifetime);
        await Assert.That(expiries.RecoveryCodeRegeneration).IsEqualTo(UtcNow + SessionLifetime);
    }

    /// <summary>
    /// The three paths agree with each other, whatever the interval happens to be.
    /// </summary>
    /// <remarks>
    /// <b>This is the claim the three handlers' remarks make and no test made.</b> Each says the interval
    /// it uses is the same one the others use, and each holds a private constant, so the three can be
    /// separated by a single edit that leaves that handler's own test green after its expectation is
    /// updated alongside. Stated as an equality between observed values rather than against a literal, so
    /// it survives a deliberate change of policy and refuses a partial one.
    /// </remarks>
    [Test]
    public async Task EveryPathThatOpensASession_AgreesWithTheOthers()
    {
        // Arrange & Act
        Expiries expiries = await OpenOneSessionOnEveryPathAsync();

        // Assert
        await Assert.That(expiries.RecoveryCodeRedemption).IsEqualTo(expiries.PasskeyAssertion);
        await Assert.That(expiries.RecoveryCodeRegeneration).IsEqualTo(expiries.PasskeyAssertion);
    }

    /// <summary>The expiry each of the three paths stamped its session with.</summary>
    private sealed record Expiries(
        DateTime PasskeyAssertion,
        DateTime RecoveryCodeRedemption,
        DateTime RecoveryCodeRegeneration);

    /// <summary>
    /// Signs in once on each path and hands back the three expiries.
    /// </summary>
    /// <remarks>
    /// Read off what the caller is <b>told</b> rather than off the stored row, because that is the value a
    /// client renders and the one a person plans around; the two agreeing is asserted by each path's own
    /// test file.
    /// </remarks>
    private static async Task<Expiries> OpenOneSessionOnEveryPathAsync() => new(
        await AssertWithAPasskeyAsync(),
        await RedeemARecoveryCodeAsync(),
        await RegenerateARecoveryCodeSetAsync());

    /// <summary>
    /// One completed passkey assertion: a real device, a real signature over the nonce the store holds.
    /// </summary>
    private static async Task<DateTime> AssertWithAPasskeyAsync()
    {
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        Guid userId = Guid.CreateVersion7();
        Credential passkey = Credential.CreatePasskey(userId, IssuedEarlier);

        InMemoryPasskeyRepository passkeys = new();
        passkeys.Register(
            passkey,
            PasskeyPublicKey.Register(passkey, device.CredentialId, device.CoseKey, device.Algorithm),
            signatureCounter: 0);

        byte[] challenge = RandomNumberGenerator.GetBytes(ChallengeBytes);

        // A synced authenticator reports zero every time, and a repeated zero is read as no movement
        // rather than as a clone — so nothing on the counter path can end this ceremony early.
        AssertionResult assertion = device.Authenticate(
            challenge,
            Origin,
            PasskeyEncoding.ToUserHandle(userId),
            signCount: 0);

        CompleteAssertionHandler handler = new(
            new StubWebAuthnChallengeStore(challenge, WebAuthnCeremony.Authentication),
            passkeys,
            new InMemorySessionRepository(),
            new RecordingUserContextWriter(),
            new StubPasskeyCeremonyPolicy(RelyingPartyId, Origin),
            new InMemoryTransactionalExecutor(),
            new RecordingPersistenceState(() => 0),
            new FakeTimeProvider(new DateTimeOffset(UtcNow)));

        EstablishedSession established = await handler.HandleAsync(new CompleteAssertionCommand(
            assertion.CredentialIdBase64Url,
            assertion.ClientDataJsonBase64Url,
            assertion.AuthenticatorDataBase64Url,
            assertion.SignatureBase64Url,
            assertion.UserHandleBase64Url));

        return established.ExpiresAtUtc;
    }

    /// <summary>One redeemed recovery code, on an account holding a real set.</summary>
    private static async Task<DateTime> RedeemARecoveryCodeAsync()
    {
        InMemoryRecoveryCodeRepository recoveryCodes = new();
        Guid userId = Guid.CreateVersion7();
        (Credential _, byte[][] verifiers) = SeedSet(recoveryCodes, userId);

        RedeemRecoveryCodeHandler handler = new(
            recoveryCodes,
            new InMemoryRecoveryCodeReadService(recoveryCodes),
            new InMemorySessionRepository(),
            new RecordingUserContextWriter(),
            new InMemoryTransactionalExecutor(),
            new RecordingPersistenceState(() => 0),
            new FakeTimeProvider(new DateTimeOffset(UtcNow)));

        RedeemedRecoveryCode redeemed = await handler.HandleAsync(
            new RedeemRecoveryCodeCommand(Base64UrlText.Encode(verifiers[0])));

        return redeemed.ExpiresAtUtc;
    }

    /// <summary>
    /// One regenerated set on an account whose replaced set was carrying a live session, which is the
    /// only arrangement in which that path opens one.
    /// </summary>
    /// <remarks>
    /// The gate is a real <see cref="PasskeyReauthentication" /> over fakes rather than a stub, for the
    /// reason <see cref="GenerateRecoveryCodesHandlerTests" /> gives: there is no interface to stub it
    /// behind, and a stubbable gate would let a test prove that a set of codes can be minted with the
    /// proof faked out.
    /// </remarks>
    private static async Task<DateTime> RegenerateARecoveryCodeSetAsync()
    {
        Guid userId = Guid.CreateVersion7();
        StubUserContext userContext = new(userId);

        // One session store: the sweep, the cascade from the replaced set's credential and the
        // establishment must all see the same rows.
        InMemorySessionRepository sessions = new();
        InMemoryRecoveryCodeRepository recoveryCodes =
            new(credential => sessions.RemoveForCredential(credential.Id));

        (Credential previousSet, byte[][] _) = SeedSet(recoveryCodes, userId);

        // A live session the replaced set opened, which is what makes this a re-establishment rather
        // than a first issue.
        await sessions.AddAsync(Session.Establish(previousSet, IssuedEarlier, UtcNow.AddHours(1)));

        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        Credential passkey = Credential.CreatePasskey(userId, IssuedEarlier);
        InMemoryPasskeyRepository passkeys = new();
        passkeys.Register(
            passkey,
            PasskeyPublicKey.Register(passkey, device.CredentialId, device.CoseKey, device.Algorithm),
            signatureCounter: 0);

        byte[] challenge = RandomNumberGenerator.GetBytes(ChallengeBytes);
        StubWebAuthnChallengeStore challenges = new(challenge, WebAuthnCeremony.Reauthentication);
        AssertionResult assertion = device.Authenticate(challenge, Origin, userHandle: null);

        FakeTimeProvider clock = new(new DateTimeOffset(UtcNow));

        // The discard is forwarded to the two fakes holding rows an attempt queues, and deliberately not
        // to the session fake: that one holds committed and pending sessions in a single list, so wiring
        // it in would clear the seeded session and the sweep would match nothing.
        RecordingPersistenceState persistenceState = new(
            () => 0,
            recoveryCodes.DiscardTrackedEntities,
            passkeys.DiscardTrackedEntities);

        GenerateRecoveryCodesHandler handler = new(
            recoveryCodes,
            userContext,
            persistenceState,
            new InMemoryTransactionalExecutor(),
            new PasskeyReauthentication(
                challenges,
                passkeys,
                userContext,
                new StubPasskeyCeremonyPolicy(RelyingPartyId, Origin)),
            new RevokeSessionsForCredentialHandler(sessions, clock),
            sessions,
            clock);

        RecoveryCodesGeneration generation = await handler.HandleAsync(new GenerateRecoveryCodesCommand(
            [.. Verifiers().Select(verifier => Base64UrlText.Encode(verifier))],
            new ReauthenticationAssertion(
                assertion.CredentialIdBase64Url,
                assertion.ClientDataJsonBase64Url,
                assertion.AuthenticatorDataBase64Url,
                assertion.SignatureBase64Url,
                assertion.UserHandleBase64Url)));

        return generation.Session?.ExpiresAtUtc
               ?? throw new InvalidOperationException(
                   "The replaced set was carrying a live session, so a regeneration had to re-establish one.");
    }

    /// <summary>Files one whole set onto an account, as a committed generation would have left it.</summary>
    private static (Credential Set, byte[][] Verifiers) SeedSet(
        InMemoryRecoveryCodeRepository recoveryCodes,
        Guid userId)
    {
        Credential set = Credential.CreateRecoveryCodes(userId, IssuedEarlier);
        byte[][] verifiers = Verifiers();

        recoveryCodes.Seed(
            set,
            [.. verifiers.Select(verifier => RecoveryCodeHash.From(set, verifier, IssuedEarlier))]);

        return (set, verifiers);
    }

    /// <summary>A well-formed set of verifiers, of the size and width the product requires.</summary>
    private static byte[][] Verifiers() =>
    [
        .. Enumerable.Range(0, CodesPerSet).Select(_ => RandomNumberGenerator.GetBytes(VerifierLength)),
    ];
}
