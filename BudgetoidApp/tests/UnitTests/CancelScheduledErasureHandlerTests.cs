using System.Data.Common;
using System.Security.Cryptography;
using Application.Abstractions;
using Application.Erasure.CancelScheduledErasure;
using Application.Passkeys;
using Application.Passkeys.Reauthentication;
using Domain.Erasure;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using TestSupport;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// What the handler behind <c>POST /api/me/erasure/schedule/cancellation</c> removes, for whom, and where
/// the re-authentication gate sits relative to every read of the schedule.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate is a real <see cref="PasskeyReauthentication" /> over fakes</b>, for the reason
/// <see cref="EraseAccountHandlerTests" /> gives: a stubbable gate would let a test here prove the
/// cancellation works with the gate faked out, which is the one thing that must never be provable.
/// </para>
/// <para>
/// <b>The gate runs before the schedule is read, and runs when there is nothing to read.</b> Both are
/// outcome pins over a single-use challenge store: a refused assertion leaves the repository untouched,
/// and an account with no schedule still spends its nonce. Gate first, because a 204 then only ever goes
/// to a caller who proved a passkey, so the answer to a bad proof never depends on what the database
/// holds. And spent either way, because a handler that looked first and returned early on "nothing
/// scheduled" would leave the requested nonce live in the shared re-authentication pool, spendable at
/// <c>POST /api/me/erasure</c> for its five minutes — hygiene, not a threat boundary.
/// </para>
/// <para>
/// <b>The identity comes from <c>IUserContext</c> and from nowhere else.</b> The command carries the
/// assertion and no member to name an account; these cases show that the account the context resolves is
/// the one whose row goes, with a second account's row beside it.
/// </para>
/// </remarks>
public sealed class CancelScheduledErasureHandlerTests
{
    /// <summary>
    /// A refused assertion throws before the repository is asked anything, and the schedule stands.
    /// </summary>
    /// <remarks>
    /// The nonce is drawn from the sign-in pool, so the gate refuses it at the ceremony check. A handler
    /// that read the schedule first — to skip the gate when there is nothing to cancel — records a read
    /// here before the refusal.
    /// </remarks>
    [Test]
    public async Task HandleAsync_RunsTheGateBeforeAnyRead()
    {
        // Arrange
        Fixture fixture = Fixture.Build(ceremony: WebAuthnCeremony.Authentication);
        fixture.Schedules.Seed(ErasureSchedule.Request(fixture.UserId, FiledAt, Delay));

        // Act
        await ThrowsRefusalAsync(() => fixture.Handler.HandleAsync(fixture.Command));

        // Assert
        await Assert.That(fixture.Schedules.ReadCalls).IsEqualTo(0);
        await Assert.That(fixture.Schedules.RemoveCalls).IsEqualTo(0);
        await Assert.That(fixture.Schedules.Stored.Keys.ToArray()).IsEquivalentTo(new[] { fixture.UserId });
    }

    /// <summary>
    /// On an accepted assertion the nonce is consumed before the schedule is read — the order, observed
    /// on one log the challenge store and the repository both write to.
    /// </summary>
    /// <remarks>
    /// The refusal case above holds the ordering only for an assertion the gate turns down. This holds it
    /// for one it accepts: a handler that read the schedule, then ran the gate, then removed would pass the
    /// refusal case (the gate still throws before the remove) and fail here.
    /// </remarks>
    [Test]
    public async Task HandleAsync_ConsumesTheNonceBeforeReadingTheSchedule()
    {
        // Arrange
        List<string> log = [];
        Fixture fixture = Fixture.Build(log: log);
        fixture.Schedules.Seed(ErasureSchedule.Request(fixture.UserId, FiledAt, Delay));

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert
        await Assert.That(log.Count).IsGreaterThan(0);
        await Assert.That(log[0]).IsEqualTo("consume");
        await Assert.That(log.IndexOf("consume")).IsLessThan(log.FindIndex(entry => entry.StartsWith("read", StringComparison.Ordinal)));
    }

    /// <summary>
    /// With an accepted assertion and a schedule standing, the schedule is removed.
    /// </summary>
    /// <remarks>
    /// The control for the refusal case: a handler that threw on every call satisfies "nothing was read".
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenAScheduleStands_RemovesIt()
    {
        // Arrange
        Fixture fixture = Fixture.Build();
        fixture.Schedules.Seed(ErasureSchedule.Request(fixture.UserId, FiledAt, Delay));

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert
        await Assert.That(fixture.Schedules.Stored.Count).IsEqualTo(0);
        await Assert.That(fixture.Schedules.RemoveCalls).IsEqualTo(1);
        await Assert.That(fixture.Challenges.ConsumeCallCount).IsEqualTo(1);
    }

