using System.Reflection;
using Domain.Accounts;
using Domain.Budgets;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Common;
using Domain.Payees;
using Domain.Security;
using Domain.Transactions;
using TUnit.Assertions.Enums;

namespace UnitTests;

/// <summary>
/// The one rule a content-key rotation can be held to by a server that can decrypt nothing: a column
/// that held a value still holds one, and a column that held nothing still holds nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read this paragraph before building anything on the type below, because the rule is weaker than
/// its name suggests.</b> A rotation re-encrypts every narrative field of an account under a new
/// content key. The server holds no key for any of it, so it cannot compare the plaintext going in
/// against the plaintext coming out — and a re-sealed envelope is byte-for-byte indistinguishable from
/// an unrelated one, because a fresh nonce changes the bytes either way and the associated data is
/// never carried inside the envelope. A client that sent "rotate" and shipped somebody else's text, or
/// its own text with a word changed, is <em>accepted</em> by everything on this side. There is exactly
/// one property left that this side can check without a key: <b>presence</b>. Whether a column holds
/// something is visible without opening it, and that is the whole of what separates "the same text
/// under a new key" from "different text" to a party in this position. It is a weak rule. It is also
/// the strongest one available, and those two sentences have to be held together — a reader who takes
/// this for an integrity check will build a completion step, an audit, or a support answer on sand.
/// The check that the new generation opens at all is the browser's, and it happens after the run.
/// </para>
/// <para>
/// <b>One owner rather than the same four lines inside six entities.</b> Six entities are getting a
/// reseal member and eight narrative columns will pass through it. A rule restated six times is a rule
/// that drifts five ways, and this is the drift nobody sees: the copy that forgot the null arm still
/// stores, still reads back and still opens, differing from the others only in what it lets a rotation
/// do to a column nobody exercised that day. The argument is
/// <see cref="NarrativeFieldLimits"/>'s about its two constants, applied one ring up — the class is the
/// unit the rule is written in, so the class is the unit the code declares.
/// </para>
/// <para>
/// <b>Refusals are <see cref="ValidationException"/>, keyed on the property the value lands in.</b>
/// That is the entity factories' idiom — <c>WrappedAccountKeys.For</c> keys on
/// <c>nameof(WrappedPrivateKey)</c>, <c>PasskeyPublicKey.Register</c> on its own member — and it is
/// available here, where it was not available to <see cref="NarrativeField"/>: that type is shared by
/// eight columns and owns none of them, so every one of them would key under one word. This rule is
/// shared by eight columns too, but it is <em>told</em> which one it is being run for, so the key is
/// the caller's to supply and the refusal names a member the request actually carries.
/// </para>
/// <para>
/// <b>Two words of prose are pinned below, and that is a compromise rather than a preference.</b> Both
/// refusals key on the same column, so the key cannot separate them; both are 400s, so the exception
/// type cannot either; and <see cref="ValidationException"/> carries nothing else — it is a message
/// dictionary and no more. That leaves the sentence as the only thing on this side that differs
/// between "you cleared a field" and "you invented one", which are opposite client bugs. So
/// <see cref="Resealed_AnswersEachMistakeWithTheSentenceWrittenForIt"/> pins one stem per arm, the
/// shape <c>RegisterAccountHandlerTests</c> uses where a key-only assertion cannot see which refusal
/// it got, and <c>AccountTests</c> uses for the one message in its file that is computed. Nothing else
/// about either sentence is asserted — <c>docs/design/voice.md</c> owns them, and a test quoting one
/// whole would be a second owner of prose meant to be rewritten.
/// </para>
/// <para>
/// <b>The robust fix is a production member this file deliberately does not invent.</b> What would make
/// the distinction checkable without touching prose at all is the arrangement
/// <see cref="ConflictKind"/> already is one file over: a closed vocabulary beside the sentence, added
/// because "every 409 in this product answers under one fixed title, so the only thing separating two
/// conflicts was a sentence written for a person to read". That is this situation exactly, one status
/// code down, and <see cref="ValidationException"/> has no equivalent. Giving it one is a change to
/// <c>Domain/Common</c> affecting every refusal in the product, which is a decision to take
/// deliberately and not a side effect of wanting a firmer assertion here. Until it is taken, the stem
/// pin is the honest ceiling, and the ceiling is lower than it first reads: it catches a pair of
/// single-stem sentences handed to the wrong arms, it does <em>not</em> catch a pair that each mention
/// both states, and it will need editing if the sentences are reworded past those two words. That case's
/// own remarks carry the mutation that proved the limit, so nobody has to rediscover it.
/// </para>
/// <para>
/// <b>Which control covers which claim</b>, because a refusal test with no accepting twin is passed by
/// a rule that refuses everything:
/// </para>
/// <list type="bullet">
/// <item>
/// "a value may be replaced by a value" — <see cref="Resealed_WithAValueOverAValue_TakesTheIncomingEnvelope"/>,
/// whose two envelopes carry different filler on purpose. This is the only case that can catch a rule
/// which judged the pair correctly and then handed back the envelope it already had: both are the same
/// width, both carry the same version byte, both satisfy every check constraint underneath, and a
/// rotation that returned the old ciphertext would leave rows sealed under a key that is about to be
/// destroyed by the promotion step, with nothing red anywhere until the account stops opening.
/// </item>
/// <item>
/// "nothing over nothing is not a refusal" —
/// <see cref="Resealed_WithNothingOverNothing_LeavesTheColumnEmpty"/>, which is the control for both
/// refusals below: without it, a rule that threw on any null at all would pass them.
/// </item>
/// <item>
/// "a rotation may not clear a field" and "a rotation may not create one" —
/// <see cref="Resealed_WithNothingOverAValue_Throws"/> and
/// <see cref="Resealed_WithAValueOverNothing_Throws"/>, with
/// <see cref="Resealed_TellsClearingAFieldApartFromInventingOne"/> holding that the two stay two and
/// <see cref="Resealed_AnswersEachMistakeWithTheSentenceWrittenForIt"/> holding that each arm's message
/// carries its own stem. That second one is weaker than it looks and its remarks say by how much; it
/// does not hold that a sentence lacks the other arm's stem, so a pair of messages each mentioning both
/// states can still be swapped with nothing red.
/// </item>
/// <item>
/// "presence is the whole of the rule, and length is somebody else's" —
/// <see cref="Resealed_WithAnEnvelopePastTheNameCap_IsStillJustAPresenceQuestion"/>, which pins an
/// absence: it goes red the day a cap check appears here. Every other case in this file uses an
/// envelope of the shortest legal length, which is inside both caps, so without it a rule that
/// re-validated a description against <see cref="NarrativeFieldLimits.NameBytes"/> would be green here
/// and would refuse real descriptions in production.
/// </item>
/// <item>
/// "it holds for all four nullable columns, keyed on each" —
/// <see cref="Resealed_KeepsThePresenceRuleForEveryNullableNarrativeColumn"/>, whose rows carry their
/// own control: each asserts by reflection that the property it names really is a nullable narrative
/// column before running the table against it.
/// </item>
/// <item>
/// "four columns are nullable, four are not, and one narrative property is not a column" —
/// <see cref="EveryNarrativeProperty_IsANullableColumn_ARequiredColumn_OrTheOneCarrier"/>, which is the
/// expressible half of the paragraph below. Read its remarks before counting properties: there are nine
/// narrative properties and eight narrative columns, and the difference is not an error.
/// </item>
/// </list>
/// <para>
/// <b>Four nullable narrative columns, not three, and the fourth is the one a reader will miss.</b> The
/// three descriptions are obvious — <c>categories.description</c>, <c>category_groups.description</c>,
/// <c>transactions.description</c> — and <see cref="Budget.Name"/> is the fourth: a budget is
/// provisioned nameless by the one path that creates an account, so the column that would otherwise be
/// a name like any other is nullable, and the rule has to hold for it identically. A reseal written
/// against "the descriptions" would leave the one narrative column a person never asked for outside the
/// rule, on the entity that anchors the rotation stamp.
/// </para>
/// <para>
/// <b>A non-nullable narrative column cannot reach this rule at all, and that sentence is only half
/// testable.</b> <c>accounts.name</c>, <c>payees.name</c>, <c>categories.name</c> and
/// <c>category_groups.name</c> are typed <see cref="NarrativeField"/> and not
/// <see cref="NarrativeField"/><c>?</c>, so presence is carried by the signature: a reseal member for
/// one of them takes a value that cannot be absent, there is no null to judge, and the compiler is what
/// says so. The expressible half is the census below, which pins <em>which</em> four they are — the
/// day somebody makes <see cref="Account.Name"/> nullable, a fifth column silently joins this rule's
/// reach and that test is what says so out loud. The inexpressible half is "nothing calls it for those
/// four", which is a fact about call sites that do not exist yet; asserting it today would mean pinning
/// the shape of <see cref="NarrativeReseal"/>'s public surface, which is a different claim wearing this
/// one's name. It is written here instead, deliberately, rather than faked with a reflection assertion
/// that would redden on the first legitimate member added beside it.
/// </para>
/// <para>
/// <b>Counting: nine narrative properties, eight narrative columns.</b> The ninth is
/// <see cref="IndexedName.Name"/>, which is a carrier and not a column — a sealed name and its blind
/// index travelling together so that a call cannot supply half a name. Each of the four name columns is
/// still its own property on its own entity, filled from that carrier by the entity's factory. The
/// census's remarks argue it; this sentence is here so that a reader who meets "eight columns" in the
/// paragraphs above does not go looking for eight properties and find nine.
/// </para>
/// <para>
/// <b>No envelope here is a real envelope, and nothing at this layer could tell.</b> The fillers are
/// framing-shaped bytes and nothing else — a nonce of zeros and a tag of zeros are well-formed by every
/// rule this side owns, as <see cref="NarrativeField"/> says of itself. What the values need to be is
/// <em>different from each other</em>, which is what makes the accepting case able to fail.
/// </para>
/// </remarks>
public sealed class NarrativeResealTests
{
    /// <summary>
    /// The column these unparameterised cases are run for — one real nullable narrative column, so the
    /// refusal key is a word a request actually carries rather than a string invented here.
    /// </summary>
    private const string Column = nameof(Category.Description);

