using Application.Passkeys;
using Application.RecoveryCodes.GenerateRecoveryCodes;
using Application.Security;
using Domain.Users;
using ValidationException = Domain.Common.ValidationException;

namespace Application.RecoveryCodes;

/// <summary>
/// What one well-formed code of a set presents: its decoded verifier, the factor it stands for, and the
/// two envelopes that factor holds the account's keys in.
/// </summary>
/// <remarks>
/// One value of this type per code, never one per set. The three key-custody members belong to the code
/// because the key-encryption key that sealed the envelopes was derived from that code, and carrying
/// them together is what makes pairing one code's verifier with another code's envelopes
/// unrepresentable past this point rather than merely unlikely.
/// </remarks>
public readonly record struct PresentedCode(
    byte[] Verifier,
    Guid FactorId,
    byte[] WrappedContentKey,
    byte[] WrappedIndexKey);

/// <summary>
/// The one decode-and-validate step for a presented set of recovery codes, on every write path that
/// accepts one.
/// </summary>
/// <remarks>
/// <para>
/// <b>One definition rather than one per handler</b>, the shape <see cref="CanonicalIdentifier"/> and
/// <see cref="WrappedKeyEnvelope"/> already hold for the members inside it, and for the same reason: two
/// callers accepting a set of recovery codes are not two decisions about what a set is. They write the
/// same rows and the same key-custody columns, so a rule that drifted on one path would file bytes the
/// other path would have refused. What stays per caller is the <em>field</em> the refusal is keyed
/// under, which is why that is a parameter and nothing else is.
/// </para>
/// <para>
/// A throw rather than a <c>Try</c> shape, unlike the two types above: there are eight rules here and a
/// caller that had to distinguish them from a <see langword="bool"/> would be re-deriving the sentence
/// this unit already wrote. <see cref="ValidationException"/> carries the key and the sentence together,
/// and <c>ValidationExceptionHandler</c> turns it into the 400 the caller meant.
/// </para>
/// </remarks>
public static class RecoveryCodeSetValidation
{
    /// <summary>How many codes an issued set holds.</summary>
    /// <remarks>
    /// <b>Product policy, and it lives in this layer</b> — the placement
    /// <c>SessionPolicy.Lifetime</c> makes the argument for. It is not a domain invariant: a set of nine
    /// codes is not a malformed set, it is a smaller quantity of a thing somebody chose, and ADR 0002
    /// keeps policy above the invariants because the bottom is the most expensive layer to change. It is
    /// not a database constraint either — a <c>CHECK</c> counting sibling rows cannot be written without
    /// a trigger, and pushing procedural logic down to satisfy "lowest layer" is the boundary that ADR
    /// draws.
    /// <para>
    /// It sits beside the rule that applies it rather than on either handler, for the reason the type's
    /// own remarks give: two write paths accepting a set have to agree on how many codes a set is, and a
    /// number owned by one of them is a number the other copies.
    /// </para>
    /// </remarks>
    public const int RequiredCodeCount = 10;