    /// <summary>
    /// With nothing scheduled the handler completes, removes nothing, and still spends the nonce.
    /// </summary>
    /// <remarks>
    /// The post-condition — no schedule stands — already holds, so completing is
    /// the answer. The consume count is what pins the gate: a handler that returned early on "nothing to
    /// cancel" completes here too, with the count at zero.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenNothingIsScheduled_RemovesNothing_AndStillRunsTheGate()
    {
        // Arrange
        Fixture fixture = Fixture.Build();

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert
        await Assert.That(fixture.Challenges.ConsumeCallCount).IsEqualTo(1);
        await Assert.That(fixture.Schedules.RemoveCalls).IsEqualTo(0);
        await Assert.That(fixture.Schedules.Stored.Count).IsEqualTo(0);
    }

    /// <summary>
    /// When the delete meets a row a concurrent cancel already removed, the handler completes.
    /// </summary>
    /// <remarks>
    /// Staged by a repository whose read finds the row and whose remove reports it already gone — the
    /// order two cancels from two tabs reach the calls in. Without an executor in this story a zero-row
    /// delete has no other cause, and the post-condition holds, so the second tab is answered 204 rather
    /// than a 500 over a cancellation that happened.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenAConcurrentCancelRemovedTheRow_Completes()
    {
        // Arrange
        ConcurrentlyCancelledRepository schedules = new();
        Fixture fixture = Fixture.Build(schedules: schedules);
        schedules.Standing = ErasureSchedule.Request(fixture.UserId, FiledAt, Delay);

        // Act
        Exception? escaped = await CaptureAsync(() => fixture.Handler.HandleAsync(fixture.Command));

        // Assert
        await Assert.That(escaped).IsNull();
        await Assert.That(schedules.RemoveCalls).IsEqualTo(1);
    }

    /// <summary>
    /// When the delete fails for any reason the repository does not answer as
    /// <see cref="ScheduleRemoval.AlreadyGone" />, the failure escapes the handler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The only failure the handler may absorb is the one the repository already turned into an
    /// answer.</b> A lost grant, a dropped connection, a conflict over some other entity the save flushed:
    /// each leaves the schedule standing. Swallowed here, the route answers 204, the client hides the
    /// notice, and the account is erased on the date the owner believes withdrawn.
    /// </para>
    /// <para>
    /// One row per shape the adapter lets through: an <see cref="InvalidOperationException" /> as EF raises
    /// for a broken context, a <see cref="DbException" /> as Npgsql raises for a refused statement or a
    /// lost connection, and a <see cref="DbUpdateConcurrencyException" /> naming no schedule entry — the
    /// conflict <c>ErasureScheduleRepository.IsAlreadyRemoved</c> refuses to read as a lost race. The
    /// thrown instance is compared by reference, so a handler that wrapped or replaced it reddens too.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(RemoveFailure.InvalidOperation)]
    [Arguments(RemoveFailure.Database)]
    [Arguments(RemoveFailure.ForeignConcurrencyConflict)]
    public async Task HandleAsync_WhenRemoveThrows_LetsTheExceptionEscape(RemoveFailure failure)
    {
        // Arrange
        Exception thrown = failure switch
        {
            RemoveFailure.InvalidOperation => new InvalidOperationException("The context is unusable."),
            RemoveFailure.Database => new RefusedStatementException(),
            RemoveFailure.ForeignConcurrencyConflict => new DbUpdateConcurrencyException("Another entity conflicted."),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
        };
        FailingRemoveRepository schedules = new(thrown);
        Fixture fixture = Fixture.Build(schedules: schedules);
        schedules.Standing = ErasureSchedule.Request(fixture.UserId, FiledAt, Delay);

        // Act
        Exception? escaped = await CaptureAsync(() => fixture.Handler.HandleAsync(fixture.Command));

        // Assert
        await Assert.That(schedules.RemoveCalls).IsEqualTo(1);
        await Assert.That(escaped).IsSameReferenceAs(thrown);
    }

