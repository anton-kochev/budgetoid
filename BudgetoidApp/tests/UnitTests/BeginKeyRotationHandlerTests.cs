using System.Reflection;
using System.Security.Cryptography;
using Application.Abstractions;
using Application.KeyRotations;
using Application.KeyRotations.BeginKeyRotation;
using Application.Passkeys;
using Application.Passkeys.Reauthentication;
using Domain.Users;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using TUnit.Assertions.Enums;
using UnitTests.Fakes;
using ValidationException = Domain.Common.ValidationException;

namespace UnitTests;

/// <summary>
/// The act that opens a key rotation: what a begin stages, where the re-authentication gate sits
/// relative to the transaction, what a second begin does to the first, and what the client is handed to
/// drive the rest of the run.
/// </summary>
/// <remarks>
/// <para>
/// <b>THREE CASES WERE DELETED HERE AND THE RULE THEY HELD MOVED RATHER THAN LAPSED. Read this before
/// writing a weaker replacement.</b> The command used to carry a <c>FactorId</c>, and this file carried
/// three refusals around it: a second live passkey factor, a factor the account does not hold, and a
/// recovery-code factor. All three existed to hold one rule — <b>the factor set a rotation commits to
/// must be exactly the account's live set, set equality in both directions, never "the factor presented
/// is one of them"</b> — whose failure mode is an authenticator the person still holds, still enrolled,
/// that can no longer unlock the account, with no repair path except a recovery code.
/// </para>
/// <para>
/// The rule now lives in the <b>staged manifest</b>. A rotation no longer names one factor; it stages
/// the whole factor set it committed to, as the manifest blob and the epoch it was read at, and
/// encapsulates the new account keys to every public key that manifest names. So "exactly the live set"
/// is a question about the manifest's contents and the epoch beside it — and <b>this slice cannot parse
/// a manifest</b>. The bytes are authenticated client-side material; nothing on this server reads
/// inside them. Rewriting the three cases into something this layer <em>can</em> check would have
/// produced a guard over the epoch alone, which admits a stale manifest carrying the right number, and
/// a green bar saying a rule is held that is not. They were deleted instead.
/// </para>
/// <para>
/// <b>What stands in for them until it lands:</b> the handler is unreachable over HTTP — no route
/// begins a rotation — so no account can reach the gap. <c>docs/business-logic/key-rotation.md</c>
/// carries the rule as prose. The commit that makes a begin route reachable owes the manifest check and
/// owes these three cases back, in whatever shape the manifest reader makes checkable.
/// </para>
/// <para>
/// <b>The gate is a real <see cref="PasskeyReauthentication" /> over fakes rather than a stub, and there
/// is no interface to stub it behind</b> — the rule <c>EraseAccountHandlerTests</c> states and the reason
/// it states: a stubbable gate would let a test here prove that a rotation can be begun with the gate
/// faked out, which is the one thing that must never be provable.
/// </para>
/// <para>
/// <b>Every refusal is pinned as an outcome — the refusal and an untouched staging table — rather than as
/// a call order.</b> A handler that reached the right order by accident passes a call-order assertion;
/// nothing passes "the account has no rotation staged" except a handler that really did refuse before it
/// wrote.
/// </para>
/// <para>
/// <b>What this file cannot see, and what therefore has to be held by review:</b> the other half of the
/// gate's placement. <see cref="HandleAsync_WhenTheUnitOfWorkIsReplayed_StillBeginsTheRotation" /> goes red
/// the moment the gate moves inside the transactional delegate, because the challenge store models single
/// use and a second consume refuses a valid begin. It says nothing about the <em>first</em> reason the
/// siblings give — that the consume commits on a save of its own, so a rolled-back begin inside that
/// transaction would restore the spent nonce and make the assertion replayable. No fake here rolls a
/// consume back, so no test in this file can fail for that. <c>EraseAccountHandler</c> and
/// <c>GenerateRecoveryCodesHandler</c> argue both halves at length; this handler must not re-derive them.
/// </para>
/// <para>
/// <b>Which side of the delegate the staging write lands on is held</b>, by
/// <see cref="HandleAsync_StagesTheRotationInsideTheUnitOfWork" /> — a fake reporting the call it
/// received rather than a model of a database. What no test here reaches is whether a rollback would
/// really take that row back, which needs a database and is the integration tier's.
/// </para>
/// <para>
/// <b>The port shape these tests imply is not yet written</b>, and is named here once so that whoever
/// greens them is not reverse-engineering it from constructor arguments:
/// <c>Domain.Users.IKeyRotationRepository</c> — <c>ListPasskeyFactorsAsync</c> (the account's live passkey
/// factors, keyed on factor id and valued by the passkey each is filed against),
/// <c>FindStagedRotationAsync</c>, <c>StageAsync</c>; and
/// <c>Application.KeyRotations.IRotationInventoryReadService</c> — <c>ListOwnedBudgetIdsAsync</c> and
/// <c>CountNarrativeRowsAsync</c>.
/// </para>
/// </remarks>
public sealed class BeginKeyRotationHandlerTests
{
    /// <summary>
    /// How many times the replaying executor runs the unit of work, standing in for one transient failure
    /// recovered by a retrying provider strategy.
    /// </summary>
    private const int ReplayedAttempts = 2;

