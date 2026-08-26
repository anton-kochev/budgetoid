using System.Security.Cryptography;
using Application.RecoveryCodes;
using Application.RecoveryCodes.GenerateRecoveryCodes;
using Domain.Users;
using TestSupport;
using ValidationException = Domain.Common.ValidationException;

namespace UnitTests;

/// <summary>
/// The one decode-and-validate step for a presented set of recovery codes, shared by every write path
/// that accepts one: what a well-formed set decodes to, the eight ways a set is refused, and the member
/// each refusal is keyed under.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared rather than per handler</b>, for the reason <c>CanonicalIdentifier</c> and
/// <c>WrappedKeyEnvelope</c> already give for the members inside it: two callers accepting a set of
/// recovery codes are not two decisions about what a set is. They write the same rows and the same
/// key-custody columns, so a rule that drifted on one path would file bytes the other path would have
/// refused. What stays per caller is the <em>field</em> the refusal is keyed under, which is why that is
/// a parameter and nothing else is.
/// </para>
/// <para>
/// <b>Eight rules, three about the set and five about one code, and the order is load-bearing.</b> The
/// size is judged first, because neither set-wide rule below can be stated about a set of the wrong size
/// at all; then every code is decoded, all ten of them, because a member is judged where it can be
/// attributed to the code that sent it; then the two rules that only exist once every code has been
/// decoded. Each test below drives exactly one of the eight, on a set that is otherwise faultless, so a
/// refusal cannot be produced by a rule the test never meant to trip.
/// </para>
/// <para>
/// <b>Which control covers which claim.</b>
/// <see cref="DecodeAndValidate_WithAWellFormedSet_DecodesEveryCodeInOrder" /> is the provable-fail
/// control for all eight refusals — without it a function that threw unconditionally passes every one of
/// them. <see cref="DecodeAndValidate_RefusesEachFaultWithASentenceOfItsOwn" /> is the control for the
/// sentences: each refusal test asserts only that a key is present, and one shared "the recovery codes
/// are invalid." satisfies all eight while telling a caller who has already proved presence nothing they
/// can act on.
/// </para>
/// <para>
/// <b>The fault sits on the last code, never the first.</b> A validation that judged <c>codes[0]</c> and
/// trusted the other nine would file nine codes' worth of unjudged bytes into the account's key custody,
/// and would be green on every case whose fault happened to sit at the front. It is also what makes the
/// ordinal in each key say something: the key names the submission a caller has to correct.
/// </para>
/// <para>
/// <b>The server cannot check entropy and no test here pretends otherwise.</b> It receives fixed-width
/// opaque bytes, and a set of ten identical zero-filled verifiers is indistinguishable here from a set a
/// good generator produced. Width, set size and distinctness are the whole of what this layer can judge.
/// </para>
/// </remarks>
public sealed class RecoveryCodeSetValidationTests
{
    /// <summary>
    /// How many codes an issued set holds.
    /// </summary>
    /// <remarks>
    /// Restated rather than read off the type under test, for the reason
    /// <c>GenerateRecoveryCodesHandlerTests.RequiredCodeCount</c> gives: a test taking its expectation
    /// from the type under test agrees with whatever that type later decides. This number is defined
    /// here, so it is the pin. The widths below are the opposite case and are read from the domain
    /// constants that own them — this unit applies those numbers, it does not define them, and a copy
    /// would let the edge and the entity drift apart while staying green.
    /// </remarks>
    private const int RequiredCodeCount = 10;

    /// <summary>Which code of the set carries the one fault, for every test that plants one.</summary>
    private const int FaultedOrdinal = RequiredCodeCount - 1;

    /// <summary>
    /// The field the refusals of the first caller are keyed under.
    /// </summary>
    /// <remarks>
    /// A literal rather than <c>nameof(GenerateRecoveryCodesCommand.Codes)</c>, because the point of the
    /// parameter is that this unit has no opinion about which member a caller carries its set on. Read
    /// off a command, this constant would quietly become a second definition of that command's shape.
    /// </remarks>
    private const string PrimaryField = "Codes";

