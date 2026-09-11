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
/// The act that opens a content-key rotation: which factor set a begin may be staged under, where the
/// re-authentication gate sits relative to the transaction, what a second begin does to the first, and
/// what the client is handed to drive the rest of the run.
/// </summary>
/// <remarks>
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
    /// The exact width of a wrapped-key envelope, and the version byte it leads with.
    /// </summary>
    /// <remarks>
    /// Literals rather than <c>WrappedAccountKeys</c>'s constants, for the reason <c>KeyRotationTests</c>
    /// gives where it writes the same two values out: every type on this path <em>reads</em> those
    /// constants, so a test that read them too would feed the code under test whatever that code currently
    /// believes.
    /// </remarks>
    private const int EnvelopeLength = 61;

    /// <inheritdoc cref="EnvelopeLength" />
    private const byte EnvelopeVersion = 1;

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
    /// <b>The highest-value case in this file.</b> The account holds a second passkey, the command names
    /// one factor, and the begin is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The staged factor set must be <em>exactly</em> the account's live passkey factor set — set equality,
    /// in both directions — and not "the supplied factor is one of the account's passkey factors". Today
    /// every account holds exactly one passkey, so the two spellings are indistinguishable on every other
    /// test here. This is the one case that tells them apart, and it is written before the day it bites.
    /// </para>
    /// <para>
    /// The day is <b>Story 11.13</b>, which makes a second passkey registrable. Under set equality a begin
    /// that names one factor out of two reddens loudly, here, at the start of the run — before a single row
    /// has been rewritten and while the client can still re-post a begin carrying both. Under "is one of"
    /// it succeeds silently, the rotation runs to completion, the promotion overwrites
    /// <c>wrapped_account_keys</c>, and the second passkey is left holding a wrapped copy of a content key
    /// that no longer opens anything — an authenticator the person still has, still enrolled, that can no
    /// longer unlock the account. There is no repair path from there that does not go through a recovery
    /// code.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheAccountHoldsASecondPasskeyFactor_RefusesAndStagesNothing()
    {
        // Arrange — two live passkey factors, and a command naming the first of them, which is exactly
        // what a client built against today's one-passkey world would send.
        Fixture fixture = Fixture.Build(passkeyFactors: 2);

        // Act
        ValidationException refusal = await ThrowsAsync<ValidationException>(
            () => fixture.Handler.HandleAsync(fixture.Command));

        // Assert — refused on the member the client can correct, and nothing staged.
        await Assert.That(refusal.Errors.Keys).Contains(nameof(BeginKeyRotationCommand.FactorId));
        await Assert.That(fixture.KeyRotations.StageCallCount).IsEqualTo(0);
        await Assert.That(fixture.KeyRotations.Staged.Count).IsEqualTo(0);
    }

    /// <summary>
    /// A factor identifier naming nothing the account holds is refused.
    /// </summary>
    [Test]
    public async Task HandleAsync_WithAFactorTheAccountDoesNotHold_RefusesAndStagesNothing()
    {
        // Arrange — a well-formed identifier that is filed against nothing at all.
        Fixture fixture = Fixture.Build();
        BeginKeyRotationCommand namingNothing = fixture.Command with { FactorId = Guid.CreateVersion7() };

        // Act
        ValidationException refusal = await ThrowsAsync<ValidationException>(
            () => fixture.Handler.HandleAsync(namingNothing));

        // Assert
        await Assert.That(refusal.Errors.Keys).Contains(nameof(BeginKeyRotationCommand.FactorId));
        await Assert.That(fixture.KeyRotations.StageCallCount).IsEqualTo(0);
    }

    /// <summary>
    /// A factor identifier naming one of the account's <b>recovery-code</b> factors is refused, even
    /// though the account genuinely holds that factor.
    /// </summary>
    /// <remarks>
    /// A set of codes is ten factors under one credential, so "the factor this rotation began under" would
    /// have ten answers; and a begin is gated on a passkey assertion, which a set of codes cannot produce.
    /// The fixture files the factor for real — it is a <c>wrapped_account_keys</c> row that is really
    /// there — so this case cannot be satisfied by a handler that merely fails to find it.
    /// <para>
    /// The consequence is deliberate and a reader will file it as a bug: somebody who lost their
    /// authenticator and signed in with a code must register a replacement passkey before they can begin a
    /// rotation. <c>docs/business-logic/key-rotation.md</c> records it as a position.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithARecoveryCodeFactor_RefusesAndStagesNothing()
    {
        // Arrange
        Fixture fixture = Fixture.Build(seedRecoveryCodeSet: true);
        Guid recoveryCodeFactor = fixture.KeyRotations.RecoveryCodeFactorsOf(fixture.UserId)[0];

        // Act
        ValidationException refusal = await ThrowsAsync<ValidationException>(
            () => fixture.Handler.HandleAsync(fixture.Command with { FactorId = recoveryCodeFactor }));

        // Assert
        await Assert.That(refusal.Errors.Keys).Contains(nameof(BeginKeyRotationCommand.FactorId));
        await Assert.That(fixture.KeyRotations.StageCallCount).IsEqualTo(0);
    }

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
        // Arrange — an abandoned run, staged under its own identifier and its own envelopes.
        Fixture fixture = Fixture.Build();
        KeyRotation abandoned = KeyRotation.Begin(
            fixture.Passkey,
            fixture.PasskeyFactorId,
            Guid.CreateVersion7(),
            Envelope(0xAA),
            Envelope(0xBB),
            Fixture.UtcNow);
        fixture.KeyRotations.SeedStagedRotation(abandoned);

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — one row, and it is this begin's.
        KeyRotation? staged = await fixture.KeyRotations.FindStagedRotationAsync(fixture.UserId);
        await Assert.That(fixture.KeyRotations.Staged.Count).IsEqualTo(1);
        await Assert.That(staged!.RotationId).IsEqualTo(fixture.Command.RotationId);
        await Assert.That(staged.RotationId).IsNotEqualTo(abandoned.RotationId);
        await Assert.That(staged.WrappedContentKey.ToArray())
            .IsEquivalentTo(fixture.Command.WrappedContentKey.ToArray(), CollectionOrdering.Matching);
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
    /// The control: the account's one passkey factor, named by the command, stages the rotation with every
    /// value the client sent on it.
    /// </summary>
    /// <remarks>
    /// Without this, a handler that refused everything passes every refusal above. The two envelopes carry
    /// <em>different</em> filler and are compared in order, so a handler that assigned one argument twice —
    /// or swapped the pair — fails here; a swapped pair is the one mistake at this layer that no width
    /// check, no version check and no database constraint can see.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithTheAccountsOnlyPasskeyFactor_StagesTheRotation()
    {
        // Arrange
        Fixture fixture = Fixture.Build();

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert
        KeyRotation? staged = await fixture.KeyRotations.FindStagedRotationAsync(fixture.UserId);
        await Assert.That(staged).IsNotNull();
        await Assert.That(staged!.UserId).IsEqualTo(fixture.UserId);
        await Assert.That(staged.FactorId).IsEqualTo(fixture.PasskeyFactorId);
        await Assert.That(staged.RotationId).IsEqualTo(fixture.Command.RotationId);
        await Assert.That(staged.WrappedContentKey.ToArray())
            .IsEquivalentTo(fixture.Command.WrappedContentKey.ToArray(), CollectionOrdering.Matching);
        await Assert.That(staged.WrappedIndexKey.ToArray())
            .IsEquivalentTo(fixture.Command.WrappedIndexKey.ToArray(), CollectionOrdering.Matching);
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
    /// A well-formed envelope: the version byte the contract defines, then filler.
    /// </summary>
    /// <remarks>
    /// Built from this file's own literals, for the reason <c>KeyRotationTests</c> gives. The filler is
    /// neither a nonce nor a ciphertext — nothing at this layer inspects either — and its only job is to
    /// let the two columns be told apart by eye in a failure message.
    /// </remarks>
    private static byte[] Envelope(byte filler)
    {
        byte[] envelope = new byte[EnvelopeLength];
        Array.Fill(envelope, filler);
        envelope[0] = EnvelopeVersion;

        return envelope;
    }

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
                    passkeyFactorIds[0],
                    Guid.CreateVersion7(),

                    // Different filler per column, so a swapped or duplicated assignment is visible.
                    Envelope(0xC0),
                    Envelope(0x1D)),
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