    /// <summary>
    /// The generation the staged manifest names, and how many bytes of it the fixture builds.
    /// </summary>
    /// <remarks>
    /// Literals rather than <c>FactorManifest</c>'s constants, for the reason <c>KeyRotationTests</c>
    /// gives where it writes the same values out: every type on this path <em>reads</em> those
    /// constants, so a test that read them too would feed the code under test whatever that code
    /// currently believes. The length is arbitrary and well under the cap — nothing in this file is
    /// about the manifest's bounds, which <c>KeyRotationTests</c> owns.
    /// </remarks>
    private const int StagedRotationEpoch = 4;

    /// <inheritdoc cref="StagedRotationEpoch" />
    private const int StagedManifestBytes = 24;

    /// <summary>
    /// The cap the API puts on a request body, which is what a chunk budget has to fit inside.
    /// </summary>
    /// <remarks>
    /// Written out here because <c>Api/Program.cs</c> owns the real number and this project deliberately
    /// does not reference <c>Api</c> — see <c>UnitTests.csproj</c>, where that absence is a pinned row of
    /// <c>ProjectReferenceGraphTests</c>. So this is a second statement of the value rather than a reading
    /// of it, and the assertion below is a bound rather than an equality: what a client may rely on is that
    /// a chunk of <c>MaxChunkBytes</c> fits in a body that also carries the rest of the request's JSON.
    /// </remarks>
    private const int RequestBodyCapBytes = 64 * 1024;

    /// <summary>
    /// A begin whose assertion does not verify stages nothing.
    /// </summary>
    /// <remarks>
    /// The store holds bytes the device never signed, which is how it answers <see langword="null" />. What
    /// is measured is not the refusal — the gate's own tests own that — but that the refusal happened
    /// <em>before</em> anything was written.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheChallengeIsNotLive_RefusesAndStagesNothing()
    {
        // Arrange
        Fixture fixture = Fixture.Build(challengeIsLive: false);

        // Act
        await ThrowsAsync<PasskeyVerificationException>(() => fixture.Handler.HandleAsync(fixture.Command));

        // Assert
        await Assert.That(fixture.KeyRotations.StageCallCount).IsEqualTo(0);
        await Assert.That(fixture.KeyRotations.Staged.Count).IsEqualTo(0);
    }

    /// <summary>
    /// A genuine assertion from an authenticator registered to somebody else stages nothing.
    /// </summary>
    /// <remarks>
    /// The account binding at unit level, the case <c>EraseAccountHandlerTests</c> exists for on its own
    /// path: the assertion carries no user handle, so nothing but the gate's owner-scoped lookup can refuse
    /// it — and a rotation begun on the strength of a stranger's device would stage the attacker's
    /// envelopes as this account's next generation.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithAPasskeyBelongingToAnotherAccount_RefusesAndStagesNothing()
    {
        // Arrange
        Fixture fixture = Fixture.Build(passkeyBelongsToAnotherAccount: true);

        // Act
        await ThrowsAsync<PasskeyVerificationException>(() => fixture.Handler.HandleAsync(fixture.Command));

        // Assert
        await Assert.That(fixture.KeyRotations.StageCallCount).IsEqualTo(0);
        await Assert.That(fixture.KeyRotations.Staged.Count).IsEqualTo(0);
    }

