using Application.Abstractions;
using Application.KeyRotations.ResealRows;
using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Domain.Security;
using Domain.Transactions;
using Domain.Users;
using TestSupport;
using TUnit.Assertions.Enums;
using UnitTests.Fakes;
using NotFoundException = Domain.Common.NotFoundException;
using ValidationException = Domain.Common.ValidationException;

namespace UnitTests;

/// <summary>
/// The chunk that re-seals rows: which run it is allowed to stamp them with, what it may do to a
/// column's presence, which rows it leaves alone, and where the write lands relative to the unit of
/// work.
/// </summary>
/// <remarks>
/// <para>
/// <b>THERE ARE FIVE ARMS AND THERE IS DELIBERATELY NO BUDGET ARM. THE REASON IS AN ACCEPTANCE
/// CRITERION AND NOT AN ACCESS MODIFIER.</b> A budget arm would need <c>rotation_id</c> in the
/// <c>budgets</c> <c>GRANT UPDATE</c> column list, and this story's own criterion is that the
/// application role holds <c>UPDATE</c> on <c>budgets.name</c> <em>and no other column</em> — an
/// omission from a column list being the only way this schema makes a column immutable. So the sixth
/// arm is not missing; it is refused, and adding it would have to be argued against that criterion
/// first. <b><c>Budget.ResealName</c> being <see langword="internal" /> is the mechanism that makes
/// the refusal a compile error</b> rather than a review note — <c>Domain.csproj</c> grants its
/// internals to <c>Infrastructure</c> alone, so nothing in the Application ring can call it whatever
/// the grant said. Nothing is lost either way: every budget row in every database is nameless today
/// and the completeness gate is presence-aware, so no budget is ever outstanding. A reader who files
/// the missing sixth arm as an oversight will "complete" the handler into a grant widening nobody
/// wanted — <c>docs/business-logic/key-rotation.md</c> argues all of it.
/// </para>
/// <para>
/// <b>The stamp and the ciphertext move together or not at all, and no case in this file can see
/// that.</b> Each reseal member assigns both in one call, so an entity cannot be half-rewritten — but
/// whether the two reach PostgreSQL inside one transaction needs a database, and
/// <c>ResealChunkTests</c> in the integration tier is where that is measured. What this file holds is
/// which side of the transactional delegate the save arrived on, which is a fact about the control
/// flow this handler produced and nothing more.
/// </para>
/// <para>
/// <b>A foreign row is invisible rather than forbidden, and the interesting half of that is not here
/// either.</b> The five sets carry the <c>BudgetIsolation</c> query filter, so a row of another budget
/// is missing from the answer rather than refused — which is a property of the real adapter's query
/// and is measured against it. <see cref="InMemoryNarrativeResealRepository" /> models only the
/// consequence: an id the port does not answer for.
/// </para>
/// <para>
/// <b>OVER-REACH IS OBSERVABLE HERE ONLY BECAUSE THE PORT CAN BE MADE TO OVER-ANSWER, AND THAT SEAM
/// IS DELIBERATE RATHER THAN CONVENIENT.</b> A fake answering exactly the ids it was handed makes
/// "iterate the command" and "iterate the dictionary the port returned" the same program, so a
/// handler written the second way — the shorter line, and one that reads perfectly well — is
/// indistinguishable from one written the first way, and a case claiming to hold that rule would be a
/// decoration however it was worded. <see cref="InMemoryNarrativeResealRepository.OverAnswers" />
/// lifts that: a list call answers every row of its kind, which is what a query that lost its
/// identifier predicate really does. Exactly one case switches it on, and the property it buys
/// belongs to the <em>handler</em> — a chunk rewrites the rows the command names and no others,
/// whatever the port hands back. That the real adapter scopes its own query is a different claim and
/// is <c>ResealChunkTests.ResealChunk_LoadsOnlyTheRowsTheChunkNames</c>'s.
/// </para>
/// <para>
/// <b>Most of that rule is already held one ring down, and knowing which part is worth more than
/// either test.</b> Each reseal member takes the row's <em>new value</em> — an <c>IndexedName</c>, a
/// <c>NarrativeField?</c> — and there is no overload that writes a stamp alone, so a row the chunk
/// supplied nothing for cannot be stamped: there is nothing to write into it. A handler that
/// over-reaches therefore has to <em>invent</em> a value for a row it was given none for, which is
/// precisely what iterating the loaded set forces somebody to write. That guarantee belongs to
/// <c>Payee.Reseal</c> and its four siblings, not to any test here, and it is what keeps the residual
/// small enough for one case to cover.
/// </para>
/// <para>
/// <b>Every refusal is pinned as a refusal <em>and</em> an unwritten account.</b> "It threw" is not
/// "it rewrote nothing": a handler that mutated the loaded entities and then refused has left a
/// tracked graph carrying new ciphertext under a stamp nobody authorised, and on a request-scoped
/// context the next save anybody makes flushes it. So each case reads the save count beside the
/// exception, and reads a row's own columns beside that.
/// </para>
/// <para>
/// <b>The port these tests drive</b>, named once so a reader is not reverse-engineering it from
/// constructor arguments: <c>Domain.Security.INarrativeResealRepository</c> — one
/// <c>List…Async(ids)</c> per arm answering a dictionary keyed on the row identifier, and one
/// <c>SaveAsync</c>. It answers a dictionary rather than a list because the handler's question is a
/// lookup and because an absent key is how the query filter reports a row it cannot see, which is the
/// same argument <c>IKeyRotationRepository.ListFactorsAsync</c> makes about factors.
/// </para>
/// </remarks>
public sealed class ResealRowsHandlerTests
{
    /// <summary>
    /// The generation the staged manifest names, and how many bytes of it the fixture builds.
    /// </summary>
    /// <remarks>
    /// Literals rather than <c>FactorManifest</c>'s constants, the idiom
    /// <c>BeginKeyRotationHandlerTests</c> keeps and argues: every type on this path reads those
    /// constants, so a test that read them too would feed the code under test whatever that code
    /// currently believes. Nothing in this file is about a manifest's bounds.
    /// </remarks>
    private const int StagedRotationEpoch = 4;

