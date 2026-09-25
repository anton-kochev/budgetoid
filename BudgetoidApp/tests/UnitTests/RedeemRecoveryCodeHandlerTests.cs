using System.Security.Cryptography;
using Application.Abstractions;
using Application.RecoveryCodes;
using Application.RecoveryCodes.RedeemRecoveryCode;
using Domain.Sessions;
using Domain.Users;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// Signing in with one recovery code, as a unit of work: what it writes, in what order, and what it
/// leaves behind when the transaction never opens, when the delegate is replayed, and when the code is
/// spent out from under it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only anonymous route in the application that performs a <c>DELETE</c>, and the only
/// one where the order of two adjacent lines is the difference between a working route and
/// <c>22P02</c> on every request.</b> <c>RecoveryCodeRedemptionTests</c> drives the same handler over
/// real HTTP against a real database and holds everything a request can observe — the byte-identical
/// refusal, the account the session lands on, the rows that survive. What it cannot observe is the
/// shape of the unit of work: whether a transaction was asked for at all, whether the tracked entities
/// were discarded inside each attempt, whether the row that was spent came from the read that named
/// its owner, and whether the identity was published before the connection was configured. Each of
/// those is a production line that can be deleted with every integration test still green, and each
/// one below is written to redden on exactly one of them.
/// </para>
/// <para>
/// <b>What a fake cannot hold, and is not pretended here.</b> The repository's own predicates are
/// invisible from this side — <see cref="InMemoryRecoveryCodeRepository.OwnerScopedLookups" /> records
/// the account a lookup was asked to scope by, never the <c>where user_id = …</c> the real query
/// carries — and the translation of EF's <c>DbUpdateConcurrencyException</c> into the redemption
/// refusal lives in <c>RecoveryCodeRepository</c>, which needs a database to raise it.
/// <see cref="HandleAsync_WhenTheCodeIsSpentByAConcurrentRedemption_RefusesAndOpensNoSession" /> holds
/// the handler's half of that — a consume refusal travels out untouched and costs no session — and
/// says so rather than implying it holds the catch.
/// </para>
/// <para>
/// <b>The two positive tests are the control for every refusal below.</b> A handler that threw at its
/// first line satisfies "no session was opened" and "no code was spent" perfectly, and would pass six
/// of the tests here; the pair asserting that the door opens for somebody is what stops that reading.
/// </para>
/// </remarks>
public sealed class RedeemRecoveryCodeHandlerTests
{
    /// <summary>
    /// How long a recovery-code sign-in lasts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Restated here rather than read off the handler, because the number is the pin</b> — a test
    /// taking its expectation from the type under test agrees with whatever that type later decides,
    /// and <c>TimeSpan.FromDays(14000)</c> would leave this file green while an intercepted code bought
    /// a session that never practically expires. It is asserted as an <em>observable</em> expiry
    /// against a fixed clock, so the consolidation of the three handlers' private constants into
    /// <c>SessionPolicy.Lifetime</c> moved not a line here and left this file meaning what it meant.
    /// </para>
    /// <para>
    /// The same interval a passkey sign-in and a regeneration get, and the equality is a rule rather
    /// than a coincidence: see <see cref="EstablishedSessionLifetimeTests" />, which is where the three
    /// paths are compared with each other.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

    /// <summary>How many codes a set holds, restated for the reason <see cref="SessionLifetime" /> is.</summary>
    private const int CodesPerSet = 10;

    /// <summary>
    /// The exact width of a verifier, decoded — <c>RecoveryCodeHash.VerifierLength</c>, restated for
    /// the same reason.
    /// </summary>
    private const int VerifierLength = 32;

    /// <summary>
    /// How many times the executor runs the unit of work in the replay tests. Two is the smallest
    /// number that is a replay at all, and nothing they measure gets sharper with more.
    /// </summary>
    private const int ReplayedAttempts = 2;