    /// <summary>
    /// The schedule removed is the one filed under <see cref="IUserContext.UserId" />, and another
    /// account's row beside it stands.
    /// </summary>
    /// <remarks>
    /// The fake answers a read only for the account it is asked about, so a handler that removed "a"
    /// schedule rather than this account's — or every schedule — shows up as the stranger's row missing.
    /// </remarks>
    [Test]
    public async Task HandleAsync_ReadsTheScheduleOfIUserContextUserId_Only()
    {
        // Arrange
        Fixture fixture = Fixture.Build();
        Guid strangerId = Guid.CreateVersion7();
        fixture.Schedules.Seed(ErasureSchedule.Request(strangerId, FiledAt.AddDays(1), Delay));
        fixture.Schedules.Seed(ErasureSchedule.Request(fixture.UserId, FiledAt, Delay));

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert
        await Assert.That(fixture.Schedules.Stored.Keys.ToArray()).IsEquivalentTo(new[] { strangerId });
        await Assert.That(fixture.Schedules.Stored[strangerId].TakesEffectAtUtc).IsEqualTo(FiledAt.AddDays(1) + Delay);
    }

    /// <summary>
    /// A genuine assertion from an authenticator registered to somebody else removes nothing.
    /// </summary>
    /// <remarks>
    /// The account binding at unit level. No user handle on the assertion, so only the owner-scoped lookup
    /// can refuse it; a gate reusing the unscoped discovery lookup verifies the signature and the request's
    /// own schedule goes on the strength of a stranger's device.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithAPasskeyBelongingToAnotherAccount_ThrowsAndRemovesNothing()
    {
        // Arrange
        Fixture fixture = Fixture.Build(passkeyBelongsToAnotherAccount: true);
        fixture.Schedules.Seed(ErasureSchedule.Request(fixture.UserId, FiledAt, Delay));

        // Act
        await ThrowsRefusalAsync(() => fixture.Handler.HandleAsync(fixture.Command));

        // Assert
        await Assert.That(fixture.Schedules.RemoveCalls).IsEqualTo(0);
        await Assert.That(fixture.Schedules.Stored.Keys.ToArray()).IsEquivalentTo(new[] { fixture.UserId });
    }

    private static readonly DateTime FiledAt = new(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Delay = TimeSpan.FromDays(7);

    private const string RelyingPartyId = "localhost";
    private const string Origin = "https://localhost:4200";
    private const int ChallengeBytes = 32;

    private static async Task<PasskeyVerificationException> ThrowsRefusalAsync(Func<Task> cancellation)
    {
        try
        {
            await cancellation();
        }
        catch (PasskeyVerificationException refusal)
        {
            return refusal;
        }

        throw new InvalidOperationException("Expected the cancellation to be refused.");
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// The handler, the gate it cancels through, and the collaborators behind both.
    /// </summary>
    private sealed record Fixture(
        CancelScheduledErasureHandler Handler,
        CancelScheduledErasureCommand Command,
        InMemoryErasureScheduleRepository Schedules,
        StubWebAuthnChallengeStore Challenges,
        Guid UserId)
    {
        private static readonly DateTime UtcNow = new(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);

        /// <param name="ceremony">Which pool the store says the nonce was drawn from.</param>
        /// <param name="passkeyBelongsToAnotherAccount">
        /// Whether the registered passkey is filed under somebody other than the request's account.
        /// </param>
        /// <param name="log">
        /// When given, the challenge store and the repository both append to it, so a test can read the
        /// order the handler reached them in.
        /// </param>
        /// <param name="schedules">A repository to use in place of the in-memory one.</param>
        public static Fixture Build(
            WebAuthnCeremony ceremony = WebAuthnCeremony.Reauthentication,
            bool passkeyBelongsToAnotherAccount = false,
            List<string>? log = null,
            IErasureScheduleRepository? schedules = null)
        {
            Guid userId = Guid.CreateVersion7();
            InMemoryErasureScheduleRepository inMemory = new();

            Guid passkeyOwnerId = passkeyBelongsToAnotherAccount ? Guid.CreateVersion7() : userId;
            SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
            Credential passkey = Credential.CreatePasskey(passkeyOwnerId, UtcNow);
            InMemoryPasskeyRepository passkeys = new();
            passkeys.Register(
                passkey,
                PasskeyPublicKey.Register(passkey, device.CredentialId, device.CoseKey, device.Algorithm),
                signatureCounter: 0);

            byte[] challenge = RandomNumberGenerator.GetBytes(ChallengeBytes);
            StubWebAuthnChallengeStore challenges = new(challenge, ceremony);

            // No user handle, for the reason EraseAccountHandlerTests gives: it keeps a stranger's
            // credential refused by the owner-scoped lookup alone.
            AssertionResult assertion = device.Authenticate(challenge, Origin, userHandle: null);
            StubUserContext userContext = new(userId);

            IWebAuthnChallengeStore store = log is null ? challenges : new LoggingChallengeStore(challenges, log);
            IErasureScheduleRepository repository =
                schedules ?? (log is null ? inMemory : new LoggingScheduleRepository(inMemory, log));

            CancelScheduledErasureHandler handler = new(
                repository,
                userContext,
                new PasskeyReauthentication(
                    store,
                    passkeys,
                    userContext,
                    new StubPasskeyCeremonyPolicy(RelyingPartyId, Origin)));

            return new Fixture(
                handler,
                new CancelScheduledErasureCommand(new ReauthenticationAssertion(
                    assertion.CredentialIdBase64Url,
                    assertion.ClientDataJsonBase64Url,
                    assertion.AuthenticatorDataBase64Url,
                    assertion.SignatureBase64Url,
                    assertion.UserHandleBase64Url)),
                inMemory,
                challenges,
                userId);
        }
    }

    /// <summary>Forwards to the stub, writing "consume" to the log first.</summary>
    private sealed class LoggingChallengeStore(IWebAuthnChallengeStore inner, List<string> log) : IWebAuthnChallengeStore
    {
        public Task<IssuedChallenge> IssueAsync(WebAuthnCeremony ceremony, CancellationToken cancellationToken = default) =>
            inner.IssueAsync(ceremony, cancellationToken);

        public Task<WebAuthnCeremony?> ConsumeAsync(ReadOnlyMemory<byte> presented, CancellationToken cancellationToken = default)
        {
            log.Add("consume");
            return inner.ConsumeAsync(presented, cancellationToken);
        }
    }

    /// <summary>Forwards to the in-memory repository, writing each call's name to the log first.</summary>
    private sealed class LoggingScheduleRepository(InMemoryErasureScheduleRepository inner, List<string> log)
        : IErasureScheduleRepository
    {
        public Task<ErasureSchedule?> FindAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            log.Add("read");
            return inner.FindAsync(userId, cancellationToken);
        }

        public Task<ErasureSchedule?> FindTrackedAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            log.Add("read tracked");
            return inner.FindTrackedAsync(userId, cancellationToken);
        }

        public Task<ErasureSchedule> AddAsync(ErasureSchedule schedule, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A cancellation files no schedule.");

        public Task<ScheduleRemoval> RemoveAsync(ErasureSchedule schedule, CancellationToken cancellationToken = default)
        {
            log.Add("remove");
            return inner.RemoveAsync(schedule, cancellationToken);
        }
    }

    /// <summary>
    /// A repository in which another tab's cancel lands between this request's read and its remove: the
    /// read finds the row, the remove reports it already gone.
    /// </summary>
    private sealed class ConcurrentlyCancelledRepository : IErasureScheduleRepository
    {
        public ErasureSchedule? Standing { get; set; }

        public int RemoveCalls { get; private set; }

        public Task<ErasureSchedule?> FindAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Standing?.UserId == userId ? Standing : null);

        public Task<ErasureSchedule?> FindTrackedAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Standing?.UserId == userId ? Standing : null);

