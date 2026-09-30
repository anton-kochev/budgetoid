using System.Security.Cryptography;
using Application.Abstractions;
using Application.Passkeys;
using Application.Passkeys.Reauthentication;
using Application.Sessions.RevokeSessionsForCredential;
using Application.Users.ChangeEmail;
using Domain.Common;
using Domain.Sessions;
using Domain.Users;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using TUnit.Assertions.Enums;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// The order an email change runs in — the passkey gate, then the subject pre-check, then the
/// account's own credential, the session sweep and the save — and what each repository answer becomes.
/// </summary>
/// <remarks>
/// <para>
/// The gate is a real <see cref="PasskeyReauthentication" /> over fakes, exactly as
/// <see cref="RevokePasskeyHandlerTests" /> and <see cref="EraseAccountHandlerTests" /> build it, and
/// for their reason: a stubbable gate would let a test here prove the change works with the proof
/// faked out.
/// </para>
/// <para>
/// <b>A new subject is a credential retired, and retiring a credential revokes before it deletes</b> —
/// the rule <c>RevokePasskeyHandler</c> and the recovery-code replacement already follow. The cascade
/// from <c>credentials</c> removes the same session rows either way, so the order is measured here on a
/// call log shared by the two fakes rather than read off the rows afterwards.
/// </para>
/// <para>
/// <b>A refusal after the sweep must not commit it.</b> The save is refused whole, but the session
/// sweep ran on an earlier save inside the same unit of work; a delegate that <em>returned</em> the
/// refusal would hand the executor a commit of that sweep, signing the person's other browsers out of
/// a credential the response says is still theirs. The cases that reach the sweep therefore ask the
/// executor whether it committed.
/// </para>
/// </remarks>
public sealed class ChangeEmailHandlerTests
{
    private const string RelyingPartyId = "localhost";
    private const string Origin = "https://localhost:4200";
    private const int ChallengeBytes = 32;

    private const string Subject = "google-subject-before";
    private const string NewSubject = "google-subject-after";
    private const string Address = "person@example.com";
    private const string NewAddress = "moved@example.com";

