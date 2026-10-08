using Application.KeyRotations;
using Application.KeyRotations.CompleteKeyRotation;
using Domain.Common;
using Domain.Users;
using UnitTests.Fakes;
using ValidationException = Domain.Common.ValidationException;

namespace UnitTests;

/// <summary>
/// The one step of a content-key rotation that destroys something: the completion, which overwrites
/// <see cref="WrappedAccountKeys.EncapsulatedAccountKeys" /> for every factor the account holds and
/// promotes <see cref="FactorManifest" /> to the staged generation.
/// </summary>
/// <remarks>
/// <para>
/// <b>EVERY OTHER STEP OF A ROTATION IS RECOVERABLE AND THIS ONE IS NOT. Read that before weakening a
/// case below.</b> A begin that goes wrong is replaced by another begin. A chunk that goes wrong is
/// re-sent. This step overwrites the only copies of the generation still in force — and if it runs
/// while a single narrative row is still sealed under the old content key, that row is unreadable
/// <b>forever</b>, no exception is thrown, no SQLSTATE is raised and nothing is logged. There is no
/// repair path. That is why the refusals in this file assert what the account <em>holds</em> and not
/// merely which exception came back: a handler that promoted every row and then threw satisfies a
/// status assertion perfectly.
/// </para>
/// <para>
/// <b>THE ORDER INSIDE THE TRANSACTION IS THE PROPERTY, AND ONE STEP OF IT IS EASY TO SKIP.</b> The
/// completeness gate is asked <em>with a rotation identifier</em> and has no idea which run an account
/// has staged — <c>IRotationCompletenessReadService</c> says so in its own words. So a handler that
/// asked it with the <b>caller's</b> identifier rather than the staged one can be handed an abandoned
/// run whose stamps happen to be a full house, be told <em>complete</em>, and destroy the live keys on
/// the strength of a different run's stamps.
/// <see cref="HandleAsync_WhenTheQuotedRotationIsNotTheStagedOne_RefusesWithoutAskingTheGate" /> is the
/// case that arranges exactly that, and it is the reason
/// <see cref="StubRotationCompletenessReadService" /> answers per identifier instead of being a
/// <see langword="bool" /> switch.
/// </para>
/// <para>
/// <b>"Staged" no longer means "in flight", which is the second thing a reader will simplify away.</b>
/// The staging row is <em>not</em> deleted by a completion — the application role holds no
/// <c>DELETE</c> on either rotation table, and a premature delete would destroy the only copies of a
/// generation rows have already been rewritten under. So a run that has completed leaves its row
/// standing, and what tells a finished run from a live one is the epoch: the staged generation must be
/// <em>above</em> the one the manifest holds.
/// <see cref="HandleAsync_WhenResentAfterASuccessfulCompletion_RefusesAndChangesNothing" /> holds it.
/// </para>
/// <para>
/// <b>What this file CANNOT see, said once so that nothing below is read as covering it.</b> Whether
/// the promotion is really flushed, whether a rollback would really take it back, whether each factor
/// takes exactly one <c>UPDATE</c>, and whether the optimistic concurrency token on
/// <c>rotation_epoch</c> fires on a racing promotion — all four need a database and belong to the
/// integration tier. So does the <c>IS DISTINCT FROM</c> trap inside the completeness read, which
/// <c>RotationCompletenessTests</c> owns: the stub here answers a question, it does not compute one.
/// And one hole is this file's own: the fake models no commit, so a handler that promoted every entity
/// and never called <c>IKeyRotationRepository.PromoteAsync</c> at all would satisfy every byte-level
/// read here. <see cref="InMemoryKeyRotationRepository.PromoteCallCount" /> and
/// <see cref="InMemoryKeyRotationRepository.ObserveAtPromote" /> are what stand in its place, and
/// every case below asserts the count.
/// </para>
/// <para>
/// <b>ONE THING IN THE FIXTURE IS LOAD-BEARING AND LOOKS LIKE UNTIDINESS: the staged seals are seeded
/// in the OPPOSITE ORDER to the factors. Do not tidy it away.</b> A completion has to pair each factor
/// with the seal carrying its own identifier; the shorter spelling walks the two sequences side by
/// side and zips them, which reads perfectly well and gives every row a well-formed 158-byte value
/// that only some <em>other</em> factor's private key can open — twelve good rows, no exception, no
/// SQLSTATE, and an account that opens with none of them. Seeded in matching order, which is what a
/// natural fixture does, a positional pairing is indistinguishable from a keyed one and every case in
/// this file passes over it. The reversal is the test; the assertions are how it reports.
/// </para>
/// <para>
/// <b>The port this file drives</b>, named here once so a reader is not reverse-engineering it from a
/// constructor: <c>Domain.Users.IKeyRotationRepository</c> — <c>FindStagedRotationAsync</c>,
/// <c>TrackFactorsAsync</c> (the <b>tracked</b> read; <c>ListFactorsAsync</c> is <c>AsNoTracking</c>
/// and a completion cannot save through it), <c>ListStagedSealsAsync</c>,
/// <c>FindFactorManifestAsync</c> and <c>PromoteAsync</c> — beside
/// <c>Application.KeyRotations.IRotationCompletenessReadService</c>, whose first caller this is.
/// </para>
/// </remarks>
public sealed class CompleteKeyRotationHandlerTests
{
    /// <summary>How many factors a set of recovery codes is.</summary>
    private const int RecoveryCodeSetSize = 10;

    /// <summary>
    /// The generation the account's manifest starts at in most cases here, and the one a successful
    /// completion moves it to.
    /// </summary>
    /// <remarks>
    /// <b>Two literals rather than one and an expression</b>, so that "the epoch rose by exactly one"
    /// is checked against a number this file states rather than against arithmetic this file also
    /// performed. Seven is an account that has rotated before, which is the ordinary case; the account
    /// that never has is <see cref="RegistrationRotationEpoch" />, and it gets its own case because
    /// that is where the read one tier down drops every never-stamped row if its null handling is
    /// written the careful-looking way.
    /// </remarks>
    private const int StoredRotationEpoch = 7;