        public Task<ErasureSchedule> AddAsync(ErasureSchedule schedule, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A cancellation files no schedule.");

        public Task<ScheduleRemoval> RemoveAsync(ErasureSchedule schedule, CancellationToken cancellationToken = default)
        {
            RemoveCalls++;
            return Task.FromResult(ScheduleRemoval.AlreadyGone);
        }
    }

    /// <summary>The failure <see cref="HandleAsync_WhenRemoveThrows_LetsTheExceptionEscape" /> stages.</summary>
    public enum RemoveFailure
    {
        InvalidOperation,
        Database,
        ForeignConcurrencyConflict,
    }

    /// <summary>A provider's refusal of the DELETE, as a lost grant or a dropped connection raises it.</summary>
    private sealed class RefusedStatementException() : DbException("The statement was refused.");

    /// <summary>
    /// A repository whose read finds the row and whose remove throws <paramref name="failure" /> — the
    /// failures the adapter does not translate into <see cref="ScheduleRemoval.AlreadyGone" />.
    /// </summary>
    private sealed class FailingRemoveRepository(Exception failure) : IErasureScheduleRepository
    {
        public ErasureSchedule? Standing { get; set; }

        public int RemoveCalls { get; private set; }

        public Task<ErasureSchedule?> FindAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Standing?.UserId == userId ? Standing : null);

        public Task<ErasureSchedule?> FindTrackedAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Standing?.UserId == userId ? Standing : null);

        public Task<ErasureSchedule> AddAsync(ErasureSchedule schedule, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A cancellation files no schedule.");

        public Task<ScheduleRemoval> RemoveAsync(ErasureSchedule schedule, CancellationToken cancellationToken = default)
        {
            RemoveCalls++;
            return Task.FromException<ScheduleRemoval>(failure);
        }
    }
}