    /// <inheritdoc cref="StagedRotationEpoch" />
    private const int StagedManifestBytes = 24;

    /// <summary>
    /// The two sentences <see cref="NarrativeReseal" /> can refuse with, quoted by the fragment that
    /// tells them apart.
    /// </summary>
    /// <remarks>
    /// Both arms are a <see cref="ValidationException" /> keyed on the same column — correct, since it is
    /// the same field being wrong — which is exactly what leaves the sentence as the only discriminator.
    /// <see cref="NarrativeReseal" />'s own remarks say so and say why no closed vocabulary sits beside
    /// it yet. Fragments rather than whole sentences, so the assertion survives a wording pass that keeps
    /// the two refusals distinguishable.
    /// </remarks>
    private const string ClearingRefusalPhrase = "cannot clear a field";

    /// <inheritdoc cref="ClearingRefusalPhrase" />
    private const string AddingRefusalPhrase = "cannot add a value";

    /// <summary>
    /// The chunk names a rotation the account does not have staged, and nothing is rewritten.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The identifier is the client's to mint and nothing about it makes it this account's run.</b>
    /// A chunk carries a <c>rotationId</c> the caller chose; <c>key_rotations</c> is keyed on the
    /// account and holds at most one row, so "is this the run in flight?" is a question with an answer
    /// and the handler has to ask it. Left unasked, a caller stamps rows with an abandoned run's
    /// identifier — or with one no <c>key_rotations</c> row has ever matched — and the completeness gate
    /// at the far end then answers <b>complete</b> for a generation nobody staged, after which the
    /// promotion overwrites the only wrapped copies of the key those rows are still sealed under.
    /// </para>
    /// <para>
    /// <b>The account really does have a rotation staged, and the chunk names a different one.</b>
    /// Arranged against an account with no staged row at all, this case would also pass against a
    /// handler that refused whenever the lookup came back null — which is a weaker rule that lets an
    /// abandoned run's identifier straight through. <b>It holds one arm of the refusal and only one</b>;
    /// <see cref="ResealChunk_WhenNoRotationIsStaged_RefusesAndRewritesNothing" /> holds the other, and
    /// neither is redundant beside the other.
    /// </para>
    /// <para>
    /// <b>What it cannot see</b> is the empty identifier, which each reseal member refuses one ring
    /// further in through its own <c>RequireRotation</c>. That is the Domain's and is covered by the
    /// entity suites.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunk_ForARotationThatIsNotTheStagedOne_RefusesAndRewritesNothing()
    {
        // Arrange — one run staged, and a chunk quoting another.
        Fixture fixture = Fixture.Build();
        Guid abandonedRotationId = Guid.CreateVersion7();
        ResealRowsCommand command = fixture.Chunk with { RotationId = abandonedRotationId };

        // The premise: the two identifiers really differ, and the account really holds one of them.
        await Assert.That(abandonedRotationId).IsNotEqualTo(fixture.RotationId);
        KeyRotation? staged = await fixture.KeyRotations.FindStagedRotationAsync(fixture.UserId);
        await Assert.That(staged!.RotationId).IsEqualTo(fixture.RotationId);

        // Act
        ValidationException refusal =
            await ThrowsAsync<ValidationException>(() => fixture.Handler.HandleAsync(command));

        // Assert — keyed on the member the caller can correct.
        await Assert.That(refusal.Errors.ContainsKey(nameof(ResealRowsCommand.RotationId))).IsTrue();

        // And nothing was rewritten — neither asked to be persisted, nor mutated in the graph.
        await Assert.That(fixture.Rows.SaveCallCount).IsEqualTo(0);
        await Assert.That(fixture.Payee.RotationId).IsNull();
        await Assert.That(fixture.Payee.NameKey.ToArray())
            .IsEquivalentTo(fixture.SeededPayeeName.BlindIndex.ToArray(), CollectionOrdering.Matching);
    }