    /// <inheritdoc cref="StoredRotationEpoch" />
    private const int PromotedRotationEpoch = 8;

    /// <summary>
    /// The generation registration files, which is the generation an account that has never rotated is
    /// still at.
    /// </summary>
    /// <remarks>
    /// A literal rather than <c>FactorManifest.MinimumRotationEpoch</c>, the idiom
    /// <c>FactorManifestTests</c> keeps and states its reason for: every type on this path reads that
    /// constant, so a test that read it too would feed the code under test whatever the code currently
    /// believes.
    /// </remarks>
    private const int RegistrationRotationEpoch = 1;

    /// <summary>
    /// The one legal width of an encapsulated pair of account keys, and the one encapsulation framing
    /// version defined today.
    /// </summary>
    /// <remarks>
    /// Literals rather than <see cref="WrappedAccountKeys.EncapsulatedAccountKeysLength" /> and
    /// <see cref="WrappedAccountKeys.EncapsulatedAccountKeysVersion" />, which is the idiom
    /// <c>WrappedAccountKeysTests</c> and <c>BeginKeyRotationHandlerTests</c> both keep: every type on
    /// this path reads those constants, so a framing that moved by an ephemeral point would be accepted
    /// by a green suite. 158 is
    /// <c>version(1) ‖ ephemeral public key(65) ‖ nonce(12) ‖ ciphertext(64) ‖ tag(16)</c>.
    /// </remarks>
    private const int SealBytes = 158;

    /// <inheritdoc cref="SealBytes" />
    private const byte SealVersion = 1;

    /// <summary>
    /// The fillers this file's payloads carry, one distinct block per role, so that "promoted" and
    /// "left alone" can never read the same.
    /// </summary>
    /// <remarks>
    /// The twelve stored rows count up from <see cref="StoredFillerBase" /> and the twelve staged seals
    /// from <see cref="SealFillerBase" />, so the two blocks do not overlap and no factor's stored value
    /// equals any factor's staged value. <see cref="NewcomerFiller" /> is the factor registered mid-run,
    /// which is outside both blocks.
    /// </remarks>
    private const byte StoredFillerBase = 0x40;

    /// <inheritdoc cref="StoredFillerBase" />
    private const byte SealFillerBase = 0x10;

    /// <inheritdoc cref="StoredFillerBase" />
    private const byte NewcomerFiller = 0xF1;

    /// <summary>How many bytes of manifest the fixture builds. Arbitrary and well under the cap.</summary>
    private const int ManifestBytes = 24;

    /// <summary>
    /// How many times the replaying executor runs the unit of work, standing in for one transient
    /// failure recovered by a retrying provider strategy.
    /// </summary>
    private const int ReplayedAttempts = 2;

    /// <summary>
    /// <b>One narrative row is still unstamped: the completion refuses and promotes nothing.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The case the whole step exists for, and the reason its assertions are about bytes.</b> The
    /// gate answering <see langword="false" /> means at least one row of this account still holds
    /// ciphertext under the content key the promotion is about to destroy the last copies of. Waved
    /// through, that row is unreadable forever — the person keeps a transaction, a payee or an account
    /// name that no key in the world opens, and nothing anywhere says so.
    /// </para>
    /// <para>
    /// <b>A status assertion alone would not be a test of this.</b> A handler that adopted every seal,
    /// promoted the manifest and <em>then</em> asked the gate throws exactly the same exception; inside
    /// one transaction the rollback would save it, and outside one it would not — and nothing about the
    /// exception can tell the two apart. So every one of the twelve factors and both halves of the
    /// manifest row are read back and compared, which no handler that promoted first can pass.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenOneNarrativeRowIsUnstamped_RefusesAndPromotesNothing()
    {
        // Arrange — a staged run the gate reports as unfinished.
        Fixture fixture = await Fixture.Build(complete: false);

        // Act
        ConflictException refusal = await ThrowsAsync<ConflictException>(
            () => fixture.Handler.HandleAsync(fixture.Command));

        // Assert — the remedy is an act on a different resource: send the outstanding chunks.
        await Assert.That(refusal.Kind).IsEqualTo(ConflictKind.RotationIncomplete);

        // And the account still holds every byte it held. This is the assertion, not the one above.
        await AssertNothingWasPromoted(fixture);
    }

    /// <summary>
    /// <b>An account that has never rotated: the completion refuses, which is what says it asks the
    /// gate at all.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every row of such an account carries a <see langword="null" /> stamp, and the spelling that
    /// silently drops them — <c>RotationId.HasValue &amp;&amp; RotationId.Value != rotationId</c>, which
    /// reads as a careful null guard — makes the read answer <em>complete</em> before a single chunk
    /// has run. <c>RotationCompletenessTests</c> owns that trap one tier down, where the query is real;
    /// nothing here models it and this case must not be read as covering it.
    /// </para>
    /// <para>
    /// <b>What it holds instead is the half that lives up here:</b> that the destructive step consults
    /// the gate at all, on the account whose keys it is about to overwrite, and that a
    /// <see langword="false" /> stops it. The account is at the generation registration filed, which is
    /// the state of every account that has never rotated, so this is the arrangement under which a gate
    /// that was never wired in is indistinguishable from one that always answers true.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheAccountHasNeverRotated_RefusesAndPromotesNothing()
    {
        // Arrange — the manifest is still at the generation registration filed, and no row is stamped.
        Fixture fixture = await Fixture.Build(
            complete: false,
            storedRotationEpoch: RegistrationRotationEpoch);

        // The premise, so the case is really about an account that has never rotated.
        await Assert.That(fixture.StoredRotationEpoch).IsEqualTo(RegistrationRotationEpoch);

        // Act
        ConflictException refusal = await ThrowsAsync<ConflictException>(
            () => fixture.Handler.HandleAsync(fixture.Command));

        // Assert
        await Assert.That(refusal.Kind).IsEqualTo(ConflictKind.RotationIncomplete);

        // The gate was asked, about this account and about the staged run — a handler that never wired
        // it in leaves this empty, and one that asked about somebody else leaves it wrong.
        await Assert.That(fixture.Completeness.Asked.Count).IsEqualTo(1);
        await Assert.That(fixture.Completeness.Asked[0])
            .IsEqualTo((fixture.UserId, fixture.RotationId));

        await AssertNothingWasPromoted(fixture);
    }