    /// <summary>The field a second caller keys its refusals under.</summary>
    private const string SecondaryField = "RecoveryCodes";

    /// <summary>
    /// The byte that says which of the two envelopes an assertion is looking at.
    /// </summary>
    /// <remarks>
    /// Two values rather than one, because the two members are otherwise indistinguishable: same width,
    /// same version, both required. It is what makes a decoder that filed the content key in the index
    /// slot visible — the one mistake at this layer that satisfies every width check, every version
    /// check and every database constraint.
    /// </remarks>
    private const byte ContentKeyPurpose = 0xC0;

    private const byte IndexKeyPurpose = 0x1D;

    /// <summary>
    /// A well-formed set decodes to one <see cref="PresentedCode" /> per submission, in order, carrying
    /// exactly the bytes that were presented.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The values are asserted, not the count.</b> A function returning ten default structs, or ten
    /// copies of the first code, satisfies every other test in this file — and what it would file is the
    /// account's key custody.
    /// </para>
    /// <para>
    /// <b>Index by index, because the pairing is the claim.</b> A code's verifier and that code's two
    /// envelopes must not come apart: the key-encryption key that sealed the envelopes was derived from
    /// that code, so pairing one code's verifier with another code's envelopes satisfies every width,
    /// version, owner and uniqueness rule this system holds and is discovered months later by somebody
    /// who redeemed a code, was handed a session, and found the account still locked. The two envelopes
    /// carry different purpose bytes, so a decoder that swapped them is red here too.
    /// </para>
    /// </remarks>
    [Test]
    public async Task DecodeAndValidate_WithAWellFormedSet_DecodesEveryCodeInOrder()
    {
        // Arrange
        RecoveryCodeSubmission[] presented = WellFormedSet();

        // Act
        IReadOnlyList<PresentedCode> decoded =
            RecoveryCodeSetValidation.DecodeAndValidate(presented, PrimaryField);

        // Assert
        await Assert.That(decoded.Count).IsEqualTo(RequiredCodeCount);

        for (int ordinal = 0; ordinal < presented.Length; ordinal++)
        {
            RecoveryCodeSubmission submission = presented[ordinal];
            PresentedCode code = decoded[ordinal];

            await Assert.That(code.Verifier).IsEquivalentTo(Base64UrlText.Decode(submission.Verifier));
            await Assert.That(code.FactorId).IsEqualTo(Guid.Parse(submission.FactorId));
            await Assert.That(code.WrappedContentKey)
                .IsEquivalentTo(Base64UrlText.Decode(submission.WrappedContentKey));
            await Assert.That(code.WrappedIndexKey)
                .IsEquivalentTo(Base64UrlText.Decode(submission.WrappedIndexKey));
        }
    }

    /// <summary>
    /// An absent set is refused, keyed on the set.
    /// </summary>
    /// <remarks>
    /// A missing array on the wire arrives here as <see langword="null" /> despite the non-nullable
    /// declaration — the serializer honours no declaration this layer makes — and it is the same refusal
    /// a set of the wrong size gets, because an absent set <em>is</em> a set of the wrong size rather
    /// than a fault. A function that dereferenced it would answer a 500 to a caller whose request was
    /// merely wrong.
    /// </remarks>
    [Test]
    public async Task DecodeAndValidate_WithNoSetAtAll_IsRefused()
    {
        // Act
        ValidationException exception = Refused(null);

        // Assert
        await AssertKeyedUnder(exception, PrimaryField);
    }

    /// <summary>
    /// A set of any size other than the one the product issues is refused, keyed on the set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both directions and the empty set, because the three fail for different reasons. Too few is a
    /// person left with fewer ways back into their account than the screen told them they had; too many
    /// is a client the server no longer agrees with about what a set is; zero is the argument a caller is
    /// most likely to treat as "nothing to do" and answer 200 to, having replaced a live set with
    /// nothing.
    /// </para>
    /// <para>
    /// <b>Keyed on the set and never on one submission</b>, which is what the size of a set is about: a
    /// caller sending nine codes has nothing to correct on any one of them.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(0)]
    [Arguments(RequiredCodeCount - 1)]
    [Arguments(RequiredCodeCount + 1)]
    public async Task DecodeAndValidate_WithTheWrongNumberOfCodes_IsRefused(int count)
    {
        // Arrange
        RecoveryCodeSubmission[] presented = WellFormedSet(count);

        // Act
        ValidationException exception = Refused(presented);

        // Assert
        await AssertKeyedUnder(exception, PrimaryField);
    }