    /// <summary>
    /// The account has <b>no</b> rotation staged, and a chunk arriving anyway is refused and rewrites
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THE OTHER ARM OF ONE REFUSAL, AND IT WAS MEASURABLY UNHELD.</b> The guard has two readings —
    /// "no run is in flight" and "a different run is" — and until this case existed no fixture in either
    /// tier arranged an account with nothing staged, so a handler spelling the guard as "refuse only
    /// when a staged row exists and disagrees" passed every case in both suites. That spelling is one
    /// operator shorter than the correct one, reads perfectly well, and is exactly the simplification a
    /// null-check-averse reader makes.
    /// </para>
    /// <para>
    /// <b>What it costs on a real account is the whole of why the guard exists.</b> The rotation
    /// identifier is minted by the client and nothing about it makes it this account's run, so a chunk
    /// admitted here stamps rows with a value no <c>key_rotations</c> row has ever matched. The rows are
    /// then re-sealed under keys nobody staged and marked as belonging to a generation nobody began —
    /// and the completeness gate at the far end, which counts stamps, answers <b>complete</b> for it.
    /// This is the damage
    /// <see cref="ResealChunk_ForARotationThatIsNotTheStagedOne_RefusesAndRewritesNothing" />'s own
    /// remarks name, arriving through the door that case cannot watch.
    /// </para>
    /// <para>
    /// <b>The chunk is the correct client's, unmodified.</b> Nothing about the request is wrong: it is
    /// well formed, every row it names exists and belongs to this budget, and every note is present
    /// where the row holds one. The only thing missing is the account's staged row — so a refusal here
    /// can be about that and nothing else, which is what keeps this case from re-measuring any of the
    /// four refusals beside it.
    /// </para>
    /// <para>
    /// <b>One refusal rather than two, and that is the handler's decision rather than this case's
    /// indifference.</b> "This account has no rotation in flight" is a fact about the account, and
    /// handing it to a caller whose request was already wrong tells them something they had no right to
    /// ask. The client's next act is identical either way — begin a run, then quote what the begin
    /// staged — so the two arms share a sentence, and this case therefore asserts the member the caller
    /// can correct rather than a wording that distinguishes them.
    /// </para>
    /// <para>
    /// <b>Refused AND unwritten, in the file's usual pair.</b> "It threw" is not "it rewrote nothing":
    /// the arms are read as a collected census so a failure names every one that moved, and the payee's
    /// ciphertext is read beside its stamp because a reseal writes both in one call.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunk_WhenNoRotationIsStaged_RefusesAndRewritesNothing()
    {
        // Arrange — an account with nothing staged, and the chunk a correct client would send.
        Fixture fixture = Fixture.Build(stageRotation: false);

        // The premise the whole case rests on, asserted against the port rather than trusted from the
        // argument: this account really has no staged row. A fixture that quietly went back to seeding
        // one would turn this into a second copy of its sibling while staying green.
        KeyRotation? staged = await fixture.KeyRotations.FindStagedRotationAsync(fixture.UserId);
        await Assert.That(staged).IsNull();

        // Act
        ValidationException refusal =
            await ThrowsAsync<ValidationException>(() => fixture.Handler.HandleAsync(fixture.Chunk));

        // Assert — keyed on the member the caller can correct, which is the same member its sibling
        // asserts because the two arms are deliberately one refusal.
        await Assert.That(refusal.Errors.ContainsKey(nameof(ResealRowsCommand.RotationId))).IsTrue();

        // And NOT ONE arm was rewritten. Collected and asserted as an empty list rather than arm by
        // arm, so a failure names every arm that was stamped and not merely the first.
        (string Arm, Guid? Stamp)[] arms =
        [
            ("accounts", fixture.Account.RotationId),
            ("payees", fixture.Payee.RotationId),
            ("category_groups", fixture.CategoryGroup.RotationId),
            ("categories", fixture.Category.RotationId),
            ("transactions", fixture.Transaction.RotationId),
        ];

        await Assert.That(arms.Where(arm => arm.Stamp is not null).Select(arm => arm.Arm).ToArray())
            .IsEmpty();

        // The stamp is the cheap half; the ciphertext is the half that is lost. Read on the arm whose
        // old value this fixture kept, in both of the columns one reseal writes together.
        await Assert.That(fixture.Payee.NameKey.ToArray())
            .IsEquivalentTo(fixture.SeededPayeeName.BlindIndex.ToArray(), CollectionOrdering.Matching);
        await Assert.That(fixture.Payee.Name.Envelope.ToArray())
            .IsEquivalentTo(
                fixture.SeededPayeeName.Name.Envelope.ToArray(),
                CollectionOrdering.Matching);

        // And nothing was asked to be persisted, which is the third failure — a handler that saved the
        // rewritten arms and then refused.
        await Assert.That(fixture.Rows.SaveCallCount).IsEqualTo(0);
    }

    /// <summary>
    /// The same chunk sent twice under the same rotation identifier succeeds both times.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>key-rotation.md</c> states this as a MUST NOT and it is the one that reads like a bug.</b>
    /// A chunk re-sent after a network timeout carries the same identifier it carried the first time,
    /// and a handler that refused a rotation identifier it had already seen would look like idempotence
    /// protection while breaking retries on exactly the long rotations that need chunking. The row it
    /// rewrites a second time is byte-identical to what it wrote the first — the client re-sends the
    /// same envelopes — so there is nothing to protect against.
    /// </para>
    /// <para>
    /// <b>The second send is the same command instance</b>, which is what a retry really posts. Building
    /// a second chunk with fresh envelopes would be a different request and would measure something else.
    /// </para>
    /// <para>
    /// <b>What still passes this:</b> a handler that never checked the staged rotation at all — the case
    /// above is the one that refuses that reading, and the two have to be read together.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunk_ResentWithTheSameRotationId_Succeeds()
    {
        // Arrange
        Fixture fixture = Fixture.Build();

        // Act — the first send, then the very same chunk again.
        await fixture.Handler.HandleAsync(fixture.Chunk);
        await fixture.Handler.HandleAsync(fixture.Chunk);

        // Assert — both were persisted, and the row carries the run it was stamped with.
        await Assert.That(fixture.Rows.SaveCallCount).IsEqualTo(2);
        await Assert.That(fixture.Payee.RotationId).IsEqualTo(fixture.RotationId);
        await Assert.That(fixture.Transaction.RotationId).IsEqualTo(fixture.RotationId);
    }

    /// <summary>
    /// A reseal may not put a value into a column that holds none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The presence rule, on the arm where a nullable column is the whole of its row's
    /// narrative.</b> <c>transactions.description</c> is the only one of the four nullable narrative
    /// columns that is, which makes a note-less transaction the honest shape for this case — and the one
    /// a real account holds thousands of.
    /// </para>
    /// <para>
    /// <b>This is the quieter of the two arms and the one a reviewer proposes relaxing</b>, because
    /// filling in an empty note harms no data. It is refused because presence is the only property this
    /// side can check at all: an arm admitting a change of presence gives up the rule entirely, and what
    /// lands in the column is text the server cannot read, attributed to somebody who never wrote it, in
    /// a run they authorised as "re-encrypt what I have". <see cref="NarrativeReseal" /> owns the rule
    /// and this case does not restate its argument.
    /// </para>
    /// <para>
    /// <b>The refusal is keyed on the column and the sentence is what says which arm answered</b>, since
    /// both arms key the same member and both are 400s.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunk_WhenAResealWouldAddAValueToANarrativeFieldThatHasNone_Refuses()
    {
        // Arrange — the account's note-less transaction, and a chunk supplying a note for it.
        Fixture fixture = Fixture.Build();
        ResealRowsCommand command = fixture.Chunk with
        {
            Transactions =
            [
                new ResealedTransaction(
                    fixture.NoteLessTransaction.Id,
                    SealedNarrative.Description("invented")),
            ],
        };

        // The premise: the row really holds no note.
        await Assert.That(fixture.NoteLessTransaction.Description).IsNull();

        // Act
        ValidationException refusal =
            await ThrowsAsync<ValidationException>(() => fixture.Handler.HandleAsync(command));

        // Assert — keyed on the column, and the adding arm by its sentence rather than the clearing one.
        await Assert.That(refusal.Errors.ContainsKey(nameof(Transaction.Description))).IsTrue();
        await Assert.That(refusal.Errors[nameof(Transaction.Description)][0]).Contains(AddingRefusalPhrase);
        await Assert.That(refusal.Errors[nameof(Transaction.Description)][0])
            .DoesNotContain(ClearingRefusalPhrase);

        // And the row is exactly as the account held it.
        await Assert.That(fixture.Rows.SaveCallCount).IsEqualTo(0);
        await Assert.That(fixture.NoteLessTransaction.Description).IsNull();
        await Assert.That(fixture.NoteLessTransaction.RotationId).IsNull();
    }