    /// <summary>
    /// <b>The caller quotes a rotation that is not the staged one: refused, and the gate is never
    /// asked.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The step that is easy to skip and expensive, arranged so that skipping it destroys the
    /// account.</b> The completeness read takes a rotation identifier and has no idea which run is
    /// staged. Here the identifier the caller sent belongs to an <em>abandoned</em> run whose stamps
    /// happen to be a full house, while the staged run is nowhere near finished — so a handler that
    /// passed the caller's identifier through is told <em>complete</em>, promotes, and overwrites the
    /// live keys on the strength of a different run's stamps, leaving every row the staged run had not
    /// yet reached sealed under a key nothing holds a copy of.
    /// </para>
    /// <para>
    /// <b>Three assertions, and each rules out a different wrong handler.</b> The exception type rules
    /// out one that substituted the staged identifier but forgot to refuse the mismatch — that one
    /// refuses too, as an incomplete run, and is a 409 where this is a 400 about a member the caller
    /// can correct. The empty <c>Asked</c> rules out one that asked the gate before comparing. The
    /// byte-level read rules out one that promoted first.
    /// </para>
    /// <para>
    /// The refusal is a <see cref="ValidationException" /> keyed on <c>RotationId</c>, which is
    /// <c>ResealRowsHandler</c>'s spelling for the same fact about a chunk and the same remedy: begin a
    /// rotation, then quote what that begin staged.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheQuotedRotationIsNotTheStagedOne_RefusesWithoutAskingTheGate()
    {
        // Arrange — an abandoned run the gate would call complete, and a staged run it would not.
        Guid abandoned = Guid.CreateVersion7();
        Fixture fixture = await Fixture.Build(
            complete: false,
            quotes: abandoned,
            alsoComplete: abandoned);

        // Both premises: the caller is quoting something else, and that something else is a full house.
        await Assert.That(fixture.Command.RotationId).IsNotEqualTo(fixture.RotationId);
        await Assert.That(
                await fixture.Completeness.EveryNarrativeRowIsStampedAsync(fixture.UserId, abandoned))
            .IsTrue();

        // Act
        ValidationException refusal = await ThrowsAsync<ValidationException>(
            () => fixture.Handler.HandleAsync(fixture.Command));

        // Assert — keyed on the member the caller can correct, and on nothing else.
        await Assert.That(refusal.Errors.ContainsKey(nameof(CompleteKeyRotationCommand.RotationId)))
            .IsTrue();
        await Assert.That(refusal.Errors.Count).IsEqualTo(1);

        // The gate was never consulted by the handler — the one entry is the premise above.
        await Assert.That(fixture.Completeness.Asked.Count).IsEqualTo(1);

        await AssertNothingWasPromoted(fixture);
    }

    /// <summary>
    /// <b>No rotation is staged at all: refused, and the gate is never asked.</b>
    /// </summary>
    /// <remarks>
    /// One refusal with the mismatch above, deliberately, and for <c>ResealRowsHandler</c>'s reason:
    /// splitting them would put "this account has no rotation in flight" into the body of a request
    /// that was already wrong, and the caller's next act is the same either way. What this case adds is
    /// that the null is reached at all — a handler written as <c>staged.RotationId != command.RotationId</c>
    /// answers a <see cref="NullReferenceException" /> here, which is a 500 for a request that is merely
    /// stale.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenNoRotationIsStaged_RefusesWithoutAskingTheGate()
    {
        // Arrange
        Fixture fixture = await Fixture.Build(rotationIsStaged: false);

        // Act
        ValidationException refusal = await ThrowsAsync<ValidationException>(
            () => fixture.Handler.HandleAsync(fixture.Command));

        // Assert
        await Assert.That(refusal.Errors.ContainsKey(nameof(CompleteKeyRotationCommand.RotationId)))
            .IsTrue();
        await Assert.That(fixture.Completeness.Asked).IsEmpty();
        await AssertNothingWasPromoted(fixture);
    }

    /// <summary>
    /// <b>A factor was registered while the run was in flight: refused, and nothing is promoted.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The orphaning this whole slice exists to prevent, at the only moment it becomes
    /// irreversible.</b> The staged run holds a seal for each of the twelve factors the account had
    /// when it began; a thirteenth has been registered since, and no seal exists for it. Promoted, the
    /// twelve adopt the new generation and the thirteenth is left holding a copy of a content key that
    /// opens nothing — an authenticator the person still has, still enrolled, still offered as a way
    /// back in. Nothing reports it, and the only repair goes through one of the twelve.
    /// </para>
    /// <para>
    /// <b>The manifest generation is deliberately left where it was, and that arrangement is the
    /// point.</b> In production a registration also promotes the manifest, so this request would
    /// usually be stopped one step earlier by the epoch rule. That is a property of <em>those</em>
    /// paths, not of this one: held only there, a factor added by any path that forgot to carry a
    /// manifest would sail through here and cost the account a way in. The set comparison is the check
    /// that does not depend on another handler having done its job, so it is measured with the epoch
    /// rule satisfied.
    /// </para>
    /// <para>
    /// <b>The gate is asserted to have answered before the refusal</b>, which is what says the set
    /// comparison — and not an unfinished run — is what stopped this.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenAFactorWasRegisteredMidRun_RefusesAndPromotesNothing()
    {
        // Arrange — twelve sealed factors, and a thirteenth registered since the begin.
        Fixture fixture = await Fixture.Build();
        fixture.KeyRotations.SeedPasskeyFactor(fixture.Passkey, Guid.CreateVersion7(), NewcomerFiller);

        // The premise: the account really does hold one more factor than the run staged a seal for.
        await Assert.That(fixture.KeyRotations.FactorsOf(fixture.UserId).Count)
            .IsEqualTo(fixture.FactorIds.Count + 1);
        await Assert.That(fixture.KeyRotations.SealsOf(fixture.UserId).Count)
            .IsEqualTo(fixture.FactorIds.Count);

        // Act
        ConflictException refusal = await ThrowsAsync<ConflictException>(
            () => fixture.Handler.HandleAsync(fixture.Command));

        // Assert — the remedy is to begin the rotation again carrying the corrected factor set, which
        // is the sentence this kind already travels with on the two paths that move a factor set.
        await Assert.That(refusal.Kind).IsEqualTo(ConflictKind.FactorSetMoved);

        // The run really was finished, so the set comparison is what refused it.
        await Assert.That(fixture.Completeness.Asked.Count).IsEqualTo(1);

        await AssertNothingWasPromoted(fixture);
    }