    /// <summary>
    /// Decodes what a caller presents, or refuses it with the one thing that is wrong with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eight rules, eight sentences of their own, each keyed on the member a caller can correct. Each
    /// says something that caller can act on, and one shared "the recovery codes are invalid." would
    /// satisfy every refusal test while telling a person who has already proved presence nothing at all.
    /// </para>
    /// <para>
    /// <b>Three of the eight are about the set and five are about one code, and they run in that order
    /// for a reason.</b> The set's size is judged first, because the two set-wide rules below cannot be
    /// stated about a set of the wrong size at all; then every code is decoded, all ten of them, because
    /// a member is judged where it can be attributed to the code that sent it; then the two rules that
    /// only exist once every code has been decoded — the ones no single submission can be wrong about on
    /// its own.
    /// </para>
    /// </remarks>
    /// <param name="codes">The set a caller presented, one whole submission per code.</param>
    /// <param name="field">
    /// The member the caller carries its set on, which every refusal below is keyed under. A parameter
    /// because two callers name that member differently, and a field hard-coded to either one sends the
    /// other caller's client to correct a member its request does not have.
    /// </param>
    /// <returns>One decoded code per submission, in the order they were presented.</returns>
    /// <exception cref="ValidationException">The set, or one of its codes, is not acceptable.</exception>
    public static IReadOnlyList<PresentedCode> DecodeAndValidate(
        IReadOnlyList<RecoveryCodeSubmission>? codes,
        string field)
    {
        // Count first. Too few is a person left with fewer ways back into their account than the
        // screen told them they had; too many is a client the server no longer agrees with about what
        // a set is; zero is the argument a handler is most likely to treat as "nothing to do" and
        // answer 200 to, having replaced a live set with nothing. A missing array on the wire arrives
        // here as null despite the non-nullable declaration, and it is the same refusal — an absent
        // set is a set of the wrong size, not a fault.
        if (codes is not { Count: RequiredCodeCount })
        {
            throw Refused(field, $"Exactly {RequiredCodeCount} recovery codes are required.");
        }

        // EVERY CODE, NEVER ONLY THE FIRST. A loop that judged codes[0] and trusted the other nine
        // would file nine codes' worth of unjudged bytes into the account's key custody, and every
        // refusal test whose fault happens to sit on the first code would still be green.
        PresentedCode[] decoded = new PresentedCode[RequiredCodeCount];
        for (int index = 0; index < codes.Count; index++)
        {
            decoded[index] = Decode(codes[index], index, field);
        }

        // The rule the count check cannot express: a set of the required size that is one code short
        // of it, because two of its members repeat. Left to the database it becomes a primary-key
        // collision on verifier_hash — a 500 for a caller whose request was merely wrong, arriving
        // after the previous set has already been deleted inside the same transaction. Left to nothing
        // at all it is a person holding a card whose entries outnumber the codes their account will
        // accept.
        //
        // It is also the closest this layer gets to the entropy it cannot measure: a client repeating
        // a verifier inside one set has randomness that is not what it claims. Compared decoded, since
        // two spellings of one value — padded and unpadded — are the same secret. A duplicate ACROSS
        // sets is invisible here by design: the previous set's rows are never loaded.
        if (decoded.Select(code => Convert.ToHexString(code.Verifier))
                .Distinct(StringComparer.Ordinal)
                .Count() != decoded.Length)
        {
            throw Refused(field, "Every recovery code verifier in the set must be different.");
        }

        // THE SAME RULE OVER THE OTHER CLIENT-MINTED VALUE, AND IT CANNOT BE LEFT TO THE CONSTRAINT.
        // Ten codes are ten factors and therefore ten identifiers, and nothing outside this request can
        // see them as a set: each one on its own is a perfectly good uuid nobody else holds, so
        // PK_wrapped_account_keys is only reached when two of them arrive together. By then the previous
        // set's credential has already been deleted inside this same transaction and the sweep of its
        // sessions has already run, so the caller — whose request was merely wrong — meets a 409 that
        // says "that factor identifier is already registered", naming a factor they have never
        // registered and sending them looking for a request they never made.
        //
        // It is also the same evidence the verifier rule above refuses on: a client repeating an
        // identifier inside one set has randomness that is not what it claims, and the other nine are no
        // more trustworthy than the repeated one. Judged here, it is a 400 with a sentence, before
        // anything has been read or removed.
        if (decoded.Select(code => code.FactorId).Distinct().Count() != decoded.Length)
        {
            throw Refused(field, "Every factor identifier in the set must be different.");
        }

        return decoded;
    }