    /// <summary>Fixed instant the handler stamps this request with, so nothing here reads a wall clock.</summary>
    private static readonly DateTime UtcNow = new(2026, 8, 11, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>When the sets these tests redeem from were issued.</summary>
    private static readonly DateTime IssuedEarlier = UtcNow.AddDays(-30);

    /// <summary>
    /// A stored code opens a full session, stamped with the handler's own instant and expiring
    /// <see cref="SessionLifetime" /> after it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The control for every refusal in this file</b>, and the only test here that says the door
    /// opens for anybody at all.
    /// </para>
    /// <para>
    /// <b>Full is derived rather than chosen.</b> <see cref="Session.Establish" /> reads the kind off
    /// the <see cref="Credential" /> it is handed, which is what makes "a sign-in reaching more of the
    /// account than its credential may" unrepresentable — so asserting the kind is really asserting
    /// that the session was opened over the matched code's <em>own set</em>, and not fabricated from a
    /// kind somebody named.
    /// </para>
    /// <para>
    /// <b>The expiry is asserted on the response and on the stored row against one expression</b>,
    /// because the member a client renders and the row the server enforces disagreeing is exactly the
    /// defect worth catching: it tells a person they have longer than they do. And it is an equality
    /// rather than "later than now", which <c>TimeSpan.FromDays(14000)</c> satisfies.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithAStoredCode_OpensAFullSessionAtTheHandlersInstant()
    {
        // Arrange
        Fixture fixture = Fixture.Build();

        // Act
        RedeemedRecoveryCode redeemed =
            (await fixture.Handler.HandleAsync(fixture.CommandFor(0))).Value;

        // Assert — what the caller is told.
        await Assert.That(redeemed.Kind).IsEqualTo(SessionKind.Full);
        await Assert.That(redeemed.ExpiresAtUtc).IsEqualTo(UtcNow + SessionLifetime);

        // And the row that was written, which is what the caller was told about.
        await Assert.That(fixture.Sessions.Sessions.Count).IsEqualTo(1);

        Session established = fixture.Sessions.Sessions[0];
        await Assert.That(established.Kind).IsEqualTo(SessionKind.Full);
        await Assert.That(established.CredentialType).IsEqualTo(CredentialType.RecoveryCodes);
        await Assert.That(established.UserId).IsEqualTo(fixture.UserId);
        await Assert.That(established.CredentialId).IsEqualTo(fixture.Set.Id);
        await Assert.That(established.CreatedAtUtc).IsEqualTo(UtcNow);
        await Assert.That(established.ExpiresAtUtc).IsEqualTo(UtcNow + SessionLifetime);
    }

    /// <summary>
    /// Exactly the presented code is spent, the count reported is what the card is worth now, and the
    /// other account's card is untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The consumed row is identified by value, not by count.</b> Nine rows and the nine
    /// <em>correct</em> rows are the same number, so a handler that removed an arbitrary row of the set
    /// would leave the same count behind while invalidating a code the person is still holding.
    /// </para>
    /// <para>
    /// <b>A second account holds a set of its own, and that is what makes the count a claim.</b> With
    /// one account in the store, "how many codes does this account hold" and "how many codes are
    /// stored" are the same number, so a count that had lost its owner predicate — the predicate being
    /// the only thing that narrows a read of a table no policy narrows, per ADR 0011 — would answer
    /// correctly anyway. <see cref="InMemoryRecoveryCodeReadService.CountedFor" /> is asserted beside
    /// it because the number can also be right while the account it was asked about is wrong.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithAStoredCode_SpendsThatCodeAloneAndReportsWhatIsLeft()
    {
        // Arrange
        Fixture fixture = Fixture.Build();

        // Act
        RedeemedRecoveryCode redeemed =
            (await fixture.Handler.HandleAsync(fixture.CommandFor(0))).Value;

        // Assert — the number in the response, and the rows it is a number about.
        await Assert.That(redeemed.Remaining).IsEqualTo(CodesPerSet - 1);
        await Assert.That(fixture.StoredHashesOf(fixture.UserId))
            .IsEquivalentTo(ExpectedHashesOf(fixture.Verifiers.Skip(1)));

        // The bystander spent nothing, and was never counted.
        await Assert.That(fixture.StoredHashesOf(fixture.OtherUserId))
            .IsEquivalentTo(ExpectedHashesOf(fixture.OtherVerifiers));
        await Assert.That(fixture.ReadService.CountedFor).IsEquivalentTo(new[] { fixture.UserId });
    }

    /// <summary>
    /// A transaction that cannot be opened leaves no session and spends no code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mutation this controls for: deleting
    /// <c>transactionalExecutor.ExecuteAsync</c> and inlining the delegate.</b> Nothing else in either
    /// suite would notice.
    /// <see cref="InMemoryTransactionalExecutor" /> and <see cref="RetryingTransactionalExecutor" />
    /// both run the body, so against either of them an inlined handler writes the same rows in the same
    /// order and answers the same 200. The transaction is what makes
    /// <see cref="IRecoveryCodeRepository.ConsumeAsync" /> and
    /// <see cref="ISessionRepository.AddAsync" /> — which save separately, on purpose, so that FR-054's
    /// ordering is an ordering of <em>statements</em> — one unit of work. Without it a failure after the
    /// consume commits burns a code and opens no session, for somebody who has already lost their
    /// authenticator.
    /// </para>
    /// <para>
    /// <b>The failure is at the open rather than at the commit</b>, for the reason
    /// <see cref="UnopenableTransactionalExecutor" /> writes out: a commit failure would have to undo
    /// rows the fakes hold in plain lists, which would make the arrangement rather than the handler
    /// responsible for the state being asserted. Both are real, and both leave the same observable
    /// behind — a unit of work that did not commit wrote nothing.
    /// </para>
    /// <para>
    /// <b>The failure is asserted to escape as itself.</b> A handler that caught it and answered with a
    /// refusal of its own would leave the same empty store, and would tell somebody still holding nine
    /// good codes that the code they presented was no good — sending them to throw the card away over a
    /// database fault.
    /// </para>
    /// <para>
    /// <see cref="UnopenableTransactionalExecutor.ExecuteCallCount" /> is asserted because "the handler
    /// asked for a transaction and was refused one" and "the handler never asked" produce the identical
    /// empty store, and only the second is the mutation worth catching.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheTransactionCannotBeOpened_SpendsNoCodeAndOpensNoSession()
    {
        // Arrange
        Fixture fixture = Fixture.Build(theTransactionOpens: false);

        // Act
        await ThrowsAsync<TransactionUnavailableException>(
            () => fixture.Handler.HandleAsync(fixture.CommandFor(0)));

        // Assert — it asked, and it was refused.
        await Assert.That(fixture.Unopenable!.ExecuteCallCount).IsEqualTo(1);

        // Nothing ran, so nothing was written: no session, and the card is whole.
        await Assert.That(fixture.Sessions.Sessions.Count).IsEqualTo(0);
        await Assert.That(fixture.StoredHashesOf(fixture.UserId))
            .IsEquivalentTo(ExpectedHashesOf(fixture.Verifiers));
    }

    /// <summary>
    /// The account is published <b>before</b> the transaction is opened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one ordering in this handler that has no other unit-level witness, and the one whose cost
    /// is total.</b> Opening a transaction opens a connection, and opening a connection is when
    /// <c>SessionContextInterceptor</c> runs its <c>set_config</c>. Publish after that and
    /// <c>app.current_user_id</c> reaches the database as <c>''</c>, so every policed statement inside
    /// the transaction fails with <c>22P02</c> — and <c>sessions</c> is policed by
    /// <c>user_isolation</c>, so the insert this handler performs is exactly such a statement. The
    /// route is not degraded by that mutation; it is dead.
    /// </para>
    /// <para>
    /// <b>An executor that never invokes the delegate is what makes the ordering observable at all.</b>
    /// Against an executor that runs it, both orderings publish before the insert and both write the
    /// same row. Here, a handler publishing inside the delegate has published nobody when this raises,
    /// and one publishing above it has published exactly the account the discovery read resolved.
    /// </para>
    /// <para>
    /// The value is asserted rather than the call, because the failure worth catching is a
    /// <em>wrong</em> account reaching the connection — the request's own, on a route whose caller is
    /// anonymous and whose browser may attach a bearer to everything.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_PublishesTheResolvedAccountBeforeItOpensTheTransaction()
    {
        // Arrange
        Fixture fixture = Fixture.Build(theTransactionOpens: false);

        // Act
        await ThrowsAsync<TransactionUnavailableException>(
            () => fixture.Handler.HandleAsync(fixture.CommandFor(0)));

        // Assert — published once, before the transaction was so much as attempted, and it is the
        // account the code named rather than any other in the store.
        await Assert.That(fixture.UserContext.Published).IsEquivalentTo(new[] { fixture.UserId });
        await Assert.That(fixture.Unopenable!.ExecuteCallCount).IsEqualTo(1);
    }

    /// <summary>
    /// A replayed unit of work opens exactly one session and spends exactly one code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mutation this controls for: deleting
    /// <c>persistenceState.DiscardTrackedEntities()</c> from the top of the delegate.</b> The API
    /// installs a retrying execution strategy, so a transient failure replays the body against a
    /// database that rolled the abandoned attempt back and a change tracker that did not. The session
    /// the abandoned attempt queued is the tracker's, not the database's — a <c>ROLLBACK</c> never saw
    /// it — so without the discard it is still queued when the surviving attempt commits: one
    /// redemption, two sessions. That is the outcome FR-054 exists to prevent, reached by a route other
    /// than the one it is written about, and no integration test can induce it against a healthy
    /// database.
    /// </para>
    /// <para>
    /// <b>The rollback is the test's own, and it has to be.</b> The fakes hold their rows in plain
    /// lists with nothing to undo, so the second attempt would otherwise meet a store in which the
    /// first attempt's <c>DELETE</c> had committed — a state production never produces, and one that
    /// would refuse the replay for a reason this test is not about. See
    /// <see cref="RetryingTransactionalExecutor" />: what a <c>ROLLBACK</c> puts back is the caller's to
    /// say, and what it must not put back is anything in the tracker, which is what keeps this
    /// measuring the discard.
    /// </para>
    /// <para>
    /// <b>The two read counts are the second half of the claim.</b> The discovery read runs once
    /// however many times the delegate is attempted, because it sits above the transaction — it is what
    /// establishes the account that has to be published before a connection is configured — and the
    /// owner-scoped read runs once per attempt, because the entity spent has to be one the surviving
    /// unit of work read. A handler with those two the other way round is green on every row count
    /// here.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheUnitOfWorkIsReplayed_OpensExactlyOneSessionAndSpendsOneCode()
    {
        // Arrange — the delegate runs twice, and a ROLLBACK puts the abandoned attempt's spent row back.
        Fixture fixture = Fixture.Build(attempts: ReplayedAttempts);
        fixture.RollBackAbandonedAttempt = fixture.RestoreTheSpentCode(0);

        // Act
        RedeemedRecoveryCode redeemed =
            (await fixture.Handler.HandleAsync(fixture.CommandFor(0))).Value;

        // Assert — one sign-in, one session, however many times the transaction was attempted.
        await Assert.That(fixture.Sessions.Sessions.Count).IsEqualTo(1);
        await Assert.That(fixture.Sessions.Sessions[0].UserId).IsEqualTo(fixture.UserId);

        // One code, and the nine left are the nine the person still holds.
        await Assert.That(redeemed.Remaining).IsEqualTo(CodesPerSet - 1);
        await Assert.That(fixture.StoredHashesOf(fixture.UserId))
            .IsEquivalentTo(ExpectedHashesOf(fixture.Verifiers.Skip(1)));

        // Read once above the transaction, once inside each attempt of it.
        await Assert.That(fixture.RecoveryCodes.DiscoveryLookupCallCount).IsEqualTo(1);
        await Assert.That(fixture.RecoveryCodes.OwnerScopedLookups.Count).IsEqualTo(ReplayedAttempts);
    }

    /// <summary>
    /// The discard is made inside every attempt, and never above the executor.
    /// </summary>
    /// <remarks>
    /// <b>Where the call is made, not merely that it is made.</b> A discard hoisted above the executor
    /// runs once and is undone by nothing — the rollback it exists to clean up after happens later — so
    /// the second attempt starts with the first attempt's leftovers exactly as if the line were absent,
    /// and every row count in
    /// <see cref="HandleAsync_WhenTheUnitOfWorkIsReplayed_OpensExactlyOneSessionAndSpendsOneCode" />
    /// would be the same as for the deletion. Recording the attempt each discard lands on is what tells
    /// the two placements apart; a plain call count cannot. <see cref="CompleteAssertionHandlerTests" />
    /// holds the identical claim for the assertion path.
    /// </remarks>
    [Test]
    public async Task HandleAsync_DiscardsTheTrackedEntitiesInsideEveryAttempt()
    {
        // Arrange
        Fixture fixture = Fixture.Build(attempts: ReplayedAttempts);
        fixture.RollBackAbandonedAttempt = fixture.RestoreTheSpentCode(0);

        // Act
        await fixture.Handler.HandleAsync(fixture.CommandFor(0));

        // Assert — once per attempt, and never on attempt zero, which is what a call outside the
        // executor would record.
        await Assert.That(fixture.PersistenceState.DiscardedOnAttempt).IsEquivalentTo(new[] { 1, 2 });
    }

    /// <summary>
    /// The row that is spent comes from a second read, taken inside the transaction and scoped by the
    /// account the discovery read resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mutation this controls for: deleting the second read and passing the discovery read's
    /// entity straight to <see cref="IRecoveryCodeRepository.ConsumeAsync" />.</b> Every other test in
    /// both suites is green after that edit — the same row is spent, the same session opens, the same
    /// count comes back — and what it drops is the third of ADR 0014's three legs: the read producing
    /// the entity and the write removing it no longer share a unit of work. It also drops the only
    /// owner predicate on the path, because the discovery read is the one statement forbidden to carry
    /// one: <c>recovery_code_hashes</c> is exempt from row-level security, an exempt table scopes
    /// nothing, and the entity travels straight on to a <c>DELETE</c> that an <em>anonymous</em>
    /// request performs.
    /// </para>
    /// <para>
    /// <b>An empty <see cref="InMemoryRecoveryCodeRepository.OwnerScopedLookups" /> is that mutation;
    /// an entry naming another account is the failure the predicate exists to refuse; one entry per
    /// attempt is what says the read sits inside the delegate.</b> A call counter states none of the
    /// three, which is why the values are recorded and compared.
    /// </para>
    /// <para>
    /// <b>What this cannot see is the repository's own <c>where user_id = …</c>.</b> A production
    /// lookup that dropped it would still be called with the right argument and would still be recorded
    /// here. That half is unobservable from any fake and from the route as well — the owner is read off
    /// the very row being matched — and it is held by the predicate being written down in
    /// <see cref="IRecoveryCodeRepository.FindOwnedByVerifierHashAsync" /> and by review.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_SpendsARowReadAgainInsideTheTransactionByTheResolvedAccount()
    {
        // Arrange
        Fixture fixture = Fixture.Build();

        // Act
        await fixture.Handler.HandleAsync(fixture.CommandFor(0));

        // Assert — one owner-scoped read, naming the account the code established and not the other one
        // the store holds.
        await Assert.That(fixture.RecoveryCodes.OwnerScopedLookups)
            .IsEquivalentTo(new[] { fixture.UserId });

        // And the unscoped discovery read ran once, which is what it is allowed to do: it is the
        // statement that establishes the identity every later statement is scoped by.
        await Assert.That(fixture.RecoveryCodes.DiscoveryLookupCallCount).IsEqualTo(1);
    }

    /// <summary>
    /// A code spent by a concurrent redemption between the read and the delete is refused, and the
    /// loser opens no session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mutation this controls for, on the handler's side: swallowing or dressing up the refusal
    /// <see cref="IRecoveryCodeRepository.ConsumeAsync" /> raises.</b> Two requests carrying one
    /// verifier both find the row and the loser's <c>DELETE</c> matches nothing; in production EF raises
    /// <c>DbUpdateConcurrencyException</c> and <c>RecoveryCodeRepository</c> translates it into the
    /// sentence asserted below. What this handler must do with it is nothing at all: let it out as the
    /// same <see cref="RecoveryCodeRedemptionException" /> every other refusal on this route is, so the
    /// loser gets the byte-identical 401 rather than a second answer telling them the value they
    /// presented was real.
    /// </para>
    /// <para>
    /// <b>The session count is the half that carries FR-054.</b> The code is spent before the session
    /// is established, and this is the arrangement where the two orderings part: the loser reaches the
    /// consume, is refused, and has written nothing. Reverse the two lines and the loser has already
    /// queued a session for a code it never spent — which over a real database is only unwound because
    /// the surrounding transaction rolls back, and is a replay window the moment that transaction is
    /// not there. <c>RecoveryCodeRedemptionTests</c> cannot show this: its second presentation is
    /// sequential and dies at the discovery read.
    /// </para>
    /// <para>
    /// <b>What this does <em>not</em> hold is the catch itself.</b> Deleting
    /// <c>RecoveryCodeRepository.ConsumeAsync</c>'s
    /// <c>catch (DbUpdateConcurrencyException) when (IsAlreadyConsumed(…))</c>, or widening it by
    /// removing the <c>when</c>, is invisible from here: the fake raises the sentence directly, because
    /// no in-memory store can produce EF's exception.
    /// <c>RecoveryCodeRepositoryTests.ConsumeAsync_WhenTheCodeIsAlreadyGone_RefusesTheRedemption</c>
    /// and its unrelated-conflict neighbour are what redden on those two.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheCodeIsSpentByAConcurrentRedemption_RefusesAndOpensNoSession()
    {
        // Arrange — the winner spends the contested row in the window between this request choosing it
        // and being handed it, which is the only place a fake can express the race.
        Fixture fixture = Fixture.Build();
        fixture.SpendOnTheNextOwnerScopedLookup(0);

        // Act
        RecoveryCodeRedemptionException refusal = await ThrowsAsync<RecoveryCodeRedemptionException>(
            () => fixture.Handler.HandleAsync(fixture.CommandFor(0)));

        // Assert — the repository's own sentence, unedited, on the exception type every refusal on this
        // route uses.
        await Assert.That(refusal.Reason)
            .IsEqualTo("The presented code was consumed by a concurrent redemption.");

        // The loser wrote nothing: the winner's row is gone and no second one went with it, and no
        // session was opened over a code this request did not spend.
        await Assert.That(fixture.Sessions.Sessions.Count).IsEqualTo(0);
        await Assert.That(fixture.StoredHashesOf(fixture.UserId))
            .IsEquivalentTo(ExpectedHashesOf(fixture.Verifiers.Skip(1)));
    }

    /// <summary>
    /// A verifier of the wrong shape is refused before the store is touched at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The claim the integration suite states it cannot make.</b> Over HTTP every entry here is
    /// byte-identical to every other refusal — deliberately — so nothing there can observe how deep one
    /// got, and <c>EveryReachableRedemptionRefusal_ProducesTheIdenticalResponse</c> says as much in its
    /// own remarks. The ceiling exists so that a caller cannot name how much work a refusal costs on an
    /// anonymous route, and <see cref="InMemoryRecoveryCodeRepository.DiscoveryLookupCallCount" /> at
    /// zero is the only place that is measurable.
    /// </para>
    /// <para>
    /// <b>Nobody is published either.</b> Publishing an account is what a matched row does; a malformed
    /// verifier has matched nothing, so a handler that resolved an identity here would be configuring a
    /// connection for a caller who has presented nothing at all.
    /// </para>
    /// <para>
    /// The short entry is the one a reader is likeliest to lose: the encoded ceiling admits 30, 31 or
    /// 32 bytes — four characters per three bytes — so the width is re-checked after the decode, and a
    /// short verifier is a shorter secret than the design claims.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(MalformedVerifier.Absent)]
    [Arguments(MalformedVerifier.Empty)]
    [Arguments(MalformedVerifier.NotBase64Url)]
    [Arguments(MalformedVerifier.Short)]
    [Arguments(MalformedVerifier.Oversized)]
    public async Task HandleAsync_WithAVerifierOfTheWrongShape_RefusesBeforeItReadsAnything(
        MalformedVerifier shape)
    {
        // Arrange
        Fixture fixture = Fixture.Build();

        // Act
        await ThrowsAsync<RecoveryCodeRedemptionException>(
            () => fixture.Handler.HandleAsync(new RedeemRecoveryCodeCommand(VerifierOf(shape))));

        // Assert — the store was never asked, nobody was published, and nothing was written.
        await Assert.That(fixture.RecoveryCodes.DiscoveryLookupCallCount).IsEqualTo(0);
        await Assert.That(fixture.RecoveryCodes.OwnerScopedLookups.Count).IsEqualTo(0);
        await Assert.That(fixture.UserContext.Published.Count).IsEqualTo(0);
        await Assert.That(fixture.Sessions.Sessions.Count).IsEqualTo(0);
        await Assert.That(fixture.StoredHashesOf(fixture.UserId))
            .IsEquivalentTo(ExpectedHashesOf(fixture.Verifiers));
    }

    /// <summary>
    /// A well-formed verifier no row answers to publishes nobody and opens nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The arm every wrong guess lands on, and the one an enumeration attempt would be built out
    /// of.</b> It is refused after the discovery read rather than before it — the read is what decides —
    /// and the point of the test is what does <em>not</em> happen next: nobody is published, no
    /// owner-scoped read is taken, and no transaction's worth of writes is attempted.
    /// </para>
    /// <para>
    /// <b>Publishing here would be the serious defect.</b> The account is published so that a policed
    /// connection can be configured for it; publishing one before a row matched would mean an
    /// anonymous caller's unmatched value had decided whose data the rest of the request runs as.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithAVerifierNoRowAnswersTo_PublishesNobodyAndWritesNothing()
    {
        // Arrange — a verifier of exactly the right width that names no stored row.
        Fixture fixture = Fixture.Build();
        string unknown = Base64UrlText.Encode(RandomNumberGenerator.GetBytes(VerifierLength));

        // Act
        await ThrowsAsync<RecoveryCodeRedemptionException>(
            () => fixture.Handler.HandleAsync(new RedeemRecoveryCodeCommand(unknown)));

        // Assert — it looked, once, and then stopped.
        await Assert.That(fixture.RecoveryCodes.DiscoveryLookupCallCount).IsEqualTo(1);
        await Assert.That(fixture.RecoveryCodes.OwnerScopedLookups.Count).IsEqualTo(0);
        await Assert.That(fixture.UserContext.Published.Count).IsEqualTo(0);
        await Assert.That(fixture.Sessions.Sessions.Count).IsEqualTo(0);

        // Both cards are whole.
        await Assert.That(fixture.StoredHashesOf(fixture.UserId))
            .IsEquivalentTo(ExpectedHashesOf(fixture.Verifiers));
        await Assert.That(fixture.StoredHashesOf(fixture.OtherUserId))
            .IsEquivalentTo(ExpectedHashesOf(fixture.OtherVerifiers));
    }

    /// <summary>
    /// The ways a verifier can be the wrong shape, as
    /// <see cref="HandleAsync_WithAVerifierOfTheWrongShape_RefusesBeforeItReadsAnything" /> drives them.
    /// </summary>
    /// <remarks>
    /// An enumeration rather than the values themselves, because two of them cannot be written as a
    /// literal argument — an oversized verifier is kilobytes of encoded text — and because a named case
    /// is what a failing run prints.
    /// </remarks>
    public enum MalformedVerifier
    {
        /// <summary>No member at all, which a body of <c>{}</c> binds to null.</summary>
        Absent,

        /// <summary>Present and empty, which is not the same code path as absent.</summary>
        Empty,

        /// <summary>The right width, carrying standard base64's two characters base64url replaces.</summary>
        NotBase64Url,

        /// <summary>A real base64url encoding of fewer bytes than a verifier is.</summary>
        Short,

        /// <summary>Far past the ceiling, and refused before anything is decoded.</summary>
        Oversized,
    }

    /// <summary>The text one <see cref="MalformedVerifier" /> is presented as.</summary>
    private static string? VerifierOf(MalformedVerifier shape) => shape switch
    {
        MalformedVerifier.Absent => null,
        MalformedVerifier.Empty => string.Empty,

        // Substituted into a good encoding rather than produced by Convert.ToBase64String, because a
        // random value encoded that way need contain neither character — its only guaranteed difference
        // is the '=' padding, which the decoder accepts deliberately. The length is untouched, so this
        // is refused for its alphabet and not for its width.
        MalformedVerifier.NotBase64Url => NotBase64Url(),
        MalformedVerifier.Short =>
            Base64UrlText.Encode(RandomNumberGenerator.GetBytes(VerifierLength - 1)),
        MalformedVerifier.Oversized =>
            Base64UrlText.Encode(RandomNumberGenerator.GetBytes(OversizedVerifierBytes)),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    /// <summary>
    /// How large the oversized verifier is: far enough over that no ceiling worth the name admits it,
    /// and small enough that the test costs nothing.
    /// </summary>
    private const int OversizedVerifierBytes = 4096;

    /// <summary>A verifier-shaped string carrying standard base64's two extra characters.</summary>
    private static string NotBase64Url()
    {
        char[] mangled = [.. Base64UrlText.Encode(RandomNumberGenerator.GetBytes(VerifierLength))];
        mangled[0] = '+';
        mangled[1] = '/';

        return new string(mangled);
    }

    /// <summary>The SHA-256 of each verifier, as hex, ordered — the value the rows hold.</summary>
    /// <remarks>
    /// Ordered on both sides of every comparison, so each one is about the set of stored values rather
    /// than about the order a fake's list happened to be in. Hex rather than the bytes, because a
    /// failure that prints two digests is one a reader can act on.
    /// </remarks>
    private static string[] ExpectedHashesOf(IEnumerable<byte[]> verifiers) =>
    [
        .. verifiers
            .Select(verifier => Convert.ToHexString(RecoveryCodeHash.HashOf(verifier).Span))
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// Runs <paramref name="action" /> and returns the exception it was expected to throw.
    /// </summary>
    /// <remarks>
    /// The catch names <typeparamref name="TException" /> exactly, so an exception of any other type
    /// escapes and fails the test as itself rather than as "the expected exception was not thrown" —
    /// which matters here, since the whole point of two of these tests is <em>which</em> exception
    /// reached the caller.
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
    /// The handler and every collaborator behind it, over two accounts that each hold a real set.
    /// </summary>
    /// <remarks>
    /// <b>Two accounts in every arrangement, not only in the tests about ownership.</b> With one set in
    /// the store, "this code's account" and "the only account there is" are the same answer, and a
    /// count or a lookup that had lost its owner would be right by accident everywhere.
    /// </remarks>
    private sealed record Fixture
    {
        public required RedeemRecoveryCodeHandler Handler { get; init; }

        public required InMemoryRecoveryCodeRepository RecoveryCodes { get; init; }

        public required InMemoryRecoveryCodeReadService ReadService { get; init; }

        public required InMemorySessionRepository Sessions { get; init; }

        public required RecordingUserContextWriter UserContext { get; init; }

        public required RecordingPersistenceState PersistenceState { get; init; }

        /// <summary>
        /// The executor that refuses to open, or <see langword="null" /> when the fixture was built
        /// with one that runs the delegate.
        /// </summary>
        /// <remarks>
        /// Exposed rather than kept private because
        /// <see cref="UnopenableTransactionalExecutor.ExecuteCallCount" /> is the observation that tells
        /// "asked for a transaction and was refused" from "never asked", and those leave the identical
        /// empty store.
        /// </remarks>
        public required UnopenableTransactionalExecutor? Unopenable { get; init; }

        /// <summary>The account whose codes the tests redeem.</summary>
        public required Guid UserId { get; init; }

        /// <summary>The credential standing for that account's set.</summary>
        public required Credential Set { get; init; }

        /// <summary>The verifiers of that set, in the order they were filed.</summary>
        public required IReadOnlyList<byte[]> Verifiers { get; init; }

        /// <summary>The bystander, whose card must never move.</summary>
        public required Guid OtherUserId { get; init; }

        public required IReadOnlyList<byte[]> OtherVerifiers { get; init; }

        /// <summary>
        /// What a <c>ROLLBACK</c> puts back before the executor replays the unit of work, or
        /// <see langword="null" /> when the abandoned attempt removed nothing there is to restore.
        /// </summary>
        /// <remarks>
        /// Settable after construction rather than a <see cref="Build" /> parameter, because only the
        /// test knows which code the abandoned attempt will have spent, and the executor is built
        /// before this fixture exists. See <see cref="RetryingTransactionalExecutor" /> for why the
        /// restoration has to be the caller's.
        /// </remarks>
        public Func<Task>? RollBackAbandonedAttempt { get; set; }

        /// <summary>The command presenting the verifier at <paramref name="index" /> of the set.</summary>
        /// <remarks>
        /// Base64url text, which is how the value crosses JSON, rather than bytes: the decode and the
        /// width re-check are the handler's first two steps and a fixture handing over bytes would step
        /// over both.
        /// </remarks>
        public RedeemRecoveryCodeCommand CommandFor(int index) =>
            new(Base64UrlText.Encode(Verifiers[index]));

        /// <summary>Every unredeemed code one account still holds, as ordered hex.</summary>
        public string[] StoredHashesOf(Guid userId) =>
        [
            .. RecoveryCodes.Hashes
                .Where(hash => hash.UserId == userId)
                .Select(hash => Convert.ToHexString(hash.VerifierHash.Span))
                .Order(StringComparer.Ordinal),
        ];

        /// <summary>
        /// Puts the code at <paramref name="index" /> back, which is what a <c>ROLLBACK</c> does to a
        /// <c>DELETE</c> an abandoned attempt issued.
        /// </summary>
        /// <remarks>
        /// Filed as a set of its own carrying that one code, because this fake has no member for
        /// restoring a row into an existing set and inventing one would be inventing behaviour. Nothing
        /// downstream can tell: the credential is the same instance, so
        /// <see cref="InMemoryRecoveryCodeRepository.FindRecoveryCodeCredentialAsync" /> answers with
        /// it either way, and every assertion in the replay tests is over codes rather than over how
        /// the fake filed them.
        /// </remarks>
        public Func<Task> RestoreTheSpentCode(int index) => () =>
        {
            RecoveryCodes.Seed(Set, [RecoveryCodeHash.From(Set, Verifiers[index], IssuedEarlier)]);

            return Task.CompletedTask;
        };

        /// <summary>
        /// Arranges for a competing redemption to spend the code at <paramref name="index" /> in the
        /// window between the owner-scoped read choosing a row and the caller being handed it.
        /// </summary>
        /// <remarks>
        /// Once, and the flag is what makes it once: the hook runs on every owner-scoped lookup, and a
        /// second spend of a row that is already gone would raise inside the arrangement rather than
        /// inside the handler.
        /// </remarks>
        public void SpendOnTheNextOwnerScopedLookup(int index)
        {
            bool spent = false;

            RecoveryCodes.OnOwnerScopedLookup = async () =>
            {
                if (spent)
                {
                    return;
                }

                spent = true;
                await RecoveryCodes.ConsumeAsync(
                    RecoveryCodeHash.From(Set, Verifiers[index], IssuedEarlier));
            };
        }

        /// <summary>
        /// Builds the handler over fresh fakes and two seeded sets.
        /// </summary>
        /// <param name="attempts">
        /// How many times the executor runs the unit of work. One is the ordinary case; more is what
        /// the replay tests need, and it is a parameter rather than a second fixture so the two share
        /// one wiring.
        /// </param>
        /// <param name="theTransactionOpens">
        /// Whether the executor can open a transaction at all. False substitutes
        /// <see cref="UnopenableTransactionalExecutor" />, which raises before it invokes the unit of
        /// work — the only arrangement in which "the handler opened a transaction" is observable.
        /// </param>
        public static Fixture Build(int attempts = 1, bool theTransactionOpens = true)
        {
            InMemoryRecoveryCodeRepository recoveryCodes = new();

            Guid userId = Guid.CreateVersion7();
            (Credential set, byte[][] verifiers) = SeedSet(recoveryCodes, userId);

            Guid otherUserId = Guid.CreateVersion7();
            (_, byte[][] otherVerifiers) = SeedSet(recoveryCodes, otherUserId);

            InMemorySessionRepository sessions = new();

            // The executor is one of two, and the fixture keeps the unopenable one so a test can ask
            // whether it was entered. A replaying executor even at one attempt, rather than
            // InMemoryTransactionalExecutor: at one attempt the two are behaviourally identical, and
            // going through this one is what gives the persistence state an attempt number to record
            // instead of a constant.
            //
            // The rollback is forwarded to the fixture rather than declared here, because what a
            // ROLLBACK puts back depends on what the test arranged. It is never invoked before the
            // second attempt, so a caller that sets nothing gets the plain single run.
            Fixture? built = null;
            UnopenableTransactionalExecutor? unopenable = theTransactionOpens ? null : new();
            RetryingTransactionalExecutor? retrying = theTransactionOpens
                ? new RetryingTransactionalExecutor(
                    attempts,
                    () => built?.RollBackAbandonedAttempt?.Invoke() ?? Task.CompletedTask)
                : null;
            ITransactionalExecutor executor = retrying is not null ? retrying : unopenable!;

            // The discard is forwarded to both fakes that stand in for the change tracker's contents.
            // The sessions one is what the replay tests measure: a session queued by an abandoned
            // attempt is the tracker's, and no test here seeds a session, so clearing the fake's whole
            // list is exactly what discarding does on this path. The recovery-code one is wired
            // although a redemption queues no code row — it reads a row and removes one — so that this
            // state stands in for the tracker rather than for the sessions; it is a no-op here by
            // construction, which is the fact RedeemRecoveryCodeHandler's own remarks write out.
            RecordingPersistenceState persistenceState = new(
                () => retrying?.Attempts ?? 0,
                sessions.DiscardTrackedEntities,
                recoveryCodes.DiscardTrackedEntities);

            RecordingUserContextWriter userContext = new();
            InMemoryRecoveryCodeReadService readService = new(recoveryCodes);

            RedeemRecoveryCodeHandler handler = new(
                recoveryCodes,
                readService,
                sessions,
                userContext,
                executor,
                persistenceState,
                new FakeTimeProvider(new DateTimeOffset(UtcNow)));

            built = new Fixture
            {
                Handler = handler,
                RecoveryCodes = recoveryCodes,
                ReadService = readService,
                Sessions = sessions,
                UserContext = userContext,
                PersistenceState = persistenceState,
                Unopenable = unopenable,
                UserId = userId,
                Set = set,
                Verifiers = verifiers,
                OtherUserId = otherUserId,
                OtherVerifiers = otherVerifiers,
            };

            return built;
        }

        /// <summary>
        /// Files one whole set onto an account the way a committed generation would have left it, and
        /// hands back the credential and the verifiers it stored the hashes of.
        /// </summary>
        /// <remarks>
        /// Through the domain factories rather than through a handler, so the arrangement does not run
        /// anything the tests are about. Random verifiers, which costs nothing: no assertion depends on
        /// the value of one — every expected hash is computed from the bytes the fixture produced — and
        /// randomness is what makes an unknown verifier reliably unknown.
        /// </remarks>
        private static (Credential Set, byte[][] Verifiers) SeedSet(
            InMemoryRecoveryCodeRepository recoveryCodes,
            Guid userId)
        {
            Credential set = Credential.CreateRecoveryCodes(userId, IssuedEarlier);
            byte[][] verifiers =
            [
                .. Enumerable.Range(0, CodesPerSet)
                    .Select(_ => RandomNumberGenerator.GetBytes(VerifierLength)),
            ];

            recoveryCodes.Seed(
                set,
                [.. verifiers.Select(verifier => RecoveryCodeHash.From(set, verifier, IssuedEarlier))]);

            return (set, verifiers);
        }
    }
}