    /// <summary>
    /// <b>A staged seal names a factor the account no longer holds: refused, and nothing is
    /// promoted.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The other direction of the comparison, and it fails differently — which is why it is written
    /// beside its mirror rather than folded into it.</b> Left unchecked, the promotion reaches for a
    /// row that is not there: a <see cref="KeyNotFoundException" /> at best, which is a 500 for a
    /// caller whose request was merely overtaken, and a silently skipped factor at worst. Compared as a
    /// set, the account is told the one thing it can act on — the factor set moved, so begin again.
    /// </para>
    /// <para>
    /// <b>The arrangement models a cascade that did not fire, and says so.</b> In production the
    /// composite <c>ON DELETE CASCADE</c> from <c>wrapped_account_keys</c> takes the seal with the row,
    /// so a revocation mid-run normally leaves eleven factors and eleven seals and this completion would
    /// proceed. The check is therefore defence in depth rather than the common path — and it is worth
    /// holding for the reason every gate on this route is: the promotion is the one act nothing can
    /// undo, so it may not depend on a foreign key having behaved.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenASealNamesAFactorTheAccountNoLongerHolds_RefusesAndPromotesNothing()
    {
        // Arrange — one of the card's ten factors is gone, and its staged seal is still standing.
        Fixture fixture = await Fixture.Build();
        Guid revoked = fixture.FactorIds[^1];
        fixture.KeyRotations.RemoveFactor(revoked);

        // Both premises: the factor is gone and the seal is not.
        await Assert.That(fixture.KeyRotations.FactorsOf(fixture.UserId)).DoesNotContain(revoked);
        await Assert.That(fixture.KeyRotations.SealsOf(fixture.UserId).Select(seal => seal.FactorId))
            .Contains(revoked);

        // Act
        ConflictException refusal = await ThrowsAsync<ConflictException>(
            () => fixture.Handler.HandleAsync(fixture.Command));

        // Assert
        await Assert.That(refusal.Kind).IsEqualTo(ConflictKind.FactorSetMoved);
        await AssertNothingWasPromoted(fixture, fixture.FactorIds.Where(id => id != revoked));
    }

    /// <summary>
    /// <b>Every row is stamped: each of the twelve factors adopts its own staged seal and the manifest
    /// generation rises by exactly one.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The control, and without it every refusal above is satisfied by a handler that refuses
    /// everything. Twelve factors rather than one, because an account holds one row per <em>factor</em>
    /// and a set of recovery codes is ten of them under a single credential: a promotion that moved the
    /// passkeys and left the card behind is the exact shape that reddens nothing and costs somebody
    /// their way back in.
    /// </para>
    /// <para>
    /// <b>Each factor is matched against its own seal and not merely against "some new bytes".</b> The
    /// two are paired by factor identifier, because a handler that walked its seals and its factors in
    /// two separately-ordered sequences would give every row a well-formed 158-byte value that only
    /// some other factor's private key can open — twelve good rows, no exception, and an account that
    /// opens with none of them. The offenders are collected and asserted empty rather than compared one
    /// at a time, so a failure names every factor that moved wrongly instead of the first.
    /// </para>
    /// <para>
    /// <b>The staging row is asserted to still stand.</b> A completion deletes nothing: the role holds
    /// no <c>DELETE</c> on either rotation table, and a premature delete destroys the only copies of a
    /// generation rows have already been rewritten under. That grant is the integration tier's to hold;
    /// what is visible here is that the handler does not reach for one.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenEveryRowIsStamped_PromotesEveryFactorAndTheManifest()
    {
        // Arrange — two passkeys and a card of ten codes, which is twelve factors.
        Fixture fixture = await Fixture.Build();

        // The premise, because every line below is vacuous if the account turned out to hold one
        // factor after all.
        await Assert.That(fixture.FactorIds.Count).IsEqualTo(2 + RecoveryCodeSetSize);

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — every factor carries the bytes ITS OWN seal staged.
        Guid[] notAdopted =
        [
            .. fixture.FactorIds.Where(factorId =>
                !(fixture.KeyRotations.FactorOf(factorId) ?? [])
                    .SequenceEqual(fixture.StagedSealKeys[factorId])),
        ];
        await Assert.That(notAdopted).IsEmpty();

        // And the manifest is the staged one, at a generation exactly one above the stored one.
        (byte[] Manifest, int RotationEpoch) manifest = fixture.CurrentManifest();
        await Assert.That(manifest.Manifest.SequenceEqual(fixture.StagedManifest)).IsTrue();
        await Assert.That(manifest.RotationEpoch).IsEqualTo(PromotedRotationEpoch);
        await Assert.That(manifest.RotationEpoch).IsEqualTo(fixture.StoredRotationEpoch + 1);

        // The gate was asked exactly once, about this account and this staged run.
        await Assert.That(fixture.Completeness.Asked.Count).IsEqualTo(1);
        await Assert.That(fixture.Completeness.Asked[0])
            .IsEqualTo((fixture.UserId, fixture.RotationId));

        // The save happened, and the staging row was left exactly where it was.
        await Assert.That(fixture.KeyRotations.PromoteCallCount).IsEqualTo(1);
        await Assert.That(fixture.KeyRotations.StageCallCount).IsEqualTo(0);
        await Assert.That(await fixture.KeyRotations.FindStagedRotationAsync(fixture.UserId))
            .IsNotNull();
    }