    /// <summary>
    /// A reseal may not take a value out of a column that holds one. <b>The arm that carries the data
    /// loss.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mirror, on a different entity on purpose.</b> Its sibling above exercises
    /// <c>Transaction.ResealDescription</c>; this one exercises <c>CategoryGroup.Reseal</c>, where the
    /// nullable description sits beside a <b>required</b> name — so the chunk is otherwise perfectly
    /// well-formed and only the note's presence is wrong. Written on one entity twice, these two cases
    /// would say nothing about whether the second reseal member routes through the rule at all.
    /// </para>
    /// <para>
    /// <b>Why this arm is the expensive one.</b> The column is nullable, so writing the absence through
    /// produces a legal row violating no constraint and byte-identical to one belonging to somebody who
    /// deliberately filed no note. Nothing in the schema can tell that bug from an operation, which is
    /// why the refusal has to live in the domain rule.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunk_WhenAResealWouldClearANarrativeFieldThatHasOne_Refuses()
    {
        // Arrange — the account's noted category group, and a chunk that re-seals its name and leaves
        // the note out.
        Fixture fixture = Fixture.Build();
        ResealRowsCommand command = fixture.Chunk with
        {
            CategoryGroups =
            [
                new ResealedCategoryGroup(
                    fixture.CategoryGroup.Id,
                    SealedNarrative.Indexed("everyday rotated"),
                    Description: null),
            ],
        };

        // The premise: the row really holds a note, so the absence below is a change rather than a
        // restatement.
        await Assert.That(fixture.CategoryGroup.Description).IsNotNull();

        // Act
        ValidationException refusal =
            await ThrowsAsync<ValidationException>(() => fixture.Handler.HandleAsync(command));

        // Assert — keyed on the column, and the clearing arm by its sentence.
        await Assert.That(refusal.Errors.ContainsKey(nameof(CategoryGroup.Description))).IsTrue();
        await Assert.That(refusal.Errors[nameof(CategoryGroup.Description)][0])
            .Contains(ClearingRefusalPhrase);
        await Assert.That(refusal.Errors[nameof(CategoryGroup.Description)][0])
            .DoesNotContain(AddingRefusalPhrase);

        // And the row is untouched — including the name, which the refused reseal must not have written
        // on its way to judging the note.
        await Assert.That(fixture.Rows.SaveCallCount).IsEqualTo(0);
        await Assert.That(fixture.CategoryGroup.Description).IsNotNull();
        await Assert.That(fixture.CategoryGroup.RotationId).IsNull();
        await Assert.That(fixture.CategoryGroup.NameKey.ToArray())
            .IsEquivalentTo(
                fixture.SeededCategoryGroupName.BlindIndex.ToArray(),
                CollectionOrdering.Matching);
    }