    /// <summary>
    /// The gate runs to completion <b>outside</b> the transactional delegate, and this is the test that
    /// goes red the instant it is moved inside one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The delegate is replayed under a retrying execution strategy and a nonce is single use. A gate
    /// inside the delegate consumes a second time on attempt two, finds the nonce already spent, and
    /// refuses a <b>valid</b> begin with the same 401 an attacker gets — because the database blinked.
    /// <c>EraseAccountHandler</c> and <c>GenerateRecoveryCodesHandler</c> write the argument out in full,
    /// including the half no unit test can reach; this handler inherits it and must not restate it.
    /// </para>
    /// <para>
    /// Written as an outcome with the consume count beside it: the rotation was staged, and the nonce was
    /// spent exactly once.
    /// </para>
    /// <para>
    /// <b>The attempt count is asserted beside them, and it is not decoration.</b> Without it a handler
    /// that opened no transaction at all would pass this case trivially — it would run once, consume once
    /// and stage once — which is "the gate is not inside the delegate" satisfied by having no delegate.
    /// The executor is entered, so the staging write is the thing being replayed.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheUnitOfWorkIsReplayed_StillBeginsTheRotation()
    {
        // Arrange — an executor that really replays the unit of work, over a store that models single use
        // rather than assuming it, which is what makes a second consume observable at all.
        Fixture fixture = Fixture.Build(attempts: ReplayedAttempts);

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert
        KeyRotation? staged = await fixture.KeyRotations.FindStagedRotationAsync(fixture.UserId);
        await Assert.That(staged).IsNotNull();
        await Assert.That(staged!.RotationId).IsEqualTo(fixture.Command.RotationId);
        await Assert.That(fixture.Challenges.ConsumeCallCount).IsEqualTo(1);

        // The unit of work really was entered and really was replayed — see the remarks.
        await Assert.That(fixture.Executor.Attempts).IsEqualTo(ReplayedAttempts);

        // One row however many attempts ran: the staging table is keyed on the account.
        await Assert.That(fixture.KeyRotations.Staged.Count).IsEqualTo(1);
    }

    /// <summary>
    /// A second begin for an account that already has a rotation staged <b>replaces</b> it, and is not a
    /// conflict.
    /// </summary>
    /// <remarks>
    /// This is the repair path and the reason a 409 would be wrong. When a completion refuses because the
    /// account's live factor set moved — a passkey registered or revoked while the run was in flight — the
    /// only way forward is a begin carrying the corrected set. Answer that with a conflict and the client
    /// is left holding a staged row it cannot replace and a rotation it cannot finish, with no route that
    /// removes one.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenARotationIsAlreadyStaged_ReplacesItRatherThanRefusing()
    {
        // Arrange — an abandoned run, staged under its own identifier, manifest and epoch. Every one of
        // the three differs from the replacing begin's, so nothing below can pass on a row that was
        // never rewritten.
        Fixture fixture = Fixture.Build();
        KeyRotation abandoned = KeyRotation.Begin(
            fixture.Passkey,
            Guid.CreateVersion7(),
            Manifest(0xAA),
            StagedRotationEpoch - 1,
            Fixture.UtcNow);
        fixture.KeyRotations.SeedStagedRotation(abandoned);

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — one row, and it is this begin's.
        KeyRotation? staged = await fixture.KeyRotations.FindStagedRotationAsync(fixture.UserId);
        await Assert.That(fixture.KeyRotations.Staged.Count).IsEqualTo(1);
        await Assert.That(staged!.RotationId).IsEqualTo(fixture.Command.RotationId);
        await Assert.That(staged.RotationId).IsNotEqualTo(abandoned.RotationId);
        await Assert.That(staged.StagedManifest.ToArray())
            .IsEquivalentTo(fixture.Command.StagedManifest.ToArray(), CollectionOrdering.Matching);
        await Assert.That(staged.StagedRotationEpoch).IsEqualTo(fixture.Command.StagedRotationEpoch);
    }