    /// <summary>
    /// <b>The same completion sent twice: the second is refused and changes nothing.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what "the staging row is not deleted" costs, and the rule that pays for it.</b> After
    /// a successful run the staged row is still there, carrying the same identifier the caller is
    /// quoting — so "is a rotation staged, and is it this one" is satisfied by a run that finished
    /// minutes ago. The only thing standing between a re-sent request and a <em>second</em> promotion
    /// is the epoch: the staged generation has to be <b>above</b> the one the manifest now holds, and
    /// after a completion it is equal to it.
    /// </para>
    /// <para>
    /// <b>What a second promotion would cost.</b> The seals are unchanged, so every factor would be
    /// rewritten with the value it already holds — harmless — and the manifest would be asked to move
    /// to a generation it is already at, which <c>FactorManifest.Promote</c> refuses as a 400 about the
    /// caller's arithmetic. That is the wrong answer to a client whose first request succeeded and
    /// whose response was lost: it says "your number is wrong" where the truth is "this is already
    /// done". Hence a kind of its own.
    /// </para>
    /// <para>
    /// <b>The commit between the two requests is explicit</b>, because the fakes model no save — see
    /// <see cref="InMemoryKeyRotationRepository.Commit" />, which exists for this and for nothing a
    /// handler does.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenResentAfterASuccessfulCompletion_RefusesAndChangesNothing()
    {
        // Arrange — one completion that worked, and its transaction committed.
        Fixture fixture = await Fixture.Build();
        await fixture.Handler.HandleAsync(fixture.Command);
        fixture.KeyRotations.Commit();

        // Act — the same request again, quoting the same run.
        ConflictException refusal = await ThrowsAsync<ConflictException>(
            () => fixture.Handler.HandleAsync(fixture.Command));

        // Assert — "already done", never "your epoch is wrong".
        await Assert.That(refusal.Kind).IsEqualTo(ConflictKind.RotationAlreadyCompleted);

        // Nothing moved a second time: the factors still carry the first completion's bytes, the
        // manifest is still at one above the stored generation, and no second save was asked for.
        Guid[] moved =
        [
            .. fixture.FactorIds.Where(factorId =>
                !(fixture.KeyRotations.FactorOf(factorId) ?? [])
                    .SequenceEqual(fixture.StagedSealKeys[factorId])),
        ];
        await Assert.That(moved).IsEmpty();

        (byte[] Manifest, int RotationEpoch) manifest = fixture.CurrentManifest();
        await Assert.That(manifest.RotationEpoch).IsEqualTo(PromotedRotationEpoch);
        await Assert.That(manifest.Manifest.SequenceEqual(fixture.StagedManifest)).IsTrue();
        await Assert.That(fixture.KeyRotations.PromoteCallCount).IsEqualTo(1);

        // The epoch rule refused it before the gate was consulted a second time, which is what says
        // "staged" alone is no longer read as "in flight".
        await Assert.That(fixture.Completeness.Asked.Count).IsEqualTo(1);
    }

    /// <summary>
    /// <b>The gate <em>refuses</em> rather than answering: the refusal escapes, and nothing is
    /// promoted.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Refusing and answering <see langword="false" /> are two different facts, and a handler that
    /// folds them is right by accident.</b> A <see langword="false" /> says every row of the account is
    /// visible and some are outstanding. <see cref="RotationScopeException" /> says the read could not
    /// see the question at all: five of the six sets it walks carry the <c>BudgetIsolation</c> filter,
    /// which takes no argument, so for an account owning a budget beside the ambient one the answer
    /// would be about part of an account — and the part it cannot reach is exactly the part the
    /// promotion would destroy. A handler that swallowed it promotes over rows the read never saw.
    /// </para>
    /// <para>
    /// <b>The assertion is that it ESCAPES, not that it becomes a conflict.</b>
    /// <see cref="RotationScopeException" /> deliberately has no <c>IExceptionHandler</c> of its own —
    /// it falls to the catch-all and surfaces as a 500, and its own remarks say why a named mapping is
    /// the seam a later reader softens into "rotate the budget we can see and warn". Turning it into a
    /// <see cref="ConflictException" /> here would be that softening, one ring up, so this case is
    /// written so that a conflict fails it: the helper catches
    /// <see cref="RotationScopeException" /> exactly, and anything else escapes and fails the test as
    /// itself.
    /// </para>
    /// <para>
    /// <b>Arranged over a run that would otherwise succeed</b> — the gate would have said complete —
    /// so the refusal is the only thing standing between this request and a promotion.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheGateRefusesForScope_LetsTheRefusalEscapeAndPromotesNothing()
    {
        // Arrange — an account whose owned budgets are not exactly the one this request is inside.
        Fixture fixture = await Fixture.Build();
        fixture.Completeness.RefusesForScope = true;

        // Act
        await ThrowsAsync<RotationScopeException>(() => fixture.Handler.HandleAsync(fixture.Command));

        // Assert — the refusal came out of the gate rather than out of something earlier that happens
        // to have the same shape.
        await Assert.That(fixture.Completeness.Asked.Count).IsEqualTo(1);
        await Assert.That(fixture.Completeness.Asked[0])
            .IsEqualTo((fixture.UserId, fixture.RotationId));

        await AssertNothingWasPromoted(fixture);
    }

    /// <summary>
    /// <b>The account holds no manifest row: the request fails, and nothing is promoted.</b>
    /// </summary>
    /// <remarks>
    /// A 500 on purpose, and the same refusal <c>RevokePasskeyHandler</c> makes in the same words.
    /// Registration has written a manifest for every account since the table existed, so there is no
    /// account this can legitimately find nothing for; filing a first one here would let a completion
    /// establish the account's factor set under bytes and an epoch nothing upstream agreed to, and
    /// skipping the promotion would leave the account's only statement of its factor set describing a
    /// generation that no longer exists. It is deliberately not a
    /// <see cref="ValidationException" />: nothing the caller sent is wrong, so there is no member to
    /// key a 400 on.
    /// <para>
    /// <b><see cref="RotationScopeException" /> derives from <see cref="InvalidOperationException" />,
    /// so it is ruled out by name below rather than left to the arrangement.</b> The gate answers
    /// complete here and cannot refuse, so the two are not reachable together today — but the catch is
    /// wide enough to accept one, and a reader who later gave the missing manifest a mapped type would
    /// find this case quietly passing on the wrong exception.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheAccountHoldsNoManifest_FailsAndPromotesNothing()
    {
        // Arrange
        Fixture fixture = await Fixture.Build(seedFactorManifest: false);

        // Act
        InvalidOperationException failure = await ThrowsAsync<InvalidOperationException>(
            () => fixture.Handler.HandleAsync(fixture.Command));

        // Assert — the missing row, and not the scope refusal wearing its base type.
        await Assert.That(failure).IsNotTypeOf<RotationScopeException>();

        // The factors are untouched, and no save was asked for.
        Guid[] moved =
        [
            .. fixture.FactorIds.Where(factorId =>
                !(fixture.KeyRotations.FactorOf(factorId) ?? [])
                    .SequenceEqual(fixture.StoredFactorKeys[factorId])),
        ];
        await Assert.That(moved).IsEmpty();
        await Assert.That(fixture.KeyRotations.PromoteCallCount).IsEqualTo(0);
    }