    /// <summary>
    /// A set of the right size holding an absent submission is refused, keyed on that submission whole.
    /// </summary>
    /// <remarks>
    /// A <c>codes</c> array holding a JSON null binds one element of <see langword="null" />, whatever
    /// the element type declares. It has nothing to decode, so it is refused as the whole submission
    /// rather than as one of its members — none of them arrived — and the count check cannot see it: the
    /// set really is ten members long.
    /// </remarks>
    [Test]
    public async Task DecodeAndValidate_WithAnAbsentSubmission_IsRefused()
    {
        // Arrange
        RecoveryCodeSubmission[] presented = WellFormedSet();
        presented[FaultedOrdinal] = null!;

        // Act
        ValidationException exception = Refused(presented);

        // Assert
        await AssertKeyedUnder(exception, CodeAt(PrimaryField, FaultedOrdinal));
    }

    /// <summary>
    /// A verifier that does not decode to exactly the specified width is refused, keyed on that code's
    /// own verifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One byte either side, because the bound is the assertion. Short means a shorter secret than the
    /// design claims, and it would hash to a perfectly well-formed 32-byte row nothing downstream could
    /// tell from a real one — the column's length check watches the <em>hash</em>, which is 32 bytes
    /// whatever went into it. Long means the client and the server disagree about what a verifier is,
    /// and since the same code also derives the key-encryption key, a width quietly accepted here
    /// surfaces much later as a key that will not unwrap.
    /// </para>
    /// <para>
    /// The width is read from <see cref="RecoveryCodeHash.VerifierLength" /> rather than restated: this
    /// unit applies that number and does not own it, and a copy here would go on agreeing with itself
    /// after the real bound moved.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(RecoveryCodeHash.VerifierLength - 1)]
    [Arguments(RecoveryCodeHash.VerifierLength + 1)]
    public async Task DecodeAndValidate_WithAVerifierOfTheWrongWidth_IsRefused(int width)
    {
        // Arrange
        RecoveryCodeSubmission[] presented = WellFormedSet();
        presented[FaultedOrdinal] = presented[FaultedOrdinal] with
        {
            Verifier = Base64UrlText.Encode(RandomNumberGenerator.GetBytes(width)),
        };

        // Act
        ValidationException exception = Refused(presented);

        // Assert
        await AssertKeyedUnder(
            exception,
            CodeMember(PrimaryField, FaultedOrdinal, nameof(RecoveryCodeSubmission.Verifier)));
    }

    /// <summary>
    /// A verifier that is not base64url is refused, keyed on that code's own verifier.
    /// </summary>
    /// <remarks>
    /// The other half of the same rule and the same sentence, which states the whole requirement rather
    /// than which half of an opaque value the caller got wrong. Base64url is the one alphabet every
    /// binary member of this exchange crosses JSON in; the empty string is driven beside it because an
    /// unbound form control sends <c>""</c> and not <see langword="null" />.
    /// </remarks>
    [Test]
    [Arguments("")]
    [Arguments("not base64url at all!")]
    public async Task DecodeAndValidate_WithAVerifierThatIsNotBase64Url_IsRefused(string verifier)
    {
        // Arrange
        RecoveryCodeSubmission[] presented = WellFormedSet();
        presented[FaultedOrdinal] = presented[FaultedOrdinal] with { Verifier = verifier };

        // Act
        ValidationException exception = Refused(presented);

        // Assert
        await AssertKeyedUnder(
            exception,
            CodeMember(PrimaryField, FaultedOrdinal, nameof(RecoveryCodeSubmission.Verifier)));
    }