    /// <summary>
    /// The account owns a budget other than the one this request operates inside, and the begin is
    /// refused.
    /// </summary>
    /// <remarks>
    /// The export's rule, in the export's spelling, and the exception is the one
    /// <c>IRotationCompletenessReadService</c> already throws — do not write a second. Refusing here rather
    /// than at completion is the cheaper half of the same refusal: a rotation that cannot finish is better
    /// not begun, and the client has re-encrypted nothing yet.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheAccountOwnsABudgetOtherThanTheAmbientOne_RefusesAndStagesNothing()
    {
        // Arrange
        Fixture fixture = Fixture.Build(ownsASecondBudget: true);

        // Act
        await ThrowsAsync<RotationScopeException>(() => fixture.Handler.HandleAsync(fixture.Command));

        // Assert
        await Assert.That(fixture.KeyRotations.StageCallCount).IsEqualTo(0);
        await Assert.That(fixture.Inventory.CountCallCount).IsEqualTo(0);
    }

    /// <summary>
    /// The request operates inside a budget the account does not own, and the begin is refused.
    /// </summary>
    /// <remarks>
    /// <b>The direction a <c>Count &gt; 1</c> guard misses.</b> One owned budget, one ambient budget, and
    /// they are different: a count reads one and waves it through, after which every row the rotation is
    /// measured against belongs to somebody else. Set equality in both directions is what refuses it, which
    /// is why this case is written beside its mirror rather than folded into it.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheAmbientBudgetIsNotOwned_RefusesAndStagesNothing()
    {
        // Arrange
        Fixture fixture = Fixture.Build(ownsTheAmbientBudget: false);

        // Act
        await ThrowsAsync<RotationScopeException>(() => fixture.Handler.HandleAsync(fixture.Command));

        // Assert
        await Assert.That(fixture.KeyRotations.StageCallCount).IsEqualTo(0);
        await Assert.That(fixture.Inventory.CountCallCount).IsEqualTo(0);
    }

    /// <summary>
    /// An unproven caller whose account owns a second budget is told <b>nothing about the second
    /// budget</b>: the assertion is refused first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one case in this file where two readings of the handler disagree, and this is the one that
    /// is right.</b> Every other scope case carries a valid assertion, so a handler that read the owned
    /// budget set and refused before verifying passes all of them — while answering a caller holding
    /// nothing but a stolen bearer token with "this account owns more than one budget".
    /// </para>
    /// <para>
    /// That is the disclosure <c>GenerateRecoveryCodesHandler</c> orders its own gate to prevent, where it
    /// argues that validating first would hand an unproven caller the two facts a client needs to present
    /// a set at all. Past the gate the same sentence costs nothing, because the caller has proved
    /// possession of an authenticator registered to this account and there is nobody left to enumerate
    /// about. <c>RotationScopeException</c> reaches the caller as a 500 whose message the Development
    /// branch of <c>GlobalExceptionHandler</c> echoes into the body, which is what makes the leak
    /// concrete rather than theoretical.
    /// </para>
    /// <para>
    /// The helper catches <see cref="PasskeyVerificationException" /> exactly, so a handler that refused
    /// for scope instead lets <c>RotationScopeException</c> escape and fails this test as itself, naming
    /// the refusal it chose.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithAnUnverifiableAssertionOnAMultiBudgetAccount_RefusesForTheAssertion()
    {
        // Arrange — both refusals are available to the handler at once: the store holds bytes the device
        // never signed, and the account owns a budget beside the ambient one.
        Fixture fixture = Fixture.Build(challengeIsLive: false, ownsASecondBudget: true);

        // Act
        await ThrowsAsync<PasskeyVerificationException>(() => fixture.Handler.HandleAsync(fixture.Command));

        // Assert — refused for the assertion, and the owned budget set was never even read.
        await Assert.That(fixture.Inventory.CountCallCount).IsEqualTo(0);
        await Assert.That(fixture.KeyRotations.StageCallCount).IsEqualTo(0);
        await Assert.That(fixture.KeyRotations.Staged.Count).IsEqualTo(0);
    }