    /// <summary>
    /// <b>The promotion happens inside the unit of work, not beside it.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// A handler that enters the executor, does nothing in the delegate and promotes afterwards
    /// satisfies every other count in this file — the executor ran, the bytes moved, the save was asked
    /// for once — and is wrong in the one way that cannot be undone. A write outside the transaction is
    /// a write nothing rolls back, and on this path that is the account's every factor rewritten under
    /// a generation whose manifest promotion was abandoned: twelve rows encapsulating keys the stored
    /// manifest does not describe, and no route that puts either side back.
    /// </para>
    /// <para>
    /// <b>The observation is the fake reporting the call it received, not a model of PostgreSQL.</b>
    /// <see cref="ObservedTransactionalExecutor" /> sets its flag around the handler's own delegate and
    /// <see cref="InMemoryKeyRotationRepository.ObserveAtPromote" /> reads it at the moment the save
    /// arrives. A null observation means nothing was ever promoted, which is a different failure from a
    /// promotion made on the wrong side, so both are named.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_PromotesInsideTheUnitOfWork()
    {
        // Arrange
        Fixture fixture = await Fixture.Build();
        fixture.KeyRotations.ObserveAtPromote = () => fixture.UnitOfWork.InsideUnitOfWork;

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert
        await Assert.That(fixture.KeyRotations.ObservationAtPromote).IsNotNull();
        await Assert.That(fixture.KeyRotations.ObservationAtPromote).IsTrue();
    }

    /// <summary>
    /// <b>A replayed unit of work still moves the generation exactly one.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The delegate is replayed under a retrying execution strategy, against a change tracker the
    /// rollback did not empty. A completion that did not discard meets its own promoted instance on the
    /// second attempt — the manifest already at <c>N + 1</c> — and <c>FactorManifest.Promote</c> refuses
    /// it, so a <b>valid</b> completion is answered with a 400 about the caller's arithmetic because the
    /// database blinked. <c>RevokePasskeyHandler</c> and <c>GenerateRecoveryCodesHandler</c> both write
    /// the argument out; this handler inherits it.
    /// </para>
    /// <para>
    /// <b>The discard is pinned by attempt number rather than by a call count</b>, the device
    /// <see cref="RecordingPersistenceState" /> exists for: one made before the executor is entered runs
    /// on attempt zero and does not survive the rollback it exists to clean up after, while one at the
    /// top of the delegate runs on every attempt. Both facts are asserted, because either alone is
    /// satisfied by the wrong placement.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheUnitOfWorkIsReplayed_PromotesTheManifestExactlyOnce()
    {
        // Arrange — an executor that really replays the unit of work.
        Fixture fixture = await Fixture.Build(attempts: ReplayedAttempts);

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — one generation moved, however many attempts ran.
        (byte[] Manifest, int RotationEpoch) manifest = fixture.CurrentManifest();
        await Assert.That(manifest.RotationEpoch).IsEqualTo(PromotedRotationEpoch);
        await Assert.That(manifest.RotationEpoch).IsEqualTo(fixture.StoredRotationEpoch + 1);

        // And every factor still carries its own seal's bytes rather than a second pass's leftovers.
        Guid[] notAdopted =
        [
            .. fixture.FactorIds.Where(factorId =>
                !(fixture.KeyRotations.FactorOf(factorId) ?? [])
                    .SequenceEqual(fixture.StagedSealKeys[factorId])),
        ];
        await Assert.That(notAdopted).IsEmpty();

        // The unit of work really was entered and really was replayed, and the discard is inside it.
        await Assert.That(fixture.Executor.Attempts).IsEqualTo(ReplayedAttempts);
        await Assert.That(fixture.PersistenceState.DiscardedOnAttempt.Contains(0)).IsFalse();
        await Assert.That(fixture.PersistenceState.DiscardedOnAttempt.Distinct().Order().ToArray())
            .IsEquivalentTo(new[] { 1, ReplayedAttempts });
    }

    /// <summary>
    /// Reads every factor and the manifest back and asserts that not one byte of either moved.
    /// </summary>
    /// <remarks>
    /// <b>This, and not the exception, is what a refusal on this route has to be measured by.</b> A
    /// handler that adopted every seal, promoted the manifest and only then refused throws exactly the
    /// exception the case expects; inside a transaction the rollback would cover it and outside one it
    /// would not, and nothing about the exception can tell the two apart. The offenders are collected
    /// and asserted empty rather than compared one at a time, so a failure names every factor that
    /// moved instead of the first.
    /// <para>
    /// <paramref name="factorIds" /> defaults to the whole seeded set. A case that took a factor away
    /// mid-run passes the survivors, because a row that is gone holds no bytes to compare.
    /// </para>
    /// </remarks>
    private static async Task AssertNothingWasPromoted(
        Fixture fixture,
        IEnumerable<Guid>? factorIds = null)
    {
        Guid[] moved =
        [
            .. (factorIds ?? fixture.FactorIds).Where(factorId =>
                !(fixture.KeyRotations.FactorOf(factorId) ?? [])
                    .SequenceEqual(fixture.StoredFactorKeys[factorId])),
        ];
        await Assert.That(moved).IsEmpty();

        (byte[] Manifest, int RotationEpoch) manifest = fixture.CurrentManifest();
        await Assert.That(manifest.RotationEpoch).IsEqualTo(fixture.StoredRotationEpoch);
        await Assert.That(manifest.Manifest.SequenceEqual(fixture.StoredManifest)).IsTrue();

        // No save was even asked for, which is the fact this fake can report about the save it cannot
        // model — see the class remarks.
        await Assert.That(fixture.KeyRotations.PromoteCallCount).IsEqualTo(0);
    }