    /// <summary>
    /// A rotation that hands back an envelope for a column that held one is taken, and what the column
    /// ends up holding is the <em>incoming</em> envelope.
    /// </summary>
    /// <remarks>
    /// The two envelopes carry different filler on purpose, and that is the whole of this test. A rule
    /// that decided the pair correctly and then returned <c>current</c> passes every assertion that only
    /// checks for a non-null result: same width, same version byte, same check constraint underneath.
    /// What it produces is a row that survived a rotation without being rotated — and the promotion step
    /// at the end of the run overwrites the live wrapped keys in place, destroying the only copies of
    /// the old ones, so that row becomes unopenable by anything in the world at the moment the run is
    /// declared a success.
    /// </remarks>
    [Test]
    public async Task Resealed_WithAValueOverAValue_TakesTheIncomingEnvelope()
    {
        // Arrange
        NarrativeField current = Field(0x11);
        NarrativeField incoming = Field(0x22);

        // Act
        NarrativeField? resealed = NarrativeReseal.Resealed(current, incoming, Column);

        // Assert — ordered, because TUnit's bare IsEquivalentTo defaults to CollectionOrdering.Any and
        // would pass on a permutation of the same bytes, which is not what an envelope is.
        await Assert.That(resealed).IsNotNull();
        await Assert.That(resealed!.Envelope.ToArray())
            .IsEquivalentTo(incoming.Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(resealed.Envelope.ToArray())
            .IsNotEquivalentTo(current.Envelope.ToArray(), CollectionOrdering.Matching);
    }

    /// <summary>
    /// A column that held nothing, and a rotation that supplies nothing for it, stays empty.
    /// </summary>
    /// <remarks>
    /// <b>The control for both refusals, and not a filler case.</b> Without it, a rule that threw
    /// whenever either side was null would pass <see cref="Resealed_WithNothingOverAValue_Throws"/> and
    /// <see cref="Resealed_WithAValueOverNothing_Throws"/> and would make every account holding a single
    /// empty description un-rotatable — a refusal the person cannot act on, because the field they are
    /// being refused for is one they never filled in.
    /// </remarks>
    [Test]
    public async Task Resealed_WithNothingOverNothing_LeavesTheColumnEmpty()
    {
        // Arrange — nothing to arrange: the absence on both sides is the input.

        // Act
        NarrativeField? resealed = NarrativeReseal.Resealed(null, null, Column);

        // Assert
        await Assert.That(resealed).IsNull();
    }

    /// <summary>
    /// A rotation that supplies nothing for a column that held a value is refused.
    /// </summary>
    /// <remarks>
    /// This is the arm that carries the data loss. The column is nullable, so writing the absence
    /// through produces a legal row that violates no constraint and is byte-identical to one belonging
    /// to somebody who deliberately filed no note — the failure <c>Category.Description</c> describes as
    /// invisible where the same omission on a <c>NOT NULL</c> name is <c>23502</c>. Nothing in the schema
    /// can tell that bug from an operation, so the refusal has to be here.
    /// </remarks>
    [Test]
    public async Task Resealed_WithNothingOverAValue_Throws()
    {
        // Arrange
        NarrativeField current = Field(0x11);

        // Act
        ValidationException exception =
            ThrowsValidationException(() => NarrativeReseal.Resealed(current, null, Column));

        // Assert — keyed on the column it was told it is running for, and on NOTHING ELSE. The exact set
        // rather than ContainsKey, because a key nobody asked for is not harmless here: the errors
        // dictionary is rendered into the problem document as it stands, nothing configures a
        // DictionaryKeyPolicy over it, so an invented word reaches the client verbatim as a 400 naming a
        // member no request carries. ContainsKey admits exactly that.
        await Assert.That(exception.Errors.Keys)
            .IsEquivalentTo(new[] { Column }, CollectionOrdering.Matching);
    }

    /// <summary>
    /// A rotation that supplies a value for a column that held nothing is refused.
    /// </summary>
    /// <remarks>
    /// The quieter arm, and the one a reviewer will propose relaxing: a rotation filling in an empty
    /// description harms no data. It is refused because presence is the <em>only</em> thing this side can
    /// check, so an arm that admits a change of presence gives up the one property the rule is made of —
    /// and what arrives in that column is text the server cannot read, attributed to a person who never
    /// wrote it, in a run they authorised as "re-encrypt what I have".
    /// </remarks>
    [Test]
    public async Task Resealed_WithAValueOverNothing_Throws()
    {
        // Arrange
        NarrativeField incoming = Field(0x22);

        // Act
        ValidationException exception =
            ThrowsValidationException(() => NarrativeReseal.Resealed(null, incoming, Column));

        // Assert — the exact key set, for the reason the clearing arm's assertion gives.
        await Assert.That(exception.Errors.Keys)
            .IsEquivalentTo(new[] { Column }, CollectionOrdering.Matching);
    }

    /// <summary>
    /// The two refusals are two, and a caller can tell which it got.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"You cleared a field" and "you invented one" are different client bugs with different
    /// remedies</b> — the first says a chunk dropped a column on its way out of the browser, the second
    /// says a chunk is sending a column the row does not have — and both key on the same property, so
    /// the key cannot be what separates them. One shared sentence across both arms is a rule that
    /// refuses correctly and reports uselessly, which is the failure <see cref="ConflictKind"/> exists
    /// one file over to prevent for conflicts.
    /// </para>
    /// <para>
    /// <b>What is asserted is that the two differ, never what either one says.</b> The sentences belong
    /// to <c>docs/design/voice.md</c>, and a test quoting one would be a second owner of prose that is
    /// meant to be rewritten. If a later commit wants a client to branch on this rather than read it, the
    /// distinction that commit adds is a closed vocabulary beside the sentence — the arrangement
    /// <see cref="ConflictException"/> already has — and this is the case that would tighten onto it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Resealed_TellsClearingAFieldApartFromInventingOne()
    {
        // Arrange
        NarrativeField value = Field(0x11);

        // Act
        ValidationException cleared =
            ThrowsValidationException(() => NarrativeReseal.Resealed(value, null, Column));
        ValidationException invented =
            ThrowsValidationException(() => NarrativeReseal.Resealed(null, value, Column));

        // Assert
        await Assert.That(cleared.Errors.Keys)
            .IsEquivalentTo(new[] { Column }, CollectionOrdering.Matching);
        await Assert.That(invented.Errors.Keys)
            .IsEquivalentTo(new[] { Column }, CollectionOrdering.Matching);
        await Assert.That(cleared.Errors[Column]).IsNotEmpty();
        await Assert.That(invented.Errors[Column]).IsNotEmpty();
        await Assert.That(string.Join("\n", cleared.Errors[Column]))
            .IsNotEqualTo(string.Join("\n", invented.Errors[Column]));
    }

    /// <summary>
    /// Each mistake is answered by the sentence written for it, and not by the other one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Exactly what this pins: each arm's message carries its own stem. Exactly what it does not: that
    /// an arm lacks the other's.</b> The distinction is not theoretical and was not reasoned — both
    /// sentences were mutated to <c>"MUTANT B: add clear 1."</c> and <c>"MUTANT B: clear add 2."</c>,
    /// which are garbage, are the wrong way round, and contain <em>both</em> stems, and every case in
    /// this file stayed green. So the pin catches the narrow swap, where each message carries one stem
    /// and the two are handed to the wrong arms. It does not catch a swap between two messages that each
    /// mention both states. Do not read it as "the sentences cannot be swapped".
    /// </para>
    /// <para>
    /// <b>The missing half is deliberate and is not worth having at this price.</b> Requiring each arm to
    /// <em>lack</em> the other's stem would close the mutation above and would also refuse "A rotation
    /// may not add a value to a field that was cleared", which says the right thing. Trading a legitimate
    /// sentence for a mutant nobody would write is the wrong trade, and a test that forbids words is a
    /// test that edits the design book from underneath it. What actually closes this is the closed
    /// vocabulary argued in the class remarks — the production member this file reports rather than
    /// invents.
    /// </para>
    /// <para>
    /// <b>Why the narrow swap is still worth catching.</b> One shared sentence tells a client nothing; a
    /// swapped pair is worse, because it sends somebody to debug the opposite of their bug — hunting a
    /// chunk that drops a column when the chunk is sending one the row has not got. The inequality in
    /// <see cref="Resealed_TellsClearingAFieldApartFromInventingOne"/> sees neither: two swapped
    /// sentences differ perfectly well.
    /// </para>
    /// <para>
    /// <b>One stem per arm and nothing else, chosen as the least that survives a rewording.</b> Neither
    /// whole sentence is asserted; both stems are ones this codebase already uses for these two states —
    /// <c>Category.Description</c>'s own remarks say "a field somebody cleared" and set "cleared"
    /// against "never filled". The comparison ignores case so that a stem opening a sentence still
    /// matches. If a later rewording drops one of these two words while still saying the right thing,
    /// this test is wrong and the row is the edit; that is the cost of prose being the only
    /// distinguisher <see cref="ValidationException"/> offers, argued in the class remarks.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(true, "clear")]
    [Arguments(false, "add")]
    public async Task Resealed_AnswersEachMistakeWithTheSentenceWrittenForIt(
        bool clearing,
        string stem)
    {
        // Arrange — one value, placed on whichever side of the rotation this row is about: held by the
        // column and not supplied (clearing), or supplied for a column that holds nothing (inventing).
        NarrativeField value = Field(0x11);
        NarrativeField? current = clearing ? value : null;
        NarrativeField? incoming = clearing ? null : value;

        // Act
        ValidationException exception =
            ThrowsValidationException(() => NarrativeReseal.Resealed(current, incoming, Column));

        // Assert
        await Assert.That(exception.Errors.Keys)
            .IsEquivalentTo(new[] { Column }, CollectionOrdering.Matching);
        await Assert.That(string.Join("\n", exception.Errors[Column]))
            .Contains(stem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An envelope past the name cap but inside the description cap passes through a description column
    /// untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This case pins an ABSENCE, which is the only reason it exists: the presence rule must not
    /// check length at all.</b> It goes red the day somebody adds cap arithmetic here, and that is the
    /// point of it rather than a side effect.
    /// </para>
    /// <para>
    /// <b>Why the absence needs guarding.</b> Every other envelope in this file is
    /// <see cref="CiphertextEnvelope.MinimumLength"/> bytes, which is inside both caps — so a rule that
    /// re-validated its input against <see cref="NarrativeFieldLimits.NameBytes"/> would be green
    /// everywhere else in this class and would refuse real descriptions in production, which run to
    /// <see cref="NarrativeFieldLimits.DescriptionBytes"/>. That is the mistake
    /// <c>Category.Description</c>'s own remarks warn about in as many words: a helper that sealed a
    /// description under the name's ceiling would refuse values this column accepts. Its mirror in
    /// <c>NarrativeFieldTests.Sealed_WithAnEnvelopeBetweenTheTwoCaps_IsDecidedByTheCeilingItWasGiven</c>
    /// uses the same width for the same reason, and is the case that proves the two caps differ.
    /// </para>
    /// <para>
    /// <b>Caps are owned elsewhere, and a second copy of that arithmetic is the copy that drifts.</b>
    /// <see cref="NarrativeFieldLimits"/> states the two numbers, <see cref="NarrativeField.Sealed"/>
    /// applies whichever one its caller names, and the column's <c>CHECK</c> constraint is built from
    /// the same constants. By the time a value reaches a reseal it has been through all three. A fourth
    /// opinion here could only ever agree redundantly or disagree silently, and the disagreeing version
    /// still stores, still reads back and still opens — it differs only in which rotations it refuses.
    /// </para>
    /// <para>
    /// <b>Both sides are over the name cap on purpose</b>, so a rule that measured the incoming envelope
    /// and a rule that measured the one already in the column both redden, rather than only whichever
    /// half a reader guessed at.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Resealed_WithAnEnvelopePastTheNameCap_IsStillJustAPresenceQuestion()
    {
        // Arrange — one byte past the name cap, which NarrativeField.Sealed accepts under the
        // description ceiling and refuses under the name one. Neither number is restated: reading both
        // is what makes this a statement about the two caps differing rather than about 1025.
        const int pastTheNameCap = NarrativeFieldLimits.NameBytes + 1;
        NarrativeField current = Field(0x11, pastTheNameCap);
        NarrativeField incoming = Field(0x22, pastTheNameCap);

        // Act
        NarrativeField? resealed = NarrativeReseal.Resealed(current, incoming, Column);

        // Assert — accepted, and handed back whole rather than clipped to anybody's ceiling.
        await Assert.That(resealed).IsNotNull();
        await Assert.That(resealed!.Envelope.Length).IsEqualTo(pastTheNameCap);
        await Assert.That(resealed.Envelope.ToArray())
            .IsEquivalentTo(incoming.Envelope.ToArray(), CollectionOrdering.Matching);
    }

    /// <summary>
    /// The whole table, run against each of the four nullable narrative columns and keyed on each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Four rows because there are four columns, though the rule only ever sees two distinct
    /// keys.</b> Three of the four are spelled <c>Description</c> and the fourth is
    /// <see cref="Budget.Name"/>, so as inputs to this rule two of the rows are duplicates — and saying
    /// so is better than letting the row count imply a coverage it does not have. What the four rows buy
    /// is the census: each one is a real column named through <c>nameof</c>, so a renamed property moves
    /// the row with it, a fifth nullable narrative column is a row somebody has to add, and the one that
    /// is not a description cannot be dropped by a reader who thinks in descriptions.
    /// </para>
    /// <para>
    /// <b>Each row controls itself.</b> The first assertion resolves the named property and checks it
    /// really is a nullable <see cref="NarrativeField"/> — without it, a row naming a column that had
    /// been made non-nullable, or removed, would keep passing while testing a string.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(nameof(Budget), nameof(Budget.Name))]
    [Arguments(nameof(Category), nameof(Category.Description))]
    [Arguments(nameof(CategoryGroup), nameof(CategoryGroup.Description))]
    [Arguments(nameof(Transaction), nameof(Transaction.Description))]
    public async Task Resealed_KeepsThePresenceRuleForEveryNullableNarrativeColumn(
        string entity,
        string column)
    {
        // Arrange — the row's own control first, so a stale row fails as a stale row.
        PropertyInfo property = NarrativeProperty(entity, column);
        await Assert.That(IsNullable(property)).IsTrue();

        NarrativeField current = Field(0x11);
        NarrativeField incoming = Field(0x22);

        // Act
        NarrativeField? overAValue = NarrativeReseal.Resealed(current, incoming, column);
        NarrativeField? overNothing = NarrativeReseal.Resealed(null, null, column);
        ValidationException cleared =
            ThrowsValidationException(() => NarrativeReseal.Resealed(current, null, column));
        ValidationException invented =
            ThrowsValidationException(() => NarrativeReseal.Resealed(null, incoming, column));

        // Assert — the same four rows of the table as above, for this column.
        await Assert.That(overAValue).IsNotNull();
        await Assert.That(overAValue!.Envelope.ToArray())
            .IsEquivalentTo(incoming.Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(overNothing).IsNull();
        await Assert.That(cleared.Errors.Keys)
            .IsEquivalentTo(new[] { column }, CollectionOrdering.Matching);
        await Assert.That(invented.Errors.Keys)
            .IsEquivalentTo(new[] { column }, CollectionOrdering.Matching);
    }

    /// <summary>
    /// Every narrative property the domain declares is a nullable column, a required column, or the one
    /// carrier that is not a column at all — and all three sets are named.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read this first: a narrative COLUMN is a property on an entity, and there are nine narrative
    /// properties but only eight columns.</b> The ninth is <see cref="IndexedName.Name"/>, and it is not
    /// a column — it is the shape a sealed name and its blind index travel in together, so that a call
    /// cannot supply half a name. An entity factory takes one <see cref="IndexedName"/> and copies it out
    /// into its own two members, so <see cref="Account.Name"/>, <see cref="Payee.Name"/>,
    /// <see cref="Category.Name"/> and <see cref="CategoryGroup.Name"/> really are four separate
    /// properties on four separate entities that happen to be filled from one parameter type. Counting
    /// properties and expecting eight is the mistake this paragraph exists to stop, because the count
    /// comes back nine and nothing about the extra one is wrong.
    /// </para>
    /// <para>
    /// <b>The carrier is pinned rather than filtered out, and that is the whole design decision
    /// here.</b> A narrower query — entities only, or a namespace exclusion, or "types EF maps" — would
    /// make this pass today and would silently swallow the next narrative property that is not a column.
    /// Whether such a property should exist is exactly the question a census is for: a second carrier is
    /// a real design event, and it should arrive as a row somebody has to write down and argue for, not
    /// as a filter quietly doing its job. So the query stays wide and all three sets are exact.
    /// </para>
    /// <para>
    /// <b>The partition is derived, the membership is pinned.</b> What separates a column from a carrier
    /// mechanically is the setter: an entity column is <c>{ get; private set; }</c> because the store
    /// materialises it, while <see cref="IndexedName"/>'s member is <c>{ get; }</c>, set once by its own
    /// constructor and never again. So the split below is read off the property rather than off a list,
    /// and only the names are written here. A get-only narrative property added to an entity would land
    /// in the carrier set and redden — correctly, because a column the store cannot write is not a
    /// column.
    /// </para>
    /// <para>
    /// <b>Why any of this is in a file about resealing.</b> The rule's reach is exactly the nullable
    /// column set: those four are the only members that can be absent, so they are the only members with
    /// a presence question. The other four carry presence in their type, and the carrier is not reachable
    /// by a reseal at all. Nothing in the schema says which columns are which, so the day a name is made
    /// nullable — or a ninth column is added — the reach changes with no other test in the suite
    /// noticing.
    /// </para>
    /// <para>
    /// <b>Discovered on one side and listed on the other, which is what makes it a census.</b> A test
    /// that derived both sides from the same reflection query would agree with whatever the domain
    /// currently says and report nothing.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EveryNarrativeProperty_IsANullableColumn_ARequiredColumn_OrTheOneCarrier()
    {
        // Arrange — every public narrative-bearing property the domain declares, wherever it lives. The
        // width of this query is deliberate; see the remarks on why nothing is filtered here.
        PropertyInfo[] narrative = typeof(Budget).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsPublic: true })
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(property => property.PropertyType == typeof(NarrativeField))
            .ToArray();

        // Act — a column is what the store can write; a carrier is set once by its own constructor.
        // CanWrite answers that whatever the setter's accessibility, which is what makes a private
        // setter still count as a column.
        string[] nullableColumns = narrative
            .Where(property => property.CanWrite && IsNullable(property))
            .Select(Qualify)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] requiredColumns = narrative
            .Where(property => property.CanWrite && !IsNullable(property))
            .Select(Qualify)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] carriers = narrative
            .Where(property => !property.CanWrite)
            .Select(Qualify)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert — ordered on both sides and compared in order, so a failure names the property that
        // moved rather than reporting a set that differs somewhere.
        await Assert.That(nullableColumns).IsEquivalentTo(
            new[]
            {
                $"{nameof(Budget)}.{nameof(Budget.Name)}",
                $"{nameof(Category)}.{nameof(Category.Description)}",
                $"{nameof(CategoryGroup)}.{nameof(CategoryGroup.Description)}",
                $"{nameof(Transaction)}.{nameof(Transaction.Description)}",
            }.Order(StringComparer.Ordinal).ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(requiredColumns).IsEquivalentTo(
            new[]
            {
                $"{nameof(Account)}.{nameof(Account.Name)}",
                $"{nameof(Category)}.{nameof(Category.Name)}",
                $"{nameof(CategoryGroup)}.{nameof(CategoryGroup.Name)}",
                $"{nameof(Payee)}.{nameof(Payee.Name)}",
            }.Order(StringComparer.Ordinal).ToArray(),
            CollectionOrdering.Matching);

        // The one property that is not a column. Its own line rather than an absence, so that a second
        // carrier has to be written down here by somebody who can say why it exists.
        await Assert.That(carriers).IsEquivalentTo(
            new[] { $"{nameof(IndexedName)}.{nameof(IndexedName.Name)}" },
            CollectionOrdering.Matching);

        // The arithmetic the rest of this file's prose rests on: eight columns, of which four can be
        // absent. Stated rather than left for a reader to count off the sets above.
        await Assert.That(nullableColumns.Length + requiredColumns.Length).IsEqualTo(8);
    }

    /// <summary>
    /// A well-formed envelope of <paramref name="width"/> bytes, filled from <paramref name="seed"/>.
    /// </summary>
    /// <remarks>
    /// The filler is not a nonce and not a ciphertext, and nothing at this layer inspects either — the
    /// server holds no value that could open one. It varies per position so that two fields built from
    /// different seeds differ in every byte after the version, which is what lets the accepting case tell
    /// the incoming envelope from the one already in the column. The width defaults to the shortest legal
    /// envelope because the presence rule is meant to be blind to length; the one case that passes a
    /// width says in its own remarks why it has to.
    /// </remarks>
    private static NarrativeField Field(byte seed, int width = CiphertextEnvelope.MinimumLength)
    {
        byte[] envelope = new byte[width];

        envelope[0] = CiphertextEnvelope.Version;

        for (int position = 1; position < envelope.Length; position++)
        {
            envelope[position] = (byte)((position * 37) + seed);
        }

        return NarrativeField.Sealed(envelope, NarrativeFieldLimits.DescriptionBytes);
    }

    /// <summary>
    /// The narrative property <paramref name="column"/> of the domain entity named
    /// <paramref name="entity"/>.
    /// </summary>
    /// <remarks>
    /// Looked up by simple name because entity names are unique across the domain's namespaces and a
    /// parameterised row cannot carry a namespace without restating one. Both lookups are
    /// <c>Single</c> on purpose: a row naming something that is no longer there, or that is now two
    /// things, fails loudly at the row rather than quietly skipping it.
    /// </remarks>
    private static PropertyInfo NarrativeProperty(string entity, string column)
    {
        Type declaring = typeof(Budget).Assembly
            .GetTypes()
            .Single(type => type is { IsClass: true, IsPublic: true } && type.Name == entity);

        return declaring
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Single(property =>
                property.Name == column && property.PropertyType == typeof(NarrativeField));
    }

    /// <summary>
    /// Whether <paramref name="property"/> is declared as accepting <see langword="null"/> on read.
    /// </summary>
    /// <remarks>
    /// Nullable reference annotations are erased from <see cref="Type"/> — both spellings are the same
    /// <see cref="NarrativeField"/> at run time — so the distinction is only reachable through the
    /// nullability metadata the compiler emits, which is what this reads.
    /// </remarks>
    private static bool IsNullable(PropertyInfo property) =>
        new NullabilityInfoContext().Create(property).ReadState == NullabilityState.Nullable;

    /// <summary>The property as this file names columns: the declaring entity, then the member.</summary>
    private static string Qualify(PropertyInfo property) =>
        $"{property.DeclaringType!.Name}.{property.Name}";

    /// <summary>
    /// Runs <paramref name="action"/> and hands back the <see cref="ValidationException"/> it raised.
    /// </summary>
    /// <remarks>
    /// The shape <c>KeyRotationTests</c> uses, restated here rather than shared: a test helper lifted
    /// into a common place is a helper every later class has to go and read before it can trust an
    /// assertion, and this one is four lines.
    /// </remarks>
    private static ValidationException ThrowsValidationException(Action action)
    {
        try
        {
            action();
        }
        catch (ValidationException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected ValidationException.");
    }
}
