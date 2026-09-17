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
/// <b>THE FACTOR-SET GATE IS BACK, OVER THE SEALS, AND THIS PARAGRAPH REPLACES THE ONE THAT RECORDED
/// ITS ABSENCE. Read it before writing a weaker case.</b> The rule has never changed — <b>the factor
/// set a rotation commits to must be exactly the account's live set, set equality in both directions,
/// never "the factor presented is one of them"</b> — and its failure mode has never changed either: an
/// authenticator the person still holds, still enrolled, that can no longer unlock the account, with no
/// repair path except a recovery code. What moved twice is where the rule is <em>checkable</em>. It was
/// held by three cases around a <c>FactorId</c> on the command; when that member went, those cases went
/// with it, because the rule had moved into the <b>staged manifest</b> and nothing on this server can
/// parse one — the bytes are authenticated client-side material, and a guard over the epoch alone would
/// have been a green bar saying a rule is held that is not.
/// </para>
/// <para>
/// It is checkable again, and in a better place than either: the command carries
/// <see cref="BeginKeyRotationCommand.Seals" />, one per factor the run stages a value for, which is
/// where the set a run <em>actually</em> commits to is stated in the clear. The handler compares that
/// set against <c>IKeyRotationRepository.ListFactorsAsync</c> in both directions, and the listing now
/// answers <b>every</b> factor rather than the passkey factors alone — so the ten factors of a recovery
/// code card are inside the set, which they never were before.
/// </para>
/// <para>
/// <b>What is still NOT held, and no case below may be read as covering it:</b> the manifest's own
/// named set. <c>command.StagedManifest</c> is authenticated by a key this server does not hold, so a
/// client may stage a manifest naming a different set than its seals and nothing refuses it. FR-123 is
/// held over the <em>seals</em> and not over the manifest. The handler is also still unreachable over
/// HTTP — no route begins a rotation — which is what keeps that residual gap affordable, and is the
/// first thing a begin route has to answer.
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
/// <b>The two ports these tests drive</b>, named here once so that a reader is not reverse-engineering
/// them from constructor arguments: <c>Domain.Users.IKeyRotationRepository</c> —
/// <c>ListFactorsAsync</c> (<b>every</b> factor the account holds, keyed on factor id and valued by the
/// <c>WrappedAccountKeys</c> row that <em>is</em> the factor, because
/// <see cref="KeyRotationSeal.For" /> takes the loaded entity), <c>FindStagedRotationAsync</c>,
/// <c>StageAsync(rotation, seals)</c>; and
/// <c>Application.KeyRotations.IRotationInventoryReadService</c> — <c>ListOwnedBudgetIdsAsync</c> and
/// <c>CountNarrativeRowsAsync</c>.
/// </para>
/// <para>
/// <b>What no case here can reach, said once so that nothing below is read as covering it:</b> the
/// per-key shape of the staging write. <see cref="InMemoryKeyRotationRepository" /> replaces an
/// account's whole seal list, which is observationally identical to the per-key converge the adapter
/// performs — so "one statement per factor, an <c>UPDATE</c> where a seal stood and an <c>INSERT</c>
/// where none did, never a <c>DELETE</c> and an <c>INSERT</c> of one key together" is invisible from
/// here and belongs to the integration tier. So does whether a rollback would really take the rows
/// back.
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
    /// The one legal width of an encapsulated pair of account keys, and the one encapsulation framing
    /// version defined today.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Literals rather than <see cref="WrappedAccountKeys.EncapsulatedAccountKeysLength" /> and
    /// <see cref="WrappedAccountKeys.EncapsulatedAccountKeysVersion" />, which is the idiom
    /// <c>WrappedAccountKeysTests</c> keeps and states its reason for.</b> Every type on this path reads
    /// those constants — <see cref="KeyRotationSeal.For" /> judges a seal by them, and the column's
    /// check constraints are rendered from them — so a test that read them too would feed the code under
    /// test whatever that code currently believes, and a framing that moved by an ephemeral point would
    /// be accepted by a green suite. Written out, a move reddens the malformed case here as well as the
    /// pins that own the format.
    /// </para>
    /// <para>
    /// 158 is <c>version(1) ‖ ephemeral public key(65) ‖ nonce(12) ‖ ciphertext(64) ‖ tag(16)</c> over
    /// one 64-byte plaintext holding both account keys. It is a <b>width and not a cap</b>: AES-GCM
    /// ciphertext is exactly as long as its plaintext, so a byte short and a byte long are both refused.
    /// </para>
    /// </remarks>
    private const int SealBytes = 158;

    /// <inheritdoc cref="SealBytes" />
    private const int SealVersion = 1;

    /// <summary>
    /// The fillers the seal payloads in this file carry, one distinct value per role.
    /// </summary>
    /// <remarks>
    /// The fixture's seals count up from <see cref="SealFillerBase" />, one per live factor, so a value
    /// read back under the wrong factor is visible rather than being two identical buffers. The other
    /// four name a seal a specific case builds: the abandoned run's, a duplicate's, a stranger factor's
    /// and a malformed one's. All of them differ from every fixture filler and from each other.
    /// </remarks>
    private const byte SealFillerBase = 0x10;

    /// <inheritdoc cref="SealFillerBase" />
    private const byte AbandonedSealFiller = 0xAB;

    /// <inheritdoc cref="SealFillerBase" />
    private const byte DuplicateSealFiller = 0xD1;

    /// <inheritdoc cref="SealFillerBase" />
    private const byte StrangerSealFiller = 0xF1;

    /// <inheritdoc cref="SealFillerBase" />
    private const byte MalformedSealFiller = 0xE2;

    /// <summary>
    /// The two sentences the factor-set gate can refuse with, quoted by the fragment that tells them
    /// apart.
    /// </summary>
    /// <remarks>
    /// Both refusals are a <see cref="ValidationException" /> keyed on the same member, which is
    /// correct — it is the same field being wrong — and is exactly what leaves the message as the only
    /// discriminator. Fragments rather than whole sentences, because the numbers in them are the
    /// account's factor count and the caller's seal count, and a test that spelled those out would be
    /// asserting the arrangement rather than which check answered.
    /// </remarks>
    private const string DistinctRefusalPhrase = "distinct factors";

    /// <inheritdoc cref="DistinctRefusalPhrase" />
    private const string SetRefusalPhrase = "a different set";

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
    /// <para>
    /// <b>The seals are replaced with the row, and the abandoned run's value is what makes that
    /// readable.</b> A handler that staged the new parent and left the previous run's seal standing
    /// would pass every assertion about the row, and the account would hold a generation whose factor
    /// copies belong to a run nobody is completing. The seeded seal carries bytes no begin in this
    /// fixture sends, so "the staged value is this begin's" is a claim about the write rather than about
    /// two buffers that happen to match.
    /// </para>
    /// <para>
    /// <b>What still passes this:</b> an implementation that clears every seal and re-inserts them all.
    /// The fake replaces the list wholesale, so a delete-then-insert of one key — the pair EF batches in
    /// no guaranteed order — is invisible from here. See the class remarks.
    /// </para>
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

        // And that run's own seal, one per factor, carrying a filler the replacing begin never sends.
        IReadOnlyDictionary<Guid, WrappedAccountKeys> factors =
            await fixture.KeyRotations.ListFactorsAsync(fixture.UserId);
        fixture.KeyRotations.SeedStagedSeals(
            fixture.UserId,
            [
                .. factors.Values.Select(factor => KeyRotationSeal.For(
                    abandoned,
                    factor,
                    SealPayload(AbandonedSealFiller))),
            ]);

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

        // And one seal per factor, carrying this begin's bytes rather than the abandoned run's.
        IReadOnlyList<KeyRotationSeal> seals = fixture.KeyRotations.SealsOf(fixture.UserId);
        await Assert.That(seals.Count).IsEqualTo(fixture.FactorIds.Count);
        await Assert.That(seals
                .Select(seal => seal.EncapsulatedAccountKeys.ToArray()[1])
                .Where(filler => filler == AbandonedSealFiller)
                .ToArray())
            .IsEmpty();
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
    /// A begin whose seals name exactly the live factor set — <b>the passkey and the ten factors of the
    /// recovery code card</b> — stages one seal per factor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the case that would have passed under the old passkey-only listing while orphaning ten
    /// factors</b>, and it is the reason the listing widened. A begin sealing the passkey alone used to
    /// be judged against a set of one, pass, promote, overwrite
    /// <see cref="WrappedAccountKeys.EncapsulatedAccountKeys" /> for that factor — and leave the card in
    /// somebody's wallet holding a copy of a content key that opens nothing. Still enrolled, still
    /// offered as a way back in, reported by nothing.
    /// </para>
    /// <para>
    /// The set is compared in both directions rather than by count, because eleven seals naming eleven
    /// factors of which one is a stranger's is also eleven. The owner of each staged seal is asserted
    /// too: <see cref="KeyRotationSeal.For" /> reads it off the rotation rather than off the factor, so
    /// a seal filed under anybody else would be an entity the factory should have refused to build.
    /// </para>
    /// <para>
    /// <b>What still passes this:</b> a seal carrying the right factor and the wrong bytes. The value is
    /// ciphertext under a public key, so nothing on this side can tell the account's new keys from any
    /// other 158 bytes of the right framing — which is why the assertion is over factor identifiers and
    /// widths rather than over what the payload means.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithASealForEveryFactor_StagesOneForTheRecoveryCodeSetToo()
    {
        // Arrange — one passkey factor and the ten factors of a card, which is what an account holds
        // from the moment registration files eleven rows in one save.
        Fixture fixture = Fixture.Build(seedRecoveryCodeSet: true);

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — the arrangement first, because every line below it is vacuous if the account turned
        // out to hold one factor after all.
        await Assert.That(fixture.FactorIds.Count).IsEqualTo(1 + Fixture.RecoveryCodeSetSize);

        IReadOnlyList<KeyRotationSeal> seals = fixture.KeyRotations.SealsOf(fixture.UserId);
        await Assert.That(seals.Count).IsEqualTo(fixture.FactorIds.Count);
        await Assert.That(seals.Select(seal => seal.FactorId).ToHashSet().SetEquals(fixture.FactorIds))
            .IsTrue();

        // The collection rather than a joined string, so every offender is named and not just the first.
        await Assert.That(seals.Where(seal => seal.UserId != fixture.UserId).ToArray()).IsEmpty();
    }

    /// <summary>
    /// A begin that staged no seal at all is refused, keyed on <c>Seals</c>, and stages nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The refusal that does not depend on the comparison below it being right.</b> Two empty sets
    /// satisfy set equality vacuously, so a listing that came back empty — which in production is what a
    /// query that lost its owner predicate answers, since <c>user_isolation</c> makes such a query empty
    /// rather than wrong — would <em>agree</em> with a client that submitted nothing, and the run would
    /// stage a generation no factor in the world can open. Neither side of that agreement is a
    /// legitimate state: registration files eleven factors in one save and every path that moves a
    /// factor set replaces rather than empties it.
    /// </para>
    /// <para>
    /// <b>What still passes this:</b> a handler that refuses an empty list by the set comparison alone,
    /// because this account really does hold factors. That is why the fixture seeds a whole card — the
    /// case is written against a <em>populated</em> account, where the two refusals are genuinely
    /// different statements, rather than against the degenerate account where they coincide.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithNoSeals_RefusesAndStagesNothing()
    {
        // Arrange
        Fixture fixture = Fixture.Build(seedRecoveryCodeSet: true);
        BeginKeyRotationCommand command = fixture.Command with { Seals = [] };

        // Act
        ValidationException refusal =
            await ThrowsAsync<ValidationException>(() => fixture.Handler.HandleAsync(command));

        // Assert — keyed on the member the caller can correct, and on nothing else.
        await Assert.That(refusal.Errors.ContainsKey(nameof(BeginKeyRotationCommand.Seals))).IsTrue();
        await Assert.That(refusal.Errors.Count).IsEqualTo(1);
        await Assert.That(fixture.KeyRotations.StageCallCount).IsEqualTo(0);
        await Assert.That(fixture.KeyRotations.Staged.Count).IsEqualTo(0);
    }

    /// <summary>
    /// No seals against a listing that answered <b>nothing</b> is still refused — the case the empty
    /// check exists for and the only one the set comparison cannot cover.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the discriminator the case above does not have.</b> With eleven live factors an empty
    /// seal list is refused by set equality whether or not the empty check exists, so that case cannot
    /// tell the two guards apart. Here both sides are empty and <b>two empty sets compare equal</b>: a
    /// handler holding set equality alone agrees with the client, stages a generation, and the account
    /// ends up with a run whose next content key no factor in the world can open — no error, no
    /// SQLSTATE, a <c>200</c>.
    /// </para>
    /// <para>
    /// <b>An account with no factors is not a state any path produces, and that is the point rather than
    /// an objection.</b> In production an empty listing is what a query that lost its owner predicate
    /// answers, because <c>user_isolation</c> makes such a query empty rather than wrong — so this
    /// arrangement is a model of a real bug one layer down, and the guard under test is what keeps that
    /// bug from being upgraded into a destroyed account.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithNoSealsAgainstAnEmptyFactorListing_StillRefuses()
    {
        // Arrange — the shape a lost owner predicate produces: the listing answers nothing, and the
        // client submitted nothing.
        Fixture fixture = Fixture.Build(passkeyFactors: 0);
        BeginKeyRotationCommand command = fixture.Command with { Seals = [] };

        // The premise, stated because every line below is about the two sides agreeing.
        await Assert.That(fixture.FactorIds).IsEmpty();

        // Act
        ValidationException refusal =
            await ThrowsAsync<ValidationException>(() => fixture.Handler.HandleAsync(command));

        // Assert
        await Assert.That(refusal.Errors.ContainsKey(nameof(BeginKeyRotationCommand.Seals))).IsTrue();
        await Assert.That(fixture.KeyRotations.StageCallCount).IsEqualTo(0);
        await Assert.That(fixture.KeyRotations.Staged.Count).IsEqualTo(0);
    }

    /// <summary>
    /// A begin whose seals miss one of the account's factors is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The direction "is each named factor one of them" misses, and the one whose failure has no
    /// repair.</b> Every seal here names a real, live factor of this account and is perfectly
    /// well-formed; what is wrong is what is <em>absent</em>. Waved through, the run completes, the
    /// promotion rewrites the factors that were sealed, and the one that was not is left holding a copy
    /// of a superseded content key — an authenticator still in the drawer, still enrolled, that can no
    /// longer unlock the account.
    /// </para>
    /// <para>
    /// The dropped factor is one of the card's ten rather than the passkey, because that is the
    /// omission the old passkey-only listing made silently and the one a future client is most likely
    /// to make: a set of codes produces no assertion, so it is the factor a reader forgets is a factor
    /// at all.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheSealsMissAFactor_RefusesAndStagesNothing()
    {
        // Arrange — eleven live factors, ten seals, and the missing one is a recovery code factor.
        Fixture fixture = Fixture.Build(seedRecoveryCodeSet: true);
        Guid dropped = fixture.KeyRotations.RecoveryCodeFactorsOf(fixture.UserId)[0];
        BeginKeyRotationCommand command = fixture.Command with
        {
            Seals = [.. fixture.Command.Seals.Where(seal => seal.FactorId != dropped)],
        };

        // The premise: one seal really did go, so the refusal below is about a short set rather than
        // about a filter that matched nothing.
        await Assert.That(command.Seals.Count).IsEqualTo(fixture.FactorIds.Count - 1);

        // Act
        ValidationException refusal =
            await ThrowsAsync<ValidationException>(() => fixture.Handler.HandleAsync(command));

        // Assert
        await Assert.That(refusal.Errors.ContainsKey(nameof(BeginKeyRotationCommand.Seals))).IsTrue();
        await Assert.That(fixture.KeyRotations.StageCallCount).IsEqualTo(0);
        await Assert.That(fixture.KeyRotations.Staged.Count).IsEqualTo(0);
    }

    /// <summary>
    /// A begin naming a factor the account does not hold is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The other direction, and it fails differently — which is why it is written beside its mirror
    /// rather than folded into it.</b> A value staged against a row a promotion will not find is, left
    /// to the database, the composite foreign key to <c>wrapped_account_keys</c> raising <c>23503</c>
    /// part-way through a save: a <c>500</c> for a caller whose request was merely wrong, on a path
    /// where some factors have already been staged.
    /// </para>
    /// <para>
    /// <b>The count is deliberately unchanged</b> — one stranger replaces one live factor, so the
    /// submitted set is the same size as the account's. A guard written as a count, or as "at least one
    /// seal per factor", passes this.
    /// </para>
    /// <para>
    /// <b>What still passes this:</b> a handler with no gate at all whose staging happens to raise on
    /// the unknown key, because <see cref="KeyRotationSeal.For" /> would never be reached. The refusal
    /// is therefore asserted as a <see cref="ValidationException" /> keyed on <c>Seals</c> — the ring
    /// that can say which <em>set</em> was wrong — and not merely as "something threw".
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithASealForAFactorTheAccountDoesNotHold_RefusesAndStagesNothing()
    {
        // Arrange — eleven seals for eleven factors, one of which this account has never held.
        Fixture fixture = Fixture.Build(seedRecoveryCodeSet: true);
        Guid stranger = Guid.CreateVersion7();
        BeginKeyRotationCommand command = fixture.Command with
        {
            Seals =
            [
                .. fixture.Command.Seals.Skip(1),
                Seal(stranger, StrangerSealFiller),
            ],
        };

        // The premise: the same number of seals as factors, so nothing below can pass on a count.
        await Assert.That(command.Seals.Count).IsEqualTo(fixture.FactorIds.Count);
        await Assert.That(fixture.FactorIds).DoesNotContain(stranger);

        // Act
        ValidationException refusal =
            await ThrowsAsync<ValidationException>(() => fixture.Handler.HandleAsync(command));

        // Assert
        await Assert.That(refusal.Errors.ContainsKey(nameof(BeginKeyRotationCommand.Seals))).IsTrue();
        await Assert.That(fixture.KeyRotations.StageCallCount).IsEqualTo(0);
        await Assert.That(fixture.KeyRotations.Staged.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Twelve seals naming eleven factors are refused <b>as a duplicate</b>, and the set comparison is
    /// not what answers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the discriminator that matters, and asserting "refused" alone would not be one.</b>
    /// The submitted factor <em>set</em> here is exactly the account's live set — a duplicate collapses
    /// inside a <c>HashSet</c> — so set equality passes, and the only thing standing between this
    /// request and a staged generation is the distinct count. A case that asserted nothing but
    /// <see cref="ValidationException" /> would be satisfied by an implementation that had never grown
    /// the distinct check: there would simply be no refusal at all, and the account would end up one
    /// seal short of what the client believed it sent, with nothing anywhere saying which factor was
    /// repeated.
    /// </para>
    /// <para>
    /// <b>The message is the only discriminator available</b>, because both refusals are a
    /// <see cref="ValidationException" /> keyed on the same member — which is correct, since both are
    /// the same field being wrong, and is exactly what makes them indistinguishable by shape. So the
    /// assertion reads the sentence: it names the distinct count, and it must <em>not</em> be the one
    /// about naming a different set.
    /// </para>
    /// <para>
    /// The ordering of the two checks is pinned by
    /// <see cref="HandleAsync_WhenADuplicateAlsoBreaksSetEquality_RefusesForTheDuplicate" /> next door,
    /// which is the input where both would fire.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithADuplicateFactorAmongTheSeals_RefusesForTheDuplicate()
    {
        // Arrange — every live factor sealed once, and one of them sealed a second time.
        Fixture fixture = Fixture.Build(seedRecoveryCodeSet: true);
        BeginKeyRotationCommand command = fixture.Command with
        {
            Seals = [.. fixture.Command.Seals, Seal(fixture.FactorIds[0], DuplicateSealFiller)],
        };

        // The premise this case turns on: the SET is right and only the count is wrong, so a handler
        // holding set equality alone has nothing to refuse.
        await Assert.That(command.Seals.Count).IsEqualTo(fixture.FactorIds.Count + 1);
        await Assert.That(command.Seals.Select(seal => seal.FactorId).ToHashSet()
                .SetEquals(fixture.FactorIds))
            .IsTrue();

        // Act
        ValidationException refusal =
            await ThrowsAsync<ValidationException>(() => fixture.Handler.HandleAsync(command));

        // Assert — the distinct refusal by its sentence, and explicitly not the set one.
        await Assert.That(refusal.Errors.ContainsKey(nameof(BeginKeyRotationCommand.Seals))).IsTrue();
        await Assert.That(refusal.Errors[nameof(BeginKeyRotationCommand.Seals)][0])
            .Contains(DistinctRefusalPhrase);
        await Assert.That(refusal.Errors[nameof(BeginKeyRotationCommand.Seals)][0])
            .DoesNotContain(SetRefusalPhrase);
        await Assert.That(fixture.KeyRotations.StageCallCount).IsEqualTo(0);
        await Assert.That(fixture.KeyRotations.Staged.Count).IsEqualTo(0);
    }

    /// <summary>
    /// When a duplicate <b>also</b> breaks set equality, the duplicate is what answers — which is the
    /// only input from which the order of the two checks is observable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the case next door cannot pin the order.</b> There the set comparison passes, so running
    /// it first changes nothing: either arrangement lands on the distinct check. Here both are false —
    /// eleven seals naming ten of the account's eleven factors — so whichever check runs first is the
    /// one whose sentence reaches the client, and reversing them flips this assertion.
    /// </para>
    /// <para>
    /// <b>And the order is not cosmetic.</b> The distinct message names how many seals were presented
    /// against how many factors they name, which is the one fact that tells a client its own
    /// randomness repeated itself. The set message says a different set was named — true, and it sends
    /// a client looking for a factor it forgot rather than at the two identical identifiers it sent.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenADuplicateAlsoBreaksSetEquality_RefusesForTheDuplicate()
    {
        // Arrange — one factor's seal dropped and one other factor's seal repeated, so the count
        // matches the account's while the set is short by one.
        Fixture fixture = Fixture.Build(seedRecoveryCodeSet: true);
        BeginKeyRotationCommand command = fixture.Command with
        {
            Seals = [.. fixture.Command.Seals.Skip(1), Seal(fixture.FactorIds[1], DuplicateSealFiller)],
        };

        // Both premises: the count is the account's, and the set is not.
        await Assert.That(command.Seals.Count).IsEqualTo(fixture.FactorIds.Count);
        await Assert.That(command.Seals.Select(seal => seal.FactorId).ToHashSet()
                .SetEquals(fixture.FactorIds))
            .IsFalse();

        // Act
        ValidationException refusal =
            await ThrowsAsync<ValidationException>(() => fixture.Handler.HandleAsync(command));

        // Assert — the distinct check ran first.
        await Assert.That(refusal.Errors[nameof(BeginKeyRotationCommand.Seals)][0])
            .Contains(DistinctRefusalPhrase);
        await Assert.That(refusal.Errors[nameof(BeginKeyRotationCommand.Seals)][0])
            .DoesNotContain(SetRefusalPhrase);
        await Assert.That(fixture.KeyRotations.StageCallCount).IsEqualTo(0);
    }

    /// <summary>
    /// The factor-set gate runs <b>after</b> the re-authentication gate and <b>before</b> the narrative
    /// row counts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two rules in one observation, and each half has its own reason. <b>After the assertion</b>, for
    /// the disclosure reason <c>GenerateRecoveryCodesHandler</c> orders its own gate by: every refusal
    /// this gate can raise carries a count of the account's factors into a message the Development
    /// branch of <c>GlobalExceptionHandler</c> echoes into the body, so a caller holding nothing but a
    /// stolen bearer token would be told how many recovery factors the account has. <b>Before the
    /// counts</b>, because a begin that will not be staged should not cost the database six counts it
    /// throws away.
    /// </para>
    /// <para>
    /// <b>One predicate at one instant rather than two counters compared afterwards</b>, the device
    /// <see cref="InMemoryKeyRotationRepository.ObserveAtStage" /> and
    /// <c>InMemoryPasskeyRepository.ObserveAtDelete</c> both are and for the reason their remarks give:
    /// two counters that both moved prove both things happened, never that one preceded the other. The
    /// gate calls nothing itself, so the observation hangs off the listing it reads one statement
    /// earlier — the only collaborator the gate touches, and one nothing else in this handler calls.
    /// </para>
    /// <para>
    /// A null observation means the factors were never listed at all, which is a different failure from
    /// a gate in the wrong place, so both are named.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_RunsTheFactorSetGateAfterTheAssertionAndBeforeTheCounts()
    {
        // Arrange
        Fixture fixture = Fixture.Build(seedRecoveryCodeSet: true);
        fixture.KeyRotations.ObserveAtListFactors =
            () => fixture.Challenges.ConsumeCallCount == 1 && fixture.Inventory.CountCallCount == 0;

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert
        await Assert.That(fixture.KeyRotations.ObservationAtListFactors).IsNotNull();
        await Assert.That(fixture.KeyRotations.ObservationAtListFactors).IsTrue();

        // And the counts really were taken afterwards, so the observation above is not "before" in the
        // sense of "never".
        await Assert.That(fixture.Inventory.CountCallCount).IsEqualTo(1);
    }

    /// <summary>
    /// A seal of the wrong width, or carrying the wrong framing version, is refused by
    /// <see cref="KeyRotationSeal.For" /> before anything is staged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The refusal is the Domain factory's and not the gate's, and the two assertions that say so are
    /// both load-bearing.</b> The key is <c>EncapsulatedAccountKeys</c> rather than <c>Seals</c> — the
    /// factory keys its refusal on the property the value lands in — and the narrative counts have
    /// already been taken, because the factor-set gate passed: every one of these seals names a live
    /// factor and the set is exactly right. Only the bytes are wrong.
    /// </para>
    /// <para>
    /// <b>Why it must happen before the write rather than during it.</b> The seals are built outside the
    /// transactional delegate, which is what puts every refusal this factory can raise ahead of the
    /// staging row and ahead of every other seal. Built inside the loop that writes them, a malformed
    /// eleventh seal would abandon a save that had already accepted ten.
    /// </para>
    /// <para>
    /// <b>Both bounds of the width, and a version one above the only one defined.</b> The width is a
    /// width and not a cap — AES-GCM ciphertext is exactly as long as its plaintext — so a byte short
    /// and a byte long are two refusals rather than one. The numbers are <see cref="SealBytes" /> and
    /// <see cref="SealVersion" />, written out here rather than read off
    /// <see cref="WrappedAccountKeys" />: see those constants for why a test reading the code's own
    /// constants would feed the code whatever it currently believes.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(SealBytes - 1, SealVersion)]
    [Arguments(SealBytes + 1, SealVersion)]
    [Arguments(SealBytes, SealVersion + 1)]
    public async Task HandleAsync_WithAMalformedSeal_RefusesBeforeAnythingIsStaged(
        int length,
        int version)
    {
        // Arrange — the whole live set sealed, and one of those seals malformed.
        Fixture fixture = Fixture.Build(seedRecoveryCodeSet: true);
        BeginKeyRotationCommand command = fixture.Command with
        {
            Seals =
            [
                new RotationSeal(fixture.FactorIds[0], Payload(length, version, MalformedSealFiller)),
                .. fixture.Command.Seals.Skip(1),
            ],
        };

        // Act
        ValidationException refusal =
            await ThrowsAsync<ValidationException>(() => fixture.Handler.HandleAsync(command));

        // Assert — the factory's key, not the gate's.
        await Assert.That(
                refusal.Errors.ContainsKey(nameof(KeyRotationSeal.EncapsulatedAccountKeys)))
            .IsTrue();
        await Assert.That(refusal.Errors.ContainsKey(nameof(BeginKeyRotationCommand.Seals))).IsFalse();

        // The gate passed — the set was right — which is what says this refusal came from the ring
        // below it rather than from the comparison above.
        await Assert.That(fixture.Inventory.CountCallCount).IsEqualTo(1);
        await Assert.That(fixture.KeyRotations.StageCallCount).IsEqualTo(0);
        await Assert.That(fixture.KeyRotations.Staged.Count).IsEqualTo(0);
    }

    /// <summary>
    /// <b>NFR-027.</b> An account holding one passkey and ten recovery-code factors begins a rotation
    /// having exercised exactly <b>one</b> authenticator, staging eleven seals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the whole point of the keypair reshape, as a test.</b> Under the arrangement it
    /// replaced, <c>wrapped_account_keys</c> held the account's two keys <em>wrapped under</em> the
    /// key-encryption key each factor derives — so re-wrapping them for eleven factors needed eleven
    /// key-encryption keys, which means the PRF output of every registered authenticator and every one
    /// of the ten codes, all present at once. An account with a hardware key in a drawer could not
    /// rotate at all, and nobody can produce ten recovery codes they have not yet used. Encapsulating to
    /// a factor's <em>public</em> half needs no secret, so a run needs the old content key and a set of
    /// public keys: one authenticator, eleven values.
    /// </para>
    /// <para>
    /// <b>The measurement is the nonce consume, and it is one per verified assertion.</b> The
    /// re-authentication gate spends exactly one challenge per assertion it verifies, and the fixture's
    /// store models single use — so "verifications" and "consumes" are the same number, and the same
    /// counter <see cref="HandleAsync_WhenTheUnitOfWorkIsReplayed_StillBeginsTheRotation" /> reads for
    /// the other half of that store's behaviour. There is also only one device in this fixture holding
    /// one key pair, so eleven seals were staged while one authenticator signed.
    /// </para>
    /// <para>
    /// <b>The seal count is asserted beside it and is not decoration.</b> One consume is trivially true
    /// of a handler that refused everything, or of one that sealed the passkey alone — the two together
    /// are what say the account's whole factor set moved forward on one touch.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithElevenFactors_StagesThemAllOnOneReauthentication()
    {
        // Arrange — one passkey, one card of ten codes.
        Fixture fixture = Fixture.Build(seedRecoveryCodeSet: true);

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — eleven factors sealed, one authenticator exercised.
        await Assert.That(fixture.KeyRotations.SealsOf(fixture.UserId).Count)
            .IsEqualTo(1 + Fixture.RecoveryCodeSetSize);
        await Assert.That(fixture.Challenges.ConsumeCallCount).IsEqualTo(1);
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
    /// material, which is why the factor-set gate is held over the <b>seals</b> and not over these
    /// bytes — see the class remarks, where what that leaves unheld is written out.
    /// </remarks>
    private static byte[] Manifest(byte seed) =>
        [.. Enumerable.Range(0, StagedManifestBytes).Select(offset => (byte)(seed + offset))];

    /// <summary>
    /// One factor's copy of the next generation's account keys, as a client presents it.
    /// </summary>
    /// <remarks>
    /// <paramref name="filler" /> is what tells one seal from another — see
    /// <see cref="SealFillerBase" />. Nothing on this side can read inside the value and nothing here
    /// pretends to: what the payload has to be is the right width carrying the right framing version,
    /// which is the whole of what <see cref="KeyRotationSeal.For" /> judges.
    /// </remarks>
    private static RotationSeal Seal(Guid factorId, byte filler) =>
        new(factorId, SealPayload(filler));

    /// <summary>A well-formed encapsulated pair of account keys, filled with <paramref name="filler" />.</summary>
    private static byte[] SealPayload(byte filler) => Payload(SealBytes, SealVersion, filler);

    /// <summary>
    /// A payload of <paramref name="length" /> bytes leading with <paramref name="version" />, so a case
    /// can build a value at either side of the width and one version above the only one defined.
    /// </summary>
    /// <remarks>
    /// A zero length is the one input with no leading byte to write, and it is left reachable
    /// deliberately: it is what an unset field on the wire arrives as, and the Domain's describers check
    /// width before version for exactly that reason.
    /// </remarks>
    private static byte[] Payload(int length, int version, byte filler)
    {
        byte[] payload = new byte[length];
        Array.Fill(payload, filler);

        if (length > 0)
        {
            payload[0] = (byte)version;
        }

        return payload;
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

        // Every factor the account holds, passkeys first. It replaces the single PasskeyFactorId this
        // record used to carry, which went unread from the day the command stopped naming one factor —
        // a run stages a value for the whole set, so "the factor" has no answer to hold on to.
        IReadOnlyList<Guid> FactorIds,
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
        /// How many live passkey factors the account holds. One is today's world; two is an account that
        /// registered a second authenticator, and is what tells set equality apart from "is one of".
        /// <b>Zero is not a state any path in the product produces</b> — registration files eleven
        /// factors in one save, and every path that moves a factor set replaces rather than empties it —
        /// and it is reachable here on purpose, as the shape a listing that lost its owner predicate
        /// answers with. One case below is written against it.
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
        /// Whether the account also holds a set of recovery codes — ten factors under one credential,
        /// every one of them a real row and <b>every one of them a factor a run must stage a seal
        /// for</b>. It used to be the opposite: those rows were seeded precisely because the port would
        /// not answer with them. A begin that skips them now is the orphaning the gate exists to refuse,
        /// so eleven is the arrangement most cases below want and one is the degenerate account.
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

            // A set of recovery codes is TEN factors under ONE credential, and every one of them is a
            // row a begin now has to stage a seal for — see InMemoryKeyRotationRepository, where the
            // reversal of the old passkey-only rule is written out.
            List<Guid> recoveryCodeFactorIds = [];
            if (seedRecoveryCodeSet)
            {
                Credential codes = Credential.CreateRecoveryCodes(userId, UtcNow);
                foreach (int _ in Enumerable.Range(0, RecoveryCodeSetSize))
                {
                    Guid factorId = Guid.CreateVersion7();
                    keyRotations.SeedRecoveryCodeFactor(codes, factorId);
                    recoveryCodeFactorIds.Add(factorId);
                }
            }

            // THE COMMAND'S SEALS ARE THE ACCOUNT'S WHOLE LIVE FACTOR SET, which is what a correct
            // client sends and what every case that is not about the gate needs in order to get past
            // it. A case that wants a wrong set derives one from this with a `with` expression, so the
            // mutation it made is visible in that case rather than buried in a flag here.
            //
            // The passkey factors lead, so FactorIds[0] is a passkey and the first recovery code factor
            // is FactorIds[1] when a card is seeded — an order the cases above read.
            Guid[] factorIds = [.. passkeyFactorIds, .. recoveryCodeFactorIds];
            RotationSeal[] seals =
            [
                .. factorIds.Select(
                    (factorId, index) => Seal(factorId, (byte)(SealFillerBase + index))),
            ];

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
                    StagedRotationEpoch,
                    seals),
                keyRotations,
                inventory,
                challenges,
                executor,
                unitOfWork,
                passkey,
                factorIds,
                userId,
                budgetId);
        }

        /// <summary>How many factors a set of recovery codes is.</summary>
        public const int RecoveryCodeSetSize = 10;
    }
}