    /// <summary>
    /// A factor identifier spelled any way but the one canonical form is refused, keyed on that code's
    /// own factor identifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The spellings are the test.</b> <see cref="Guid.TryParseExact(string, string, out Guid)" />
    /// under <c>"D"</c> admits upper-case and mixed-case hex and trims whitespace before it reads the
    /// format at all, so a validation written as a parse alone accepts every one of the first three
    /// below and hands back a value the client cannot recognise as the bytes it bound. That value is the
    /// associated data both envelopes were sealed with, so both stop opening, permanently, with nothing
    /// naming the cause.
    /// </para>
    /// <para>
    /// The all-zero uuid is refused for its own reason rather than as a spelling: it is what an unset
    /// field sends and the one value two accounts reach independently, and ten codes would send it ten
    /// times — the set would take itself down on its own insert.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("6E8BC430-9C3A-11D9-9669-0800200C9A66")]
    [Arguments("6e8bc4309c3a11d996690800200c9a66")]
    [Arguments(" 6e8bc430-9c3a-11d9-9669-0800200c9a66 ")]
    [Arguments("00000000-0000-0000-0000-000000000000")]
    [Arguments("")]
    [Arguments("not a uuid")]
    public async Task DecodeAndValidate_WithAFactorIdOutsideTheOneCanonicalSpelling_IsRefused(string factorId)
    {
        // Arrange
        RecoveryCodeSubmission[] presented = WellFormedSet();
        presented[FaultedOrdinal] = presented[FaultedOrdinal] with { FactorId = factorId };

        // Act
        ValidationException exception = Refused(presented);

        // Assert
        await AssertKeyedUnder(
            exception,
            CodeMember(PrimaryField, FaultedOrdinal, nameof(RecoveryCodeSubmission.FactorId)));
    }

    /// <summary>
    /// A malformed wrapped content key is refused, keyed on that code's own content key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The envelope carries the legal width and an <em>unrecognised version byte</em>, which is the
    /// sharpest single arrangement available: it is not null, not empty, decodes cleanly and is exactly
    /// the right size, so nothing but a real call through <c>WrappedKeyEnvelope.TryDecode</c> refuses it.
    /// A validation that null-checked the member, or measured its length, is green on this and would file
    /// a client's arbitrary bytes into half of the account's key custody.
    /// </para>
    /// <para>
    /// The two envelopes are judged separately because they are supplied separately —
    /// <see cref="DecodeAndValidate_WithAMalformedWrappedIndexKey_IsRefused" /> is the same arrangement
    /// on the other member, and a validation that decoded one and passed the other through fails exactly
    /// one of the pair.
    /// </para>
    /// </remarks>
    [Test]
    public async Task DecodeAndValidate_WithAMalformedWrappedContentKey_IsRefused()
    {
        // Arrange
        RecoveryCodeSubmission[] presented = WellFormedSet();
        presented[FaultedOrdinal] = presented[FaultedOrdinal] with
        {
            WrappedContentKey = Base64UrlText.Encode(MisversionedEnvelope(ContentKeyPurpose)),
        };

        // Act
        ValidationException exception = Refused(presented);

        // Assert
        await AssertKeyedUnder(
            exception,
            CodeMember(PrimaryField, FaultedOrdinal, nameof(RecoveryCodeSubmission.WrappedContentKey)));
    }

    /// <summary>
    /// A malformed wrapped index key is refused, keyed on that code's own index key.
    /// </summary>
    /// <remarks>
    /// The other half of the pair above. Both members are required and neither may be passed through:
    /// half an account's key custody filed from unjudged bytes is discovered the day somebody needs the
    /// keys, and not before.
    /// </remarks>
    [Test]
    public async Task DecodeAndValidate_WithAMalformedWrappedIndexKey_IsRefused()
    {
        // Arrange
        RecoveryCodeSubmission[] presented = WellFormedSet();
        presented[FaultedOrdinal] = presented[FaultedOrdinal] with
        {
            WrappedIndexKey = Base64UrlText.Encode(MisversionedEnvelope(IndexKeyPurpose)),
        };

        // Act
        ValidationException exception = Refused(presented);

        // Assert
        await AssertKeyedUnder(
            exception,
            CodeMember(PrimaryField, FaultedOrdinal, nameof(RecoveryCodeSubmission.WrappedIndexKey)));
    }