    /// <summary>
    /// The staging write happens <b>inside</b> the unit of work, not beside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A handler that enters the executor, does nothing in the delegate and stages afterwards satisfies
    /// every count this file can take — the executor ran, the row is there, the nonce was spent once —
    /// and is wrong in the one way that matters: a write outside the transaction is a write nothing rolls
    /// back. On this path that is a staged next generation of the account's keys surviving a failure that
    /// abandoned everything else the request was doing.
    /// </para>
    /// <para>
    /// <b>The observation is the fake reporting the call it received, not a model of PostgreSQL.</b>
    /// <see cref="ObservedTransactionalExecutor" /> sets its flag around the handler's own delegate and
    /// <see cref="InMemoryKeyRotationRepository.ObserveAtStage" /> reads it at the moment the write
    /// arrives, so what is asserted is the control flow the handler produced.
    /// <c>InMemoryPasskeyRepository.ObserveAtDelete</c> is the same device, and
    /// <c>RevokePasskeyHandlerTests</c> and <c>GenerateRecoveryCodesHandlerTests</c> both read it back in
    /// exactly this shape.
    /// </para>
    /// <para>
    /// <b>What it still cannot see</b> is whether the write would really have been rolled back. That
    /// needs a database and belongs to the integration tier; nothing here should be read as covering it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_StagesTheRotationInsideTheUnitOfWork()
    {
        // Arrange
        Fixture fixture = Fixture.Build();
        fixture.KeyRotations.ObserveAtStage = () => fixture.UnitOfWork.InsideUnitOfWork;

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — null would mean nothing was ever staged, which is a different failure from a write
        // made on the wrong side of the delegate, so both are named.
        await Assert.That(fixture.KeyRotations.ObservationAtStage).IsNotNull();
        await Assert.That(fixture.KeyRotations.ObservationAtStage).IsTrue();
    }

    /// <summary>
    /// The control: a verified assertion stages the rotation with every value the client sent on it.
    /// </summary>
    /// <remarks>
    /// Without this, a handler that refused everything passes every refusal above. The manifest carries
    /// distinct bytes and is compared in order, because TUnit's bare <c>IsEquivalentTo</c> defaults to
    /// <see cref="CollectionOrdering.Any" /> and a handler that reversed or rebuilt the buffer would
    /// satisfy anything weaker — and for a blob nothing on this side can read, position is the whole of
    /// what "the bytes the client sent" means.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithAVerifiedAssertion_StagesTheRotation()
    {
        // Arrange
        Fixture fixture = Fixture.Build();

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert
        KeyRotation? staged = await fixture.KeyRotations.FindStagedRotationAsync(fixture.UserId);
        await Assert.That(staged).IsNotNull();
        await Assert.That(staged!.UserId).IsEqualTo(fixture.UserId);
        await Assert.That(staged.RotationId).IsEqualTo(fixture.Command.RotationId);
        await Assert.That(staged.StagedManifest.ToArray())
            .IsEquivalentTo(fixture.Command.StagedManifest.ToArray(), CollectionOrdering.Matching);
        await Assert.That(staged.StagedRotationEpoch).IsEqualTo(fixture.Command.StagedRotationEpoch);
        await Assert.That(staged.StartedAtUtc).IsEqualTo(Fixture.UtcNow);
    }