    /// <summary>
    /// A chunk naming some of an account's rows leaves the rest exactly as they were — <b>stamp
    /// included</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A rotation is chunked, so "the rows this request did not name" is the ordinary state rather
    /// than an edge.</b> The failure this refuses is a handler that walked the account instead of the
    /// command — which would be invisible on a small fixture and, on a real account, would stamp rows it
    /// never re-sealed. A stamp on un-rewritten ciphertext is the one signal the destructive completion
    /// step trusts, so a false stamp is a row promoted out of reach with nothing thrown and nothing
    /// logged.
    /// </para>
    /// <para>
    /// <b>Both halves of the second payee are read.</b> The stamp alone would pass against a handler
    /// that rewrote the ciphertext of every row and stamped only the ones it was given — which is the
    /// same data loss arriving through the other door.
    /// </para>
    /// <para>
    /// <b>Two arms rather than one, and <c>transactions</c> is the second because of what it costs
    /// there.</b> Every other arm holds a row per thing a person names; this one holds a row per
    /// movement of money, so it is the largest table in the product by a wide margin on any account
    /// that has been used — an over-reaching handler rewrites more rows there than in the other four
    /// put together, and every one of them under a stamp the rest of the run never earned. The two
    /// siblings also fail differently: a payee's blind index is the whole of counterparty
    /// deduplication, while a transaction's note is free text nothing looks up.
    /// </para>
    /// <para>
    /// <b>THE PORT IS SET TO OVER-ANSWER, AND WITHOUT THAT THIS CASE COULD NOT FAIL AT ALL.</b>
    /// <see cref="InMemoryNarrativeResealRepository" /> normally answers exactly the ids it was handed,
    /// which makes "iterate the command" and "iterate the dictionary the port returned" the same
    /// program — so the defect below would be unobservable and the case would be a decoration whatever
    /// its name said. <see cref="InMemoryNarrativeResealRepository.OverAnswers" /> makes a list call
    /// answer every row of its kind, which is what a query that lost its identifier predicate really
    /// does, and the two programs come apart.
    /// </para>
    /// <para>
    /// <b>What is therefore under test is a property of the handler and not of the query: a chunk
    /// rewrites the rows the command names and no others, whatever the port hands back.</b> Defence in
    /// depth, and worth having on this path specifically — what is being written is a stamp the
    /// destructive completion step trusts, so a row rewritten because it happened to be in the answer
    /// carries a claim the client never made. That the real adapter scopes its query is a separate
    /// claim, held by <c>ResealChunkTests.ResealChunk_LoadsOnlyTheRowsTheChunkNames</c>, which asks the
    /// adapter for its answer rather than reading a chunk's side effects.
    /// </para>
    /// <para>
    /// <b>Most of this rule is already held by the Domain, and the remainder is what this case is
    /// for.</b> Each reseal member takes the row's new value — an <see cref="IndexedName" />, a
    /// <see cref="NarrativeField" />? — and there is no overload that writes a stamp alone, so a row the
    /// chunk supplied nothing for cannot be stamped without a value being invented for it. What is left
    /// over is a handler that invents one by reaching into the command for a value that is not this
    /// row's, which is exactly what iterating the loaded set forces somebody to write.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunk_RewritesNoRowItWasNotGiven()
    {
        // Arrange — the account holds two payees and two noted transactions; the chunk names one of
        // each, and the port is made to hand back both of each.
        Fixture fixture = Fixture.Build();
        fixture.Rows.OverAnswers = true;
        ResealRowsCommand command = fixture.Chunk with
        {
            Payees = [new ResealedPayee(fixture.Payee.Id, SealedNarrative.Indexed("grocer rotated"))],
            Transactions =
            [
                new ResealedTransaction(
                    fixture.Transaction.Id,
                    SealedNarrative.Description("groceries note rotated")),
            ],
        };

        // The premise: both siblings really exist, both really carry a narrative value — a row bearing
        // none is one a chunk is right to skip, so an unnoted sibling would prove nothing — and neither
        // is named by the chunk.
        await Assert.That(fixture.OtherPayee.Id).IsNotEqualTo(fixture.Payee.Id);
        await Assert.That(fixture.OtherTransaction.Id).IsNotEqualTo(fixture.Transaction.Id);
        await Assert.That(fixture.OtherTransaction.Description).IsNotNull();
        await Assert.That(command.Payees.Select(payee => payee.Id).ToArray())
            .DoesNotContain(fixture.OtherPayee.Id);
        await Assert.That(command.Transactions.Select(entry => entry.Id).ToArray())
            .DoesNotContain(fixture.OtherTransaction.Id);

        // And the premise the whole case rests on: the port really does hand the handler rows the
        // command never named. Asserted against the port rather than trusted from the flag, because a
        // seam that silently stopped over-answering would turn this case back into the decoration it
        // was written to stop being — and it would do so while staying green.
        IReadOnlyDictionary<Guid, Payee> answered =
            await fixture.Rows.ListPayeesAsync([fixture.Payee.Id]);
        await Assert.That(answered.ContainsKey(fixture.OtherPayee.Id)).IsTrue();

        // Act — whatever escapes is CAPTURED rather than allowed to end the test, and that is not
        // leniency. A handler driving from the loaded set rewrites the payee sibling and only then
        // meets a refusal further down the arms — measured: it reaches Transaction.ResealDescription on
        // the account's note-less row and NarrativeReseal refuses the note it invented for it. Let that
        // escape and the test reports a ValidationException from three frames away while the damage
        // this case is named for goes unmentioned. Captured, the assertions below name the rewritten
        // row, and the escape is asserted afterwards so nothing is swallowed.
        Exception? escaped = await CaptureAsync(() => fixture.Handler.HandleAsync(command));

        // Assert — the named rows moved. This is also what stops the capture above from turning a
        // handler that refused on its way in into a pass: a chunk that did nothing leaves these null.
        await Assert.That(fixture.Payee.RotationId).IsEqualTo(fixture.RotationId);
        await Assert.That(fixture.Transaction.RotationId).IsEqualTo(fixture.RotationId);

        // And the unnamed payee did not, in either column. This is the assertion the defect lands on:
        // payees carry a required name and no nullable column, so nothing one ring down refuses a
        // reseal of one — the Domain's presence rule cannot help here and this line is the whole guard.
        await Assert.That(fixture.OtherPayee.RotationId).IsNull();
        await Assert.That(fixture.OtherPayee.NameKey.ToArray())
            .IsEquivalentTo(
                fixture.SeededOtherPayeeName.BlindIndex.ToArray(),
                CollectionOrdering.Matching);
        await Assert.That(fixture.OtherPayee.Name.Envelope.ToArray())
            .IsEquivalentTo(
                fixture.SeededOtherPayeeName.Name.Envelope.ToArray(),
                CollectionOrdering.Matching);

        // Nor the unnamed transaction, in either of the two columns it has.
        await Assert.That(fixture.OtherTransaction.RotationId).IsNull();
        await Assert.That(fixture.OtherTransaction.Description!.Envelope.ToArray())
            .IsEquivalentTo(
                fixture.SeededOtherTransactionNote.Envelope.ToArray(),
                CollectionOrdering.Matching);

        // Last, because it is the least informative of the failures available here: a correct chunk
        // over an over-answering port completes.
        await Assert.That(escaped).IsNull();
    }