    /// <summary>
    /// Ten codes of which two carry the same verifier is refused, keyed on the set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule the count check cannot express: this set <em>is</em> ten members long and is nine codes
    /// deep. Left to the database it becomes a primary-key collision on <c>verifier_hash</c> — a 500 for
    /// a caller whose request was merely wrong, arriving after the previous set has already been deleted
    /// inside the same transaction.
    /// </para>
    /// <para>
    /// <b>Keyed on the set, and this is the key that could be argued either way.</b> Two submissions are
    /// wrong and neither is wrong on its own — each is a perfectly good code until the other is read — so
    /// the fault belongs to the set, and naming one of the pair would be picking a culprit between two
    /// identical claims. The repeated code keeps its own factor identifier, so the refusal is
    /// attributable to the verifier rule rather than to the one below it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task DecodeAndValidate_WithADuplicateVerifierInTheSet_IsRefused()
    {
        // Arrange
        RecoveryCodeSubmission[] presented = WellFormedSet();
        presented[FaultedOrdinal] = presented[FaultedOrdinal] with { Verifier = presented[0].Verifier };

        // Act
        ValidationException exception = Refused(presented);

        // Assert
        await AssertKeyedUnder(exception, PrimaryField);
    }

    /// <summary>
    /// Ten codes of which two carry the same factor identifier is refused, keyed on the set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same rule over the other client-minted value, and it cannot be left to the constraint. Ten
    /// codes are ten factors and nothing outside this request can see them as a set: each identifier on
    /// its own is a perfectly good uuid nobody else holds, so <c>PK_wrapped_account_keys</c> is only
    /// reached when two of them arrive together — by which time the previous set's credential has been
    /// deleted inside the same transaction, and the caller meets a 409 naming a factor they have never
    /// registered.
    /// </para>
    /// <para>
    /// The repeated code keeps its own verifier, so this cannot be the verifier rule refusing first —
    /// which is the whole reason the two duplicate tests are separate.
    /// </para>
    /// </remarks>
    [Test]
    public async Task DecodeAndValidate_WithADuplicateFactorIdInTheSet_IsRefused()
    {
        // Arrange
        RecoveryCodeSubmission[] presented = WellFormedSet();
        presented[FaultedOrdinal] = presented[FaultedOrdinal] with { FactorId = presented[0].FactorId };

        // Act
        ValidationException exception = Refused(presented);

        // Assert
        await AssertKeyedUnder(exception, PrimaryField);
    }

    /// <summary>
    /// Every refusal is keyed under the field this call was given, ordinal and member included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one behaviour in this file that is new rather than moved.</b> The rules and their sentences
    /// come across unchanged from the handler that used to own them privately; what the extraction adds
    /// is that the member a refusal is filed under belongs to the caller, because two callers carry a set
    /// on two differently named members. A field hard-coded to either one sends the other caller's client
    /// to correct a member its request does not have.
    /// </para>
    /// <para>
    /// <b>Both key shapes, not just the bare field.</b> A set-wide refusal lands on the field itself; a
    /// refusal about one code lands on <c>field[ordinal].Member</c>, and an implementation that
    /// parameterized only the first would be green on half of this. The ordinal is in the key rather than
    /// in the sentence so the message can state the requirement whole while the key says which submission
    /// to correct.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(PrimaryField)]
    [Arguments(SecondaryField)]
    public async Task DecodeAndValidate_KeysItsRefusalsUnderTheFieldItWasGiven(string field)
    {
        // Arrange — one set that is the wrong size, and one whose last code carries a bad verifier.
        RecoveryCodeSubmission[] misSized = WellFormedSet(RequiredCodeCount - 1);
        RecoveryCodeSubmission[] misVerified = WellFormedSet();
        misVerified[FaultedOrdinal] = misVerified[FaultedOrdinal] with { Verifier = "not base64url at all!" };

        // Act
        ValidationException aboutTheSet = Refused(misSized, field);
        ValidationException aboutOneCode = Refused(misVerified, field);

        // Assert
        await AssertKeyedUnder(aboutTheSet, field);
        await AssertKeyedUnder(
            aboutOneCode,
            CodeMember(field, FaultedOrdinal, nameof(RecoveryCodeSubmission.Verifier)));
    }