    /// <summary>
    /// The answer carries the row count of each of the six narrative-bearing tables, on its own member.
    /// </summary>
    /// <remarks>
    /// The client drives the rest of the run against these as a progress denominator, so a transposed pair
    /// is not cosmetic: it reports a rotation as finished while a table still has rows to go. Every count
    /// in the fixture is distinct for exactly that reason.
    /// </remarks>
    [Test]
    public async Task HandleAsync_AnswersTheRowCountOfEachNarrativeBearingTable()
    {
        // Arrange
        Fixture fixture = Fixture.Build();

        // Act
        KeyRotationBegun begun = await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — named one by one rather than compared whole, so a failure says which table moved.
        await Assert.That(begun.Inventory.Accounts).IsEqualTo(Fixture.SeededInventory.Accounts);
        await Assert.That(begun.Inventory.Payees).IsEqualTo(Fixture.SeededInventory.Payees);
        await Assert.That(begun.Inventory.CategoryGroups).IsEqualTo(Fixture.SeededInventory.CategoryGroups);
        await Assert.That(begun.Inventory.Categories).IsEqualTo(Fixture.SeededInventory.Categories);
        await Assert.That(begun.Inventory.Transactions).IsEqualTo(Fixture.SeededInventory.Transactions);
        await Assert.That(begun.Inventory.Budgets).IsEqualTo(Fixture.SeededInventory.Budgets);
    }

    /// <summary>
    /// The inventory carries counts and nothing else — no narrative value crosses this boundary.
    /// </summary>
    /// <remarks>
    /// A census rather than an assertion about one call, because what is being held is what may be
    /// <em>added</em>. The progress denominator is the one place on this path where somebody reaches for
    /// "and the names of the payees, so the screen can say which one it is on", and every narrative column
    /// in the product is a sealed envelope the server cannot open — so the useful version of that member
    /// cannot exist and the useless version would ship ciphertext to a progress bar.
    /// </remarks>
    [Test]
    public async Task RotationInventory_CarriesCountsAndNothingElse()
    {
        // Arrange
        PropertyInfo[] members = typeof(RotationInventory)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        // Act
        string[] notCounts =
        [
            .. members
                .Where(member => member.PropertyType != typeof(int))
                .Select(member => $"{member.Name}: {member.PropertyType.Name}"),
        ];

        // Assert — the collection rather than a joined string, so every offender is named and not just the
        // first one.
        await Assert.That(notCounts).IsEmpty();
        await Assert.That(members.Length).IsGreaterThan(0);
    }

    /// <summary>
    /// The answer names a chunk budget the client can actually post.
    /// </summary>
    /// <remarks>
    /// A bound rather than an equality — see <see cref="RequestBodyCapBytes" /> for why the exact number is
    /// not readable from here. What a client may rely on is that a chunk of this size fits inside a request
    /// body that also carries the rest of its JSON, so a budget at or above the cap is a guaranteed 413 on
    /// every chunk of every rotation, and a budget of zero is a rotation that can never make progress.
    /// </remarks>
    [Test]
    public async Task HandleAsync_AnswersAChunkBudgetThatFitsInsideTheRequestBodyCap()
    {
        // Arrange
        Fixture fixture = Fixture.Build();

        // Act
        KeyRotationBegun begun = await fixture.Handler.HandleAsync(fixture.Command);

        // Assert
        await Assert.That(begun.MaxChunkBytes).IsGreaterThan(0);
        await Assert.That(begun.MaxChunkBytes).IsLessThan(RequestBodyCapBytes);
    }

    /// <summary>
    /// A staged factor manifest of distinct bytes, so an assertion over it is about position.
    /// </summary>
    /// <remarks>
    /// <paramref name="seed" /> is what tells one fixture's manifest from another's — the abandoned
    /// rotation and the replacing one have to differ, or the replacement test could pass on a row that
    /// was never rewritten. Nothing at this layer parses a manifest: it is authenticated client-side
    /// material, and the whole reason three refusals were deleted from this file is that this server
    /// cannot read inside it.
    /// </remarks>
    private static byte[] Manifest(byte seed) =>
        [.. Enumerable.Range(0, StagedManifestBytes).Select(offset => (byte)(seed + offset))];

    /// <summary>
    /// Runs <paramref name="action" /> and returns the exception it was expected to throw.
    /// </summary>
    /// <remarks>
    /// The catch names <typeparamref name="TException" /> exactly, so an exception of any other type
    /// escapes and fails the test as itself rather than as "the expected exception was not thrown" — which
    /// matters here, where three different refusals are being told apart.
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

    private const string RelyingPartyId = "localhost";
    private const string Origin = "https://localhost:4200";
    private const int ChallengeBytes = 32;