    /// <summary>
    /// A chunk whose last arm names a row the ambient budget cannot see is refused <b>before any
    /// entity is mutated</b>, not merely before anything is saved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule is: resolve every row across all five arms, then mutate.</b> The shape somebody
    /// writes naturally is the other one — load an arm, apply it, move to the next — and it is wrong in
    /// a way no assertion about the database can see. A reseal applied to a tracked entity is a pending
    /// <c>UPDATE</c> on a request-scoped context; the refusal on the fifth arm stops <em>this</em> save,
    /// and the next thing in the request that saves anything flushes the four arms that were already
    /// rewritten. They then carry new ciphertext under a stamp naming a chunk that was refused, which is
    /// the one signal the destructive completion step trusts.
    /// </para>
    /// <para>
    /// <b>This is why the save count is not the assertion.</b> <c>SaveCallCount == 0</c> is true of the
    /// natural wrong shape as well as the right one — it is what
    /// <c>ResealChunkTests.ResealChunk_WhenARowBelongsToAnotherBudget_IsRefused</c> can see from the
    /// database, and the reason it is true there is that nothing saved, not that nothing moved. The
    /// entities themselves are what tells the two apart, and the in-memory port hands back the very
    /// instances the fixture holds, so this tier can read them after the throw. The count is asserted
    /// too, because a handler that saved and <em>then</em> refused is a third failure neither of the
    /// others names.
    /// </para>
    /// <para>
    /// <b>The invisible row is in the last arm and a satisfiable row is in the first</b>, which is the
    /// only arrangement that distinguishes the two shapes: an unreadable row in the first arm is
    /// refused before anything has been applied whichever way the handler is written.
    /// </para>
    /// <para>
    /// <b>It is also the <see cref="NotFoundException" /> refusal's only unit-tier home.</b> Why that
    /// exception rather than a validation error: the five sets carry the <c>BudgetIsolation</c> query
    /// filter, so the row is <em>invisible</em> and not forbidden, and a caller that read a missing key
    /// as "nothing to do here" would answer success to a chunk that re-sealed nothing — after which the
    /// client counts those rows as done and the completeness gate refuses a run it believes it
    /// finished.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunk_WhenTheLastArmNamesAnInvisibleRow_RefusesBeforeMutatingAnything()
    {
        // Arrange — four arms this account can satisfy, and a transaction arm naming a row no read of
        // this budget can answer for.
        Fixture fixture = Fixture.Build();
        Guid invisibleTransactionId = Guid.CreateVersion7();
        ResealRowsCommand command = fixture.Chunk with
        {
            Transactions =
            [
                new ResealedTransaction(
                    invisibleTransactionId,
                    SealedNarrative.Description("stranger note rotated")),
            ],
        };

        // The premise this case turns on: the arms ahead of the failing one are genuinely satisfiable,
        // so a handler applying as it goes really would have rewritten them.
        await Assert.That(command.Accounts[0].Id).IsEqualTo(fixture.Account.Id);
        await Assert.That(command.Payees[0].Id).IsEqualTo(fixture.Payee.Id);
        await Assert.That(invisibleTransactionId).IsNotEqualTo(fixture.Transaction.Id);

        // Act
        NotFoundException refusal =
            await ThrowsAsync<NotFoundException>(() => fixture.Handler.HandleAsync(command));

        // Assert — it refused. The message is not pinned: it reaches a caller as a 404 body, and
        // asserting its wording here would make that a phrasing test.
        await Assert.That(refusal).IsNotNull();

        // And NOT ONE entity moved. Collected and asserted as an empty list rather than arm by arm, so
        // a failure names every arm that was rewritten and not merely the first — the shape this suite
        // uses wherever a census would otherwise be truncated into one name.
        (string Arm, Guid? Stamp)[] arms =
        [
            ("accounts", fixture.Account.RotationId),
            ("payees", fixture.Payee.RotationId),
            ("category_groups", fixture.CategoryGroup.RotationId),
            ("categories", fixture.Category.RotationId),
            ("transactions", fixture.Transaction.RotationId),
        ];

        await Assert.That(arms.Where(arm => arm.Stamp is not null).Select(arm => arm.Arm).ToArray())
            .IsEmpty();

        // The stamp is the cheap half; the ciphertext is the half that is lost. Read on the two arms
        // ahead of the failure whose old value this fixture kept, because a reseal writes both columns
        // in one call and an assertion over one of them would pass against a member that wrote the
        // other.
        await Assert.That(fixture.Payee.NameKey.ToArray())
            .IsEquivalentTo(
                fixture.SeededPayeeName.BlindIndex.ToArray(),
                CollectionOrdering.Matching);
        await Assert.That(fixture.CategoryGroup.NameKey.ToArray())
            .IsEquivalentTo(
                fixture.SeededCategoryGroupName.BlindIndex.ToArray(),
                CollectionOrdering.Matching);

        // And nothing was asked to be persisted, which is the third failure — a handler that saved the
        // four legible arms and then refused for the fifth.
        await Assert.That(fixture.Rows.SaveCallCount).IsEqualTo(0);
    }

    /// <summary>
    /// The control: a chunk for the staged run rewrites and stamps every arm it names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Without this, a handler that refused everything satisfies every refusal above.</b> That is the
    /// counterweight <c>RotationCompletenessTests</c> keeps for its own gate and for the same reason: a
    /// rotation that can never make progress is a feature that silently does not work rather than a
    /// crash anybody reports.
    /// </para>
    /// <para>
    /// <b>All five arms are named in one chunk, and each is asserted on its own line.</b> An arm a
    /// handler forgot is a table whose rows stay outstanding for ever, and the completeness gate then
    /// refuses the whole run with nothing saying which arm went missing — so a single "something
    /// changed" assertion would be the shape that hides it.
    /// </para>
    /// <para>
    /// <b>The ciphertext is asserted beside the stamp, in order.</b> The envelopes the chunk carries are
    /// derived from labels that differ from the seeded ones, so a handler that stamped the rows and
    /// discarded the values fails here — that is the half of "together" this tier can see, and
    /// <c>ResealChunkTests</c> holds the half that needs a transaction. TUnit's bare
    /// <c>IsEquivalentTo</c> defaults to <see cref="CollectionOrdering.Any" />, and for bytes nothing on
    /// this side can read, position is the whole of what "the value the client sent" means.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunk_ForTheStagedRotation_RewritesAndStampsEveryArm()
    {
        // Arrange
        Fixture fixture = Fixture.Build();

        // Act
        await fixture.Handler.HandleAsync(fixture.Chunk);

        // Assert — one line per arm, so a failure says which one stood still.
        await Assert.That(fixture.Account.RotationId).IsEqualTo(fixture.RotationId);
        await Assert.That(fixture.Payee.RotationId).IsEqualTo(fixture.RotationId);
        await Assert.That(fixture.CategoryGroup.RotationId).IsEqualTo(fixture.RotationId);
        await Assert.That(fixture.Category.RotationId).IsEqualTo(fixture.RotationId);
        await Assert.That(fixture.Transaction.RotationId).IsEqualTo(fixture.RotationId);

        // And the values really moved, rather than the stamp arriving on its own.
        await Assert.That(fixture.Payee.Name.Envelope.ToArray())
            .IsEquivalentTo(
                fixture.Chunk.Payees[0].Name.Name.Envelope.ToArray(),
                CollectionOrdering.Matching);
        await Assert.That(fixture.Payee.NameKey.ToArray())
            .IsEquivalentTo(
                fixture.Chunk.Payees[0].Name.BlindIndex.ToArray(),
                CollectionOrdering.Matching);
        await Assert.That(fixture.Transaction.Description!.Envelope.ToArray())
            .IsEquivalentTo(
                fixture.Chunk.Transactions[0].Description!.Envelope.ToArray(),
                CollectionOrdering.Matching);

        // And it was persisted once, not once per arm.
        await Assert.That(fixture.Rows.SaveCallCount).IsEqualTo(1);
    }