    /// <summary>
    /// The eight refusals say eight different things.
    /// </summary>
    /// <remarks>
    /// The "real sentence" half of the family, which no single refusal test can state: each of the eight
    /// asserts that a key is present, and one shared "The recovery codes are invalid." satisfies all of
    /// them while telling a caller who has already proved presence nothing they can act on. Distinctness
    /// is what makes each sentence carry its own content — including the two envelope messages, which
    /// differ only in which member they name and are the pair most likely to be collapsed into one.
    /// </remarks>
    [Test]
    public async Task DecodeAndValidate_RefusesEachFaultWithASentenceOfItsOwn()
    {
        // Arrange
        RecoveryCodeSubmission[] absentSubmission = WellFormedSet();
        absentSubmission[FaultedOrdinal] = null!;

        RecoveryCodeSubmission[] badVerifier = WellFormedSet();
        badVerifier[FaultedOrdinal] = badVerifier[FaultedOrdinal] with { Verifier = "not base64url at all!" };

        RecoveryCodeSubmission[] badFactorId = WellFormedSet();
        badFactorId[FaultedOrdinal] = badFactorId[FaultedOrdinal] with { FactorId = "not a uuid" };

        RecoveryCodeSubmission[] badContentKey = WellFormedSet();
        badContentKey[FaultedOrdinal] = badContentKey[FaultedOrdinal] with
        {
            WrappedContentKey = Base64UrlText.Encode(MisversionedEnvelope(ContentKeyPurpose)),
        };

        RecoveryCodeSubmission[] badIndexKey = WellFormedSet();
        badIndexKey[FaultedOrdinal] = badIndexKey[FaultedOrdinal] with
        {
            WrappedIndexKey = Base64UrlText.Encode(MisversionedEnvelope(IndexKeyPurpose)),
        };

        RecoveryCodeSubmission[] duplicateVerifier = WellFormedSet();
        duplicateVerifier[FaultedOrdinal] = duplicateVerifier[FaultedOrdinal] with
        {
            Verifier = duplicateVerifier[0].Verifier,
        };

        RecoveryCodeSubmission[] duplicateFactorId = WellFormedSet();
        duplicateFactorId[FaultedOrdinal] = duplicateFactorId[FaultedOrdinal] with
        {
            FactorId = duplicateFactorId[0].FactorId,
        };

        // Act
        string[] sentences =
        [
            SentenceOf(null),
            SentenceOf(absentSubmission),
            SentenceOf(badVerifier),
            SentenceOf(badFactorId),
            SentenceOf(badContentKey),
            SentenceOf(badIndexKey),
            SentenceOf(duplicateVerifier),
            SentenceOf(duplicateFactorId),
        ];

        // Assert — the absent set and the wrong size deliberately share one sentence, so eight
        // arrangements carry eight distinct messages only because the missing set is not among them.
        await Assert.That(sentences.All(sentence => !string.IsNullOrWhiteSpace(sentence))).IsTrue();
        await Assert.That(sentences.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(sentences.Length);
    }

    /// <summary>
    /// Drives a refusal and returns the exception it carried.
    /// </summary>
    /// <remarks>
    /// The catch names <see cref="ValidationException" /> exactly, so anything else escapes and fails the
    /// test as itself rather than as "the expected exception was not thrown". The alias at the top of the
    /// file is load-bearing: both <c>Domain.Common</c> and the framework declare one, and only the
    /// domain's is what <c>ValidationExceptionHandler</c> turns into a 400 with field errors on it.
    /// </remarks>
    private static ValidationException Refused(
        IReadOnlyList<RecoveryCodeSubmission>? codes,
        string field = PrimaryField)
    {
        try
        {
            RecoveryCodeSetValidation.DecodeAndValidate(codes, field);
        }
        catch (ValidationException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected the set to be refused.");
    }

    /// <summary>Drives a refusal and hands back the one sentence it carried.</summary>
    private static string SentenceOf(IReadOnlyList<RecoveryCodeSubmission>? codes) =>
        string.Join(" ", Refused(codes).Errors.Values.SelectMany(messages => messages));

    /// <summary>
    /// One error, under exactly the expected key.
    /// </summary>
    /// <remarks>
    /// The count is asserted beside the key, because a refusal reporting several members at once is a
    /// different contract from the one every caller of this unit is written against — and a
    /// <c>ContainsKey</c> assertion cannot tell the two apart.
    /// </remarks>
    private static async Task AssertKeyedUnder(ValidationException exception, string key)
    {
        await Assert.That(exception.Errors.Count).IsEqualTo(1);
        await Assert.That(exception.Errors.Keys.First()).IsEqualTo(key);
    }

    /// <summary>The key one submission of the set is refused under, when the whole submission is wrong.</summary>
    /// <remarks>
    /// Spelled out here rather than read off the type under test, because the shape of the key is the
    /// pin: a test taking it from the production helper agrees with a key that had quietly stopped naming
    /// the submission at all.
    /// </remarks>
    private static string CodeAt(string field, int ordinal) => $"{field}[{ordinal}]";

    /// <summary>The key one code's member is refused under.</summary>
    private static string CodeMember(string field, int ordinal, string member) =>
        $"{CodeAt(field, ordinal)}.{member}";

    /// <summary>
    /// <paramref name="count" /> whole submissions, every member of every one of them faultless.
    /// </summary>
    /// <remarks>
    /// <b>Faultless on every path, which is what keeps each test on its own subject.</b> A malformed
    /// envelope or a repeated factor introduced here would be refused before or instead of the rule a
    /// test was written for, and that test would go on passing while saying nothing. Each submission gets
    /// a fresh verifier and a fresh factor identifier, so the two distinctness rules are only ever
    /// tripped by a test that trips them on purpose.
    /// </remarks>
    private static RecoveryCodeSubmission[] WellFormedSet(int count = RequiredCodeCount) =>
    [
        .. Enumerable.Range(0, count).Select(_ => new RecoveryCodeSubmission(
            Base64UrlText.Encode(RandomNumberGenerator.GetBytes(RecoveryCodeHash.VerifierLength)),
            Guid.CreateVersion7().ToString("D"),
            Base64UrlText.Encode(Envelope(ContentKeyPurpose)),
            Base64UrlText.Encode(Envelope(IndexKeyPurpose)))),
    ];

    /// <summary>
    /// A well-formed wrapped-key envelope: the one version the contract defines, the byte that says which
    /// of the two members this is, and random bytes to the exact width.
    /// </summary>
    /// <remarks>
    /// The width and the version are read off <see cref="WrappedAccountKeys" /> rather than restated.
    /// Nothing in this file is about the envelope's shape — <c>WrappedKeyEnvelopeTests</c> owns that — so
    /// a copy of either bound would turn every test here red on the day it moved, for a reason none of
    /// them is about.
    /// </remarks>
    private static byte[] Envelope(byte purpose)
    {
        byte[] envelope = RandomNumberGenerator.GetBytes(WrappedAccountKeys.EnvelopeLength);
        envelope[0] = WrappedAccountKeys.EnvelopeVersion;
        envelope[1] = purpose;

        return envelope;
    }

    /// <summary>
    /// The same envelope carrying a version this deployment has never implemented.
    /// </summary>
    /// <remarks>
    /// The legal width and a leading byte one greater than the only version defined, so it differs from a
    /// well-formed envelope in one bit of one byte. That is what makes it proof the member was really
    /// decoded rather than merely present.
    /// </remarks>
    private static byte[] MisversionedEnvelope(byte purpose)
    {
        byte[] envelope = Envelope(purpose);
        envelope[0] = (byte)(WrappedAccountKeys.EnvelopeVersion + 1);

        return envelope;
    }
}