    /// <summary>Fixed instant for every seeded row and for the handler's clock.</summary>
    private static readonly DateTime UtcNow = new(2026, 9, 30, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// A refused passkey leaves the request with no account to act on, so nothing is read after it.
    /// </summary>
    /// <remarks>
    /// The command asks for everything at once — a new subject and a new address — so that every read
    /// and write the handler owns had a reason to run. The subject lookup is counted too: it is the
    /// exempt discovery lookup, and an unproven caller asking it is an oracle for which Google
    /// identities hold an account.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheAssertionFails_ReadsAndWritesNothing()
    {
        // Arrange — the store holds bytes the device never signed, which is how it answers null.
        Fixture fixture = Fixture.Build(challengeIsLive: false);
        await fixture.SeedLiveSessionsAsync(fixture.Federated, count: 1);

        // Act
        await ThrowsAsync<PasskeyVerificationException>(
            () => fixture.Handler.HandleAsync(fixture.CommandFor(NewSubject, NewAddress)));

        // Assert
        await Assert.That(fixture.Users.FederatedLookupCount).IsEqualTo(0);
        await Assert.That(fixture.EmailChanges.CallCount).IsEqualTo(0);
        await Assert.That(fixture.Executor.Entered).IsEqualTo(0);
        await Assert.That(fixture.Sessions.RevokeForCredentialCallCount).IsEqualTo(0);
        await Assert.That(fixture.User.Email.Value).IsEqualTo(Address);
    }

    /// <summary>
    /// A Google identity another account signs up with is refused before any transaction opens.
    /// </summary>
    [Test]
    public async Task HandleAsync_WhenTheSubjectBelongsToAnotherAccount_RefusesBeforeTheTransaction()
    {
        // Arrange — a second account filed under the subject the person chose at Google.
        Fixture fixture = Fixture.Build();
        User rival = User.CreateWithId(Guid.CreateVersion7(), "rival@example.com", UtcNow);
        fixture.Users.Seed(
            rival,
            Credential.CreateFederated(rival.Id, Credential.GoogleProvider, NewSubject, UtcNow));
        await fixture.SeedLiveSessionsAsync(fixture.Federated, count: 1);

        // Act
        ConflictException conflict = await ThrowsAsync<ConflictException>(
            () => fixture.Handler.HandleAsync(fixture.CommandFor(NewSubject, NewAddress)));

        // Assert
        await Assert.That(conflict.Kind).IsEqualTo(ConflictKind.ProviderIdentityInUse);
        await Assert.That(fixture.Executor.Entered).IsEqualTo(0);
        await Assert.That(fixture.EmailChanges.CallCount).IsEqualTo(0);
        await Assert.That(fixture.Sessions.RevokeForCredentialCallCount).IsEqualTo(0);
    }

    /// <summary>
    /// The retired credential's sessions are revoked <b>before</b> the save that deletes it.
    /// </summary>
    /// <remarks>
    /// The two write entries are compared in order, and the sweep's entry names the credential it swept,
    /// so a sweep keyed on the newly filed credential — which has no sessions and reports zero — is red
    /// here rather than green on a matching count of calls.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithANewSubject_RevokesTheRetiredCredentialsSessionsBeforeApplying()
    {
        // Arrange
        Fixture fixture = Fixture.Build();
        await fixture.SeedLiveSessionsAsync(fixture.Federated, count: 1);

        // Act
        await fixture.Handler.HandleAsync(fixture.CommandFor(NewSubject, Address));

        // Assert
        await Assert.That(fixture.CallLog)
            .IsEquivalentTo(
                new[]
                {
                    LoggingSessionRepository.RevokeEntryFor(fixture.Federated.Id),
                    InMemoryEmailChangeRepository.ApplyEntry,
                },
                CollectionOrdering.Matching);
        await Assert.That(fixture.EmailChanges.ApplyCalls.Single().Retired?.Id).IsEqualTo(fixture.Federated.Id);
    }

    /// <summary>
    /// The number in the answer is the number the sweep reported, and only the retired credential's.
    /// </summary>
    /// <remarks>
    /// Three sessions rather than one, so a hard-coded <c>1</c> or a <c>&gt; 0 ? 1 : 0</c> is red. A
    /// fourth session, opened by the account's passkey, stays live: a sweep keyed on the account rather
    /// than the credential would sign the person out of the browser in their hand.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithANewSubject_ReportsTheSessionsItEnded()
    {
        // Arrange
        Fixture fixture = Fixture.Build();
        await fixture.SeedLiveSessionsAsync(fixture.Federated, count: 3);
        Session passkeySession = Session.Establish(fixture.Passkey, UtcNow, UtcNow.AddHours(1));
        await fixture.Sessions.AddAsync(passkeySession);

        // Act
        EmailChange change = await fixture.Handler.HandleAsync(fixture.CommandFor(NewSubject, Address));

        // Assert
        await Assert.That(change.SessionsEnded).IsEqualTo(3);
        await Assert.That(passkeySession.RevokedAtUtc).IsNull();
    }

    /// <summary>
    /// The account chosen at Google is the one already attached, with the address already stored.
    /// </summary>
    [Test]
    public async Task HandleAsync_WhenNothingChanged_WritesNothingAndEndsNoSessions()
    {
        // Arrange
        Fixture fixture = Fixture.Build();
        await fixture.SeedLiveSessionsAsync(fixture.Federated, count: 1);

        // Act
        EmailChange change = await fixture.Handler.HandleAsync(fixture.CommandFor(Subject, Address));

        // Assert
        await Assert.That(change.SessionsEnded).IsEqualTo(0);
        await Assert.That(fixture.EmailChanges.ApplyCalls.Count).IsEqualTo(0);
        await Assert.That(fixture.Sessions.RevokeForCredentialCallCount).IsEqualTo(0);
    }

    /// <summary>
    /// Every attempt of the unit of work starts by discarding what the change tracker holds.
    /// </summary>
    /// <remarks>
    /// The execution strategy replays the delegate against a database that rolled the abandoned attempt
    /// back and a change tracker that did not, and the gate before it left the proving passkey tracked.
    /// A same-subject change retires nothing, so the one discard per attempt recorded here is the FIRST
    /// one, and never on attempt zero — which is what a call made before the executor was entered would
    /// record.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheUnitOfWorkIsReplayed_DiscardsTrackedEntitiesAtTheStartOfEveryAttempt()
    {
        // Arrange
        RetryingTransactionalExecutor executor = new(2);
        Fixture fixture = Fixture.Build(replaying: executor);

        // Act
        await fixture.Handler.HandleAsync(fixture.CommandFor(Subject, NewAddress));

        // Assert
        await Assert.That(executor.Attempts).IsEqualTo(2);
        await Assert.That(fixture.PersistenceState.DiscardedOnAttempt)
            .IsEquivalentTo(new[] { 1, 2 }, CollectionOrdering.Matching);
    }

    /// <summary>
    /// The same Google identity with a new address moves the address and keeps the credential.
    /// </summary>
    /// <remarks>
    /// The credential is not retired, so the browsers it signed in stay signed in; ending them here
    /// would sign a person out for a change to a column no session depends on.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithTheSameSubjectAndANewAddress_ChangesTheAddressAndRevokesNothing()
    {
        // Arrange
        Fixture fixture = Fixture.Build();
        await fixture.SeedLiveSessionsAsync(fixture.Federated, count: 1);

        // Act
        EmailChange change = await fixture.Handler.HandleAsync(fixture.CommandFor(Subject, NewAddress));

        // Assert
        FederatedIdentityChange applied = fixture.EmailChanges.ApplyCalls.Single();
        await Assert.That(applied.Retired).IsNull();
        await Assert.That(applied.Filed).IsNull();
        await Assert.That(applied.Email.Value).IsEqualTo(NewAddress);
        await Assert.That(fixture.User.Email.Value).IsEqualTo(NewAddress);
        await Assert.That(fixture.Sessions.RevokeForCredentialCallCount).IsEqualTo(0);
        await Assert.That(change.SessionsEnded).IsEqualTo(0);
    }

    /// <summary>
    /// <see cref="EmailChangeOutcome.EmailTaken" /> is ambiguous, and a re-read of the subject settles
    /// it: a racing account now filed under that subject makes this a Google identity in use.
    /// </summary>
    /// <remarks>
    /// The rival is filed the moment the save is entered, so the pre-check before the transaction found
    /// nobody and only a read <em>after</em> the refused save can see it — the registration handler's
    /// resolution of the same ambiguity.
    /// </remarks>
    [Test]
    public async Task HandleAsync_OnEmailTaken_WithTheSubjectNowHeldElsewhere_AnswersProviderIdentityInUse()
    {
        // Arrange
        Fixture fixture = Fixture.Build();
        await fixture.SeedLiveSessionsAsync(fixture.Federated, count: 1);
        User rival = User.CreateWithId(Guid.CreateVersion7(), NewAddress, UtcNow);
        fixture.EmailChanges.Outcome = EmailChangeOutcome.EmailTaken;
        fixture.EmailChanges.OnApply = () => fixture.Users.Seed(
            rival,
            Credential.CreateFederated(rival.Id, Credential.GoogleProvider, NewSubject, UtcNow));

        // Act
        ConflictException conflict = await ThrowsAsync<ConflictException>(
            () => fixture.Handler.HandleAsync(fixture.CommandFor(NewSubject, NewAddress)));

        // Assert — and the sweep that ran before the refused save was not committed with it.
        await Assert.That(conflict.Kind).IsEqualTo(ConflictKind.ProviderIdentityInUse);
        await Assert.That(fixture.Executor.Committed).IsEqualTo(0);
    }

    /// <summary>
    /// <see cref="EmailChangeOutcome.EmailTaken" /> with the subject held by nobody but this account is
    /// the address alone colliding.
    /// </summary>
    /// <remarks>
    /// The subject is the account's own, so the re-read answers <em>this</em> account's id rather than
    /// nothing. A handler that read any answer at all as "somebody else holds it" is red here.
    /// </remarks>
    [Test]
    public async Task HandleAsync_OnEmailTaken_WithTheSubjectHeldByNobodyElse_AnswersEmailAlreadyLinked()
    {
        // Arrange
        Fixture fixture = Fixture.Build();
        fixture.EmailChanges.Outcome = EmailChangeOutcome.EmailTaken;

        // Act
        ConflictException conflict = await ThrowsAsync<ConflictException>(
            () => fixture.Handler.HandleAsync(fixture.CommandFor(Subject, NewAddress)));

        // Assert
        await Assert.That(conflict.Kind).IsEqualTo(ConflictKind.EmailAlreadyLinked);
        await Assert.That(fixture.User.Email.Value).IsEqualTo(Address);
    }

    /// <summary>
    /// A racing change moved the account's credential between this request's read and its save.
    /// </summary>
    [Test]
    public async Task HandleAsync_OnFederatedCredentialMoved_AnswersAccountIdentityMoved()
    {
        // Arrange
        Fixture fixture = Fixture.Build();
        await fixture.SeedLiveSessionsAsync(fixture.Federated, count: 1);
        fixture.EmailChanges.Outcome = EmailChangeOutcome.FederatedCredentialMoved;

        // Act
        ConflictException conflict = await ThrowsAsync<ConflictException>(
            () => fixture.Handler.HandleAsync(fixture.CommandFor(NewSubject, NewAddress)));

        // Assert — and the sweep that ran before the refused save was not committed with it.
        await Assert.That(conflict.Kind).IsEqualTo(ConflictKind.AccountIdentityMoved);
        await Assert.That(fixture.Executor.Committed).IsEqualTo(0);
    }

    /// <summary>
    /// An account without a federated credential cannot exist, so its absence is raised rather than
    /// answered — a 500 on purpose.
    /// </summary>
    /// <remarks>
    /// <see cref="InvalidOperationException" />, the integrity failure <c>RevokePasskeyHandler</c> raises
    /// for a missing manifest. Not a <see cref="ConflictException" /> and not a not-found: nothing the
    /// caller sent is wrong and nothing they could send would help.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheAccountHoldsNoFederatedCredential_Throws()
    {
        // Arrange
        Fixture fixture = Fixture.Build(seedFederatedCredential: false);

        // Act
        await ThrowsAsync<InvalidOperationException>(
            () => fixture.Handler.HandleAsync(fixture.CommandFor(NewSubject, NewAddress)));

        // Assert
        await Assert.That(fixture.EmailChanges.ApplyCalls.Count).IsEqualTo(0);
        await Assert.That(fixture.Sessions.RevokeForCredentialCallCount).IsEqualTo(0);
    }

    /// <summary>
    /// The subject is trimmed before the discovery lookup is asked about it, on the pre-check and on
    /// the <see cref="EmailChangeOutcome.EmailTaken" /> re-read alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="FederatedIdentityChange.Decide" /> trims the subject and <c>CreateFederated</c> stores
    /// it trimmed, while the lookup matches ordinally. An untrimmed lookup therefore finds nobody for a
    /// subject another account holds, and the pre-check waves through a change the save will then refuse
    /// — or, on the re-read, answers "the address alone collided" for a Google identity in use.
    /// </para>
    /// <para>
    /// One test and one assertion over the recorded list, because the two lookups fail for the same
    /// reason and the list in call order names which of the two forgot. The save answers
    /// <see cref="EmailChangeOutcome.EmailTaken" /> so that the re-read is reached at all.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithASubjectInSurroundingWhitespace_PreChecksAndReReadsTheTrimmedSubject()
    {
        // Arrange
        Fixture fixture = Fixture.Build();
        fixture.EmailChanges.Outcome = EmailChangeOutcome.EmailTaken;

        // Act
        await ThrowsAsync<ConflictException>(
            () => fixture.Handler.HandleAsync(fixture.CommandFor($" \t{NewSubject} ", NewAddress)));

        // Assert — the pre-check first, then the re-read, each asked about the trimmed subject.
        await Assert.That(fixture.Users.FederatedLookupSubjects)
            .IsEquivalentTo(new[] { NewSubject, NewSubject }, CollectionOrdering.Matching);
    }

    /// <summary>
    /// A save refused on the credential's <c>(provider, subject)</c> index is a Google identity another
    /// account holds, and the sweep before it does not commit.
    /// </summary>
    /// <remarks>
    /// The pre-check found nobody, so this is the race it cannot close: another account filed the
    /// subject between that read and this save. The answer is the pre-check's own, because the fact is
    /// the same one reached later.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenApplyAnswersSubjectTaken_AnswersProviderIdentityInUse()
    {
        // Arrange
        Fixture fixture = Fixture.Build();
        await fixture.SeedLiveSessionsAsync(fixture.Federated, count: 1);
        fixture.EmailChanges.Outcome = EmailChangeOutcome.SubjectTaken;

        // Act
        ConflictException conflict = await ThrowsAsync<ConflictException>(
            () => fixture.Handler.HandleAsync(fixture.CommandFor(NewSubject, NewAddress)));

        // Assert — and the sweep that ran before the refused save was not committed with it.
        await Assert.That(conflict.Kind).IsEqualTo(ConflictKind.ProviderIdentityInUse);
        await Assert.That(fixture.Executor.Committed).IsEqualTo(0);
    }

    /// <summary>
    /// The command names no account, so the only account any port may be handed is the session's.
    /// </summary>
    /// <remarks>
    /// A new subject and a new address, so every member of the port is reached — the account, its
    /// credential and the save — and each is asked about the one id <see cref="IUserContext" /> carries.
    /// </remarks>
    [Test]
    public async Task HandleAsync_UsesTheSessionsAccountIdAndNeverAnIdFromTheCommand()
    {
        // Arrange
        Fixture fixture = Fixture.Build();

        // Act
        await fixture.Handler.HandleAsync(fixture.CommandFor(NewSubject, NewAddress));

        // Assert
        await Assert.That(fixture.EmailChanges.ApplyCalls.Count).IsEqualTo(1);
        await Assert.That(fixture.EmailChanges.UserIdsReceived.Distinct().ToList())
            .IsEquivalentTo(new[] { fixture.User.Id });
        await Assert.That(fixture.EmailChanges.ApplyCalls.Single().Filed?.UserId).IsEqualTo(fixture.User.Id);
    }

    /// <summary>
    /// Runs <paramref name="action" /> and returns the exception it was expected to throw.
    /// </summary>
    /// <remarks>
    /// The catch names <typeparamref name="TException" /> exactly, so an exception of any other type
    /// escapes and fails the test as itself.
    /// </remarks>
    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    /// <summary>
    /// The handler, the real gate it proves through, and the fakes behind both.
    /// </summary>
    private sealed record Fixture(
        ChangeEmailHandler Handler,
        User User,
        Credential Federated,
        Credential Passkey,
        InMemoryEmailChangeRepository EmailChanges,
        InMemoryUserRepository Users,
        InMemorySessionRepository Sessions,
        CommitRecordingTransactionalExecutor Executor,
        RecordingPersistenceState PersistenceState,
        ReauthenticationAssertion Assertion,
        List<string> CallLog)
    {
        /// <summary>The change the provider asserted, proved by the account's passkey.</summary>
        public ChangeEmailCommand CommandFor(string subject, string email) => new(subject, email, Assertion);

        /// <summary>Files <paramref name="count" /> live sessions opened by <paramref name="credential" />.</summary>
        public async Task SeedLiveSessionsAsync(Credential credential, int count)
        {
            for (int session = 0; session < count; session++)
            {
                await Sessions.AddAsync(Session.Establish(credential, UtcNow, UtcNow.AddHours(1)));
            }
        }

        /// <param name="challengeIsLive">
        /// Whether the store holds the bytes the device signed. False is how the gate refuses.
        /// </param>
        /// <param name="seedFederatedCredential">
        /// Whether the account holds its federated credential — which every real account does.
        /// </param>
        /// <param name="replaying">
        /// A replaying executor to run the unit of work on in place of the commit-recording one; the
        /// persistence state then records each discard against the executor's attempt number.
        /// </param>
        public static Fixture Build(
            bool challengeIsLive = true,
            bool seedFederatedCredential = true,
            RetryingTransactionalExecutor? replaying = null)
        {
            User user = User.CreateWithId(Guid.CreateVersion7(), Address, UtcNow);
            Credential federated = Credential.CreateFederated(user.Id, Credential.GoogleProvider, Subject, UtcNow);
            Credential passkey = Credential.CreatePasskey(user.Id, UtcNow);

            List<string> callLog = [];

            // The account as the email-change port sees it, and the subject as the discovery lookup sees
            // it. Two fakes over one account, because production reads it through two ports.
            InMemoryEmailChangeRepository emailChanges = new(callLog);
            InMemoryUserRepository users = new(new InMemoryTransactionRepository());
            if (seedFederatedCredential)
            {
                emailChanges.Seed(user, federated, passkey);
                users.Seed(user, federated);
            }
            else
            {
                emailChanges.Seed(user, passkey);
            }

            SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
            InMemoryPasskeyRepository passkeys = new();
            passkeys.Register(
                passkey,
                PasskeyPublicKey.Register(passkey, device.CredentialId, device.CoseKey, device.Algorithm),
                signatureCounter: 0);

            byte[] signedChallenge = RandomNumberGenerator.GetBytes(ChallengeBytes);
            byte[] storedChallenge = challengeIsLive
                ? signedChallenge
                : RandomNumberGenerator.GetBytes(ChallengeBytes);
            StubWebAuthnChallengeStore challenges = new(storedChallenge, WebAuthnCeremony.Reauthentication);
            AssertionResult assertion = device.Authenticate(signedChallenge, Origin, userHandle: null);

            StubUserContext userContext = new(user.Id);
            InMemorySessionRepository sessions = new();
            CommitRecordingTransactionalExecutor executor = new();
            FakeTimeProvider clock = new(new DateTimeOffset(UtcNow));
            RecordingPersistenceState persistenceState = replaying is null
                ? new RecordingPersistenceState(() => 1)
                : new RecordingPersistenceState(() => replaying.Attempts);

            ChangeEmailHandler handler = new(
                emailChanges,
                users,
                userContext,
                persistenceState,
                (ITransactionalExecutor?)replaying ?? executor,
                new PasskeyReauthentication(
                    challenges,
                    passkeys,
                    userContext,
                    new StubPasskeyCeremonyPolicy(RelyingPartyId, Origin)),
                new RevokeSessionsForCredentialHandler(new LoggingSessionRepository(sessions, callLog), clock),
                clock);

            return new Fixture(
                handler,
                user,
                federated,
                passkey,
                emailChanges,
                users,
                sessions,
                executor,
                persistenceState,
                new ReauthenticationAssertion(
                    assertion.CredentialIdBase64Url,
                    assertion.ClientDataJsonBase64Url,
                    assertion.AuthenticatorDataBase64Url,
                    assertion.SignatureBase64Url,
                    assertion.UserHandleBase64Url),
                callLog);
        }
    }

    /// <summary>
    /// Writes the credential sweep to the call log the email-change fake shares, then forwards.
    /// </summary>
    private sealed class LoggingSessionRepository(ISessionRepository inner, List<string> callLog)
        : ISessionRepository
    {
        public static string RevokeEntryFor(Guid credentialId) => $"revoke-sessions:{credentialId}";

        public Task AddAsync(Session session, SessionToken token, CancellationToken cancellationToken = default) =>
            inner.AddAsync(session, token, cancellationToken);

        public Task<Session?> FindByIdAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            inner.FindByIdAsync(sessionId, cancellationToken);

        public Task<int> RevokeForCredentialAsync(
            Guid credentialId,
            DateTime revokedAtUtc,
            CancellationToken cancellationToken = default)
        {
            callLog.Add(RevokeEntryFor(credentialId));

            return inner.RevokeForCredentialAsync(credentialId, revokedAtUtc, cancellationToken);
        }

        public Task<bool> RevokeAsync(Guid sessionId, DateTime revokedAtUtc, CancellationToken cancellationToken = default) =>
            inner.RevokeAsync(sessionId, revokedAtUtc, cancellationToken);
    }
}