    /// <summary>
    /// The save happens <b>inside</b> the unit of work, not beside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not in the caller's list, and written because every case above passes against a handler that
    /// opens no transaction at all.</b> A handler that enters the executor, does nothing in the delegate
    /// and saves afterwards satisfies every count in this file — the rows moved, the save happened once
    /// — and is wrong in the one way that matters here: five arms save together or the account ends up
    /// half under each key, and a write outside the transaction is a write nothing rolls back.
    /// </para>
    /// <para>
    /// <b>The observation is the fake reporting the call it received, not a model of PostgreSQL.</b>
    /// <see cref="ObservedTransactionalExecutor" /> sets its flag around the handler's own delegate and
    /// <see cref="InMemoryNarrativeResealRepository.ObserveAtSave" /> reads it as the save arrives, so
    /// what is asserted is the control flow the handler produced. Whether a rollback would really take
    /// the rows back needs a database and belongs to <c>ResealChunkTests</c>.
    /// </para>
    /// <para>
    /// A null observation means nothing was ever saved, which is a different failure from a write on the
    /// wrong side of the delegate, so both are named.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunk_SavesInsideTheUnitOfWork()
    {
        // Arrange
        Fixture fixture = Fixture.Build();
        fixture.Rows.ObserveAtSave = () => fixture.UnitOfWork.InsideUnitOfWork;

        // Act
        await fixture.Handler.HandleAsync(fixture.Chunk);

        // Assert
        await Assert.That(fixture.Rows.ObservationAtSave).IsNotNull();
        await Assert.That(fixture.Rows.ObservationAtSave).IsTrue();
    }