    /// <summary>
    /// The handler, the gate it begins through, and the collaborators behind both — assembled once so no
    /// test has to restate a seven-argument constructor.
    /// </summary>
    private sealed record Fixture(
        BeginKeyRotationHandler Handler,
        BeginKeyRotationCommand Command,
        InMemoryKeyRotationRepository KeyRotations,
        StubRotationInventoryReadService Inventory,
        StubWebAuthnChallengeStore Challenges,
        RetryingTransactionalExecutor Executor,
        ObservedTransactionalExecutor UnitOfWork,
        Credential Passkey,
        Guid PasskeyFactorId,
        Guid UserId,
        Guid BudgetId)
    {
        /// <summary>
        /// Fixed instant for every seeded row, so nothing in this file depends on the wall clock.
        /// </summary>
        public static readonly DateTime UtcNow = new(2026, 9, 10, 11, 12, 13, DateTimeKind.Utc);

        /// <summary>
        /// The counts the inventory read answers with. <b>Six distinct numbers</b>, so a handler that
        /// filled one member from another's count fails rather than agreeing by coincidence.
        /// </summary>
        public static readonly RotationInventory SeededInventory = new(
            Accounts: 3,
            Payees: 41,
            CategoryGroups: 5,
            Categories: 17,
            Transactions: 926,
            Budgets: 1);

        /// <summary>
        /// Builds the handler over fresh fakes, with a real device holding a real key pair and a real
        /// signature over the challenge the store was handed.
        /// </summary>
        /// <param name="passkeyFactors">
        /// How many live passkey factors the account holds. One is today's world; two is Story 11.13's,
        /// and is what tells set equality apart from "is one of".
        /// </param>
        /// <param name="attempts">
        /// How many times the executor runs the unit of work. One is the ordinary case; more than one is
        /// what the replay test needs, and it is a parameter rather than a second fixture so that the two
        /// share one wiring.
        /// </param>
        /// <param name="challengeIsLive">
        /// Whether the store holds the bytes the device signed. False leaves it holding a different nonce,
        /// which is how a store answers null without a second fake.
        /// </param>
        /// <param name="passkeyBelongsToAnotherAccount">
        /// Whether the registered passkey is filed under somebody other than the account the request
        /// authenticates as. The assertion is otherwise identical and carries no user handle, so the
        /// owner-scoped lookup is the only thing that can refuse it.
        /// </param>
        /// <param name="seedRecoveryCodeSet">
        /// Whether the account also holds a set of recovery codes — ten factors under one credential, every
        /// one of them a real row and none of them a factor a rotation may begin under.
        /// </param>
        /// <param name="ownsASecondBudget">Whether the account owns a budget beside the ambient one.</param>
        /// <param name="ownsTheAmbientBudget">
        /// Whether the budget this request operates inside is one the account owns. False is the direction
        /// a count-based guard misses.
        /// </param>
        public static Fixture Build(
            int passkeyFactors = 1,
            int attempts = 1,
            bool challengeIsLive = true,
            bool passkeyBelongsToAnotherAccount = false,
            bool seedRecoveryCodeSet = false,
            bool ownsASecondBudget = false,
            bool ownsTheAmbientBudget = true)
        {
            Guid userId = Guid.CreateVersion7();
            Guid budgetId = Guid.CreateVersion7();
            StubUserContext userContext = new(userId);
            StubBudgetContext budgetContext = new(budgetId);

            // The passkey is filed under whoever owns the device, which is the request's own account
            // unless a test says otherwise. Nothing else about the ceremony changes with it.
            Guid passkeyOwnerId = passkeyBelongsToAnotherAccount ? Guid.CreateVersion7() : userId;
            SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
            Credential passkey = Credential.CreatePasskey(passkeyOwnerId, UtcNow);
            InMemoryPasskeyRepository passkeys = new();
            passkeys.Register(
                passkey,
                PasskeyPublicKey.Register(passkey, device.CredentialId, device.CoseKey, device.Algorithm),
                signatureCounter: 0);

            // Every live passkey factor is filed against the request's OWN account, including the second
            // one: 11.13 registers a second authenticator to the same person, not to another. The command
            // names the first, which is what a client built for one passkey sends.
            InMemoryKeyRotationRepository keyRotations = new();
            Guid[] passkeyFactorIds = [.. Enumerable.Range(0, passkeyFactors).Select(_ => Guid.CreateVersion7())];
            foreach (Guid factorId in passkeyFactorIds)
            {
                keyRotations.SeedPasskeyFactor(
                    passkeyBelongsToAnotherAccount ? Credential.CreatePasskey(userId, UtcNow) : passkey,
                    factorId);
            }

            if (seedRecoveryCodeSet)
            {
                Credential codes = Credential.CreateRecoveryCodes(userId, UtcNow);
                foreach (int _ in Enumerable.Range(0, RecoveryCodeSetSize))
                {
                    keyRotations.SeedRecoveryCodeFactor(codes, Guid.CreateVersion7());
                }
            }

            // Signed bytes and stored bytes are the same nonce unless the caller wants a dead one.
            byte[] signedChallenge = RandomNumberGenerator.GetBytes(ChallengeBytes);
            byte[] storedChallenge = challengeIsLive
                ? signedChallenge
                : RandomNumberGenerator.GetBytes(ChallengeBytes);
            StubWebAuthnChallengeStore challenges = new(storedChallenge, ceremony: WebAuthnCeremony.Reauthentication);

            // No user handle, for the reason EraseAccountHandlerTests spells out: with the account's own
            // handle present, a lookup that had lost its owner filter would still be turned down by the
            // handle check below it, and the stranger's-passkey test would stay green over a gate with no
            // binding left.
            AssertionResult assertion = device.Authenticate(signedChallenge, Origin, userHandle: null);

            // The owned set the scope gate compares against the ambient budget. Both directions are
            // reachable: an owned budget the request is not inside, and an ambient budget the account does
            // not own.
            List<Guid> ownedBudgetIds = [];
            if (ownsTheAmbientBudget)
            {
                ownedBudgetIds.Add(budgetId);
            }

            if (ownsASecondBudget || !ownsTheAmbientBudget)
            {
                ownedBudgetIds.Add(Guid.CreateVersion7());
            }

            StubRotationInventoryReadService inventory = new(SeededInventory, [.. ownedBudgetIds]);

            // A replaying executor even at one attempt, rather than InMemoryTransactionalExecutor: at one
            // attempt the two are behaviourally identical, and going through this one is what makes the
            // replay case a parameter rather than a second wiring.
            RetryingTransactionalExecutor executor = new(attempts);

            // Wrapped rather than replaced: the replay behaviour stays the shared fake's, and the
            // wrapper adds the one fact a collaborator needs to say which side of the delegate it was
            // called from. See ObservedTransactionalExecutor.
            ObservedTransactionalExecutor unitOfWork = new(executor);

            BeginKeyRotationHandler handler = new(
                keyRotations,
                inventory,
                userContext,
                budgetContext,
                unitOfWork,
                new PasskeyReauthentication(
                    challenges,
                    passkeys,
                    userContext,
                    new StubPasskeyCeremonyPolicy(RelyingPartyId, Origin)),
                new FakeTimeProvider(new DateTimeOffset(UtcNow)));

            return new Fixture(
                handler,
                new BeginKeyRotationCommand(
                    new ReauthenticationAssertion(
                        assertion.CredentialIdBase64Url,
                        assertion.ClientDataJsonBase64Url,
                        assertion.AuthenticatorDataBase64Url,
                        assertion.SignatureBase64Url,
                        assertion.UserHandleBase64Url),
                    Guid.CreateVersion7(),

                    // Distinct bytes, so the read-back below is about position rather than content.
                    Manifest(0xC0),
                    StagedRotationEpoch),
                keyRotations,
                inventory,
                challenges,
                executor,
                unitOfWork,
                passkey,
                passkeyFactorIds[0],
                userId,
                budgetId);
        }

        /// <summary>How many factors a set of recovery codes is.</summary>
        private const int RecoveryCodeSetSize = 10;
    }
}