    /// <summary>
    /// Decodes the code at <paramref name="ordinal"/>, or refuses it naming which code and which of its
    /// members was wrong.
    /// </summary>
    /// <remarks>
    /// The ordinal is in every key rather than in the sentences, so the message states the requirement
    /// whole — the same for all ten codes — while the key says which submission to correct. A refusal
    /// naming only the set would leave a client re-deriving ten codes when one of them was the problem.
    /// </remarks>
    private static PresentedCode Decode(RecoveryCodeSubmission? submission, int ordinal, string field)
    {
        // Declared nullable against a non-nullable element type, because a `codes` array holding a JSON
        // null binds one — the serializer honours no declaration this layer makes. It has nothing to
        // decode, and it is refused as the whole submission rather than as one of its members, because
        // none of them arrived.
        if (submission is null)
        {
            throw Refused(
                CodeAt(field, ordinal),
                "Each recovery code must carry a verifier, a factor identifier and both wrapped keys.");
        }

        // One refusal covers "not base64url" and "wrong width" because the sentence states the
        // whole requirement, and because splitting them would tell a caller which half of an
        // opaque value it got wrong. The ceiling is the width itself, so no oversized member is
        // ever decoded — PasskeyEncoding.TryDecode judges the encoded length before it validates
        // or allocates.
        //
        // Reused rather than re-spelled: base64url is the one alphabet every binary member of this
        // exchange crosses JSON in, and two decoders with different bounds is a difference nobody
        // meant.
        if (!PasskeyEncoding.TryDecode(submission.Verifier, RecoveryCodeHash.VerifierLength, out byte[]? verifier)
            || verifier.Length != RecoveryCodeHash.VerifierLength)
        {
            throw Refused(
                CodeMember(field, ordinal, nameof(RecoveryCodeSubmission.Verifier)),
                "Each recovery code verifier must be base64url text decoding to exactly "
                + $"{RecoveryCodeHash.VerifierLength} bytes.");
        }

        // One spelling of the identifier and no more, judged by CanonicalIdentifier because this path and
        // passkey registration write the same column and must not drift on what that spelling is. The
        // rule is shared; this sentence is not — it names which of the ten codes to correct. The all-zero
        // uuid is worse here than on the other path: ten codes would send it ten times and the set would
        // take itself down on its own insert.
        if (!CanonicalIdentifier.TryParse(submission.FactorId, out Guid factorId))
        {
            throw Refused(
                CodeMember(field, ordinal, nameof(RecoveryCodeSubmission.FactorId)),
                "The factor identifier must be a uuid in the lower-case 36-character hyphenated form "
                + "with no surrounding whitespace, and not the all-zero uuid.");
        }

        // ONE SENTENCE PER MEMBER, STATING THE WHOLE REQUIREMENT, for the reason the verifier refusal
        // above gives about its own: splitting "not base64url" from "wrong width" from "unknown version"
        // would tell a caller which half of an opaque value it got wrong. The two envelopes are judged
        // separately because they are supplied separately — a caller that decoded one and passed the
        // other through would file whatever a client felt like sending into half of the account's key
        // custody.
        //
        // The width and the version are read off the entity that refuses a row against them, never
        // written out here: a message carrying its own copy of either goes on being confident after the
        // real bound has moved.
        if (!WrappedKeyEnvelope.TryDecode(submission.WrappedContentKey, out byte[]? wrappedContentKey))
        {
            throw Refused(
                CodeMember(field, ordinal, nameof(RecoveryCodeSubmission.WrappedContentKey)),
                MalformedEnvelope("wrapped content key"));
        }

        if (!WrappedKeyEnvelope.TryDecode(submission.WrappedIndexKey, out byte[]? wrappedIndexKey))
        {
            throw Refused(
                CodeMember(field, ordinal, nameof(RecoveryCodeSubmission.WrappedIndexKey)),
                MalformedEnvelope("wrapped index key"));
        }

        return new PresentedCode(verifier, factorId, wrappedContentKey, wrappedIndexKey);
    }

    /// <summary>
    /// The key one submission of the set is refused under, when the whole submission is what is wrong.
    /// </summary>
    private static string CodeAt(string field, int ordinal) => $"{field}[{ordinal}]";

    /// <summary>
    /// The key one code's member is refused under: which submission of the set, and which member of it.
    /// </summary>
    private static string CodeMember(string field, int ordinal, string member) =>
        $"{CodeAt(field, ordinal)}.{member}";

    /// <summary>
    /// What is required of <paramref name="member"/>, said whole rather than split into which part of it
    /// was wrong.
    /// </summary>
    private static string MalformedEnvelope(string member) =>
        $"The {member} must be base64url text decoding to exactly "
        + $"{WrappedAccountKeys.EnvelopeLength} bytes carrying envelope version "
        + $"{WrappedAccountKeys.EnvelopeVersion}.";

    // Domain.Common.ValidationException by name, because both layers declare one and only that one is
    // what ValidationExceptionHandler turns into a 400 with the field errors on it.
    //
    // The field is a parameter rather than one constant for the whole method, because a refusal is only
    // actionable if it is filed under the member the caller can correct: a malformed envelope reported
    // against the whole set would send a client to re-derive ten codes that were never the problem. The
    // three set-wide rules land on the caller's own field, because a set is what is wrong with those;
    // everything a single submission can be wrong about lands on that submission's own member — see
    // CodeMember.
    private static ValidationException Refused(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}