    /// <summary>
    /// Runs <paramref name="action" /> and returns the exception it was expected to throw.
    /// </summary>
    /// <remarks>
    /// The catch names <typeparamref name="TException" /> exactly, so an exception of any other type
    /// escapes and fails the test as itself rather than as "the expected exception was not thrown" —
    /// which matters here, where a refusal about a rotation identifier and a refusal about a column's
    /// presence are both a <see cref="ValidationException" /> and a missing row is not.
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
    /// Runs an action and hands back whatever escaped it, or <see langword="null" />.
    /// </summary>
    /// <remarks>
    /// For the one case where the failure that matters happens <em>before</em> the throw, so letting
    /// the exception end the test would report a frame three rings down instead of the row that was
    /// rewritten. Every other case here wants <see cref="ThrowsAsync{TException}" />, which names the
    /// type it expects and lets anything else fail as itself.
    /// </remarks>
    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception escaped)
        {
            return escaped;
        }
    }

    /// <summary>
    /// The handler, the account it rewrites, and the chunk a correct client sends — assembled once so
    /// no case has to restate a four-argument constructor or a five-armed command.
    /// </summary>
    private sealed record Fixture(
        ResealRowsHandler Handler,
        ResealRowsCommand Chunk,
        InMemoryNarrativeResealRepository Rows,
        InMemoryKeyRotationRepository KeyRotations,
        ObservedTransactionalExecutor UnitOfWork,
        Guid UserId,
        Guid BudgetId,
        Guid RotationId,
        Account Account,
        Payee Payee,
        Payee OtherPayee,
        CategoryGroup CategoryGroup,
        Category Category,
        Transaction Transaction,
        Transaction OtherTransaction,
        Transaction NoteLessTransaction,
        IndexedName SeededPayeeName,
        IndexedName SeededOtherPayeeName,
        IndexedName SeededCategoryGroupName,
        NarrativeField SeededOtherTransactionNote)
    {
        /// <summary>
        /// Fixed instant for every seeded row, so nothing in this file depends on the wall clock.
        /// </summary>
        public static readonly DateTime UtcNow = new(2026, 9, 10, 11, 12, 13, DateTimeKind.Utc);

        /// <summary>Minor unit of the USD rows seeded here; precision is what no case is about.</summary>
        private const int UsdMinorUnit = 2;

        /// <summary>
        /// Builds the handler over fresh fakes: an account with one row in each of the five arms, an
        /// unnamed sibling on two of them, a note-less transaction, and — unless
        /// <paramref name="stageRotation" /> says otherwise — one rotation staged.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The staged rotation is built by <c>KeyRotation.Begin</c> over a real passkey credential</b>
        /// rather than assembled by hand, so the row this fixture holds is one a begin could really have
        /// written — the idiom <c>KeyRotationRepositoryTests</c> keeps, and the only way past that
        /// factory's own refusals.
        /// </para>
        /// <para>
        /// <b><paramref name="stageRotation" /> DEFAULTS TO THE STAGED ACCOUNT, AND THE PARAMETER EXISTS
        /// BECAUSE ITS ABSENCE LEFT HALF A GUARD UNHELD.</b> Every other case in this file wants an
        /// account with a run in flight, and for as long as that was the only shape available the
        /// refusal's two arms could not be told apart: a handler refusing only when the staged
        /// identifier <em>differs</em> — and letting a missing row through — satisfied every one of
        /// them. <see cref="ResealChunk_WhenNoRotationIsStaged_RefusesAndRewritesNothing" /> is the one
        /// case that passes <see langword="false" />, and it is the only thing in either tier that
        /// arranges an account with nothing staged.
        /// </para>
        /// </remarks>
        /// <param name="stageRotation">
        /// Whether the account holds a staged rotation. <see langword="false" /> is the account a chunk
        /// arrives at when no begin ever ran, or when the run it names was abandoned and cleared.
        /// </param>
        public static Fixture Build(bool stageRotation = true)
        {
            Guid userId = Guid.CreateVersion7();
            Guid budgetId = Guid.CreateVersion7();
            Guid rotationId = Guid.CreateVersion7();

            InMemoryKeyRotationRepository keyRotations = new();

            // The identifier is minted either way, because it is the CLIENT's to mint: a chunk naming a
            // run no key_rotations row has ever matched is a well-formed request carrying a Guid, and
            // the whole question is what the handler does with it.
            if (stageRotation)
            {
                keyRotations.SeedStagedRotation(KeyRotation.Begin(
                    Credential.CreatePasskey(userId, UtcNow),
                    rotationId,
                    Manifest(0x20),
                    StagedRotationEpoch,
                    UtcNow));
            }

            // Every seeded name is kept beside the entity, because a case asserting that an untouched
            // row is untouched has to compare against what it held rather than against "not the new
            // value" — two envelopes that merely differ would satisfy a handler that wrote a third.
            IndexedName accountName = SealedNarrative.Indexed("household checking");
            IndexedName payeeName = SealedNarrative.Indexed("household grocer");
            IndexedName otherPayeeName = SealedNarrative.Indexed("household baker");
            IndexedName groupName = SealedNarrative.Indexed("household everyday");
            IndexedName categoryName = SealedNarrative.Indexed("household groceries");

            Account account = Account.Create(
                Guid.CreateVersion7(),
                budgetId,
                accountName,
                AccountType.Checking,
                0m,
                "USD",
                UsdMinorUnit,
                UtcNow);
            Payee payee = Payee.Create(Guid.CreateVersion7(), budgetId, payeeName, UtcNow);
            Payee otherPayee = Payee.Create(Guid.CreateVersion7(), budgetId, otherPayeeName, UtcNow);
            CategoryGroup group = CategoryGroup.Create(
                Guid.CreateVersion7(),
                budgetId,
                groupName,
                SealedNarrative.Description("household group note"),
                0,
                UtcNow);
            Category category = Category.Create(
                Guid.CreateVersion7(),
                budgetId,
                group.Id,
                categoryName,
                SealedNarrative.Description("household category note"),
                0,
                UtcNow);
            Transaction transaction = Transaction.Create(
                Guid.CreateVersion7(),
                budgetId,
                account.Id,
                -10m,
                UsdMinorUnit,
                new DateOnly(2026, 6, 12),
                SealedNarrative.Description("household groceries"),
                UtcNow);

            // The unnamed sibling on the arm where over-reach costs the most. It carries a note on
            // purpose: a transaction bearing no narrative is one a chunk is right never to visit, so a
            // bare sibling could not tell "left alone" from "skipped".
            NarrativeField otherTransactionNote = SealedNarrative.Description("household hardware");
            Transaction otherTransaction = Transaction.Create(
                Guid.CreateVersion7(),
                budgetId,
                account.Id,
                -47m,
                UsdMinorUnit,
                new DateOnly(2026, 6, 14),
                otherTransactionNote,
                UtcNow);

            // No note at all, which is what most transactions in a real account are and is the shape
            // the presence rule's adding arm needs.
            Transaction noteLess = Transaction.Create(
                Guid.CreateVersion7(),
                budgetId,
                account.Id,
                -3m,
                UsdMinorUnit,
                new DateOnly(2026, 6, 13),
                description: null,
                UtcNow);

            InMemoryNarrativeResealRepository rows = new();
            rows.Seed(account);
            rows.Seed(payee);
            rows.Seed(otherPayee);
            rows.Seed(group);
            rows.Seed(category);
            rows.Seed(transaction);
            rows.Seed(otherTransaction);
            rows.Seed(noteLess);

            // THE CHUNK A CORRECT CLIENT SENDS: one entry per arm, every value re-sealed under a label
            // that differs from the seeded one so a handler that discarded the payload is visible, and
            // every note present exactly where the row already holds one. The note-less transaction is
            // deliberately absent — a chunk with nothing to rewrite on a row does not visit it, which
            // is also why the completeness gate is presence-aware. A case that wants a wrong chunk
            // derives one from this with a `with` expression, so its mutation is visible where it is
            // made rather than behind a flag here.
            ResealRowsCommand chunk = new(
                rotationId,
                [new ResealedAccount(account.Id, SealedNarrative.Indexed("checking rotated"))],
                [new ResealedPayee(payee.Id, SealedNarrative.Indexed("grocer rotated"))],
                [
                    new ResealedCategoryGroup(
                        group.Id,
                        SealedNarrative.Indexed("everyday rotated"),
                        SealedNarrative.Description("group note rotated")),
                ],
                [
                    new ResealedCategory(
                        category.Id,
                        SealedNarrative.Indexed("groceries rotated"),
                        SealedNarrative.Description("category note rotated")),
                ],
                [
                    new ResealedTransaction(
                        transaction.Id,
                        SealedNarrative.Description("groceries note rotated")),
                ]);

            ObservedTransactionalExecutor unitOfWork = new(new InMemoryTransactionalExecutor());
            ResealRowsHandler handler = new(
                rows,
                keyRotations,
                new StubUserContext(userId),
                unitOfWork);

            return new Fixture(
                handler,
                chunk,
                rows,
                keyRotations,
                unitOfWork,
                userId,
                budgetId,
                rotationId,
                account,
                payee,
                otherPayee,
                group,
                category,
                transaction,
                otherTransaction,
                noteLess,
                payeeName,
                otherPayeeName,
                groupName,
                otherTransactionNote);
        }

        /// <summary>
        /// A staged factor manifest of distinct bytes. Nothing on this path reads into one.
        /// </summary>
        private static byte[] Manifest(byte seed) =>
            [.. Enumerable.Range(0, StagedManifestBytes).Select(offset => (byte)(seed + offset))];
    }
}