    /// <summary>A manifest of distinct bytes, so an assertion over it is about position.</summary>
    /// <remarks>
    /// <paramref name="seed" /> is what tells the stored manifest from the staged one, so a case cannot
    /// pass on a row that was never rewritten. Nothing at this layer parses a manifest: it is
    /// authenticated by a key this server does not hold.
    /// </remarks>
    private static byte[] Manifest(byte seed) =>
        [.. Enumerable.Range(0, ManifestBytes).Select(offset => (byte)(seed + offset))];

    /// <summary>A well-formed encapsulated pair of account keys, filled with <paramref name="filler" />.</summary>
    private static byte[] SealPayload(byte filler)
    {
        byte[] payload = new byte[SealBytes];
        Array.Fill(payload, filler);
        payload[0] = SealVersion;

        return payload;
    }

    /// <summary>
    /// Runs <paramref name="action" /> and returns the exception it was expected to throw.
    /// </summary>
    /// <remarks>
    /// The catch names <typeparamref name="TException" /> exactly, so an exception of any other type
    /// escapes and fails the test as itself rather than as "the expected exception was not thrown" —
    /// which matters here, where a 400, two different 409s and a 500 are being told apart.
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

        throw new NothingWasThrownException($"Expected {typeof(TException).Name}.");
    }

    /// <summary>
    /// What <see cref="ThrowsAsync{TException}" /> raises when the action threw nothing at all.
    /// </summary>
    /// <remarks>
    /// A type of its own rather than the house sentinel, which is an
    /// <see cref="InvalidOperationException" />. One case here <em>expects</em> an
    /// <see cref="InvalidOperationException" /> — the missing manifest row, which is a 500 by decision
    /// — so the shared spelling would let the helper's own sentinel stand in for the refusal under
    /// test, and would report a handler that threw nothing as "Expected InvalidOperationException",
    /// which reads as a tautology.
    /// </remarks>
    private sealed class NothingWasThrownException(string message) : Exception(message);

    /// <summary>
    /// The handler and the collaborators behind it, assembled once so no case has to restate a
    /// five-argument constructor — and carrying what the arrangement <em>claimed</em>, so a refusal can
    /// be measured against the account it was supposed to leave alone.
    /// </summary>
    /// <remarks>
    /// <b>There is no re-authentication gate on this record and that is not an omission.</b> A
    /// completion spends no nonce and verifies no assertion: the begin is the act a passkey is proved
    /// for, and a run in flight is already the account's own. What guards this route is the identity on
    /// the session and the four ordered refusals below it.
    /// </remarks>
    private sealed record Fixture(
        CompleteKeyRotationHandler Handler,
        CompleteKeyRotationCommand Command,
        InMemoryKeyRotationRepository KeyRotations,
        StubRotationCompletenessReadService Completeness,
        RecordingPersistenceState PersistenceState,
        RetryingTransactionalExecutor Executor,
        ObservedTransactionalExecutor UnitOfWork,
        Credential Passkey,
        IReadOnlyList<Guid> FactorIds,
        IReadOnlyDictionary<Guid, byte[]> StoredFactorKeys,
        IReadOnlyDictionary<Guid, byte[]> StagedSealKeys,
        byte[] StoredManifest,
        byte[] StagedManifest,
        int StoredRotationEpoch,
        Guid RotationId,
        Guid UserId)
    {
        /// <summary>Fixed instant for every seeded row, so nothing here depends on the wall clock.</summary>
        public static readonly DateTime UtcNow = new(2026, 9, 10, 11, 12, 13, DateTimeKind.Utc);

        /// <summary>
        /// The manifest the account would hold if this unit of work committed now.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The fixture seeded none, which every case but one arranges deliberately — so this throws
        /// rather than returning a value a comparison would quietly pass over.
        /// </exception>
        public (byte[] Manifest, int RotationEpoch) CurrentManifest() =>
            KeyRotations.FactorManifestOf(UserId)
            ?? throw new InvalidOperationException("This fixture seeded no factor manifest.");

        /// <summary>
        /// Builds the handler over fresh fakes, with an account holding two passkeys and a card of ten
        /// recovery codes — twelve factors, which is what makes a promotion that covers the passkeys
        /// and forgets the card visible.
        /// </summary>
        /// <param name="attempts">
        /// How many times the executor runs the unit of work. One is the ordinary case; more than one
        /// is what the replay case needs, and it is a parameter rather than a second fixture so the two
        /// share one wiring.
        /// </param>
        /// <param name="complete">
        /// Whether the completeness gate reports the staged run as finished. <see langword="false" /> is
        /// an account with at least one narrative row still sealed under the old content key, which is
        /// the state the promotion must never run in.
        /// </param>
        /// <param name="rotationIsStaged">
        /// Whether the account has a rotation staged at all. <see langword="false" /> is a caller
        /// quoting a run that was replaced, or one that never existed.
        /// </param>
        /// <param name="seedFactorManifest">
        /// Whether the account holds the manifest row registration files. <see langword="false" /> is
        /// not a state any path produces, and it is reachable here on purpose: it is a 500 by decision.
        /// </param>
        /// <param name="storedRotationEpoch">
        /// The generation the account's manifest is at. <see cref="RegistrationRotationEpoch" /> is an
        /// account that has never rotated.
        /// </param>
        /// <param name="quotes">
        /// The identifier the command carries, when a case needs it to be something other than the
        /// staged run's.
        /// </param>
        /// <param name="alsoComplete">
        /// A second run the gate calls finished. It exists for one case: an abandoned run whose stamps
        /// are a full house, which is what makes "the handler substitutes the staged identifier"
        /// observable at all.
        /// </param>
        public static async Task<Fixture> Build(
            int attempts = 1,
            bool complete = true,
            bool rotationIsStaged = true,
            bool seedFactorManifest = true,
            // Qualified, because the record's own positional member shadows the outer constant here —
            // and unqualified it compiles to nothing a reader would predict.
            int storedRotationEpoch = CompleteKeyRotationHandlerTests.StoredRotationEpoch,
            Guid? quotes = null,
            Guid? alsoComplete = null)
        {
            Guid userId = Guid.CreateVersion7();
            Guid rotationId = Guid.CreateVersion7();
            StubUserContext userContext = new(userId);
            InMemoryKeyRotationRepository keyRotations = new();

            // TWO PASSKEYS AND ONE CARD, WHICH IS TWELVE FACTORS UNDER THREE CREDENTIALS. A set of
            // recovery codes is ten separate secrets under a single Credential — ten key-encryption
            // keys, ten key pairs, ten rows — so a fixture built on one passkey would let a promotion
            // that moves "the factor" pass while orphaning ten.
            Credential passkey = Credential.CreatePasskey(userId, UtcNow);
            Credential secondPasskey = Credential.CreatePasskey(userId, UtcNow);
            Credential recoveryCodes = Credential.CreateRecoveryCodes(userId, UtcNow);

            List<Guid> factorIds = [];
            Dictionary<Guid, byte[]> storedFactorKeys = [];
            Dictionary<Guid, byte[]> stagedSealKeys = [];

            foreach (Credential credential in new[] { passkey, secondPasskey })
            {
                Guid factorId = Guid.CreateVersion7();
                keyRotations.SeedPasskeyFactor(
                    credential,
                    factorId,
                    (byte)(StoredFillerBase + factorIds.Count));
                factorIds.Add(factorId);
            }

            foreach (int _ in Enumerable.Range(0, RecoveryCodeSetSize))
            {
                Guid factorId = Guid.CreateVersion7();
                keyRotations.SeedRecoveryCodeFactor(
                    recoveryCodes,
                    factorId,
                    (byte)(StoredFillerBase + factorIds.Count));
                factorIds.Add(factorId);
            }

            // What the arrangement CLAIMS each row holds now and what it claims each row's seal stages,
            // kept apart in two blocks of fillers that do not overlap — so "promoted" and "left alone"
            // can never read the same, for any factor.
            for (int index = 0; index < factorIds.Count; index++)
            {
                storedFactorKeys[factorIds[index]] = SealPayload((byte)(StoredFillerBase + index));
                stagedSealKeys[factorIds[index]] = SealPayload((byte)(SealFillerBase + index));
            }

            byte[] storedManifest = Manifest(0x80);
            byte[] stagedManifest = Manifest(0xC0);

            if (seedFactorManifest)
            {
                keyRotations.SeedFactorManifest(userId, storedManifest, storedRotationEpoch);
            }

            // The staged generation is one above the stored one, which is what the begin that produced
            // it had to send and what makes the run "in flight" rather than finished.
            KeyRotation rotation = KeyRotation.Begin(
                passkey,
                rotationId,
                stagedManifest,
                storedRotationEpoch + 1,
                UtcNow);

            if (rotationIsStaged)
            {
                keyRotations.SeedStagedRotation(rotation);

                // Through KeyRotationSeal.For over the loaded rows, never fabricated, so every seal
                // this fixture stages is one a real begin could have staged.
                //
                // STAGED IN THE OPPOSITE ORDER TO THE FACTORS, WHICH IS NOT TIDINESS. A completion has
                // to pair each factor with the seal carrying ITS OWN identifier; the shorter spelling
                // walks two sequences side by side and zips them, which reads perfectly well and gives
                // every row a well-formed 158-byte value that only some other factor's private key can
                // open — twelve good rows, no exception, no SQLSTATE, and an account that opens with
                // none of them. Seeded in the same order, a positional pairing is indistinguishable
                // from a keyed one and every case in this file passes over it. Reversed, it is not.
                //
                // DO NOT TIDY THIS AWAY. A fixture that staged the seals in factor order would read
                // more naturally, would change no assertion in this file, and would silently retire
                // the only thing standing between a Zip and an account that opens with none of its
                // twelve factors. The reversal IS the test; the assertions below are how it reports.
                IReadOnlyDictionary<Guid, WrappedAccountKeys> factors =
                    await keyRotations.ListFactorsAsync(userId);
                keyRotations.SeedStagedSeals(
                    userId,
                    [
                        .. ((IEnumerable<Guid>)factorIds).Reverse().Select(factorId =>
                            KeyRotationSeal.For(
                                rotation,
                                factors[factorId],
                                stagedSealKeys[factorId])),
                    ]);
            }

            List<Guid> finishedRuns = [];

            if (complete)
            {
                finishedRuns.Add(rotationId);
            }

            if (alsoComplete is { } abandoned)
            {
                finishedRuns.Add(abandoned);
            }

            StubRotationCompletenessReadService completeness = new([.. finishedRuns]);

            // A replaying executor even at one attempt, rather than InMemoryTransactionalExecutor: at
            // one attempt the two are behaviourally identical, and going through this one is what makes
            // the replay case a parameter rather than a second wiring.
            RetryingTransactionalExecutor executor = new(attempts);

            // Wrapped rather than replaced, so the replay behaviour stays the shared fake's and the
            // wrapper adds the one fact a collaborator needs to say which side of the delegate it was
            // called from.
            ObservedTransactionalExecutor unitOfWork = new(executor);

            RecordingPersistenceState persistenceState = new(
                () => executor.Attempts,
                keyRotations.DiscardTrackedEntities);

            CompleteKeyRotationHandler handler = new(
                keyRotations,
                completeness,
                userContext,
                persistenceState,
                unitOfWork);

            return new Fixture(
                handler,
                new CompleteKeyRotationCommand(quotes ?? rotationId),
                keyRotations,
                completeness,
                persistenceState,
                executor,
                unitOfWork,
                passkey,
                factorIds,
                storedFactorKeys,
                stagedSealKeys,
                storedManifest,
                stagedManifest,
                storedRotationEpoch,
                rotationId,
                userId);
        }
    }
}
